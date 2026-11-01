using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FormPotok.Infrastructure.OcrRuntime;

internal sealed class WindowsTextProcessResourceGuard : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle completionPort;

    private WindowsTextProcessResourceGuard(
        SafeFileHandle job, SafeFileHandle completionPort, Process process, StreamReader stdout, StreamReader stderr)
    {
        this.job = job;
        this.completionPort = completionPort;
        Process = process;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    internal static WindowsTextProcessResourceGuard Start(ProcessStartInfo startInfo, long? memoryLimit)
    {
        var job = CreateJobObjectW(nint.Zero, null);
        var port = CreateIoCompletionPort(new nint(-1), nint.Zero, nuint.Zero, 1);
        Process? process = null;
        StreamReader? stdout = null;
        StreamReader? stderr = null;
        try
        {
            Check(!job.IsInvalid && !port.IsInvalid);
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { LimitFlags = 0x2000 }, // KILL_ON_JOB_CLOSE, no breakaway.
            };
            if (memoryLimit is { } bytes)
            {
                // Both limits are committed virtual memory, not resident working set/RSS.
                limits.Basic.LimitFlags |= 0x100 | 0x200;
                limits.ProcessMemoryLimit = checked((nuint)bytes);
                limits.JobMemoryLimit = checked((nuint)bytes);
            }

            SetJobInformation(job, 9, limits);
            SetJobInformation(job, 7, new CompletionAssociation { Key = new nint(1), Port = port.DangerousGetHandle() });

            using var output = new ChildPipe();
            using var error = new ChildPipe();
            using var input = new ChildPipe();
            // Only these three handles are inherited; the job/port stay parent-owned.
            using var attributes = new StartupAttributes(
                [input.Read.DangerousGetHandle(), output.Write.DangerousGetHandle(), error.Write.DangerousGetHandle()],
                job.DangerousGetHandle());
            var startup = new StartupInfoEx
            {
                Info = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100, // STARTF_USESTDHANDLES
                    Input = input.Read.DangerousGetHandle(),
                    Output = output.Write.DangerousGetHandle(),
                    Error = error.Write.DangerousGetHandle(),
                },
                Attributes = attributes.Pointer,
            };
            // CreateProcess inherits a job atomically via JOB_LIST. Suspension also protects
            // managed handle/reader setup; there is no start-then-assign execution window.
            var commandLine = new StringBuilder(string.Join(' ',
                new[] { startInfo.FileName }.Concat(startInfo.ArgumentList).Select(QuoteArgument)));
            var environment = Marshal.StringToHGlobalUni(string.Join('\0', startInfo.Environment
                .Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
            ProcessInformation native;
            try
            {
                Check(CreateProcessW(null, commandLine, nint.Zero, nint.Zero, true,
                    0x08000000 | 0x00080000 | 0x00000400 | 0x00000004,
                    environment, startInfo.WorkingDirectory, ref startup, out native));
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }

            using var processHandle = new SafeFileHandle(native.Process, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(native.Thread, ownsHandle: true);
            try
            {
                process = Process.GetProcessById(native.ProcessId);
                _ = process.Handle; // Retain an identity-bound handle before the worker can exit.
                stdout = output.OpenReader();
                stderr = error.OpenReader();
                output.Write.Dispose();
                error.Write.Dispose();
                input.Write.Dispose(); // Worker stdin is EOF.
                Check(ResumeThread(threadHandle) != uint.MaxValue);
                return new WindowsTextProcessResourceGuard(job, port, process, stdout, stderr);
            }
            catch
            {
                // Setup failed while suspended: no worker code/descendant has run.
                Check(TerminateJobObject(job, 1));
                WaitForSingleObject(processHandle, uint.MaxValue);
                throw;
            }
        }
        catch
        {
            job.Dispose();
            port.Dispose();
            stdout?.Dispose();
            stderr?.Dispose();
            process?.Dispose();
            throw;
        }
    }

    internal bool ReadMemoryLimitViolation()
    {
        // Hard caps deny allocations regardless of notification delivery. Completion-port
        // memory messages are advisory; missing notifications cannot disable the hard cap.
        for (var i = 0; i < 256; i++)
        {
            if (!GetQueuedCompletionStatus(completionPort, out var message, out _, out _, 0))
            {
                if (Marshal.GetLastWin32Error() == 258) // WAIT_TIMEOUT
                {
                    return false;
                }

                throw new Win32Exception();
            }

            if (message is 9 or 10) // PROCESS_MEMORY_LIMIT / JOB_MEMORY_LIMIT
            {
                return true;
            }
        }

        return false;
    }

    internal async Task TerminateAndWaitAsync()
    {
        if (!TerminateJobObject(job, 1))
        {
            // Closing the last job handle is the fail-closed kill-on-close fallback.
            job.Dispose();
            await Process.WaitForExitAsync();
            throw new LocalOcrRuntimeException("TEXT_ONLY_RESOURCE_GUARD_FAILED");
        }

        while (true)
        {
            Check(QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<BasicAccounting>(), nint.Zero));
            if (accounting.ActiveProcesses == 0)
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        job.Dispose();
        completionPort.Dispose();
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }

        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void SetJobInformation<T>(SafeFileHandle job, int type, T information) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            Check(SetInformationJobObject(job, type, pointer, size));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private sealed class ChildPipe : IDisposable
    {
        private bool readerTransferred;
        internal SafeFileHandle Read { get; }
        internal SafeFileHandle Write { get; }

        internal ChildPipe()
        {
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            Check(CreatePipe(out var read, out var write, ref security, 0));
            Read = read;
            Write = write;
        }

        internal StreamReader OpenReader()
        {
            var reader = new StreamReader(new FileStream(Read, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8);
            readerTransferred = true;
            return reader;
        }

        public void Dispose()
        {
            if (!readerTransferred)
            {
                Read.Dispose();
            }

            Write.Dispose();
        }
    }

    private sealed class StartupAttributes : IDisposable
    {
        internal nint Pointer { get; private set; }
        private nint values;
        private bool initialized;

        internal StartupAttributes(nint[] handles, nint job)
        {
            try
            {
                nuint size = 0;
                InitializeProcThreadAttributeList(nint.Zero, 2, 0, ref size);
                Pointer = Marshal.AllocHGlobal(checked((nint)size));
                Check(InitializeProcThreadAttributeList(Pointer, 2, 0, ref size));
                initialized = true;
                values = Marshal.AllocHGlobal(4 * nint.Size);
                for (var i = 0; i < handles.Length; i++)
                {
                    Marshal.WriteIntPtr(values, i * nint.Size, handles[i]);
                }

                Marshal.WriteIntPtr(values, 3 * nint.Size, job);
                Check(UpdateProcThreadAttribute(Pointer, 0, 0x20002, values, (nuint)(3 * nint.Size), nint.Zero, nint.Zero));
                Check(UpdateProcThreadAttribute(Pointer, 0, 0x2000D, values + 3 * nint.Size, (nuint)nint.Size, nint.Zero, nint.Zero));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(Pointer);
            }

            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(values);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Size; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CompletionAssociation { public nint Key, Port; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, nint information, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out BasicAccounting information, int size, nint returnedSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateIoCompletionPort(nint file, nint existingPort, nuint key, uint threads);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out nuint key, out nint overlapped, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint attributes, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        nint environment, string directory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
}
