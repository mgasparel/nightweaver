using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nightweaver.Execution;

/// <summary>Creates a process with its job assigned as part of CreateProcessW, before user code can run.</summary>
internal static class WindowsProcessLauncher
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private const int ProcThreadAttributeJobList = 0x0002000D;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint Infinite = 0xFFFFFFFF;

    internal static WindowsAgentProcess Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent CLI execution requires Windows.");

        SafeKernelHandle? job = null;
        SafeFileHandle? childStdinRead = null;
        SafeFileHandle? parentStdinWrite = null;
        SafeFileHandle? parentStdoutRead = null;
        SafeFileHandle? childStdoutWrite = null;
        SafeFileHandle? parentStderrRead = null;
        SafeFileHandle? childStderrWrite = null;
        SafeKernelHandle? process = null;
        SafeKernelHandle? thread = null;
        FileStream? stdinStream = null;
        FileStream? stdoutStream = null;
        FileStream? stderrStream = null;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw LastError("CreateJobObjectW");
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                throw LastError("SetInformationJobObject(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)");

            CreatePipePair(out childStdinRead, out parentStdinWrite);
            CreatePipePair(out parentStdoutRead, out childStdoutWrite);
            CreatePipePair(out parentStderrRead, out childStderrWrite);
            ClearInherit(parentStdinWrite);
            ClearInherit(parentStdoutRead);
            ClearInherit(parentStderrRead);

            using var attributes = new ProcessAttributeList(2);
            attributes.AddHandleList([childStdinRead, childStdoutWrite, childStderrWrite]);
            attributes.AddJobList(job);

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = childStdinRead.DangerousGetHandle(),
                    StandardOutput = childStdoutWrite.DangerousGetHandle(),
                    StandardError = childStderrWrite.DangerousGetHandle()
                },
                AttributeList = attributes.Pointer
            };
            using var commandLine = new SafeHGlobalBuffer(SerializeCommandLine(executable, arguments));
            var environmentBlock = BuildEnvironmentBlock(environment);
            using var environmentPointer = new SafeHGlobalBuffer(environmentBlock);
            if (!CreateProcessW(executable, commandLine.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                    ExtendedStartupInfoPresent | CreateNoWindow | CreateUnicodeEnvironment,
                    environmentPointer.DangerousGetHandle(), workingDirectory, ref startup, out var processInformation))
                throw LastError($"CreateProcessW('{executable}')");

            // Transfer both returned kernel handles. The primary thread is closed immediately; the process handle
            // remains available to observe and preserve the root Agent CLI exit code.
            process = new SafeKernelHandle(processInformation.Process, ownsHandle: true);
            thread = new SafeKernelHandle(processInformation.Thread, ownsHandle: true);
            thread.Dispose();
            thread = null;

            // The child ends were inherited only through HANDLE_LIST; close the parent's copies so EOF is reliable.
            childStdinRead.Dispose(); childStdinRead = null;
            childStdoutWrite.Dispose(); childStdoutWrite = null;
            childStderrWrite.Dispose(); childStderrWrite = null;

            stdinStream = new FileStream(parentStdinWrite, FileAccess.Write, 64 * 1024, isAsync: false);
            parentStdinWrite = null;
            stdoutStream = new FileStream(parentStdoutRead, FileAccess.Read, 64 * 1024, isAsync: false);
            parentStdoutRead = null;
            stderrStream = new FileStream(parentStderrRead, FileAccess.Read, 64 * 1024, isAsync: false);
            parentStderrRead = null;
            var result = new WindowsAgentProcess(job, process, stdinStream, stdoutStream, stderrStream);
            job = null;
            process = null;
            stdinStream = null;
            stdoutStream = null;
            stderrStream = null;
            return result;
        }
        catch
        {
            // If CreateProcess succeeded but a later stream wrapper failed, closing the sole job handle still
            // applies KILL_ON_JOB_CLOSE to the already-contained process tree.
            process?.Dispose();
            thread?.Dispose();
            job?.Dispose();
            stdinStream?.Dispose(); stdoutStream?.Dispose(); stderrStream?.Dispose();
            childStdinRead?.Dispose(); parentStdinWrite?.Dispose();
            parentStdoutRead?.Dispose(); childStdoutWrite?.Dispose();
            parentStderrRead?.Dispose(); childStderrWrite?.Dispose();
            throw;
        }
    }

    internal static string ResolveExecutable(string name, IReadOnlyDictionary<string, string> environment)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new FileNotFoundException("Prepared Agent CLI executable name is empty or invalid.");

        var extensions = new[] { string.Empty, ".exe", ".com" };
        if (Path.IsPathFullyQualified(name))
        {
            foreach (var extension in extensions)
            {
                var candidate = name + extension;
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            if (File.Exists(name + ".cmd") || File.Exists(name + ".bat"))
                throw new NotSupportedException($"Agent CLI '{name}' is installed only as a .cmd/.bat shim; shell shims are not supported.");
            throw new FileNotFoundException($"Agent CLI executable '{name}' was not found.");
        }

        var path = environment.FirstOrDefault(pair => string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
        if (path is not null)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory.Trim('"'), name + extension);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }

            if (path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(directory => File.Exists(Path.Combine(directory.Trim('"'), name + ".cmd"))
                    || File.Exists(Path.Combine(directory.Trim('"'), name + ".bat"))))
                throw new NotSupportedException($"Agent CLI '{name}' is installed only as a .cmd/.bat shim on PATH; shell shims are not supported.");
        }

        throw new FileNotFoundException($"Agent CLI executable '{name}' was not found on PATH.");
    }

    internal static string SerializeCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteWindowsArgument));

    internal static string QuoteWindowsArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0')) throw new ArgumentException("Windows command-line arguments cannot contain NUL.", nameof(argument));
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment) variables[pair.Key] = pair.Value;

        // Each entry has its own NUL; the extra NUL ends the complete UTF-16 environment block.
        return variables.Count == 0 ? "\0\0" : string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
    }

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write)
    {
        var security = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out read, out write, ref security, 0)) throw LastError("CreatePipe");
    }

    private static void ClearInherit(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0)) throw LastError("SetHandleInformation(parent pipe)");
    }

    private static Win32Exception LastError(string operation) => new(Marshal.GetLastWin32Error(), operation);

    private sealed class ProcessAttributeList : IDisposable
    {
        private IntPtr pointer;
        private IntPtr handleArray;
        private IntPtr jobArray;

        internal ProcessAttributeList(int count)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref size);
            if (size == 0) throw LastError("InitializeProcThreadAttributeList(size)");
            pointer = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(pointer, count, 0, ref size))
            {
                var error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(pointer);
                pointer = IntPtr.Zero;
                throw new Win32Exception(error, "InitializeProcThreadAttributeList");
            }
        }

        internal IntPtr Pointer => pointer;

        internal void AddHandleList(IReadOnlyList<SafeFileHandle> handles)
        {
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * handles.Count);
            for (var index = 0; index < handles.Count; index++)
                Marshal.WriteIntPtr(handleArray, index * IntPtr.Size, handles[index].DangerousGetHandle());
            if (!UpdateProcThreadAttribute(pointer, 0, (IntPtr)ProcThreadAttributeHandleList, handleArray,
                    (IntPtr)(IntPtr.Size * handles.Count), IntPtr.Zero, IntPtr.Zero))
                throw LastError("UpdateProcThreadAttribute(HANDLE_LIST)");
        }

        internal void AddJobList(SafeKernelHandle job)
        {
            jobArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobArray, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(pointer, 0, (IntPtr)ProcThreadAttributeJobList, jobArray,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw LastError("UpdateProcThreadAttribute(JOB_LIST)");
        }

        public void Dispose()
        {
            if (pointer != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(pointer);
                Marshal.FreeHGlobal(pointer);
                pointer = IntPtr.Zero;
            }
            if (handleArray != IntPtr.Zero) { Marshal.FreeHGlobal(handleArray); handleArray = IntPtr.Zero; }
            if (jobArray != IntPtr.Zero) { Marshal.FreeHGlobal(jobArray); jobArray = IntPtr.Zero; }
        }
    }

    private sealed class SafeHGlobalBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeHGlobalBuffer(string value) : base(ownsHandle: true) => SetHandle(Marshal.StringToHGlobalUni(value));
        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal uint Cb;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2;
        internal IntPtr Reserved2Pointer;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeKernelHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeKernelHandle job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeKernelHandle job, int informationClass,
        out JobObjectBasicAccountingInformation information, uint informationLength, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeKernelHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount,
        uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint exitCode);

    internal sealed class WindowsAgentProcess : IDisposable
    {
        private SafeKernelHandle? job;
        private SafeKernelHandle? process;
        private bool disposed;

        internal WindowsAgentProcess(SafeKernelHandle job, SafeKernelHandle process,
            FileStream stdin, FileStream stdout, FileStream stderr)
        {
            this.job = job;
            this.process = process;
            StandardInput = stdin;
            StandardOutput = stdout;
            StandardError = stderr;
        }

        internal FileStream StandardInput { get; }
        internal FileStream StandardOutput { get; }
        internal FileStream StandardError { get; }

        internal Task<uint> WaitForRootExitAsync() => Task.Run(() =>
        {
            var handle = process ?? throw new ObjectDisposedException(nameof(WindowsAgentProcess));
            var wait = WaitForSingleObject(handle, Infinite);
            if (wait == WaitFailed) throw LastError("WaitForSingleObject(process)");
            if (wait != WaitObject0) throw new IOException($"Unexpected process wait result 0x{wait:X8}.");
            if (!GetExitCodeProcess(handle, out var exitCode)) throw LastError("GetExitCodeProcess");
            return exitCode;
        });

        internal async Task TerminateAndConfirmEmptyAsync(TimeSpan cleanupBudget)
        {
            var handle = job ?? throw new ObjectDisposedException(nameof(WindowsAgentProcess));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (!TerminateJobObject(handle, 1))
            {
                var error = Marshal.GetLastWin32Error();
                if (!TryGetActiveProcesses(handle, out var active, out var queryError) || active != 0)
                    throw new AggregateException("TerminateJobObject failed and the job was not proven empty.",
                        new Win32Exception(error, "TerminateJobObject"), queryError is null ? new IOException("Job still has active processes.") : new Win32Exception(queryError.Value, "QueryInformationJobObject"));
            }

            while (true)
            {
                if (!TryGetActiveProcesses(handle, out var active, out var queryError))
                    throw new Win32Exception(queryError!.Value, "QueryInformationJobObject(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)");
                if (active == 0) return;
                if (timer.Elapsed >= cleanupBudget)
                    throw new TimeoutException($"Job Object still reports {active} active process(es) after {cleanupBudget}.");
                await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
            }
        }

        private static bool TryGetActiveProcesses(SafeKernelHandle handle, out ulong active, out int? error)
        {
            if (!QueryInformationJobObject(handle, JobObjectBasicAccountingInformationClass,
                    out var info, (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero))
            {
                active = 0;
                error = Marshal.GetLastWin32Error();
                return false;
            }
            active = info.ActiveProcesses;
            error = null;
            return true;
        }

        internal bool CloseJob()
        {
            var current = Interlocked.Exchange(ref job, null);
            current?.Dispose();
            return current is not null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Closing the sole job handle is the final kill-on-close backstop, including partial cleanup failures.
            CloseJob();
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            Interlocked.Exchange(ref process, null)?.Dispose();
        }
    }

    internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeKernelHandle() : base(ownsHandle: true) { }
        internal SafeKernelHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
