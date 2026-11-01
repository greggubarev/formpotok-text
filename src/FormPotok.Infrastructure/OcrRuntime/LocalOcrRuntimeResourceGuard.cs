using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FormPotok.Infrastructure.OcrRuntime;

public sealed class LocalOcrRuntimeException : Exception
{
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "TEXT_ONLY_TIMEOUT", "TEXT_ONLY_MEMORY_LIMIT", "TEXT_ONLY_OUTPUT_LIMIT",
        "TEXT_ONLY_RESOURCE_GUARD_FAILED", "TEXT_ONLY_RUNTIME_FAILED", "TEXT_ONLY_INVALID_RESPONSE",
        "TEXT_ONLY_RUNTIME_MISSING",
        "OFFLINE_MODELS_MISSING", "TEXT_ONLY_SOURCE_LIMIT", "TEXT_ONLY_PAGE_LIMIT",
        "TEXT_ONLY_PIXEL_LIMIT", "TEXT_ONLY_EMPTY_DOCUMENT", "TEXT_ONLY_SOURCE_INVALID",
        "TEXT_ONLY_SOURCE_MISSING", "TEXT_ONLY_UNSUPPORTED_SOURCE",
    };

    public LocalOcrRuntimeException(string errorCode)
        : base(Normalize(errorCode)) => ErrorCode = Normalize(errorCode);

    public string ErrorCode { get; }

    private static string Normalize(string? code) => code is not null && KnownCodes.Contains(code)
        ? code : "TEXT_ONLY_RUNTIME_FAILED";

    internal static string? ReadWorkerCode(string payload)
    {
        var code = ReadJsonWorkerCode(payload);
        if (code is not null)
        {
            return code;
        }

        string? lastCode = null;
        using var lines = new StringReader(payload);
        while (lines.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('{') && ReadJsonWorkerCode(trimmed) is { } recordCode)
            {
                lastCode = recordCode;
                continue;
            }

            // Keep legacy code/traceback compatibility without retaining their messages.
            foreach (var knownCode in KnownCodes)
            {
                if (trimmed.Equals(knownCode, StringComparison.Ordinal)
                    || trimmed.StartsWith(knownCode + ":", StringComparison.Ordinal)
                    || trimmed.StartsWith("RuntimeError: " + knownCode + ":", StringComparison.Ordinal))
                {
                    lastCode = knownCode;
                    break;
                }
            }
        }

        return lastCode;
    }

    private static string? ReadJsonWorkerCode(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("errorCode", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                return Normalize(code.GetString());
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

internal sealed class OwnedOcrRuntimeProcess : IAsyncDisposable
{
    private readonly WindowsTextProcessResourceGuard? windows;

    private OwnedOcrRuntimeProcess(ProcessStartInfo startInfo)
    {
        if (OperatingSystem.IsWindows())
        {
            windows = WindowsTextProcessResourceGuard.Start(startInfo, null);
            Process = windows.Process;
            StandardOutput = windows.StandardOutput;
            StandardError = windows.StandardError;
        }
        else
        {
            Process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start OCR process.");
            StandardOutput = Process.StandardOutput;
            StandardError = Process.StandardError;
        }
    }

    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }
    internal static OwnedOcrRuntimeProcess Start(ProcessStartInfo startInfo) => new(startInfo);

    internal async Task StopAsync()
    {
        if (windows is not null)
        {
            await windows.TerminateAndWaitAsync();
        }
        else if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
        }

        await Process.WaitForExitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            windows?.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            Process.Dispose();
        }
    }
}

internal static class LocalOcrRuntimeResourceGuard
{
    internal const int MaximumStdoutCharacters = 16 * 1024 * 1024;
    private const int MaximumStderrCharacters = 64 * 1024;

    internal static async Task<string> RunAsync(
        ProcessStartInfo startInfo, long? memoryLimit, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var stopMonitor = new CancellationTokenSource();
        WindowsTextProcessResourceGuard? windows = null;
        Process? process = null;
        StreamReader? stdout = null;
        StreamReader? stderr = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        Task? monitorTask = null;
        Task? exitTask = null;
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    windows = WindowsTextProcessResourceGuard.Start(startInfo, memoryLimit);
                    process = windows.Process;
                    stdout = windows.StandardOutput;
                    stderr = windows.StandardError;
                    monitorTask = MonitorAsync(windows, failure, stopMonitor.Token);
                }
                else
                {
                    // A requested hard cap must not silently become a sampling-only RSS check.
                    if (memoryLimit.HasValue)
                    {
                        throw new PlatformNotSupportedException();
                    }

                    process = Process.Start(startInfo) ?? throw new InvalidOperationException();
                    stdout = process.StandardOutput;
                    stderr = process.StandardError;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 193)
            {
                throw new LocalOcrRuntimeException("TEXT_ONLY_RUNTIME_MISSING");
            }
            catch (Exception)
            {
                throw new LocalOcrRuntimeException("TEXT_ONLY_RESOURCE_GUARD_FAILED");
            }

