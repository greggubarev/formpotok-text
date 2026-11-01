using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FormPotok.Application.Contracts;
using FormPotok.Ocr.Contracts;

[assembly: InternalsVisibleTo("FormPotok.Desktop.Services.Tests")]

namespace FormPotok.Infrastructure.OcrRuntime;

public sealed class LocalOcrRuntimeClient(LocalOcrRuntimeOptions options) : IOcrRuntimeClient
{
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static readonly HttpClient BootstrapHttpClient = CreateBootstrapHttpClient();

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task<OcrRuntimeHealth> HealthcheckAsync(CancellationToken cancellationToken = default) =>
        WithTextOnlyErrorsAsync(() => HealthcheckCoreAsync(cancellationToken), cancellationToken);

    private async Task<OcrRuntimeHealth> HealthcheckCoreAsync(CancellationToken cancellationToken)
    {
        var argumentList = new List<string>
        {
            "healthcheck",
        };

        if (!string.IsNullOrWhiteSpace(options.TesseractExecutablePath))
        {
            argumentList.Add("--tesseract-path");
            argumentList.Add(options.TesseractExecutablePath);
        }

        if (!string.IsNullOrWhiteSpace(options.TessdataDirectory))
        {
            argumentList.Add("--tessdata-dir");
            argumentList.Add(options.TessdataDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.PaddleDetectionModelDirectory))
        {
            argumentList.Add("--paddle-det-model-dir");
            argumentList.Add(options.PaddleDetectionModelDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.PaddleRecognitionModelDirectory))
        {
            argumentList.Add("--paddle-rec-model-dir");
            argumentList.Add(options.PaddleRecognitionModelDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.PaddleRecognitionModelName))
        {
            argumentList.Add("--paddle-rec-model-name");
            argumentList.Add(options.PaddleRecognitionModelName);
        }

        var stdout = await RunAsync(argumentList, options.HealthcheckTimeout, cancellationToken);

        return JsonSerializer.Deserialize<OcrRuntimeHealth>(stdout, SerializerOptions)
            ?? throw new InvalidOperationException("Sidecar healthcheck returned an empty payload.");
    }

    public Task<ExtractionEnvelope> ExtractAsync(string inputPath, CancellationToken cancellationToken = default) =>
        WithTextOnlyErrorsAsync(() => ExtractWithMemoryFallbackAsync(inputPath, cancellationToken), cancellationToken);

