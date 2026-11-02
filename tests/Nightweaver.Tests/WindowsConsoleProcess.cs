using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nightweaver.Tests;

/// <summary>Starts a hidden apphost in its own real Windows console and process group for control-event tests.</summary>
internal sealed class WindowsConsoleProcess : IDisposable
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const ushort ShowWindowHidden = 0;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;
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
    internal string? OutputLogPath { get; }

    internal static WindowsConsoleProcess Start(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?> environment, string? outputLogPath = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A Windows console process is required for this integration test.");

        var commandLine = new StringBuilder(QuoteArgument(executable));
        foreach (var argument in arguments)
            commandLine.Append(' ').Append(QuoteArgument(argument));

        var environmentBlock = BuildEnvironmentBlock(environment);
        var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
        var commandLinePointer = Marshal.StringToHGlobalUni(commandLine.ToString());
        var outputHandle = IntPtr.Zero;
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseShowWindow,
            ShowWindow = ShowWindowHidden
        };

        if (outputLogPath is not null)
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true
            };
            outputHandle = CreateFileW(outputLogPath, GenericWrite, FileShareRead | FileShareWrite | FileShareDelete,
                ref security, CreateAlways, FileAttributeNormal, IntPtr.Zero);
            if (outputHandle == InvalidHandleValue)
            {
                Marshal.FreeHGlobal(commandLinePointer);
                Marshal.FreeHGlobal(environmentPointer);
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create console process log '{outputLogPath}'.");
            }
            startup.Flags |= StartfUseStdHandles;
            startup.StandardInput = GetStdHandle(StdInputHandle);
            startup.StandardOutput = outputHandle;
            startup.StandardError = outputHandle;
        }

        try
        {
            if (!CreateProcessW(executable, commandLinePointer, IntPtr.Zero, IntPtr.Zero,
                    inheritHandles: outputHandle != IntPtr.Zero,
                    CreateNewConsole | CreateNewProcessGroup | CreateUnicodeEnvironment,
                    environmentPointer, workingDirectory, ref startup, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start isolated console process '{executable}'.");

            try
            {
                return new WindowsConsoleProcess(Process.GetProcessById(checked((int)information.ProcessId)),
                    information.Process, outputLogPath);
            }
            finally
            {
                _ = CloseHandle(information.Thread);
            }
        }
        finally
        {
            if (outputHandle != IntPtr.Zero && outputHandle != InvalidHandleValue) _ = CloseHandle(outputHandle);
            Marshal.FreeHGlobal(commandLinePointer);
            Marshal.FreeHGlobal(environmentPointer);
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
        return checked((int)exitCode);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        ref SecurityAttributes securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
