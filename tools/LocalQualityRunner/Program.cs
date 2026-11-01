using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FormPotok.PilotCore.LocalText;

internal static class Program
{
    private const int MaxText = 10_000_000;
    private const int MaxPredictions = 100_000;
    private const int MaxConfigBytes = 64 * 1024;
    private const int MaxOutputBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Configuration(int TimeoutSeconds, Candidate[] Candidates);
    private sealed record Candidate(string Name, string Executable, string[] Arguments);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--help"] or ["-h"] || args.Length == 0) { Help(); return 0; }
            if (args is ["--self-test"]) { await SelfTest(); Console.WriteLine("Self-test passed."); return 0; }
            if (args is ["--self-test-child", var file])
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.Write(await File.ReadAllTextAsync(file));
                return 0;
            }
            if (args is ["group", var groupRoot, var sourceHash, var groupName])
            {
                var grouped = await new LocalTextQualityLab(groupRoot).AssignSourceGroupAsync(sourceHash, groupName);
                Console.WriteLine($"Grouped source {grouped.SourceSha256}; split: {grouped.Split}; group digest: {grouped.SourceGroupSha256}");
                return 0;
            }
            if (args is ["freeze", var freezeRoot])
            {
                Console.WriteLine(await new LocalTextQualityLab(freezeRoot).FreezeSnapshotAsync());
                return 0;
            }
            if (args is ["audit", var auditRoot, var auditSnapshot, var auditOutput])
            {
                var rootPath = Path.GetFullPath(auditRoot);
                var outputPath = Path.GetFullPath(auditOutput);
                EnsureOutside(rootPath, outputPath);
                var report = await LocalQualityLeakageAudit.AuditSnapshotAsync(new LocalTextQualityLab(rootPath), auditSnapshot);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(report, Json));
                    File.Move(temporaryPath, outputPath, true);
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                Console.WriteLine($"Compared {report.ComparedPairs} cross-split pairs; potential leaks: {report.PotentialLeaks.Count}; truncated texts: {report.TruncatedTexts}; output: {outputPath}");
                return 0;
            }
            if (args is not ["run", _, _, _] and not ["run", _, _, _, _]) { Help(); return 2; }
            var rootArg = args[1];
            var outputArg = args[2];
            var configArg = args[3];
            var snapshotHash = args.Length == 5 ? args[4] : null;
            var root = Path.GetFullPath(rootArg);
            var output = Path.GetFullPath(outputArg);
            var configPath = Path.GetFullPath(configArg);
            EnsureOutside(root, output);
            EnsureOutside(root, configPath);
            var config = await ReadConfiguration(configPath);
            var lab = new LocalTextQualityLab(root);
            var samples = 0;
            if (snapshotHash is null)
                samples = (await lab.ListSamplesAsync()).Count(sample => sample.Split == "holdout");
            else
                await foreach (var _ in lab.StreamSnapshotCasesAsync(snapshotHash, "holdout")) samples++;
            if ((long)samples * config.Candidates.Length > MaxPredictions)
                throw new InvalidDataException("Prediction count exceeds the limit.");

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var scratch = Path.Combine(Path.GetTempPath(), "local-quality-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            var count = 0;
            try
            {
                await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartArray();
                var cases = snapshotHash is null ? lab.StreamCasesAsync("holdout") : lab.StreamSnapshotCasesAsync(snapshotHash, "holdout");
                await foreach (var entry in cases)
                {
                    var source = Path.Combine(root, "sources", entry.Sample.SourceSha256 + entry.Sample.SourceExtension);
                    foreach (var candidate in config.Candidates)
                    {
                        var prediction = await RunCandidate(candidate, entry.Sample, source, scratch, config.TimeoutSeconds);
                        JsonSerializer.Serialize(writer, prediction, Json);
                        writer.Flush();
                        if (stream.Length > MaxOutputBytes) throw new InvalidDataException("Prediction JSON exceeds the report input limit.");
                        count++;
                    }
                }
                writer.WriteEndArray();
                writer.Flush();
                if (stream.Length > MaxOutputBytes) throw new InvalidDataException("Prediction JSON exceeds the report input limit.");
            }
            catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
            finally { Directory.Delete(scratch, recursive: true); }
            File.Move(temporary, output, true);
            Console.WriteLine($"Diagnostic only; prediction count: {count}; source count: {samples}; snapshot: {snapshotHash ?? "none"}; output: {output}");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<Configuration> ReadConfiguration(string path)
    {
        if (new FileInfo(path).Length > MaxConfigBytes) throw new InvalidDataException("Configuration is too large.");
        var config = JsonSerializer.Deserialize<Configuration>(await File.ReadAllBytesAsync(path), Json)
            ?? throw new InvalidDataException("Configuration is empty.");
        if (config.TimeoutSeconds is < 1 or > 3600 || config.Candidates is not { Length: > 0 and <= 32 })
            throw new InvalidDataException("Timeout or candidate count is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in config.Candidates)
        {
            if (candidate is null || candidate.Name is null || candidate.Executable is null || candidate.Arguments is null
                || candidate.Name is "current-at-review" || candidate.Name.Length is < 1 or > 64
                || candidate.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                || !names.Add(candidate.Name) || candidate.Arguments.Length is < 1 or > 16
                || candidate.Arguments.Any(arg => arg is null || arg.Length > 4096)
                || candidate.Arguments.Count(arg => arg == "{source}") != 1
                || !Path.IsPathFullyQualified(candidate.Executable) || !File.Exists(candidate.Executable))
                throw new InvalidDataException("Candidate configuration is invalid; use unique names, an absolute local executable and one {source} argument.");
        }
        return config with { Candidates = config.Candidates.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray() };
    }

    private static async Task<LocalQualityPrediction> RunCandidate(Candidate candidate, LocalQualitySample sample,
        string source, string scratch, int timeoutSeconds)
    {
        var stopwatch = Stopwatch.StartNew();
        var working = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        var copy = Path.Combine(working, "source" + sample.SourceExtension);
        try
        {
            File.Copy(source, copy);
            await using (var input = File.OpenRead(copy))
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input)) != sample.SourceSha256)
                    throw new InvalidDataException("Source copy does not match the saved case.");
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(candidate.Executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = working,
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false, true)
            };
            foreach (var arg in candidate.Arguments)
                process.StartInfo.ArgumentList.Add(arg == "{source}" ? copy : arg);
            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            string stdout;
            try
            {
                var outTask = ReadBounded(process.StandardOutput, MaxText, timeout.Token);
                var errTask = ReadBounded(process.StandardError, 16_384, timeout.Token);
                var waitTask = process.WaitForExitAsync(timeout.Token);
                var pending = new List<Task> { waitTask, outTask, errTask };
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    await completed;
                    pending.Remove(completed);
                }
                stdout = await outTask;
                _ = await errTask;
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            await using var copyStream = File.OpenRead(copy);
            var currentHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(copyStream));
            if (currentHash != sample.SourceSha256) throw new InvalidDataException("Candidate modified its input copy.");
            if (process.ExitCode != 0) throw new InvalidDataException("Candidate exited with a nonzero code.");
            return new(candidate.Name, sample.SourceSha256, stdout, stopwatch.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception error)
        {
            var code = error switch
            {
                OperationCanceledException => "timeout",
                InvalidDataException => "invalid-output-or-source",
                IOException => "io-error",
                UnauthorizedAccessException => "access-denied",
                _ => "candidate-error",
            };
            return new(candidate.Name, sample.SourceSha256, null, stopwatch.Elapsed.TotalMilliseconds, null, code);
        }
        finally { Directory.Delete(working, recursive: true); }
    }

    private static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return result.ToString();
            if (result.Length + count > limit) throw new InvalidDataException("Candidate output exceeded the limit.");
            result.Append(buffer, 0, count);
        }
    }

    private static void EnsureOutside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative))
            throw new InvalidOperationException("Configuration and prediction output must be outside laboratory storage.");
    }

    private static async Task SelfTest()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var directory = Path.Combine(Path.GetTempPath(), "local-quality-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.png");
            await File.WriteAllTextAsync(source, "sample text");
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var candidate = new Candidate("fixture", executable, ["--self-test-child", "{source}"]);
            var prediction = await RunCandidate(candidate, new(hash, ".png", hash, hash, "holdout", 1), source, directory, 10);
            if (prediction.Text != "sample text" || prediction.Error is not null || prediction.PeakMemoryBytes is not null)
                throw new InvalidOperationException("Adapter self-test failed: " + prediction.Error);
            var roundTrip = JsonSerializer.Deserialize<List<LocalQualityPrediction>>(JsonSerializer.Serialize(new[] { prediction }, Json), Json);
            if (roundTrip?.Single().SourceSha256 != hash) throw new InvalidOperationException("Prediction JSON self-test failed.");

            var labRoot = Path.Combine(directory, "lab");
            string holdoutText;
            string holdoutHash;
            var nonce = 0;
            do
            {
                holdoutText = "sample text " + nonce++;
                holdoutHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(holdoutText)));
            } while (LocalTextQualityLab.SplitForSource(holdoutHash) != "holdout");
            var sample = new LocalQualitySample(holdoutHash, ".png", holdoutHash, holdoutHash, "holdout", 1);
            Directory.CreateDirectory(Path.Combine(labRoot, "samples"));
            Directory.CreateDirectory(Path.Combine(labRoot, "sources"));
            Directory.CreateDirectory(Path.Combine(labRoot, "references", holdoutHash));
            Directory.CreateDirectory(Path.Combine(labRoot, "baselines", holdoutHash));
            await File.WriteAllTextAsync(Path.Combine(labRoot, "samples", holdoutHash + ".json"), JsonSerializer.Serialize(sample, Json));
            await File.WriteAllTextAsync(Path.Combine(labRoot, "sources", holdoutHash + ".png"), holdoutText);
            await File.WriteAllTextAsync(Path.Combine(labRoot, "references", holdoutHash, holdoutHash + ".txt"), holdoutText);
            await File.WriteAllTextAsync(Path.Combine(labRoot, "baselines", holdoutHash, holdoutHash + ".txt"), holdoutText);
            var configPath = Path.Combine(directory, "candidates.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new Configuration(10, [candidate]), Json));
            var outputPath = Path.Combine(directory, "predictions.json");
            if (await Main(["run", labRoot, outputPath, configPath]) != 0)
                throw new InvalidOperationException("End-to-end runner self-test failed.");
            var produced = JsonSerializer.Deserialize<List<LocalQualityPrediction>>(await File.ReadAllTextAsync(outputPath), Json)
                ?? throw new InvalidOperationException("Prediction output was empty.");
            var report = await LocalQualityEvaluator.EvaluateAsync(new LocalTextQualityLab(labRoot), produced);
            if (report.SourceDocuments != 1 || report.Candidates.Single(c => c.Candidate == "fixture").Completed != 1)
                throw new InvalidOperationException("Quality evaluator rejected the prediction.");
            var groupIndex = 0;
            string group;
            do group = "fixture-group-" + groupIndex++;
            while (LocalTextQualityLab.SplitForSource(LocalTextQualityLab.HashSourceGroup(group)) != "holdout");
            if (await Main(["group", labRoot, holdoutHash, group]) != 0)
                throw new InvalidOperationException("Group assignment self-test failed.");
            var snapshot = await new LocalTextQualityLab(labRoot).FreezeSnapshotAsync();
            if (await Main(["run", labRoot, outputPath, configPath, snapshot]) != 0)
                throw new InvalidOperationException("Frozen runner self-test failed.");
            produced = JsonSerializer.Deserialize<List<LocalQualityPrediction>>(await File.ReadAllTextAsync(outputPath), Json)
                ?? throw new InvalidOperationException("Frozen prediction output was empty.");
            report = await LocalQualityEvaluator.EvaluateSnapshotAsync(new LocalTextQualityLab(labRoot), snapshot, produced);
            if (report.SnapshotSha256 != snapshot || report.SourceGroups != 1
                || report.Candidates.Single(c => c.Candidate == "fixture").Completed != 1)
                throw new InvalidOperationException("Frozen quality evaluator rejected the prediction.");
            var auditPath = Path.Combine(directory, "audit.json");
            if (await Main(["audit", labRoot, snapshot, auditPath]) != 0)
                throw new InvalidOperationException("Cross-split audit self-test failed.");
            var audit = JsonSerializer.Deserialize<LocalQualityLeakageReport>(await File.ReadAllTextAsync(auditPath), Json);
            if (audit?.SnapshotSha256 != snapshot || audit.PotentialLeaks.Count != 0)
                throw new InvalidOperationException("Cross-split audit report is invalid.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Help() => Console.WriteLine("""
        LocalQualityRunner run <lab-root> <predictions.json> <candidates.json>
        LocalQualityRunner run <lab-root> <predictions.json> <candidates.json> <snapshot-sha256>
        LocalQualityRunner group <lab-root> <source-sha256> <source-group-name>
        LocalQualityRunner freeze <lab-root>
        LocalQualityRunner audit <lab-root> <snapshot-sha256> <report.json>
        LocalQualityRunner --self-test

        candidates.json example:
        {"timeoutSeconds":300,"candidates":[{"name":"local-v1","executable":"C:\\path\\to\\ocr.exe","arguments":["{source}"]}]}

        Assign the same group name to rescans and variants of one source or template. The group name is stored
        only as a digest. Freeze pins source, reference, baseline, group and split. Pass its digest to run and
        to LocalTextSmoke --quality-report for a reproducible comparison. Group assignment is manual; it does
        not prove that all near-duplicates have been identified. Output remains diagnostic only.
        Nonzero exit, timeout, oversized output and input mutation become per-source errors. Output is compatible
        with LocalTextSmoke --quality-report. Executables must be trusted and provisioned offline; this tool does
        not grant or enforce OS-level network isolation. Peak memory is null because child trees are not measured.
        The audit is a heuristic for cross-split template overlap. A clean audit does not prove independence.
        """);
}