    private async Task<ExtractionEnvelope> ExtractWithMemoryFallbackAsync(string inputPath, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            return await ExtractCoreAsync(inputPath, options.DisableTextDocumentUnwarping,
                options.ExtractTimeout, cancellationToken);
        }
        catch (LocalOcrRuntimeException error) when (options.RetryTextWithoutUnwarpingOnMemoryLimit
            && !options.DisableTextDocumentUnwarping && options.TextOnly && options.OfflineOnly
            && options.PreferredEngine == "paddle" && error.ErrorCode == "TEXT_ONLY_MEMORY_LIMIT")
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = options.ExtractTimeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new LocalOcrRuntimeException("TEXT_ONLY_TIMEOUT");
            return await ExtractCoreAsync(inputPath, true, remaining, cancellationToken);
        }
    }

    private async Task<ExtractionEnvelope> ExtractCoreAsync(string inputPath, bool disableTextDocumentUnwarping,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if ((disableTextDocumentUnwarping || options.RetryTextWithoutUnwarpingOnMemoryLimit)
            && (!options.TextOnly || !options.OfflineOnly || options.PreferredEngine != "paddle"))
        {
            throw new LocalOcrRuntimeException("TEXT_ONLY_RESOURCE_GUARD_FAILED");
        }
        if (options.ForceTextOriginalVariant && (!options.TextOnly || !options.OfflineOnly
            || options.PreferredEngine != "paddle" || !disableTextDocumentUnwarping))
        {
            throw new LocalOcrRuntimeException("TEXT_ONLY_RESOURCE_GUARD_FAILED");
        }
        if (options.TileTextPage && !options.ForceTextOriginalVariant)
        {
            throw new LocalOcrRuntimeException("TEXT_ONLY_RESOURCE_GUARD_FAILED");
        }

        var outputRoot = string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? Path.Combine(options.WorkingDirectory, "output")
            : options.OutputDirectory;
        var runDirectory = Path.Combine(outputRoot, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var outputPath = Path.Combine(runDirectory, "extraction-envelope.json");

        var argumentList = new List<string>
        {
            "extract",
            "--input",
            inputPath,
            "--output",
            outputPath,
            "--engine",
            options.PreferredEngine,
            "--paddle-profile",
            options.PaddleProfile,
            "--tesseract-lang",
            options.TesseractLanguages,
        };

        if (options.TextOnly)
        {
            argumentList.Add("--text-only");
            if (options.OfflineOnly)
            {
                argumentList.Add("--offline");
                argumentList.Add("--max-pages");
                argumentList.Add(options.MaximumPages.ToString(CultureInfo.InvariantCulture));
                argumentList.Add("--max-pixels");
                argumentList.Add(options.MaximumPagePixels.ToString(CultureInfo.InvariantCulture));
                if (disableTextDocumentUnwarping)
                    argumentList.Add("--disable-text-document-unwarping");
                if (options.ForceTextOriginalVariant)
                    argumentList.Add("--force-text-original-variant");
                if (options.TileTextPage)
                    argumentList.Add("--tile-text-page");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.PaddleDetectionModelDirectory))
        {
            argumentList.Add("--paddle-det-model-dir");
            argumentList.Add(options.PaddleDetectionModelDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.PaddleRecognitionModelDirectory))
        {
            argumentList.Add("--paddle-rec-model-dir");
            argumentList.Add(options.PaddleRecognitionModelDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.PaddleRecognitionModelName))
        {
            argumentList.Add("--paddle-rec-model-name");
            argumentList.Add(options.PaddleRecognitionModelName);
        }

        if (!string.IsNullOrWhiteSpace(options.TesseractExecutablePath))
        {
            argumentList.Add("--tesseract-path");
            argumentList.Add(options.TesseractExecutablePath);
        }

        if (!string.IsNullOrWhiteSpace(options.TessdataDirectory))
        {
            argumentList.Add("--tessdata-dir");
            argumentList.Add(options.TessdataDirectory);
        }

        var stdout = await RunAsync(argumentList, timeout, cancellationToken);
        return JsonSerializer.Deserialize<ExtractionEnvelope>(stdout, SerializerOptions)
            ?? throw new InvalidOperationException("Sidecar extract returned an empty payload.");
    }

    private async Task<string> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.TextOnly && (options.MaximumPages <= 0 || options.MaximumPagePixels <= 0
            || options.MaximumProcessMemoryBytes is <= 0))
        {
            throw new LocalOcrRuntimeException("TEXT_ONLY_RESOURCE_GUARD_FAILED");
        }

        var sidecarExecutablePath = ResolveSidecarExecutablePath();
        var useBundledExecutable = !string.IsNullOrWhiteSpace(sidecarExecutablePath);
        var pythonExecutablePath = useBundledExecutable
            ? options.PythonExecutablePath
            : await ResolvePythonExecutablePathAsync(cancellationToken);

        if (!useBundledExecutable && !IsExecutableAvailable(pythonExecutablePath))
        {
            throw new FileNotFoundException("Python executable for OCR sidecar was not found.", pythonExecutablePath);
        }

        if (!useBundledExecutable && !File.Exists(options.SidecarScriptPath))
        {
            throw new FileNotFoundException("OCR sidecar script was not found.", options.SidecarScriptPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = useBundledExecutable ? sidecarExecutablePath! : pythonExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Path.GetDirectoryName(options.SidecarScriptPath) ?? Environment.CurrentDirectory
                : options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!useBundledExecutable)
        {
            startInfo.ArgumentList.Add(options.SidecarScriptPath);
        }

        startInfo.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        if (!string.IsNullOrWhiteSpace(options.PaddleModelCacheHome))
        {
            var cacheHome = Path.GetFullPath(options.PaddleModelCacheHome);
            startInfo.Environment["FORMPOTOK_OCR_PADDLE_CACHE_HOME"] = cacheHome;
            startInfo.Environment["PADDLE_PDX_CACHE_HOME"] = cacheHome;
        }

        if (options.OfflineOnly)
        {
            if (options.TextOnly)
            {
                // Keep startup allocations bounded before native inference libraries load.
                foreach (var key in new[] { "OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS" })
                    startInfo.Environment[key] = "2";
                startInfo.Environment["PADDLE_PDX_EAGER_INIT"] = "False";
            }
            startInfo.Environment["HF_HUB_OFFLINE"] = "1";
            startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
            startInfo.Environment["HF_DATASETS_OFFLINE"] = "1";
            startInfo.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            startInfo.Environment["DO_NOT_TRACK"] = "1";
            // Defense in depth for libraries honoring proxies; this is not a network sandbox.
            foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            {
                startInfo.Environment[key] = "http://127.0.0.1:1";
            }

            startInfo.Environment["NO_PROXY"] = string.Empty;
            startInfo.Environment["no_proxy"] = string.Empty;
        }

        foreach (var argument in arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.TextOnly)
        {
            return await LocalOcrRuntimeResourceGuard.RunAsync(
                startInfo, options.MaximumProcessMemoryBytes, timeout, cancellationToken);
        }

        await using var owned = OwnedOcrRuntimeProcess.Start(startInfo);
        var process = owned.Process;

        var stdoutTask = owned.StandardOutput.ReadToEndAsync();
        var stderrTask = owned.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            await owned.StopAsync();
            await DrainAfterKillAsync(process, stdoutTask, stderrTask);
            throw new TimeoutException($"OCR sidecar did not finish within {timeout.TotalMinutes:0.#} minutes.");
        }
        catch (OperationCanceledException)
        {
            await owned.StopAsync();
            await DrainAfterKillAsync(process, stdoutTask, stderrTask);
            throw;
        }

        await owned.StopAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"OCR sidecar exited with code {process.ExitCode}.{Environment.NewLine}{stderr}".Trim());
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException("OCR sidecar completed successfully but produced no stdout payload.");
        }

        return stdout;
    }

    private async Task<string> ResolvePythonExecutablePathAsync(CancellationToken cancellationToken)
    {
        if (options.OfflineOnly || !options.AutoBootstrapPythonEnvironment)
        {
            return options.PythonExecutablePath;
        }

        return await EnsurePythonEnvironmentAsync(cancellationToken);
    }

    private async Task<T> WithTextOnlyErrorsAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (LocalOcrRuntimeException) when (options.TextOnly)
        {
            throw;
        }
        catch (OperationCanceledException) when (options.TextOnly && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (options.TextOnly)
        {
            throw new LocalOcrRuntimeException(ex switch
            {
                JsonException => "TEXT_ONLY_INVALID_RESPONSE",
                FileNotFoundException => "TEXT_ONLY_RUNTIME_MISSING",
                _ => "TEXT_ONLY_RUNTIME_FAILED",
            });
        }
    }

    private static async Task DrainAfterKillAsync(Process process, params Task<string>[] readers)
    {
        await process.WaitForExitAsync();
        // Observe both readers even if one stream fails.
        try
        {
            await Task.WhenAll(readers);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> EnsurePythonEnvironmentAsync(CancellationToken cancellationToken)
    {
        var sidecarDirectory = ResolveSidecarDirectory();
        var bootstrapLogPath = ResolveBootstrapLogPath(sidecarDirectory);
        var requirementsPath = Path.Combine(sidecarDirectory, "requirements-cpu.txt");
        if (!File.Exists(requirementsPath))
        {
            throw new FileNotFoundException("OCR sidecar requirements file was not found.", requirementsPath);
        }

        await BootstrapLock.WaitAsync(cancellationToken);
        try
        {
            await AppendBootstrapLogAsync(bootstrapLogPath, $"Bootstrap start. sidecar={sidecarDirectory}", cancellationToken);
            var venvPython = ResolveVenvPythonPath(sidecarDirectory);
            var markerPath = Path.Combine(sidecarDirectory, ".venv", ".formpotok-ocr-ready.json");
            var requirementsHash = await ComputeSha256Async(requirementsPath, cancellationToken);
            var markerIsValid = await IsBootstrapMarkerValidAsync(markerPath, requirementsHash, cancellationToken);
            if (await TryUseExistingVenvAsync(
                    venvPython,
                    markerPath,
                    requirementsHash,
                    sidecarDirectory,
                    cancellationToken))
            {
                var markerStatus = markerIsValid ? "valid" : "repaired";
                await AppendBootstrapLogAsync(
                    bootstrapLogPath,
                    $"Existing OCR venv is runnable; readiness marker {markerStatus}: {venvPython}",
                    cancellationToken);
                return venvPython;
            }

            var bootstrapPython = await ResolveBootstrapPythonPathAsync(options.PythonExecutablePath, sidecarDirectory, bootstrapLogPath, cancellationToken);
            var venvDirectory = Path.Combine(sidecarDirectory, ".venv");
            Directory.CreateDirectory(sidecarDirectory);

            await RunBootstrapProcessAsync(bootstrapPython, ["-m", "venv", venvDirectory], sidecarDirectory, TimeSpan.FromMinutes(5), cancellationToken, bootstrapLogPath);
            venvPython = ResolveVenvPythonPath(sidecarDirectory);
            if (!File.Exists(venvPython))
            {
                throw new FileNotFoundException("Python virtual environment was created, but its python executable was not found.", venvPython);
            }

            await InstallPythonRequirementsAsync(venvPython, requirementsPath, sidecarDirectory, bootstrapLogPath, cancellationToken);

            await WriteBootstrapMarkerAtomicallyAsync(markerPath, requirementsHash, cancellationToken);
            await AppendBootstrapLogAsync(bootstrapLogPath, "Bootstrap completed.", cancellationToken);
            return venvPython;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AppendBootstrapLogBestEffortAsync(bootstrapLogPath, $"Bootstrap failed: {ex}");
            throw new InvalidOperationException(
                $"Не удалось автоматически подготовить локальное распознавание. Подробности: {bootstrapLogPath}. Причина: {BuildBootstrapFailureSummary(ex)}",
                ex);
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    private string ResolveSidecarDirectory() =>
        string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? Path.GetDirectoryName(options.SidecarScriptPath) ?? Environment.CurrentDirectory
            : options.WorkingDirectory;

    private static string ResolveVenvPythonPath(string sidecarDirectory)
    {
        var primary = OperatingSystem.IsWindows()
            ? Path.Combine(sidecarDirectory, ".venv", "Scripts", "python.exe")
            : Path.Combine(sidecarDirectory, ".venv", "bin", "python3");
        if (File.Exists(primary) || OperatingSystem.IsWindows())
        {
            return primary;
        }

        return Path.Combine(sidecarDirectory, ".venv", "bin", "python");
    }

    internal static async Task<string> ResolveBootstrapPythonPathAsync(
        string configuredPython,
        string sidecarDirectory,
        string bootstrapLogPath,
        CancellationToken cancellationToken)
    {
        var canUseConfiguredPython = !OperatingSystem.IsMacOS() || IsFilePath(configuredPython);
        if (OperatingSystem.IsMacOS())
        {
            if (canUseConfiguredPython
                && IsBootstrapPythonCandidate(configuredPython, sidecarDirectory)
                && await CanRunProcessAsync(configuredPython, ["--version"], sidecarDirectory, TimeSpan.FromSeconds(10), cancellationToken))
            {
                return configuredPython;
            }

            return await EnsureManagedMacPythonAsync(sidecarDirectory, bootstrapLogPath, cancellationToken);
        }

        var candidates = BuildBootstrapPythonCandidates(
            configuredPython,
            sidecarDirectory,
            Environment.GetEnvironmentVariable("PATH"));
        foreach (var candidate in candidates)
        {
            if (await CanRunProcessAsync(candidate, ["--version"], sidecarDirectory, TimeSpan.FromSeconds(10), cancellationToken))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Python 3 was not found and automatic managed Python installation is only supported on macOS.");
    }

    internal static IReadOnlyList<string> BuildBootstrapPythonCandidates(
        string configuredPython,
        string sidecarDirectory,
        string? pathValue)
    {
        var candidates = new List<string>();
        if (IsBootstrapPythonCandidate(configuredPython, sidecarDirectory))
        {
            candidates.Add(configuredPython);
        }

        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(pathValue))
            {
                foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var directory = rawDirectory.Trim('"');
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    string candidate;
                    try
                    {
                        candidate = Path.GetFullPath(Path.Combine(directory, "python.exe"));
                    }
                    catch
                    {
                        continue;
                    }

                    if (IsBootstrapPythonCandidate(candidate, sidecarDirectory))
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }
        else
        {
            candidates.Add("python3");
            candidates.Add("python");
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsBootstrapPythonCandidate(string? value, string sidecarDirectory)
    {
        if (string.IsNullOrWhiteSpace(value)
            || IsInsideVenv(value, sidecarDirectory)
            || (OperatingSystem.IsWindows() && IsWindowsAppExecutionAlias(value)))
        {
            return false;
        }

        return !IsFilePath(value) || File.Exists(value);
    }

    internal static bool IsWindowsAppExecutionAlias(string value)
    {
        if (!IsFilePath(value))
        {
            return false;
        }

        try
        {
            var path = Path.GetFullPath(value);
            return path.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsInsideVenv(string value, string sidecarDirectory)
    {
        if (!IsFilePath(value))
        {
            return false;
        }

        var venvRoot = Path.GetFullPath(Path.Combine(sidecarDirectory, ".venv")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(value);
        return path.StartsWith(venvRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsBootstrapMarkerValidAsync(string markerPath, string requirementsHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken));
            return document.RootElement.TryGetProperty("requirementsHash", out var hash)
                && string.Equals(hash.GetString(), requirementsHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static async Task<bool> TryUseExistingVenvAsync(
        string venvPython,
        string markerPath,
        string requirementsHash,
        string sidecarDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(venvPython)
            || !await CanRunProcessAsync(
                venvPython,
                ["--version"],
                sidecarDirectory,
                TimeSpan.FromSeconds(10),
                cancellationToken))
        {
            return false;
        }

        if (!await IsBootstrapMarkerValidAsync(markerPath, requirementsHash, cancellationToken))
        {
            await WriteBootstrapMarkerAtomicallyAsync(markerPath, requirementsHash, cancellationToken);
        }

        return true;
    }

    private static async Task WriteBootstrapMarkerAtomicallyAsync(
        string markerPath,
        string requirementsHash,
        CancellationToken cancellationToken)
    {
        var markerDirectory = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException("OCR readiness marker directory could not be resolved.");
        Directory.CreateDirectory(markerDirectory);
        var temporaryPath = Path.Combine(
            markerDirectory,
            $".{Path.GetFileName(markerPath)}.{Guid.NewGuid():N}.tmp");
        var marker = new
        {
            requirementsHash,
            createdAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(marker, SerializerOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken));
    }

    private static async Task<string> EnsureManagedMacPythonAsync(
        string sidecarDirectory,
        string bootstrapLogPath,
        CancellationToken cancellationToken)
    {
        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        var targetRoot = Path.Combine(sidecarDirectory, ".managed-python", rid);
        var pythonPath = ResolveManagedMacPythonPath(targetRoot);
        if (await CanRunProcessAsync(pythonPath, ["--version"], sidecarDirectory, TimeSpan.FromSeconds(10), cancellationToken))
        {
            await AppendBootstrapLogAsync(bootstrapLogPath, $"Existing managed Python is ready: {pythonPath}", cancellationToken);
            return pythonPath;
        }

        var target = rid == "osx-arm64" ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
        var downloadRoot = Path.Combine(sidecarDirectory, ".managed-python", "_downloads");
        var extractRoot = Path.Combine(sidecarDirectory, ".managed-python", $"extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloadRoot);
        Directory.CreateDirectory(extractRoot);

        var archivePath = ResolveCachedStandalonePythonArchive(sidecarDirectory, target);
        if (archivePath is null)
        {
            var asset = await ResolveStandalonePythonAssetAsync(rid, cancellationToken);
            archivePath = Path.Combine(downloadRoot, asset.Name);
            if (!File.Exists(archivePath))
            {
                await AppendBootstrapLogAsync(bootstrapLogPath, $"Downloading managed Python: {asset.Name}", cancellationToken);
                await using var input = await BootstrapHttpClient.GetStreamAsync(asset.Url, cancellationToken);
                await using var output = File.Create(archivePath);
                await input.CopyToAsync(output, cancellationToken);
            }
        }
        else
        {
            await AppendBootstrapLogAsync(bootstrapLogPath, $"Using packaged managed Python archive: {archivePath}", cancellationToken);
        }

        await RunBootstrapProcessAsync("/usr/bin/tar", ["-xzf", archivePath, "-C", extractRoot], sidecarDirectory, TimeSpan.FromMinutes(5), cancellationToken, bootstrapLogPath);
        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetRoot)!);
        Directory.Move(extractRoot, targetRoot);
        pythonPath = ResolveManagedMacPythonPath(targetRoot);
        await RunBootstrapProcessAsync("/bin/chmod", ["+x", pythonPath], sidecarDirectory, TimeSpan.FromSeconds(30), cancellationToken, bootstrapLogPath);

        if (await CanRunProcessAsync(pythonPath, ["--version"], sidecarDirectory, TimeSpan.FromSeconds(10), cancellationToken))
        {
            return pythonPath;
        }

        throw new FileNotFoundException("Managed macOS Python was downloaded, but python3 was not found in the extracted runtime.", pythonPath);
    }

    private static string ResolveManagedMacPythonPath(string targetRoot)
    {
        var candidates = new[]
        {
            Path.Combine(targetRoot, "python", "bin", "python3"),
            Path.Combine(targetRoot, "python", "install", "bin", "python3"),
            Path.Combine(targetRoot, "install", "bin", "python3"),
            Path.Combine(targetRoot, "bin", "python3"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static async Task<(string Name, string Url)> ResolveStandalonePythonAssetAsync(string rid, CancellationToken cancellationToken)
    {
        var target = rid == "osx-arm64" ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
        using var stream = await BootstrapHttpClient.GetStreamAsync(
            "https://api.github.com/repos/astral-sh/python-build-standalone/releases/latest",
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var assets = document.RootElement.GetProperty("assets")
            .EnumerateArray()
            .Select(asset => new
            {
                Name = asset.GetProperty("name").GetString() ?? string.Empty,
                Url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty,
            })
            .Where(asset =>
                asset.Name.StartsWith("cpython-", StringComparison.OrdinalIgnoreCase)
                && asset.Name.Contains(target, StringComparison.OrdinalIgnoreCase)
                && asset.Name.EndsWith("install_only.tar.gz", StringComparison.OrdinalIgnoreCase)
                && !asset.Name.Contains("freethreaded", StringComparison.OrdinalIgnoreCase)
                && !asset.Name.Contains("debug", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.Url))
            .ToArray();

        foreach (var versionPrefix in new[] { "cpython-3.11.", "cpython-3.10.", "cpython-3.12." })
        {
            var match = assets.FirstOrDefault(asset => asset.Name.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return (match.Name, match.Url);
            }
        }

        var fallback = assets.FirstOrDefault();
        if (fallback is not null)
        {
            return (fallback.Name, fallback.Url);
        }

        throw new InvalidOperationException($"No standalone CPython install_only asset was found for {target}.");
    }

    private static async Task RunBootstrapProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? bootstrapLogPath = null)
    {
        if (!string.IsNullOrWhiteSpace(bootstrapLogPath))
        {
            await AppendBootstrapLogAsync(bootstrapLogPath, $"> {fileName} {string.Join(' ', arguments)}", cancellationToken);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        OwnedOcrRuntimeProcess owned;
        try
        {
            owned = OwnedOcrRuntimeProcess.Start(startInfo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Cannot start bootstrap command: {fileName}", ex);
        }

        await using var owner = owned;
        var process = owned.Process;
        var stdoutTask = owned.StandardOutput.ReadToEndAsync();
        var stderrTask = owned.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            await owned.StopAsync();
            await DrainAfterKillAsync(process, stdoutTask, stderrTask);
            throw new TimeoutException($"OCR bootstrap command did not finish within {timeout.TotalMinutes:0.#} minutes: {fileName}");
        }
        catch (OperationCanceledException)
        {
            await owned.StopAsync();
            await DrainAfterKillAsync(process, stdoutTask, stderrTask);
            throw;
        }

        await owned.StopAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(bootstrapLogPath))
        {
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                await AppendBootstrapLogAsync(bootstrapLogPath, stdout.Trim(), cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                await AppendBootstrapLogAsync(bootstrapLogPath, stderr.Trim(), cancellationToken);
            }
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"OCR bootstrap command failed with code {process.ExitCode}: {fileName}{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}".Trim());
        }
    }

    private string? ResolveSidecarExecutablePath()
    {
        if (string.IsNullOrWhiteSpace(options.SidecarExecutablePath))
        {
            return null;
        }

        var path = Path.GetFullPath(options.SidecarExecutablePath);
        return File.Exists(path) ? path : null;
    }

    private static bool IsExecutableAvailable(string executable) =>
        !IsFilePath(executable) || File.Exists(executable);

    private static bool IsFilePath(string value) =>
        value.Contains(Path.DirectorySeparatorChar)
        || value.Contains(Path.AltDirectorySeparatorChar)
        || Path.IsPathFullyQualified(value);

    internal static async Task<bool> CanRunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            await using var owned = OwnedOcrRuntimeProcess.Start(startInfo);
            var process = owned.Process;
            var stdoutTask = owned.StandardOutput.ReadToEndAsync();
            var stderrTask = owned.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                await owned.StopAsync();
                await DrainAfterKillAsync(process, stdoutTask, stderrTask);
                return false;
            }
            catch (OperationCanceledException)
            {
                await owned.StopAsync();
                await DrainAfterKillAsync(process, stdoutTask, stderrTask);
                throw;
            }

            await owned.StopAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateBootstrapHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FormPotok/0.1");
        return client;
    }

    private async Task InstallPythonRequirementsAsync(
        string venvPython,
        string requirementsPath,
        string sidecarDirectory,
        string bootstrapLogPath,
        CancellationToken cancellationToken)
    {
        var wheelhouseDirectory = ResolvePackagedWheelhouseDirectory(sidecarDirectory);
        if (wheelhouseDirectory is not null)
        {
            try
            {
                await AppendBootstrapLogAsync(bootstrapLogPath, $"Installing OCR requirements from packaged wheelhouse: {wheelhouseDirectory}", cancellationToken);
                await RunBootstrapProcessAsync(
                    venvPython,
                    ["-m", "pip", "install", "--no-index", "--find-links", wheelhouseDirectory, "-r", requirementsPath],
                    sidecarDirectory,
                    TimeSpan.FromMinutes(20),
                    cancellationToken,
                    bootstrapLogPath);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await AppendBootstrapLogAsync(bootstrapLogPath, $"Packaged wheelhouse install failed, falling back to internet install: {ex.Message}", cancellationToken);
            }
        }

        await RunBootstrapProcessAsync(venvPython, ["-m", "pip", "install", "--upgrade", "pip", "wheel", "setuptools"], sidecarDirectory, TimeSpan.FromMinutes(10), cancellationToken, bootstrapLogPath);
        await RunBootstrapProcessAsync(venvPython, ["-m", "pip", "install", "-r", requirementsPath], sidecarDirectory, TimeSpan.FromMinutes(Math.Max(options.BootstrapTimeout.TotalMinutes, 40)), cancellationToken, bootstrapLogPath);
    }

    private static string? ResolvePackagedWheelhouseDirectory(string sidecarDirectory)
    {
        var wheelhouseDirectory = Path.Combine(sidecarDirectory, "bootstrap-cache", "wheels");
        return Directory.Exists(wheelhouseDirectory) && Directory.EnumerateFiles(wheelhouseDirectory, "*.whl").Any()
            ? wheelhouseDirectory
            : null;
    }

    private static string? ResolveCachedStandalonePythonArchive(string sidecarDirectory, string target)
    {
        var pythonCacheDirectory = Path.Combine(sidecarDirectory, "bootstrap-cache", "python");
        if (!Directory.Exists(pythonCacheDirectory))
        {
            return null;
        }

        var assets = Directory.EnumerateFiles(pythonCacheDirectory, "*.tar.gz")
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith("cpython-", StringComparison.OrdinalIgnoreCase)
                    && name.Contains(target, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith("install_only.tar.gz", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("freethreaded", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("debug", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        foreach (var versionPrefix in new[] { "cpython-3.11.", "cpython-3.10.", "cpython-3.12." })
        {
            var match = assets.FirstOrDefault(path => Path.GetFileName(path).StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return assets.FirstOrDefault();
    }

    private static string ResolveBootstrapLogPath(string sidecarDirectory) =>
        Path.Combine(sidecarDirectory, ".bootstrap", "ocr-bootstrap.log");

    private static async Task AppendBootstrapLogAsync(
        string bootstrapLogPath,
        string message,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bootstrapLogPath)!);
        await File.AppendAllTextAsync(
            bootstrapLogPath,
            $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}",
            Encoding.UTF8,
            cancellationToken);
    }

    private static async Task AppendBootstrapLogBestEffortAsync(string bootstrapLogPath, string message)
    {
        try
        {
            await AppendBootstrapLogAsync(bootstrapLogPath, message, CancellationToken.None);
        }
        catch
        {
            // Logging must not hide the original bootstrap failure.
        }
    }

    private static string BuildBootstrapFailureSummary(Exception exception)
    {
        var cursor = exception;
        while (cursor.InnerException is not null)
        {
            cursor = cursor.InnerException;
        }

        return string.IsNullOrWhiteSpace(cursor.Message)
            ? cursor.GetType().Name
            : cursor.Message;
    }
}
