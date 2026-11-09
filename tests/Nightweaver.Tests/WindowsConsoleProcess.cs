using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Nightweaver.Tests;

/// <summary>Starts a hidden apphost in its own real Windows console and process group for control-event tests.</summary>
internal sealed class WindowsConsoleProcess : IDisposable
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const ushort ShowWindowHidden = 0;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint DuplicateSameAccess = 0x00000002;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const int StdInputHandle = -10;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly Process process;
    private readonly IntPtr nativeProcessHandle;
    private bool disposed;

    private WindowsConsoleProcess(Process process, IntPtr nativeProcessHandle, string? outputLogPath)
    {
        this.process = process;
        this.nativeProcessHandle = nativeProcessHandle;
        OutputLogPath = outputLogPath;
    }

    internal int Id => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal string? OutputLogPath { get; }

    internal void KillEntireProcessTree()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }

    internal static WindowsConsoleProcess Start(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?> environment, string? outputLogPath = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A Windows console process is required for this integration test.");

        executable = ResolveExecutablePath(executable);
        var commandLine = new StringBuilder(QuoteArgument(executable));
        foreach (var argument in arguments)
            commandLine.Append(' ').Append(QuoteArgument(argument));

        var environmentBlock = BuildEnvironmentBlock(environment);
        IntPtr environmentPointer = IntPtr.Zero;
        IntPtr commandLinePointer = IntPtr.Zero;
        var outputHandle = IntPtr.Zero;
        var inheritedInputHandle = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var processHandle = IntPtr.Zero;
        var threadHandle = IntPtr.Zero;
        var attributeListInitialized = false;
        Process? managedProcess = null;
        WindowsConsoleProcess? result = null;
        Exception? startFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
            commandLinePointer = Marshal.StringToHGlobalUni(commandLine.ToString());

            ProcessInformation information;
            if (outputLogPath is null)
            {
                var startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfo>(),
                    Flags = StartfUseShowWindow,
                    ShowWindow = ShowWindowHidden
                };
                if (!CreateProcessW(executable, commandLinePointer, IntPtr.Zero, IntPtr.Zero,
                        inheritHandles: false,
                        CreateNewConsole | CreateNewProcessGroup | CreateUnicodeEnvironment,
                        environmentPointer, workingDirectory, ref startup, out information))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, $"Could not start isolated console process '{executable}'.");
                }
            }
            else
            {
                var security = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    InheritHandle = true
                };
                outputHandle = CreateFileW(outputLogPath, GenericWrite,
                    FileShareRead | FileShareWrite | FileShareDelete, ref security, CreateAlways,
                    FileAttributeNormal, IntPtr.Zero);
                if (outputHandle == InvalidHandleValue)
                {
                    var error = Marshal.GetLastWin32Error();
                    outputHandle = IntPtr.Zero;
                    throw new Win32Exception(error, $"Could not create console process log '{outputLogPath}'.");
                }

                var standardInput = GetStdHandle(StdInputHandle);
                var childStandardInput = standardInput;
                var hasStandardInputHandle = standardInput != IntPtr.Zero && standardInput != InvalidHandleValue;
                if (hasStandardInputHandle)
                {
                    if (!DuplicateHandle(GetCurrentProcess(), standardInput, GetCurrentProcess(),
                            out inheritedInputHandle, 0, inheritHandle: true, DuplicateSameAccess))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new Win32Exception(error, "Could not duplicate the console process standard input handle.");
                    }
                    childStandardInput = inheritedInputHandle;
                }

                var handleCount = hasStandardInputHandle ? 2 : 1;
                nuint attributeListSize = 0;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                if (attributeListSize == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, "Could not size the console process attribute list.");
                }
                attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, "Could not initialize the console process attribute list.");
                }
                attributeListInitialized = true;

                handleList = Marshal.AllocHGlobal(checked(IntPtr.Size * handleCount));
                Marshal.WriteIntPtr(handleList, 0, outputHandle);
                if (hasStandardInputHandle)
                    Marshal.WriteIntPtr(handleList, IntPtr.Size, inheritedInputHandle);
                if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributeHandleList,
                        handleList, (IntPtr)(IntPtr.Size * handleCount), IntPtr.Zero, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, "Could not restrict console process handle inheritance.");
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseShowWindow | StartfUseStdHandles,
                        ShowWindow = ShowWindowHidden,
                        StandardInput = childStandardInput,
                        StandardOutput = outputHandle,
                        StandardError = outputHandle
                    },
                    AttributeList = attributeList
                };
                if (!CreateProcessW(executable, commandLinePointer, IntPtr.Zero, IntPtr.Zero,
                        inheritHandles: true,
                        ExtendedStartupInfoPresent | CreateNewConsole | CreateNewProcessGroup | CreateUnicodeEnvironment,
                        environmentPointer, workingDirectory, ref startup, out information))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, $"Could not start isolated console process '{executable}'.");
                }
            }

            processHandle = information.Process;
            threadHandle = information.Thread;
            managedProcess = Process.GetProcessById(checked((int)information.ProcessId));
            result = new WindowsConsoleProcess(managedProcess, processHandle, outputLogPath);
            managedProcess = null;
            processHandle = IntPtr.Zero;
        }
        catch (Exception exception)
        {
            startFailure = exception;
        }
        finally
        {
            try { managedProcess?.Dispose(); }
            catch (Exception exception) { cleanupFailure ??= exception; }
            if (processHandle != IntPtr.Zero)
                cleanupFailure ??= TryTerminateUnwrappedProcess(processHandle);
            if (threadHandle != IntPtr.Zero) _ = CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero) _ = CloseHandle(processHandle);
            if (inheritedInputHandle != IntPtr.Zero) _ = CloseHandle(inheritedInputHandle);
            if (outputHandle != IntPtr.Zero && outputHandle != InvalidHandleValue) _ = CloseHandle(outputHandle);
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (commandLinePointer != IntPtr.Zero) Marshal.FreeHGlobal(commandLinePointer);
            if (environmentPointer != IntPtr.Zero) Marshal.FreeHGlobal(environmentPointer);
        }

        if (startFailure is not null)
        {
            if (cleanupFailure is not null)
                startFailure.Data["WindowsConsoleProcess cleanup failure"] = cleanupFailure;
            ExceptionDispatchInfo.Capture(startFailure).Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        return result!;
    }

    private static Exception? TryTerminateUnwrappedProcess(IntPtr processHandle)
    {
        try
        {
            if (!TerminateProcess(processHandle, 1))
            {
                var terminateError = Marshal.GetLastWin32Error();
                if (WaitForSingleObject(processHandle, 0) != WaitObject0)
                    return new Win32Exception(terminateError, "Could not terminate the unwrapped console process.");
            }

            var wait = WaitForSingleObject(processHandle, 5000);
            if (wait == WaitObject0) return null;
            return wait == WaitFailed
                ? new Win32Exception(Marshal.GetLastWin32Error(), "Could not confirm termination of the unwrapped console process.")
                : new TimeoutException("The unwrapped console process did not terminate within five seconds.");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    internal async Task<int> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Console process {process.Id} did not exit within {timeout}.");
        }
        if (!GetExitCodeProcess(nativeProcessHandle, out var exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read exit code for console process {process.Id}.");
        return unchecked((int)exitCode);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { process.WaitForExit(5000); }
            catch (InvalidOperationException) { }
        }
        process.Dispose();
        _ = CloseHandle(nativeProcessHandle);
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && !key.StartsWith('='))
                values[key] = value;
        }
        foreach (var (key, value) in overrides)
        {
            if (value is null) values.Remove(key);
            else values[key] = value;
        }

        var block = new StringBuilder();
        foreach (var (key, value) in values)
            block.Append(key).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return block.ToString();
    }

    private static string ResolveExecutablePath(string executable)
    {
        if (Path.IsPathFullyQualified(executable)) return executable;
        if (executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return Path.GetFullPath(executable);

        string[] extensions = Path.HasExtension(executable) ? [string.Empty] : [string.Empty, ".exe", ".cmd", ".bat"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), executable + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException($"Could not find executable '{executable}' on PATH.");
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character => !char.IsWhiteSpace(character) && character != '"'))
            return argument;

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
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
        internal ushort Reserved2Size;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
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
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        ref SecurityAttributes securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