            stdoutTask = CaptureAsync(stdout, MaximumStdoutCharacters, () => failure.TrySetResult("TEXT_ONLY_OUTPUT_LIMIT"));
            stderrTask = CaptureStderrTailAsync(stderr);
            exitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(exitTask, failure.Task).WaitAsync(deadline.Token);
            if (completed == failure.Task)
            {
                throw new LocalOcrRuntimeException(await failure.Task);
            }

            await exitTask;
            // A successful root must not leave children alive or inherited pipes open.
            if (windows is not null)
            {
                await windows.TerminateAndWaitAsync();
                if (windows.ReadMemoryLimitViolation())
                {
                    failure.TrySetResult("TEXT_ONLY_MEMORY_LIMIT");
                }
            }

            var captured = await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(deadline.Token);
            if (failure.Task.IsCompleted)
            {
                throw new LocalOcrRuntimeException(await failure.Task);
            }

            var errorCode = LocalOcrRuntimeException.ReadWorkerCode(captured[0]);
            if (process.ExitCode != 0)
            {
                throw new LocalOcrRuntimeException(errorCode
                    ?? LocalOcrRuntimeException.ReadWorkerCode(captured[1]) ?? "TEXT_ONLY_RUNTIME_FAILED");
            }

            if (errorCode is not null)
            {
                throw new LocalOcrRuntimeException(errorCode);
            }

            if (string.IsNullOrWhiteSpace(captured[0]))
            {
                throw new LocalOcrRuntimeException("TEXT_ONLY_INVALID_RESPONSE");
            }

            return captured[0];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalOcrRuntimeException("TEXT_ONLY_TIMEOUT");
        }
        finally
        {
            stopMonitor.Cancel();
            if (monitorTask is not null)
            {
                await ObserveAsync(monitorTask);
            }

            var cleanupFailed = false;
            if (process is not null)
            {
                try
                {
                    if (windows is not null)
                    {
                        await windows.TerminateAndWaitAsync();
                    }
                    else if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    cleanupFailed = true;
                    windows?.Dispose(); // Kill-on-close still applies if termination/query failed.
                }

                await process.WaitForExitAsync();
            }

            if (exitTask is not null)
            {
                await ObserveAsync(exitTask);
            }

            // Readers are not tied to caller cancellation: kill first, drain and observe both.
            if (stdoutTask is not null && stderrTask is not null)
            {
                await ObserveAsync(Task.WhenAll(stdoutTask, stderrTask));
            }

            stdout?.Dispose();
            stderr?.Dispose();
            process?.Dispose();
            windows?.Dispose();
            if (cleanupFailed)
            {
                throw new LocalOcrRuntimeException("TEXT_ONLY_RESOURCE_GUARD_FAILED");
            }
        }
    }

    private static async Task MonitorAsync(
        WindowsTextProcessResourceGuard guard, TaskCompletionSource<string> failure, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (guard.ReadMemoryLimitViolation())
                {
                    failure.TrySetResult("TEXT_ONLY_MEMORY_LIMIT");
                    return;
                }

                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            failure.TrySetResult("TEXT_ONLY_RESOURCE_GUARD_FAILED");
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, int limit, Action? onOverflow)
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];
        var overflowed = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            var available = limit - captured.Length;
            captured.Append(buffer, 0, Math.Min(available, read));
            if (read > available && !overflowed)
            {
                overflowed = true;
                onOverflow?.Invoke();
            }
        }

        return captured.ToString();
    }

    internal static async Task<string> CaptureStderrTailAsync(StreamReader reader)
    {
        // Drain every chunk while retaining only a fixed-size diagnostic tail.
        var tail = new char[MaximumStderrCharacters];
        var buffer = new char[4096];
        var next = 0;
        var count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            var first = Math.Min(read, tail.Length - next);
            buffer.AsSpan(0, first).CopyTo(tail.AsSpan(next));
            buffer.AsSpan(first, read - first).CopyTo(tail);
            next = (next + read) % tail.Length;
            count = Math.Min(count + read, tail.Length);
        }

        return string.Create(count, (Tail: tail, Start: count == tail.Length ? next : 0), static (destination, state) =>
        {
            var first = Math.Min(destination.Length, state.Tail.Length - state.Start);
            state.Tail.AsSpan(state.Start, first).CopyTo(destination);
            state.Tail.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
        });
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Cleanup observes every task without replacing the bounded caller error.
        }
    }
}
