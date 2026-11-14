using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO.Pipes;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Nightweaver.Tests;

namespace Nightweaver.Tests.AgentInvocationFixture;

public sealed class FixtureMarker { }

internal static class Program
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint ControlBreakEvent = 1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--native-handle-probe")
            return RunNativeHandleProbeCoordinator(args);
        if (args.Length > 0 && args[0] == "--native-handle-child")
            return RunNativeHandleProbeChild(args);
        if (args.Length > 0 && args[0] == "--native-helper-stdio-child")
            return RunNativeHelperStdioChild(args);
        if (args.Length > 0 && args[0] == "--native-helper-inspect-child")
            return RunNativeHelperInspectChild(args);
        if (args.Length > 0 && args[0] == "--native-helper-no-log-child")
            return RunNativeHelperNoLogChild(args);
        if (args.Length > 0 && args[0] == "stream-cleanup-producer")
            return AgentStreamCleanupFixture.RunProducer(args);
        if (args.Length > 0 && args[0] == "stream-cleanup-race-producer")
            return AgentStreamCleanupFixture.RunRaceProducer(args);
        if (args.Length > 0 && args[0] == "stream-cleanup-holder")
            return AgentStreamCleanupFixture.RunHolder(args);
        if (args.Length > 0 && args[0].StartsWith("--evidence-", StringComparison.Ordinal))
            return RunEvidenceProbe(args);

        if (args.Length == 2 && args[0] == "--nightweaver-send-control-break")
            return SendConsoleBreak(args[1]);

        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "bd", StringComparison.OrdinalIgnoreCase))
            return RunBdShim(args);
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "git", StringComparison.OrdinalIgnoreCase))
            return RunGitShim(args);
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "gh", StringComparison.OrdinalIgnoreCase))
            return RunGhShim(args);

        if (Environment.GetEnvironmentVariable("NIGHTWEAVER_INVOCATION_PURPOSE") == "outcome-classifier")
            return RunOutcomeClassifier();

        var classifierAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION");
        if (classifierAction is not null) return RunClassifierAction(classifierAction);
        var stageAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION");
        if (stageAction is not null) return RunStageAction(stageAction);
        if (args.Length == 0) return 64;
        switch (args[0])
        {
            case "inspect":
            {
                using var input = Console.OpenStandardInput();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                var report = new
                {
                    stdinBase64 = Convert.ToBase64String(bytes.ToArray()),
                    cwd = Environment.CurrentDirectory,
                    beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID"),
                    tmp = Environment.GetEnvironmentVariable("TMP"),
                    temp = Environment.GetEnvironmentVariable("TEMP"),
                    tmpdir = Environment.GetEnvironmentVariable("TMPDIR"),
                    slotId = Environment.GetEnvironmentVariable("NIGHTWEAVER_SLOT_ID"),
                    ambient = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_AMBIENT"),
                    auth = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_AUTH"),
                    args = args.Skip(1).ToArray()
                };
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report)));
                Console.OpenStandardOutput().Flush();
                return args.Length > 1 && int.TryParse(args[^1], out var exitCode) ? exitCode : 0;
            }
            case "flood":
            {
                using var input = Console.OpenStandardInput();
                input.CopyTo(Stream.Null);
                var size = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1_200_000;
                var output = Console.OpenStandardOutput();
                var error = Console.OpenStandardError();
                var outBuffer = Enumerable.Repeat((byte)'O', size).ToArray();
                var errBuffer = Enumerable.Repeat((byte)'E', size).ToArray();
                output.Write(outBuffer);
                output.Flush();
                error.Write(errBuffer);
                error.Flush();
                return 0;
            }
            case "exit":
                Console.OpenStandardInput().CopyTo(Stream.Null);
                return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            case "close-stdin":
                Console.OpenStandardInput().Dispose();
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes("stdin-closed\n"));
                return 0;
            case "live-marker":
                if (args.Length < 3) return 65;
                Console.WriteLine("live-marker-before-exit " + (args.Length > 3 ? args[3] : "default"));
                Console.Out.Flush();
                File.WriteAllText(args[1], "waiting", new UTF8Encoding(false));
                WaitForFile(args[2], TimeSpan.FromMinutes(30));
                Console.WriteLine("live-marker-after-release " + (args.Length > 3 ? args[3] : "default"));
                Console.Out.Flush();
                return 0;
            case "live-split-utf8":
            {
                if (args.Length < 3) return 65;
                var output = Console.OpenStandardOutput();
                output.Write(Encoding.UTF8.GetBytes("prefix-"));
                output.Write([0xE2, 0x82]);
                output.Flush();
                File.WriteAllText(args[1], "waiting", new UTF8Encoding(false));
                WaitForFile(args[2], TimeSpan.FromMinutes(30));
                output.Write([0xAC]);
                output.Write(Encoding.UTF8.GetBytes("-suffix\n"));
                output.Flush();
                return 0;
            }
            case "never-read":
                if (args.Length > 1) File.WriteAllText(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                while (true) Thread.Sleep(1000);
            case "spawn-hold":
            {
                if (args.Length < 3) return 65;
                File.WriteAllText(args[1] + ".root", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SpawnProcess("relay", args[1], args[2]);
                WaitForFile(args[1]);
                WaitForFile(args[1] + ".child");
                WaitForFile(args[2]);
                var exitCode = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes("root-exit\n"));
                return exitCode;
            }
            case "relay":
                if (args.Length < 3) return 65;
                File.WriteAllText(args[1] + ".child", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SpawnProcess("hold", args[1]);
                WaitForFile(args[1]);
                WaitForFile(args[2]);
                return 0;
            case "hold":
                File.WriteAllText(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                while (true) Thread.Sleep(1000);
            case "hold-identity":
                File.WriteAllText(args[1], $"{Environment.ProcessId}|{Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks}", new UTF8Encoding(false));
                while (true) Thread.Sleep(1000);
            default:
                return 66;
        }
    }

    private static int RunNativeHandleProbeCoordinator(string[] args)
    {
        if (args.Length is < 3 or > 4) return 67;
        var root = Path.GetFullPath(args[1]);
        var nonce = args[2];
        var injectFailureAfterPeek = args.Length == 4 && args[3] == "fail-after-peek";
        Directory.CreateDirectory(root);
        if (args.Length == 4 && args[3] == "semantics")
            return RunNativeHelperSemantics(root, nonce);
        var report = new NativeHandleProbeReport { Nonce = nonce };
        var exitCode = 0;
        try
        {
            report.NoChildWriterOpen = ObserveNoChildWriterOpen();
            report.NoChildWriterClosed = ObserveNoChildWriterClosed();
            report.NonInheritableChild = NewObservation(root, "noninheritable", HandleInheritability.None);
            ObserveChild(root, nonce, report.NonInheritableChild, HandleInheritability.None, injectFailureAfterPeek);
            report.InheritableChild = NewObservation(root, "inheritable", HandleInheritability.Inheritable);
            ObserveChild(root, nonce, report.InheritableChild, HandleInheritability.Inheritable, false);
        }
        catch (Exception exception)
        {
            report.Error = exception.ToString();
            Console.Error.WriteLine(exception);
            exitCode = 2;
        }

        try { WriteNativeHandleProbeReport(root, report); }
        catch (Exception exception)
        {
            report.ReportWriteError = exception.ToString();
            Console.Error.WriteLine(exception);
            exitCode = 2;
        }
        return exitCode;
    }

    private static NativePipePeek ObserveNoChildWriterOpen()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        return Peek(pipe.SafePipeHandle);
    }

    private static NativePipePeek ObserveNoChildWriterClosed()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        pipe.DisposeLocalCopyOfClientHandle();
        return Peek(pipe.SafePipeHandle);
    }

    private static NativeHandleChildObservation NewObservation(string root, string mode,
        HandleInheritability inheritability) => new()
    {
        Mode = mode,
        WriterWasInheritable = inheritability == HandleInheritability.Inheritable,
        ChildLogPath = Path.Combine(root, $"{mode}.child.log")
    };

    private static void ObserveChild(string root, string nonce, NativeHandleChildObservation observation,
        HandleInheritability inheritability, bool injectFailureAfterPeek)
    {
        var readyPath = Path.Combine(root, $"{observation.Mode}.ready.json");
        var releasePath = Path.Combine(root, $"{observation.Mode}.release");
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The fixture coordinator has no process path.");
        var pipe = new AnonymousPipeServerStream(PipeDirection.In, inheritability);
        WindowsConsoleProcess? child = null;
        Exception? observationFailure = null;
        try
        {
            if (injectFailureAfterPeek) Directory.CreateDirectory(releasePath);
            child = WindowsConsoleProcess.Start(executable,
                ["--native-handle-child", readyPath, releasePath, nonce], root,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), observation.ChildLogPath);
            WaitForFile(readyPath, TimeSpan.FromSeconds(20));
            observation.Ready = JsonSerializer.Deserialize<NativeHandleChildReady>(File.ReadAllText(readyPath))
                ?? throw new InvalidDataException("Native handle child readiness report was empty.");
            if (observation.Ready.Nonce != nonce) throw new InvalidDataException("Native handle child readiness nonce did not match.");
            if (observation.Ready.ProcessId != child.Id) throw new InvalidDataException("Native handle child PID did not match the helper process.");

            observation.SameChildAliveBeforePeek = IsSameProcessAlive(observation.Ready.ProcessId,
                observation.Ready.StartTimeUtcTicks);
            pipe.DisposeLocalCopyOfClientHandle();
            observation.WriterClosedBeforePeek = true;
            observation.WhileAlive = Peek(pipe.SafePipeHandle);
            observation.SameChildAliveAfterPeek = IsSameProcessAlive(observation.Ready.ProcessId,
                observation.Ready.StartTimeUtcTicks);
            if (injectFailureAfterPeek)
                throw new InvalidOperationException("Injected probe infrastructure failure after the live-child Peek.");

            WriteAtomicText(releasePath, nonce);
            observation.ExitCode = child.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            observation.ChildExited = child.HasExited;
            observation.AfterExit = Peek(pipe.SafePipeHandle);
        }
        catch (Exception exception) { observationFailure = exception; }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        try
                        {
                            WriteAtomicText(releasePath, nonce);
                            observation.ExitCode = child.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                        }
                        catch (Exception exception) { AppendCleanupError(observation, "release/wait", exception); }

                        if (!child.HasExited)
                        {
                            try
                            {
                                child.KillEntireProcessTree();
                                observation.ExitCode = child.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                            }
                            catch (Exception exception) { AppendCleanupError(observation, "kill/wait", exception); }
                        }
                    }
                    observation.ChildExited |= child.HasExited;
                }
                catch (Exception exception) { AppendCleanupError(observation, "exit observation", exception); }

                try { child.Dispose(); }
                catch (Exception exception) { AppendCleanupError(observation, "dispose", exception); }
                if (observation.Ready is not null)
                {
                    try
                    {
                        observation.ChildGoneAfterCleanup = !IsSameProcessAlive(observation.Ready.ProcessId,
                            observation.Ready.StartTimeUtcTicks);
                    }
                    catch (Exception exception) { AppendCleanupError(observation, "owned-child confirmation", exception); }
                }
            }

            if (!observation.WriterClosedBeforePeek)
            {
                try
                {
                    pipe.DisposeLocalCopyOfClientHandle();
                    observation.WriterClosedBeforePeek = true;
                }
                catch (Exception exception) { AppendCleanupError(observation, "close parent writer", exception); }
            }
            try { pipe.Dispose(); }
            catch (Exception exception) { AppendCleanupError(observation, "dispose pipe", exception); }
        }

        if (observationFailure is not null)
        {
            observation.Error = observationFailure.ToString();
            throw new AggregateException("The probe observation failed; partial state and cleanup are retained.",
                observationFailure);
        }
        if (observation.CleanupError is not null)
            throw new IOException($"Probe cleanup failed for {observation.Mode}: {observation.CleanupError}");
    }

    private static void AppendCleanupError(NativeHandleChildObservation observation, string phase, Exception exception)
    {
        var entry = $"{phase}: {exception}";
        observation.CleanupError = observation.CleanupError is null
            ? entry
            : observation.CleanupError + Environment.NewLine + entry;
    }

    private static bool IsSameProcessAlive(int processId, long expectedStartTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static NativePipePeek Peek(SafePipeHandle readHandle)
    {
        var success = PeekNamedPipe(readHandle, IntPtr.Zero, 0, out var bytesRead,
            out var totalBytesAvailable, out var bytesLeftThisMessage);
        var error = success ? 0 : Marshal.GetLastWin32Error();
        return new NativePipePeek(success, bytesRead, totalBytesAvailable, bytesLeftThisMessage, error);
    }

    private static int RunNativeHandleProbeChild(string[] args)
    {
        if (args.Length != 4) return 67;
        var readyPath = Path.GetFullPath(args[1]);
        var releasePath = Path.GetFullPath(args[2]);
        var nonce = args[3];
        var ready = new NativeHandleChildReady(Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, nonce);
        WriteAtomicText(readyPath, JsonSerializer.Serialize(ready));
        WaitForFile(releasePath, TimeSpan.FromSeconds(30));
        return string.Equals(File.ReadAllText(releasePath), nonce, StringComparison.Ordinal) ? 0 : 68;
    }

    private static void WriteNativeHandleProbeReport(string root, NativeHandleProbeReport report)
        => File.WriteAllText(Path.Combine(root, "probe-report.json"), JsonSerializer.Serialize(report), new UTF8Encoding(false));

    private static int RunNativeHelperSemantics(string root, string nonce)
    {
        var report = new NativeHelperSemanticsReport { Nonce = nonce };
        var overrideKey = $"NIGHTWEAVER_HELPER_OVERRIDE_{nonce}";
        var removalKey = $"NIGHTWEAVER_HELPER_REMOVAL_{nonce}";
        var previousOverride = Environment.GetEnvironmentVariable(overrideKey);
        var previousRemoval = Environment.GetEnvironmentVariable(removalKey);
        Environment.SetEnvironmentVariable(overrideKey, "coordinator-default");
        Environment.SetEnvironmentVariable(removalKey, "must-be-removed");
        try
        {
            report.MissingExecutableError = CaptureLaunchFailure(root,
                Path.Combine(root, "missing-fully-qualified.exe"), Path.Combine(root, "missing-executable.log"));
            report.InvalidLogError = CaptureLaunchFailure(root, GetFixtureExecutable(),
                Path.Combine(root, "missing-log-directory", "invalid.log"));
            report.InvalidLogDestinationAbsent = !File.Exists(Path.Combine(root, "missing-log-directory", "invalid.log"));
            report.MissingExecutableLogClosed = VerifyOwnedLogCanBeRenamedAndDeleted(
                Path.Combine(root, "missing-executable.log"));

            var workingDirectory = Path.Combine(root, "work directory with spaces");
            Directory.CreateDirectory(workingDirectory);
            var reportPath = Path.Combine(root, "stdio-child.json");
            var logPath = Path.Combine(root, "stdio-child.log");
            var stdioChildId = 0;
            using (var child = WindowsConsoleProcess.Start(GetFixtureExecutable(),
                       ["--native-helper-stdio-child", reportPath, "argument with spaces", "quoted\"argument", nonce],
                       workingDirectory,
                       new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                       {
                           [overrideKey] = "overridden value",
                           [removalKey] = null
                       }, logPath))
            {
                stdioChildId = child.Id;
                report.StdioChildId = stdioChildId;
                using var runningChild = Process.GetProcessById(child.Id);
                report.StdioChildStartTicks = runningChild.StartTime.ToUniversalTime().Ticks;
                report.StdioExitCode = child.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            }
            report.StdioChildGone = !IsSameProcessAlive(stdioChildId, report.StdioChildStartTicks);
            report.StdioChild = JsonSerializer.Deserialize<NativeHelperStdioChildReport>(File.ReadAllText(reportPath));
            report.MergedOutput = File.ReadAllText(logPath);
            report.StdioLogClosed = VerifyOwnedLogCanBeRenamedAndDeleted(logPath);

            report.ZeroInput = ObserveInputHandle(root, "zero", IntPtr.Zero);
            report.InvalidInput = ObserveInputHandle(root, "invalid", new IntPtr(-1));
            var currentInput = GetStdHandle(-10);
            if (currentInput == IntPtr.Zero || currentInput == new IntPtr(-1))
                throw new InvalidOperationException("The isolated coordinator has no valid redirected stdin for handle observations.");
            if (!DuplicateHandle(GetCurrentProcess(), currentInput, GetCurrentProcess(), out var nonInheritableInput,
                    0, inheritHandle: false, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create noninheritable stdin observation handle.");
            try { report.NonInheritableInput = ObserveInputHandle(root, "noninheritable", nonInheritableInput); }
            finally { _ = CloseHandle(nonInheritableInput); }

            report.ConsoleInput = ObserveConsoleInput(root);

            var noLogReportPath = Path.Combine(root, "no-log-child.json");
            var noLogPath = Path.Combine(root, "no-log-child.log");
            using (var noLogChild = WindowsConsoleProcess.Start(GetFixtureExecutable(),
                       ["--native-helper-no-log-child", noLogReportPath, "no-log argument with spaces",
                           "quoted\"no-log", overrideKey, removalKey], workingDirectory,
                       new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                       {
                           [overrideKey] = "no-log override",
                           [removalKey] = null
                       }))
                report.NoLogExitCode = noLogChild.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            report.NoLogChild = JsonSerializer.Deserialize<NativeHelperNoLogChildReport>(File.ReadAllText(noLogReportPath));
            report.NoLogLogAbsent = !File.Exists(noLogPath);
        }
        catch (Exception exception) { report.Error = exception.ToString(); }
        finally
        {
            Environment.SetEnvironmentVariable(overrideKey, previousOverride);
            Environment.SetEnvironmentVariable(removalKey, previousRemoval);
            File.WriteAllText(Path.Combine(root, "semantics-report.json"),
                JsonSerializer.Serialize(report), new UTF8Encoding(false));
        }
        Console.Error.WriteLine(report.Error ?? "Native helper semantics probe completed.");
        return report.Error is null ? 0 : 2;
    }

    private static int? CaptureLaunchFailure(string root, string executable, string logPath)
    {
        try
        {
            using var unexpected = WindowsConsoleProcess.Start(executable, [], root,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), logPath);
            _ = unexpected.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return null;
        }
        catch (Win32Exception exception) { return exception.NativeErrorCode; }
    }

    private static bool VerifyOwnedLogCanBeRenamedAndDeleted(string path)
    {
        if (!File.Exists(path)) return false;
        using (File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
        var renamed = path + ".renamed";
        File.Move(path, renamed);
        File.Delete(renamed);
        return !File.Exists(path) && !File.Exists(renamed);
    }

    private static NativeHelperInputObservation ObserveInputHandle(string root, string mode, IntPtr inputHandle)
    {
        var original = GetStdHandle(-10);
        if (!SetStdHandle(-10, inputHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not set {mode} stdin observation handle.");
        try
        {
            var reportPath = Path.Combine(root, $"stdin-{mode}.json");
            var logPath = Path.Combine(root, $"stdin-{mode}.log");
            using (var child = WindowsConsoleProcess.Start(GetFixtureExecutable(),
                       ["--native-helper-inspect-child", reportPath, mode], root,
                       new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), logPath))
                if (child.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() != 0)
                    throw new InvalidOperationException($"The {mode} stdin observation child failed.");
            return JsonSerializer.Deserialize<NativeHelperInputObservation>(File.ReadAllText(reportPath))
                ?? throw new InvalidDataException($"The {mode} stdin observation was empty.");
        }
        finally
        {
            _ = SetStdHandle(-10, original);
        }
    }

    private static NativeHelperInputObservation ObserveConsoleInput(string root)
    {
        var original = GetStdHandle(-10);
        var allocated = false;
        try
        {
            if (!GetConsoleMode(original, out _))
            {
                _ = FreeConsole();
                if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate an isolated console for stdin observation.");
                allocated = true;
            }
            var consoleInput = CreateFileW("CONIN$", GenericRead | GenericWrite,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (consoleInput == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open isolated console stdin.");
            try
            {
                var sourceIsConsole = GetConsoleMode(consoleInput, out _);
                return ObserveInputHandle(root, "console", consoleInput) with
                {
                    SourceConsoleModeSucceeded = sourceIsConsole
                };
            }
            finally { _ = CloseHandle(consoleInput); }
        }
        finally
        {
            _ = SetStdHandle(-10, original);
            if (allocated) _ = FreeConsole();
        }
    }

    private static string GetFixtureExecutable()
    {
        var fixtureAssembly = typeof(FixtureMarker).Assembly.Location;
        var executable = Path.ChangeExtension(fixtureAssembly, ".exe");
        return File.Exists(executable) ? executable : throw new FileNotFoundException("The fixture apphost is missing.", executable);
    }

    private static int RunNativeHelperStdioChild(string[] args)
    {
        if (args.Length != 5) return 67;
        using var input = Console.OpenStandardInput();
        using var inputBytes = new MemoryStream();
        input.CopyTo(inputBytes);
        var report = new NativeHelperStdioChildReport(args[2..4],
            Path.GetFullPath(Environment.CurrentDirectory),
            Environment.GetEnvironmentVariable($"NIGHTWEAVER_HELPER_OVERRIDE_{args[4]}"),
            Environment.GetEnvironmentVariable($"NIGHTWEAVER_HELPER_REMOVAL_{args[4]}"),
            Encoding.UTF8.GetString(inputBytes.ToArray()));
        File.WriteAllText(args[1], JsonSerializer.Serialize(report), new UTF8Encoding(false));
        Console.Out.WriteLine($"stdout-sentinel:{args[4]}");
        Console.Out.Flush();
        Console.Error.WriteLine($"stderr-sentinel:{args[4]}");
        Console.Error.Flush();
        return 0;
    }

    private static int RunNativeHelperNoLogChild(string[] args)
    {
        if (args.Length != 6) return 67;
        var report = new NativeHelperNoLogChildReport(args[2..4], Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable(args[4]), Environment.GetEnvironmentVariable(args[5]));
        File.WriteAllText(args[1], JsonSerializer.Serialize(report), new UTF8Encoding(false));
        return 0;
    }

    private static int RunNativeHelperInspectChild(string[] args)
    {
        if (args.Length < 2) return 67;
        var input = GetStdHandle(-10);
        var information = GetHandleInformation(input, out var flags);
        var console = GetConsoleMode(input, out _);
        var report = new NativeHelperInputObservation(args.Length >= 3 ? args[2] : "no-log",
            input.ToInt64(), information, console, information ? flags : null,
            Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable(args.Length >= 4 ? args[3] : string.Empty),
            Environment.GetEnvironmentVariable(args.Length >= 5 ? args[4] : string.Empty));
        File.WriteAllText(args[1], JsonSerializer.Serialize(report), new UTF8Encoding(false));
        return 0;
    }

    private sealed class NativeHelperSemanticsReport
    {
        public string Nonce { get; set; } = string.Empty;
        public int? MissingExecutableError { get; set; }
        public int? InvalidLogError { get; set; }
        public bool MissingExecutableLogClosed { get; set; }
        public bool InvalidLogDestinationAbsent { get; set; }
        public NativeHelperStdioChildReport? StdioChild { get; set; }
        public int? StdioChildId { get; set; }
        public long StdioChildStartTicks { get; set; }
        public int? StdioExitCode { get; set; }
        public bool StdioChildGone { get; set; }
        public string? MergedOutput { get; set; }
        public bool StdioLogClosed { get; set; }
        public NativeHelperInputObservation? ZeroInput { get; set; }
        public NativeHelperInputObservation? InvalidInput { get; set; }
        public NativeHelperInputObservation? NonInheritableInput { get; set; }
        public NativeHelperInputObservation? ConsoleInput { get; set; }
        public int? NoLogExitCode { get; set; }
        public NativeHelperNoLogChildReport? NoLogChild { get; set; }
        public bool NoLogLogAbsent { get; set; }
        public string? Error { get; set; }
    }

    private sealed record NativeHelperStdioChildReport(string[] Arguments, string WorkingDirectory,
        string? OverrideValue, string? RemovedValue, string StandardInput);
    private sealed record NativeHelperNoLogChildReport(string[] Arguments, string WorkingDirectory,
        string? OverrideValue, string? RemovedValue);
    private sealed record NativeHelperInputObservation(string Mode, long RawHandle, bool HandleInformationSucceeded,
        bool ConsoleModeSucceeded, uint? HandleFlags, string? CurrentDirectory, string? EnvironmentValue,
        string? RemovedEnvironmentValue, bool? SourceConsoleModeSucceeded = null);

    private sealed record NativePipePeek(bool Success, uint BytesRead, uint TotalBytesAvailable,
        uint BytesLeftThisMessage, int Error);
    private sealed record NativeHandleChildReady(int ProcessId, long StartTimeUtcTicks, string Nonce);
    private sealed class NativeHandleChildObservation
    {
        public string Mode { get; set; } = string.Empty;
        public bool WriterWasInheritable { get; set; }
        public NativeHandleChildReady? Ready { get; set; }
        public bool WriterClosedBeforePeek { get; set; }
        public bool SameChildAliveBeforePeek { get; set; }
        public NativePipePeek? WhileAlive { get; set; }
        public bool SameChildAliveAfterPeek { get; set; }
        public bool ChildExited { get; set; }
        public int? ExitCode { get; set; }
        public NativePipePeek? AfterExit { get; set; }
        public string ChildLogPath { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string? CleanupError { get; set; }
        public bool ChildGoneAfterCleanup { get; set; }
    }

    private sealed class NativeHandleProbeReport
    {
        public string Nonce { get; set; } = string.Empty;
        public NativePipePeek? NoChildWriterOpen { get; set; }
        public NativePipePeek? NoChildWriterClosed { get; set; }
        public NativeHandleChildObservation? NonInheritableChild { get; set; }
        public NativeHandleChildObservation? InheritableChild { get; set; }
        public string? Error { get; set; }
        public string? ReportWriteError { get; set; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint bufferSize,
        out uint bytesRead, out uint totalBytesAvailable, out uint bytesLeftThisMessage);

    private static int RunOutcomeClassifier()
    {
        using var input = Console.OpenStandardInput();
        using var stream = new MemoryStream();
        input.CopyTo(stream);
        var prompt = Encoding.UTF8.GetString(stream.ToArray());
        var classifierPidPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_PID_FILE");
        if (!string.IsNullOrWhiteSpace(classifierPidPath))
        {
            var startTime = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(classifierPidPath, $"{Environment.ProcessId}|{startTime}", new UTF8Encoding(false));
        }
        var classifierStartedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_STARTED_FILE");
        if (!string.IsNullOrWhiteSpace(classifierStartedFile))
            File.WriteAllText(classifierStartedFile, Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID") ?? string.Empty,
                new UTF8Encoding(false));
        var classifierReleaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_RELEASE_FILE");
        if (!string.IsNullOrWhiteSpace(classifierReleaseFile))
            WaitForFile(classifierReleaseFile, TimeSpan.FromMinutes(2));
        const string evidenceMarker = "Evidence JSON:\n";
        var evidenceStart = prompt.IndexOf(evidenceMarker, StringComparison.Ordinal);
        if (evidenceStart < 0) return 65;
        evidenceStart += evidenceMarker.Length;
        var evidenceEnd = prompt.IndexOf("\nReturn only one JSON object", evidenceStart, StringComparison.Ordinal);
        if (evidenceEnd < 0) return 65;
        using var evidence = JsonDocument.Parse(prompt[evidenceStart..evidenceEnd]);
        var root = evidence.RootElement;
        var role = root.GetProperty("role").GetString();
        var sources = root.GetProperty("sources").ToString();
        var choice = sources.Contains("product decision:", StringComparison.OrdinalIgnoreCase)
            ? "product_decision_required"
            : role switch
            {
                "implementer" => "ready_for_review",
                "reviewer" when sources.Contains("blocking finding:", StringComparison.OrdinalIgnoreCase) => "changes_required",
                "reviewer" => "approved",
                "qa" when sources.Contains("qa failure:", StringComparison.OrdinalIgnoreCase) => "changes_required",
                "qa" => "approved",
                "merger" when sources.Contains("nightweaver-merger-rejection-v1", StringComparison.Ordinal) => "changes_required",
                "merger" when sources.Contains("nightweaver-merge-v1", StringComparison.Ordinal) => "approved",
                _ => "incomplete_or_unclear"
            };
        var schema = "nightweaver-" + role + "-outcome-v1";
        var question = role switch
        {
            "implementer" => "implementer_outcome",
            "reviewer" => "reviewer_outcome",
            "qa" => "qa_outcome",
            "merger" => "merger_outcome",
            _ => "unknown_outcome"
        };
        var response = JsonSerializer.Serialize(new
        {
            schema,
            question,
            choice
        });
        Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(response));
        Console.OpenStandardOutput().Flush();
        return 0;
    }

    private static int RunGhShim(string[] args)
    {
        var logPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_GH_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
            File.AppendAllText(logPath, JsonSerializer.Serialize(args) + Environment.NewLine, new UTF8Encoding(false));

        var branch = Run("git", ["branch", "--show-current"]);
        var head = Run("git", ["rev-parse", "HEAD"]);
        if (args.Length >= 2 && args[0] == "pr" && args[1] == "checks")
        {
            var failed = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CI_RESULT") == "fail";
            if (args.Contains("--json", StringComparer.Ordinal))
            {
                Console.WriteLine(JsonSerializer.Serialize(new[]
                {
                    new { name = "fixture checks", state = failed ? "FAILURE" : "SUCCESS", bucket = failed ? "fail" : "pass" }
                }));
            }
            else
            {
                Console.WriteLine(failed
                    ? $"Required check fixture checks failed for synchronized HEAD {head.Trim()}."
                    : $"All required fixture checks passed for {head.Trim()}.");
            }
            return failed ? 1 : 0;
        }

        if (args.Length >= 2 && args[0] == "pr" && args[1] == "merge")
        {
            if (!args.Contains("--squash", StringComparer.Ordinal)
                || !args.Contains("--delete-branch", StringComparer.Ordinal))
                throw new InvalidOperationException("Fixture merge requires --squash and --delete-branch.");
            Run("git", ["push", "origin", "HEAD:refs/heads/main"]);
            if (args.Contains("--delete-branch", StringComparer.Ordinal))
                Run("git", ["push", "origin", "--delete", branch.Trim()]);
            Console.WriteLine("Fixture pull request merged.");
            return 0;
        }

        var remoteMain = Run("git", ["ls-remote", "origin", "refs/heads/main"])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var state = string.Equals(remoteMain, head.Trim(), StringComparison.OrdinalIgnoreCase) ? "MERGED" : "OPEN";
        var response = JsonSerializer.Serialize(new
        {
            state,
            baseRefName = "main",
            headRefName = branch.Trim(),
            headRefOid = head.Trim(),
            url = "https://github.com/nightweaver-fixture/target/pull/123",
            baseRepository = new
            {
                nameWithOwner = "nightweaver-fixture/target",
                url = "https://github.com/nightweaver-fixture/target"
            },
            mergedAt = state == "MERGED" ? "2026-11-04T20:00:00Z" : null,
            mergeCommit = state == "MERGED" ? new { oid = head.Trim() } : null
        });
        Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(response));
        Console.OpenStandardOutput().Flush();
        return 0;
    }

    private static int RunEvidenceProbe(string[] args)
    {
        switch (args[0])
        {
            case "--evidence-started-marker-write" when args.Length == 3:
                try
                {
                    var beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID")
                        ?? throw new InvalidOperationException("NIGHTWEAVER_BEAD_ID is missing.");
                    File.WriteAllText(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    WaitForFile(args[2], TimeSpan.FromSeconds(20));
                    WriteStartedMarker(ResolveStartedMarkerPath(beadId), beadId);
                    return 0;
                }
                catch (IOException exception)
                {
                    Console.Error.WriteLine($"fixture-started-marker-sharing-ioexception hresult={exception.HResult}");
                    return 23;
                }
            case "--evidence-legacy-hold" when args.Length == 4:
            {
                using var stream = new FileStream(args[1], FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
                File.WriteAllText(args[2], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                WaitForFile(args[3], TimeSpan.FromSeconds(20));
                return 0;
            }
            case "--evidence-legacy-append" when args.Length == 3:
                try
                {
                    File.AppendAllText(args[1], args[2] + Environment.NewLine, new UTF8Encoding(false));
                    return 0;
                }
                catch (IOException exception)
                {
                    Console.Error.WriteLine($"evidence-legacy-sharing-ioexception hresult={exception.HResult}");
                    return 23;
                }
            case "--evidence-hold-mutex" when args.Length == 4:
                ManualPilotEvidenceLog.HoldMutexForProbe(args[1], args[2], args[3]);
                return 0;
            case "--evidence-append" when args.Length == 5:
                File.WriteAllText(args[3], "started");
                Console.WriteLine("evidence-append-entering-helper");
                Console.Out.Flush();
                ManualPilotEvidenceLog.AppendLine(args[1], args[2]);
                File.WriteAllText(args[4], "completed");
                return 0;
            case "--evidence-read" when args.Length == 5:
                File.WriteAllText(args[3], "started");
                Console.WriteLine("evidence-read-entering-helper");
                Console.Out.Flush();
                File.WriteAllText(args[4], JsonSerializer.Serialize(ManualPilotEvidenceLog.ReadAllLines(args[1])));
                return 0;
            case "--evidence-batch" when args.Length == 6:
            {
                var worker = args[2];
                var count = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(args[4], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                WaitForFile(args[5], TimeSpan.FromSeconds(20));
                var payload = new string('界', 2048);
                for (var sequence = 0; sequence < count; sequence++)
                {
                    var record = JsonSerializer.Serialize(new
                    {
                        worker,
                        sequence,
                        pid = Environment.ProcessId,
                        payload
                    });
                    ManualPilotEvidenceLog.AppendLine(args[1], record);
                }

                return 0;
            }
            default:
                return 68;
        }
    }

    private static int RunBdShim(string[] args)
    {
        var realBd = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_REAL_PATH")
            ?? throw new InvalidOperationException("The native bd shim real executable path is missing.");
        var mode = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_MODE");
        var targetId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_TARGET");
        var logPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            File.AppendAllText(logPath, JsonSerializer.Serialize(new { args, mode }) + Environment.NewLine, new UTF8Encoding(false));
        }

        var updateIndex = Array.FindIndex(args, argument => argument == "update");
        var isRecoveryRelease = updateIndex >= 0 && updateIndex + 1 < args.Length
            && (targetId is null || args[updateIndex + 1] == targetId)
            && HasOptionValue(args, "--if-status", "in_progress")
            && HasOptionValue(args, "--status", "open");
        var isStartupPauseWrite = updateIndex >= 0 && updateIndex + 1 < args.Length
            && (targetId is null || args[updateIndex + 1] == targetId)
            && HasOptionValue(args, "--add-label", "needs-human");
        var isStartupDiagnosticComment = Array.FindIndex(args, argument => argument == "comments") is var commentIndex
            && commentIndex >= 0 && commentIndex + 2 < args.Length
            && args[commentIndex + 1] == "add"
            && (targetId is null || args[commentIndex + 2] == targetId);

        if (mode == "startup-cleanup-pause-race" && targetId is not null && logPath is not null
            && args.Contains("--include-ephemeral", StringComparer.Ordinal)
            && File.ReadAllLines(logPath).Count(line => line.Contains("--include-ephemeral", StringComparison.Ordinal)
                ) == 3)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", targetId, "--add-label", "needs-human"]);
            if (mutation.ExitCode != 0)
            {
                WriteResult(mutation);
                return mutation.ExitCode;
            }
        }
        if (mode == "startup-cleanup-label-write-failure" && isStartupPauseWrite)
        {
            Console.Error.WriteLine("fixture startup cleanup label write failure");
            return 75;
        }
        if (mode == "startup-cleanup-comment-write-failure" && isStartupDiagnosticComment)
        {
            Console.Error.WriteLine("fixture startup cleanup comment write failure");
            return 76;
        }

        if (mode == "read-failure" && args.Contains("list", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("fixture tracker read failure");
            return 71;
        }
        if (mode == "recovery-write-failure" && isRecoveryRelease)
        {
            Console.Error.WriteLine("fixture recovery write failure");
            return 72;
        }
        if (mode == "recovery-guard-mismatch" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--status", "closed", "--force"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
        }
        if (mode == "recovery-assignee-mismatch" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--assignee", "person@example.invalid"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
        }
        if (mode == "recovery-label-race" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--add-label", "needs-human"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
        }
        if (mode == "recovery-uncertain-foreign" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--assignee", "person@example.invalid"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
            Console.Error.WriteLine("fixture recovery write failed after a concurrent foreign claim");
            return 74;
        }

        if (mode == "force-stop-finalization-gate" && isRecoveryRelease)
        {
            var startedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_STARTED_FILE")
                ?? throw new InvalidOperationException("The native bd finalization gate requires a started-file path.");
            var releaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_RELEASE_FILE")
                ?? throw new InvalidOperationException("The native bd finalization gate requires a release-file path.");
            WriteProcessIdentity(startedFile);
            WaitForFile(releaseFile, TimeSpan.FromMinutes(2));
            if (Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_COMMIT_AFTER_RELEASE") == "false")
            {
                Console.Error.WriteLine("fixture guarded release did not commit before force stop");
                return 75;
            }
        }

        var result = RunRealBd(realBd, args);
        WriteResult(result);
        if (mode == "recovery-uncertain-write" && isRecoveryRelease && result.ExitCode == 0) return 73;
        return result.ExitCode;
    }

    private static int RunGitShim(string[] args)
    {
        var realGit = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_REAL_PATH")
            ?? throw new InvalidOperationException("The native git shim real executable path is missing.");
        var mode = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_SHIM_MODE");
        var targetWorktree = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_SHIM_TARGET_WORKTREE");
        if (mode == "force-stop-status-gate" && targetWorktree is not null
            && string.Equals(Path.GetFullPath(Environment.CurrentDirectory), Path.GetFullPath(targetWorktree), StringComparison.OrdinalIgnoreCase)
            && args.SequenceEqual(["status", "--porcelain"], StringComparer.Ordinal))
        {
            var startedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_SHIM_STARTED_FILE")
                ?? throw new InvalidOperationException("The native git verification gate requires a started-file path.");
            var releaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_SHIM_RELEASE_FILE")
                ?? throw new InvalidOperationException("The native git verification gate requires a release-file path.");
            AppendShimLog($"gate-start pid={Environment.ProcessId} cwd={Environment.CurrentDirectory} release={releaseFile} release_exists={File.Exists(releaseFile)}");
            WriteProcessIdentity(startedFile);
            WaitForFile(releaseFile, TimeSpan.FromMinutes(2));
            AppendShimLog($"gate-released pid={Environment.ProcessId}");
        }
        if (mode == "graceful-stop-preparation-gate" && targetWorktree is not null
            && string.Equals(Path.GetFullPath(Environment.CurrentDirectory), Path.GetFullPath(targetWorktree), StringComparison.OrdinalIgnoreCase)
            && args.SequenceEqual(["status", "--porcelain=v1", "--untracked-files=all"], StringComparer.Ordinal))
        {
            var startedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_SHIM_STARTED_FILE")
                ?? throw new InvalidOperationException("The native Merger preparation gate requires a started-file path.");
            var releaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_SHIM_RELEASE_FILE")
                ?? throw new InvalidOperationException("The native Merger preparation gate requires a release-file path.");
            AppendShimLog($"preparation-gate-start pid={Environment.ProcessId} cwd={Environment.CurrentDirectory} release={releaseFile} release_exists={File.Exists(releaseFile)}");
            WriteProcessIdentity(startedFile);
            WaitForFile(releaseFile, TimeSpan.FromMinutes(2));
            AppendShimLog($"preparation-gate-released pid={Environment.ProcessId}");
        }

        var exitCode = RunRealGit(realGit, args);
        AppendShimLog($"git-exit pid={Environment.ProcessId} code={exitCode}");
        return exitCode;
    }

    private static void AppendShimLog(string line)
    {
        var path = Environment.GetEnvironmentVariable("NIGHTWEAVER_GIT_SHIM_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalizedPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) normalizedPath = normalizedPath.ToUpperInvariant();
        var mutexName = $"NightweaverGitShimLog-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))}";
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Timed out waiting to append to the native Git shim log.");

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(line);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void WriteProcessIdentity(string path) =>
        NativeProcessIdentityFile.Write(path);

    private static int RunRealGit(string executable, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the real git executable.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        try { Console.Out.Write(stdout); Console.Error.Write(stderr); }
        catch (IOException) { }
        return process.ExitCode;
    }

    private static bool HasOptionValue(string[] args, string option, string value)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == option && args[index + 1] == value) return true;
        return false;
    }

    private static FixtureProcessResult RunRealBd(string executable, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the real bd executable.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new(process.ExitCode, stdout, stderr);
    }

    private static void WriteResult(FixtureProcessResult result)
    {
        Console.Out.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
    }

    private sealed record FixtureProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static int RunClassifierAction(string action)
    {
        using var input = Console.OpenStandardInput();
        using var inputBytes = new MemoryStream();
        input.CopyTo(inputBytes);
        var exactInput = inputBytes.ToArray();
        var capturedInputPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CAPTURE_STDIN");
        if (!string.IsNullOrWhiteSpace(capturedInputPath)) File.WriteAllBytes(capturedInputPath, exactInput);

        var reportPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                cwd = Environment.CurrentDirectory,
                beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID"),
                tmp = Environment.GetEnvironmentVariable("TMP"),
                temp = Environment.GetEnvironmentVariable("TEMP"),
                tmpdir = Environment.GetEnvironmentVariable("TMPDIR"),
                arguments = Environment.GetCommandLineArgs().Skip(1).ToArray()
            }), new UTF8Encoding(false));
        }

        var output = Console.OpenStandardOutput();
        var error = Console.OpenStandardError();
        switch (action)
        {
            case "duplicate":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}\n"));
                return 0;
            case "different":
                output.Write(Encoding.UTF8.GetBytes(" {\"verdict\":\"different\"} \n"));
                return 0;
            case "stderr-verdict":
                error.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"different\"}"));
                return 0;
            case "nonzero":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                return 17;
            case "invalid":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\",\"verdict\":\"different\"}"));
                return 0;
            case "oversized":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                output.Write(Enumerable.Repeat((byte)' ', 20 * 1024).ToArray());
                return 0;
            case "timeout-descendant":
            {
                var handshake = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_HANDSHAKE")
                    ?? throw new InvalidOperationException("Classifier timeout handshake path is missing.");
                File.WriteAllText(handshake + ".root", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var classifierAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION");
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", null);
                try { SpawnProcess("hold", handshake + ".child"); }
                finally { Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", classifierAction); }
                WaitForFile(handshake + ".child");
                while (true) Thread.Sleep(1000);
            }
            case "flood":
                output.Write(Enumerable.Repeat((byte)'O', 600_000).ToArray());
                error.Write(Enumerable.Repeat((byte)'E', 600_000).ToArray());
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                return 0;
            default:
                return 67;
        }
    }

    private static int RunStageAction(string action)
    {
        using var dispatchBytes = new MemoryStream();
        Console.OpenStandardInput().CopyTo(dispatchBytes);
        var dispatchPrompt = Encoding.UTF8.GetString(dispatchBytes.ToArray());
        var beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID")
            ?? throw new InvalidOperationException("NIGHTWEAVER_BEAD_ID is missing.");
        var mergerBeadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_MERGER_BEAD_ID");
        if (string.Equals(beadId, mergerBeadId, StringComparison.Ordinal))
            action = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_MERGER_ACTION") ?? action;
        if (action == "manual-delivery-pilot"
            && dispatchPrompt.Split('\n').Any(line => line.Trim() == "Stage: stage:merge"))
            action = "merger-verified-merge";
        var pidPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE");
        var pidDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(pidDirectory))
            pidPath = Path.Combine(pidDirectory, beadId + ".pid");
        if (!string.IsNullOrWhiteSpace(pidPath))
        {
            var startTime = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(pidPath, $"{Environment.ProcessId}|{startTime}");
        }
        var launchLog = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG");
        if (!string.IsNullOrWhiteSpace(launchLog))
            AppendStageLaunchRecord(launchLog, beadId);

        switch (action)
        {
            case "planning-draft-gated":
            case "planning-draft":
            case "planning-approve":
            {
                if (action == "planning-draft-gated")
                {
                    WriteStartedMarker(ResolveStartedMarkerPath(beadId), beadId);
                    WaitForFile(Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RELEASE_FILE")
                        ?? throw new InvalidOperationException("Planning release path is missing."));
                }
                const string marker = "Host grounding (copy exactly into evidence):\n";
                var grounding = JsonDocument.Parse(dispatchPrompt[(dispatchPrompt.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..]).RootElement.Clone();
                object evidence;
                if (action is "planning-draft" or "planning-draft-gated")
                    evidence = new
                    {
                        schema = "nightweaver-plan-v1", grounding,
                        tasks = new[] { new { id = "slice-1", title = "Deliver behavior", outcome = "Requested behavior works",
                            context = "README.md describes the base contract", approach = "Extend the documented behavior",
                            acceptanceCriteria = new[] { "Requested behavior is observable" }, verification = "Run the behavior check", dependsOn = Array.Empty<string>() },
                            new { id = "slice-2", title = "Deliver dependent behavior", outcome = "Dependent behavior works",
                                context = "README.md describes the shared contract", approach = "Extend the first behavior",
                                acceptanceCriteria = new[] { "Dependent behavior is observable" }, verification = "Run the dependent behavior check", dependsOn = new[] { "slice-1" } } },
                        constraints = Array.Empty<object>(),
                        coverage = new[] { new { requirement = "Requested behavior", taskIds = new[] { "slice-1", "slice-2" }, satisfiedEvidence = (string?)null, authorizedExclusion = (string?)null } }
                    };
                else
                {
                    using var comments = JsonDocument.Parse(Run("bd", ["comments", beadId, "--json"]));
                    var candidate = comments.RootElement.EnumerateArray().Last(c => c.GetProperty("text").GetString()!.Contains("nightweaver-plan-v1", StringComparison.Ordinal));
                    var text = candidate.GetProperty("text").GetString()!;
                    evidence = new
                    {
                        schema = "nightweaver-plan-review-v1", grounding,
                        planCommentId = candidate.GetProperty("id").ToString(),
                        planRevision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                        assessment = "Task is bounded, executable, verifies behavior and covers the complete Product.",
                        revalidation = (string?)null, findings = Array.Empty<object>(), questions = Array.Empty<object>()
                    };
                }
                Run("bd", ["comments", "add", beadId, JsonSerializer.Serialize(evidence)]);
                break;
            }
            case "manual-delivery-pilot":
            {
                var stage = dispatchPrompt.Split('\n')
                    .FirstOrDefault(line => line.StartsWith("Stage: ", StringComparison.Ordinal))?["Stage: ".Length..]
                    ?? "(none)";
                var evidencePath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_EVIDENCE")
                    ?? throw new InvalidOperationException("The manual pilot evidence path is missing.");
                var gateDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_GATE_DIRECTORY")
                    ?? throw new InvalidOperationException("The manual pilot gate directory is missing.");
                var gateBead = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_GATE_BEAD");
                var secondGateBead = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_GATE_BEAD2");
                var role = stage switch
                {
                    "stage:implement" => "implementer",
                    "stage:review" => "reviewer",
                    "stage:qa" => "qa",
                    _ => throw new InvalidOperationException($"The manual pilot received unsupported stage '{stage}'.")
                };
                var headBefore = Run("git", ["rev-parse", "HEAD"]);
                var record = JsonSerializer.Serialize(new
                {
                    bead_id = beadId,
                    role,
                    stage,
                    pid = Environment.ProcessId,
                    started_at = DateTimeOffset.UtcNow,
                    cwd = Environment.CurrentDirectory,
                    head_before = headBefore.Trim()
                });
                Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
                ManualPilotEvidenceLog.AppendLine(evidencePath, record);
                Console.WriteLine($"manual-delivery-live bead={beadId} role={role} stage={stage} head={headBefore.Trim()}");
                Console.Out.Flush();

                if (role == "implementer")
                {
                    if (string.Equals(beadId, gateBead, StringComparison.Ordinal)
                        || string.Equals(beadId, secondGateBead, StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(gateDirectory);
                        if (string.Equals(beadId,
                            Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_RETAIN_SENTINEL_BEAD"),
                            StringComparison.Ordinal))
                        {
                            var retainedSentinel = Path.Combine(Environment.CurrentDirectory, "manual-pilot-retained-sentinel.txt");
                            if (!File.Exists(retainedSentinel))
                                File.WriteAllText(retainedSentinel, "retain across runtime recovery\n", new UTF8Encoding(false));
                        }
                        WriteAtomicText(Path.Combine(gateDirectory, beadId + ".started"),
                            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        WaitForFile(Path.Combine(gateDirectory, beadId + ".release"), TimeSpan.FromMinutes(5));
                    }

                    var currentBead = ReadFixtureBead(beadId);
                    var repairCycles = currentBead.TryGetProperty("metadata", out var metadata)
                        && metadata.TryGetProperty("repair_cycles", out var currentCycles) ? currentCycles.GetInt32() : 0;
                    var output = Path.Combine(Environment.CurrentDirectory, "manual-delivery-" + beadId + ".txt");
                    File.WriteAllText(output,
                        $"Committed by the isolated native pilot for {beadId}; repair cycle {repairCycles}; invocation process {Environment.ProcessId}.\n",
                        new UTF8Encoding(false));
                    Run("git", ["add", Path.GetFileName(output)]);
                    Run("git", ["commit", "-m", $"manual Delivery pilot implementation {beadId} cycle {repairCycles}"]);
                    var branch = Run("git", ["branch", "--show-current"]).Trim();
                    Run("git", ["push", "origin", $"HEAD:refs/heads/{branch}"]);
                }
                else if (role == "reviewer")
                {
                    var rejectionBead = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_REJECT_BEAD");
                    var exhaustionBead = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_EXHAUST_BEAD");
                    var state = ReadFixtureBead(beadId);
                    var repairCycles = state.TryGetProperty("metadata", out var metadata)
                        && metadata.TryGetProperty("repair_cycles", out var cycles) ? cycles.GetInt32() : 0;
                    var exhaustedPilot = string.Equals(beadId, exhaustionBead, StringComparison.Ordinal)
                        && repairCycles <= ReadPilotRepairLimit();
                    if (exhaustedPilot)
                        Run("bd", ["comments", "add", beadId,
                            $"Blocking finding: automatic pilot rejection cycle {repairCycles}; fix the documented issue before review can approve."]);
                    else if (string.Equals(beadId, rejectionBead, StringComparison.Ordinal) && repairCycles == 0)
                        Run("bd", ["comments", "add", beadId, "Blocking finding: first pilot review requires the missing edge case; implement the correction before approval."]);
                    else
                        Run("bd", ["comments", "add", beadId, "Reviewer evidence: pilot change is ready for QA."]);
                }
                else
                {
                    Run("bd", ["comments", "add", beadId, "QA evidence: disposable native pilot behavior matches the task acceptance criteria."]);
                }
                break;
            }
            case "slot-implement":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-implement-unlabeled":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-implement-push":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-implement-pause":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation before pause"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                Run("bd", ["comments", "add", beadId, "Implementer product decision: what database schema should be used?"]);
                break;
            case "slot-implement-pause-unlabeled":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation before pause"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                Run("bd", ["comments", "add", beadId, "Implementer product decision: should this feature be enabled by default?"]);
                break;
            case "slot-review-pause":
                Run("bd", ["comments", "add", beadId, "Reviewer product decision: does this error handling match product requirements?"]);
                break;
            case "slot-qa-pause":
                Run("bd", ["comments", "add", beadId, "QA product decision: is this edge case behavior intended?"]);
                break;
            case "pause-nonzero":
                Run("bd", ["comments", "add", beadId, "question"]);
                Run("bd", ["update", beadId, "--add-label", "product-decision-required"]);
                return 19;
            case "pause-dirty":
                Run("bd", ["comments", "add", beadId, "question"]);
                Run("bd", ["update", beadId, "--add-label", "product-decision-required"]);
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "dirty.txt"), "dirty\n");
                break;
            case "pause-stage-change":
                Run("bd", ["comments", "add", beadId, "question"]);
                Run("bd", ["update", beadId, "--remove-label", "stage:implement", "--add-label", "stage:review", "--add-label", "product-decision-required"]);
                break;
            case "pool-implement-gated":
            {
                var startedFile = ResolveStartedMarkerPath(beadId);
                var releaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RELEASE_FILE")
                    ?? throw new InvalidOperationException("Pool fixture release-file path is missing.");
                var releaseDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(releaseDirectory))
                    releaseFile = Path.Combine(releaseDirectory, beadId + ".release");
                var worktreeMarker = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_FILE");
                var worktreeMarkerDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(worktreeMarkerDirectory))
                    worktreeMarker = Path.Combine(worktreeMarkerDirectory, beadId + ".path");
                var liveMarker = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_LIVE_MARKER");
                if (!string.IsNullOrWhiteSpace(liveMarker))
                {
                    Console.WriteLine(liveMarker);
                    Console.Out.Flush();
                }
                if (!string.IsNullOrWhiteSpace(worktreeMarker))
                {
                    File.WriteAllText(worktreeMarker, Environment.CurrentDirectory, new UTF8Encoding(false));
                    var retainedWorkFile = Path.Combine(Environment.CurrentDirectory, "force-stop-retained.txt");
                    File.WriteAllText(retainedWorkFile,
                        "uncommitted work must survive force stop\n", new UTF8Encoding(false));
                    if (Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_COMMIT_WORKTREE_MARKER") == "true")
                    {
                        Run("git", ["add", "force-stop-retained.txt"]);
                        Run("git", ["commit", "-m", "fixture retained work seed"]);
                        Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                    }
                }
                var descendantPidFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_DESCENDANT_PID_FILE");
                var descendantPidDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_DESCENDANT_PID_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(descendantPidDirectory))
                    descendantPidFile = Path.Combine(descendantPidDirectory, beadId + ".pid");
                if (!string.IsNullOrWhiteSpace(descendantPidFile))
                {
                    var fixtureAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION");
                    Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", null);
                    try { SpawnProcess("hold-identity", descendantPidFile); }
                    finally { Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", fixtureAction); }
                    WaitForFile(descendantPidFile);
                }
                WriteStartedMarker(startedFile, beadId);
                var readyDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_READY_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(readyDirectory))
                    File.WriteAllText(Path.Combine(readyDirectory, beadId + ".ready"), "waiting for release", new UTF8Encoding(false));
                WaitForFile(releaseFile, TimeSpan.FromMinutes(2));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            }
            case "pool-implement-fail-gated":
            {
                var startedFile = ResolveStartedMarkerPath(beadId);
                var releaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RELEASE_FILE")
                    ?? throw new InvalidOperationException("Pool fixture release-file path is missing.");
                WriteStartedMarker(startedFile, beadId);
                WaitForFile(releaseFile, TimeSpan.FromMinutes(2));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "unfinished-after-stop.txt"),
                    "preserve this failed current turn\n", new UTF8Encoding(false));
                return 23;
            }
            case "pool-implement-restart-observe":
            {
                var startedFile = ResolveStartedMarkerPath(beadId);
                var markerDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_DIRECTORY")
                    ?? throw new InvalidOperationException("Restart observer marker directory is missing.");
                var retainedFile = Path.Combine(Environment.CurrentDirectory, "force-stop-retained.txt");
                File.WriteAllText(Path.Combine(markerDirectory, beadId + ".restart"),
                    $"{Environment.CurrentDirectory}|retained={File.Exists(retainedFile)}", new UTF8Encoding(false));
                WriteStartedMarker(startedFile, beadId);
                var releaseDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY")
                    ?? throw new InvalidOperationException("Restart observer release directory is missing.");
                WaitForFile(Path.Combine(releaseDirectory, beadId + ".release"), TimeSpan.FromMinutes(2));
                break;
            }
            case "slot-implement-repair":
                File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "delivery-repair.txt"), "repair committed\n");
                Run("git", ["add", "delivery-repair.txt"]);
                Run("git", ["commit", "-m", "slot fixture repair"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-review":
                Run("bd", ["comments", "add", beadId, "Reviewer evidence: no blocking findings remain; approval is supported."]);
                break;
            case var reviewRejection when action.StartsWith("slot-review-reject-", StringComparison.Ordinal):
            {
                var cycle = int.Parse(reviewRejection["slot-review-reject-".Length..], System.Globalization.CultureInfo.InvariantCulture);
                Run("bd", ["comments", "add", beadId,
                    $"Blocking finding: reviewer cycle {cycle}; the serialized response is malformed; correct it and add a reproducible regression check."]);
                break;
            }
            case "slot-qa":
                Run("bd", ["comments", "add", beadId, "QA evidence: required checks passed and observed behavior matches acceptance criteria."]);
                break;
            case var qaRejection when action.StartsWith("slot-qa-reject-", StringComparison.Ordinal):
            {
                var cycle = int.Parse(qaRejection["slot-qa-reject-".Length..], System.Globalization.CultureInfo.InvariantCulture);
                Run("bd", ["comments", "add", beadId,
                    $"QA failure: cycle {cycle}; POST /fixture returned 500; expected 200. Reproduce with the fixture command and fix the handler."]);
                break;
            }
            case "plan-challenge":
                UpdateLabels(beadId, "stage:plan", "stage:plan-challenge");
                break;
            case "plan-commit":
                UpdateLabels(beadId, "stage:plan", "stage:plan-challenge");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "challenge-commit":
                UpdateLabels(beadId, "stage:plan-challenge", "stage:plan-approved");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "challenge":
                UpdateLabels(beadId, "stage:plan-challenge", "stage:plan-approved");
                break;
            case "planned-materialization":
            case "planned-materialization-nonzero":
            case "partial-materialization":
            case "planned-without-link":
            case "planned-stolen-owner":
            {
                using var created = JsonDocument.Parse(Run("bd", ["create", "--title", "Fixture plan bead", "--type", "task", "--json"]));
                var childId = created.RootElement.GetProperty("id").GetString()!;
                if (action != "planned-without-link")
                    Run("bd", ["dep", "add", childId, beadId, "--type", "discovered-from"]);
                if (action == "partial-materialization") return 19;
                UpdateLabels(beadId, "stage:plan-approved", "planned");
                if (action == "planned-materialization-nonzero") return 19;
                if (action == "planned-stolen-owner") Run("bd", ["update", beadId, "--assignee", "other-owner"]);
                break;
            }
            case "review":
                Run("git", ["commit", "--allow-empty", "-m", "fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "review-nonzero":
                Console.WriteLine("fixture returned a nonzero exit after a legal Bead transition");
                return 19;
            case "nonzero-dirty":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "README.md"), "retry must restore tracked content\n");
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "retry-untracked.txt"), "retry must remove this\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "keep.bin"), "ignored sentinel\n");
                Console.WriteLine("fixture returned nonzero after creating retry cleanup sentinels");
                return 19;
            case "nonzero-retain":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "attempt-untracked.txt"), "retain failed attempt evidence\n");
                Console.WriteLine("fixture returned nonzero while inherited work was present");
                return 19;
            case "resume-inherited":
                Run("git", ["add", "-A"]);
                Run("git", ["commit", "-m", "resume inherited retained work"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "review-dirty":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fixture-dirty.txt"), "dirty\n");
                break;
            case "review-commit":
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "review-qa-commit":
                UpdateLabels(beadId, "stage:review", "stage:qa");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "review-qa":
                UpdateLabels(beadId, "stage:review", "stage:qa");
                break;
            case "qa-merge-commit":
                UpdateLabels(beadId, "stage:qa", "stage:merge");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "qa-merge":
                UpdateLabels(beadId, "stage:qa", "stage:merge");
                break;
            case "reject-without-increment":
                UpdateLabels(beadId, "stage:review", "stage:implement");
                break;
            case "reject-with-increment":
                Run("bd", ["update", beadId, "--remove-label", "stage:review", "--add-label", "stage:implement", "--set-metadata", "repair_cycles=1"]);
                break;
            case "noop":
                break;
            case "timeout":
                Console.WriteLine("fixture waiting for executor timeout");
                Console.Out.Flush();
                while (true) Thread.Sleep(1000);
            case "close":
                Run("bd", ["close", beadId]);
                break;
            case "close-commit":
                Run("git", ["commit", "--allow-empty", "-m", "fixture Merger commit"]);
                Run("bd", ["close", beadId]);
                break;
            case "merger-verified-merge":
            {
                var pilotMerger = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION") == "manual-delivery-pilot";
                var diagnosticsEnabled = pilotMerger && ManualPilotMergerDiagnosticsEnabled();
                if (diagnosticsEnabled)
                    WriteManualPilotMergerDiagnostic(beadId, "invocation", "started", null, null, null, null);
                var pilotGateBead = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_GATE_BEAD");
                if (string.Equals(beadId, pilotGateBead, StringComparison.Ordinal))
                {
                    var pilotGateDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_GATE_DIRECTORY")
                        ?? throw new InvalidOperationException("The manual pilot Merger gate directory is missing.");
                    RunManualPilotMergerStep(beadId, "initial-merger-gate", null, () =>
                    {
                        Directory.CreateDirectory(pilotGateDirectory);
                        File.WriteAllText(Path.Combine(pilotGateDirectory, beadId + ".merger.started"),
                            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false));
                        WaitForFile(Path.Combine(pilotGateDirectory, beadId + ".merger.release"), TimeSpan.FromMinutes(5));
                    });
                }
                var mergerStartedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_MERGER_STARTED_FILE");
                if (!string.IsNullOrWhiteSpace(mergerStartedFile))
                {
                    var mergerReleaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_MERGER_RELEASE_FILE")
                        ?? throw new InvalidOperationException("Merger release file is missing.");
                    RunManualPilotMergerStep(beadId, "secondary-merger-gate", null, () =>
                    {
                        File.WriteAllText(mergerStartedFile, beadId, new UTF8Encoding(false));
                        WaitForFile(mergerReleaseFile, TimeSpan.FromMinutes(2));
                    });
                }
                var defaultBranch = Environment.GetEnvironmentVariable("NIGHTWEAVER_DEFAULT_BRANCH") ?? "main";
                const string pullRequest = "https://github.com/nightweaver-fixture/target/pull/123";
                var branch = pilotMerger
                    ? RunManualPilotMergerStep(beadId, "read-owned-branch", null,
                        () => Run("git", ["branch", "--show-current"]).Trim())
                    : Run("git", ["branch", "--show-current"]).Trim();
                var head = pilotMerger
                    ? RunManualPilotMergerStep(beadId, "read-initial-head", null,
                        () => Run("git", ["rev-parse", "HEAD"]).Trim())
                    : string.Empty;
                if (pilotMerger)
                {
                    ManualPilotMergerChange.SynchronizeWithDefaultBranch(Environment.CurrentDirectory, defaultBranch,
                        (workingDirectory, arguments) => RunManualPilotMergerStep(beadId,
                            arguments[0] == "fetch" ? "fetch-default-before-rebase"
                                : arguments[0] == "rebase" ? "rebase-default" : "read-rebase-state",
                            head, () => RunGitInDirectory(workingDirectory, arguments)));
                }
                if (pilotMerger)
                {
                    RunManualPilotMergerStep(beadId, "fetch-default-for-current-head-check", head,
                        () => Run("git", ["fetch", "origin", defaultBranch]));
                    head = RunManualPilotMergerStep(beadId, "read-current-head", head,
                        () => Run("git", ["rev-parse", "HEAD"]).Trim());
                }
                else
                {
                    // Preserve the non-pilot fixture command sequence exactly.
                    Run("git", ["fetch", "origin", defaultBranch]);
                    head = Run("git", ["rev-parse", "HEAD"]).Trim();
                }
                var defaultHead = pilotMerger
                    ? RunManualPilotMergerStep(beadId, "read-default-head", head,
                        () => Run("git", ["rev-parse", $"origin/{defaultBranch}"]).Trim())
                    : Run("git", ["rev-parse", $"origin/{defaultBranch}"]).Trim();
                if (pilotMerger)
                {
                    RunManualPilotMergerStep(beadId, "verify-default-ancestor", head, () =>
                    {
                        if (!IsAncestor(defaultHead, head))
                            throw new InvalidOperationException("Fixture branch is not synchronized with its configured default branch.");
                    });
                }
                else if (!IsAncestor(defaultHead, head))
                    throw new InvalidOperationException("Fixture branch is not synchronized with its configured default branch.");

                var mergerFile = pilotMerger ? $"merger-verified-{beadId}.txt" : "merger-verified.txt";
                var mergerContent = pilotMerger
                    ? $"fixture merge after synchronized current-head checks for {beadId}\n"
                    : "fixture merge after synchronized current-head checks\n";
                if (pilotMerger)
                {
                    var preparedChange = RunManualPilotMergerStep(beadId, "validate-or-create-expected-change", head, () =>
                        ManualPilotMergerChange.EnsureExpectedCommit(beadId, branch, Environment.CurrentDirectory,
                            (workingDirectory, arguments) => RunManualPilotMergerStep(beadId,
                                "git-" + string.Join('-', arguments), head,
                                () => RunGitInDirectory(workingDirectory, arguments))));
                    head = preparedChange.HeadSha;
                    WriteManualPilotMergerDiagnostic(beadId,
                        preparedChange.Reused ? "expected-commit-reused" : "expected-commit-created",
                        "completed", head, null, null, null);
                    if (preparedChange.Reused)
                    {
                        var previous = RunManualPilotMergerStep(beadId, "read-existing-pull-request", head,
                            () => ReadPullRequest(pullRequest));
                        if (previous.GetProperty("state").GetString() == "MERGED")
                        {
                            using var checks = JsonDocument.Parse(RunManualPilotMergerStep(beadId,
                                "read-existing-required-checks", head,
                                () => Run("gh", ["pr", "checks", pullRequest, "--required", "--json", "name,state,bucket"])));
                            var confirmation = RunManualPilotMergerStep(beadId, "confirm-existing-merge", head,
                                () => ReadPullRequest(pullRequest));
                            var completedCommit = ManualPilotMergerRecovery.VerifyMerged(
                                confirmation, checks.RootElement, head, branch, defaultBranch);
                            PublishVerifiedMergerEvidence(beadId, pullRequest, head, completedCommit, diagnosticsEnabled);
                            break;
                        }
                    }
                }
                else
                {
                    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, mergerFile), mergerContent, new UTF8Encoding(false));
                    Run("git", ["add", mergerFile]);
                    Run("git", ["commit", "-m", "fixture verified Merger change"]);
                    head = Run("git", ["rev-parse", "HEAD"]).Trim();
                }
                if (pilotMerger)
                    RunManualPilotMergerStep(beadId, "push-checked-branch", head,
                        () => Run("git", ["push", "--force-with-lease", "origin", $"HEAD:refs/heads/{branch}"]));
                else
                    Run("git", ["push", "--force-with-lease", "origin", $"HEAD:refs/heads/{branch}"]);

                var interruptBead = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_INTERRUPT_BEAD");
                var interruptDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_INTERRUPT_DIRECTORY");
                var interruptMarker = !string.IsNullOrWhiteSpace(interruptDirectory)
                    ? Path.Combine(interruptDirectory, beadId + ".merger.interrupted-once") : null;
                if (pilotMerger && string.Equals(beadId, interruptBead, StringComparison.Ordinal)
                    && interruptMarker is not null && !File.Exists(interruptMarker))
                {
                    RunManualPilotMergerStep(beadId, "controlled-post-push-interruption", head, () =>
                    {
                        WriteFlushedText(interruptMarker, $"{head}\n{DateTimeOffset.UtcNow:O}\n");
                        throw new InvalidOperationException("Controlled one-shot interruption after expected commit and branch push, before PR checks.");
                    });
                }

                var retryGateBead = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_RETRY_GATE_BEAD");
                var retryGateDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_RETRY_GATE_DIRECTORY");
                if (pilotMerger && string.Equals(beadId, retryGateBead, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(interruptMarker) && File.Exists(interruptMarker))
                {
                    if (string.IsNullOrWhiteSpace(retryGateDirectory))
                        throw new InvalidOperationException("The manual-pilot Merger retry gate directory is missing.");
                    RunManualPilotMergerStep(beadId, "retry-before-current-head-checks-gate", head, () =>
                    {
                        Directory.CreateDirectory(retryGateDirectory);
                        WriteFlushedText(Path.Combine(retryGateDirectory, beadId + ".merger.retry.started"),
                            $"{Environment.ProcessId}|{head}\n");
                        WaitForFile(Path.Combine(retryGateDirectory, beadId + ".merger.retry.release"), TimeSpan.FromMinutes(2));
                    });
                }

                var beforeChecks = RunManualPilotMergerStep(beadId, "read-pull-request-before-checks", head,
                    () => ReadPullRequest(pullRequest));
                if (beforeChecks.GetProperty("state").GetString() != "OPEN"
                    || beforeChecks.GetProperty("headRefOid").GetString() != head)
                    throw new InvalidOperationException("Fixture pull request did not identify the synchronized current HEAD.");
                RunManualPilotMergerStep(beadId, "run-current-head-required-checks", head,
                    () => Run("gh", ["pr", "checks", pullRequest, "--watch"]));
                var afterChecks = RunManualPilotMergerStep(beadId, "read-pull-request-after-checks", head,
                    () => ReadPullRequest(pullRequest));
                if (afterChecks.GetProperty("headRefOid").GetString() != head)
                    throw new InvalidOperationException("Fixture pull request head changed while checks were running.");
                RunManualPilotMergerStep(beadId, "merge-current-head-pull-request", head,
                    () => Run("gh", ["pr", "merge", pullRequest, "--squash", "--delete-branch"]));
                var merged = RunManualPilotMergerStep(beadId, "verify-merged-pull-request", head,
                    () => ReadPullRequest(pullRequest));
                if (merged.GetProperty("state").GetString() != "MERGED")
                    throw new InvalidOperationException("Fixture pull request was not confirmed merged.");

                var mergeCommit = merged.GetProperty("mergeCommit").GetProperty("oid").GetString();
                PublishVerifiedMergerEvidence(beadId, pullRequest, head, mergeCommit, diagnosticsEnabled);
                break;
            }
            case "merger-reject-checks":
            {
                var defaultBranch = Environment.GetEnvironmentVariable("NIGHTWEAVER_DEFAULT_BRANCH") ?? "main";
                var branch = Run("git", ["branch", "--show-current"]).Trim();
                Run("git", ["fetch", "origin", defaultBranch]);
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "merger-rejected.txt"),
                    "fixture change rejected by required CI checks\n", new UTF8Encoding(false));
                Run("git", ["add", "merger-rejected.txt"]);
                Run("git", ["commit", "-m", "fixture Merger change rejected by checks"]);
                var head = Run("git", ["rev-parse", "HEAD"]).Trim();
                Run("git", ["push", "--force-with-lease", "origin", $"HEAD:refs/heads/{branch}"]);
                var pullRequest = "https://github.com/nightweaver-fixture/target/pull/123";
                var beforeChecks = ReadPullRequest(pullRequest);
                if (beforeChecks.GetProperty("state").GetString() != "OPEN"
                    || beforeChecks.GetProperty("headRefOid").GetString() != head)
                    throw new InvalidOperationException("Fixture pull request did not identify the synchronized current HEAD before rejection.");

                var checkSource = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CHECK_SOURCE") ?? "remote";
                string subject;
                string summary;
                string details;
                int? localExitCode = null;
                if (checkSource == "local")
                {
                    const string command = "dotnet build";
                    var artifactsPath = Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "local-check-artifacts");
                    var build = RunCapturingExitCode("dotnet", ["build", "LocalCheckFixture.csproj", "--nologo", "--artifacts-path", artifactsPath]);
                    if (build.ExitCode == 0)
                        throw new InvalidOperationException("The controlled local build was expected to fail.");
                    var output = build.StandardOutput + Environment.NewLine + build.StandardError;
                    details = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(line => line.Contains("error ", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("The controlled local build returned nonzero without a compiler diagnostic.");
                    subject = command;
                    localExitCode = build.ExitCode;
                    summary = $"Local dotnet build failed for synchronized HEAD {head} with exit code {build.ExitCode}.";
                    var localEvidenceMetadata = JsonSerializer.Serialize(new { command, checked_head = head, exit_code = build.ExitCode });
                    Console.WriteLine($"NIGHTWEAVER_LOCAL_CHECK_V1 {localEvidenceMetadata}");
                    Console.WriteLine(output);
                    Console.WriteLine(summary);
                    Console.WriteLine(details);
                    Console.WriteLine("END_NIGHTWEAVER_LOCAL_CHECK_V1");
                }
                else if (checkSource == "remote")
                {
                    try
                    {
                        Run("gh", ["pr", "checks", pullRequest, "--watch"]);
                        throw new InvalidOperationException("The controlled fixture check was expected to fail.");
                    }
                    catch (InvalidOperationException exception) when (exception.Message.Contains("exited 1", StringComparison.Ordinal))
                    {
                        subject = "fixture checks";
                        summary = "Required CI rejected the synchronized Merger branch.";
                        details = $"Required check '{subject}' failed for synchronized HEAD {head}; inspect merger-rejected.txt and fix the reported behavior.";
                    }
                }
                else throw new InvalidOperationException($"Unsupported fixture check source '{checkSource}'.");

                AbortRebaseIfActive();
                var findingFields = new Dictionary<string, object?>
                {
                    ["schema"] = "nightweaver-merger-rejection-v1",
                    ["kind"] = "checks",
                    ["pull_request"] = pullRequest,
                    ["checked_head"] = head,
                    ["subject"] = subject,
                    ["summary"] = summary,
                    ["details"] = details,
                    ["check_source"] = checkSource
                };
                if (localExitCode is not null) findingFields["local_exit_code"] = localExitCode.Value;
                Run("bd", ["comments", "add", beadId, JsonSerializer.Serialize(findingFields)]);
                if (checkSource == "remote") Console.WriteLine($"Merger rejection evidence: {subject}; {details}");
                var persisted = ReadFixtureBead(beadId);
                var metadata = persisted.GetProperty("metadata");
                var cycles = metadata.TryGetProperty("repair_cycles", out var count) ? count.GetInt32() : 0;
                if (cycles != 0)
                    throw new InvalidOperationException($"Expected repair_cycles=0 before rejection, observed {cycles}.");
                var unchanged = ReadFixtureBead(beadId);
                var labels = unchanged.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ToArray();
                var state = unchanged.GetProperty("metadata");
                var unchangedCycles = state.TryGetProperty("repair_cycles", out var unchangedCounter)
                    ? unchangedCounter.GetInt32()
                    : 0;
                if (unchanged.GetProperty("status").GetString() != "in_progress"
                    || unchanged.GetProperty("assignee").GetString() != Environment.GetEnvironmentVariable("BD_ACTOR")
                    || !labels.Contains("stage:merge", StringComparer.Ordinal)
                    || labels.Contains("stage:implement", StringComparer.Ordinal)
                    || !labels.Contains("keep:label", StringComparer.Ordinal)
                    || unchangedCycles != 0
                    || state.GetProperty("retained").GetString() != "fixture")
                    throw new InvalidOperationException("Merger agent changed lifecycle state while recording rejection evidence.");
                break;
            }
            case "merger-resumed-implement":
            {
                var bead = ReadFixtureBead(beadId);
                var labels = bead.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ToArray();
                var metadata = bead.GetProperty("metadata");
                var expectedCycles = int.Parse(Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_EXPECTED_REPAIR_CYCLES") ?? "1",
                    System.Globalization.CultureInfo.InvariantCulture);
                var expectedFinding = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_EXPECTED_FINDING")
                    ?? throw new InvalidOperationException("Expected Merger finding is missing.");
                var comments = JsonDocument.Parse(Run("bd", ["comments", beadId, "--json"]));
                var foundFinding = comments.RootElement.EnumerateArray().Any(comment =>
                    comment.GetProperty("text").GetString()?.Contains(expectedFinding, StringComparison.Ordinal) == true);
                var warmSentinel = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_WARM_SENTINEL")
                    ?? throw new InvalidOperationException("Warm artifact path is missing.");
                var tmpSentinel = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_TMP_SENTINEL")
                    ?? throw new InvalidOperationException("Temporary artifact path is missing.");
                if (bead.GetProperty("status").GetString() != "in_progress"
                    || !string.Equals(bead.GetProperty("assignee").GetString(), Environment.GetEnvironmentVariable("BD_ACTOR"), StringComparison.Ordinal)
                    || !labels.Contains("stage:implement", StringComparer.Ordinal)
                    || !labels.Contains("keep:label", StringComparer.Ordinal)
                    || metadata.GetProperty("repair_cycles").GetInt32() != expectedCycles
                    || metadata.GetProperty("retained").GetString() != "fixture"
                    || !foundFinding || !File.Exists(warmSentinel) || !File.Exists(tmpSentinel))
                    throw new InvalidOperationException("Delivery Implementer could not observe the rejection finding, repair budget, or retained worktree/tmp resources.");

                var evidencePath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RESUME_EVIDENCE")
                    ?? throw new InvalidOperationException("Delivery resume evidence path is missing.");
                File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
                {
                    beadId,
                    role = "implementer",
                    repairCycles = expectedCycles,
                    finding = expectedFinding,
                    warmArtifactPresent = true,
                    tmpArtifactPresent = true
                }), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-repair.txt"),
                    "Delivery resumed from the verified Merger rejection.\n", new UTF8Encoding(false));
                if (Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_FIX_LOCAL_BUILD") == "true")
                {
                    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "BrokenFixture.cs"),
                        "public sealed class LocalCheckFixture { public int Repaired => 1; }\n", new UTF8Encoding(false));
                    Run("dotnet", ["build", "LocalCheckFixture.csproj", "--nologo", "--artifacts-path",
                        Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "local-check-artifacts")]);
                    Run("git", ["add", "BrokenFixture.cs"]);
                }
                Run("git", ["add", "delivery-repair.txt"]);
                Run("git", ["commit", "-m", "fixture Delivery repair after Merger rejection"]);
                var branch = Run("git", ["branch", "--show-current"]).Trim();
                Run("git", ["push", "origin", $"HEAD:refs/heads/{branch}"]);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown fixture Agent action '{action}'.");
        }

        Console.WriteLine($"fixture action completed: {action}");
        return 0;
    }

    private static void UpdateLabels(string beadId, string oldLabel, string newLabel) =>
        Run("bd", ["update", beadId, "--remove-label", oldLabel, "--add-label", newLabel]);

    private static int SendConsoleBreak(string processIdText)
    {
        if (!OperatingSystem.IsWindows() || !uint.TryParse(processIdText, out var processId)) return 68;
        // A console helper started under a test host may inherit its own console despite
        // CREATE_NO_WINDOW. Detach first so it can attach to the isolated apphost console.
        _ = FreeConsole();
        if (!AttachConsole(processId)) return Marshal.GetLastWin32Error();
        try
        {
            return GenerateConsoleCtrlEvent(ControlBreakEvent, processId) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { _ = FreeConsole(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    private static bool IsAncestor(string ancestor, string descendant)
    {
        var result = RunAllowFailure("git", ["merge-base", "--is-ancestor", ancestor, descendant]);
        return result.ExitCode == 0;
    }

    private static void RunManualPilotMergerStep(string beadId, string phase, string? currentHead, Action action) =>
        _ = RunManualPilotMergerStep(beadId, phase, currentHead, () =>
        {
            action();
            return true;
        });

    private static T RunManualPilotMergerStep<T>(string beadId, string phase, string? currentHead, Func<T> action)
    {
        if (!ManualPilotMergerDiagnosticsEnabled()) return action();

        var stopwatch = Stopwatch.StartNew();
        WriteManualPilotMergerDiagnostic(beadId, phase, "started", currentHead, null, null, null);
        try
        {
            var result = action();
            stopwatch.Stop();
            WriteManualPilotMergerDiagnostic(beadId, phase, "completed", currentHead,
                stopwatch.ElapsedMilliseconds, null, null);
            return result;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            WriteManualPilotMergerDiagnostic(beadId, phase, "failed", currentHead,
                stopwatch.ElapsedMilliseconds, exception.ToString(), TryReadExitStatus(exception.Message));
            throw;
        }
    }

    private static void WriteManualPilotMergerDiagnostic(string beadId, string phase, string status,
        string? currentHead, long? elapsedMilliseconds, string? failure, int? childCommandExitStatus)
    {
        if (!ManualPilotMergerDiagnosticsEnabled()) return;
        var path = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_DIAGNOSTICS_PATH");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Manual-pilot Merger diagnostics path is missing.");

        var process = Process.GetCurrentProcess();
        var processStartedAt = process.StartTime.ToUniversalTime();
        var timeoutSeconds = ReadConfiguredRoleTimeoutSeconds();
        var record = JsonSerializer.Serialize(new
        {
            schema = "manual-pilot-merger-step-v1",
            bead_id = beadId,
            phase,
            status,
            process_id = process.Id,
            process_started_at = processStartedAt,
            invocation_identity = $"{process.Id}-{processStartedAt.Ticks}",
            configured_role_timeout_seconds = timeoutSeconds,
            observed_at = DateTimeOffset.UtcNow,
            elapsed_ms = elapsedMilliseconds,
            current_head = currentHead,
            child_command_exit_status = childCommandExitStatus,
            failure
        });
        var bytes = Encoding.UTF8.GetBytes(record + Environment.NewLine);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool ManualPilotMergerDiagnosticsEnabled() =>
        Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION") == "manual-delivery-pilot"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_MERGER_DIAGNOSTICS_PATH"));

    private static int? ReadConfiguredRoleTimeoutSeconds()
    {
        var configPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_CONFIG");
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var minutes = document.RootElement.GetProperty("agent_defaults").GetProperty("timeout_minutes").GetInt32();
        return checked(minutes * 60);
    }

    private static int? TryReadExitStatus(string message)
    {
        var marker = " exited ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = message.IndexOfAny([':', '.', ' ', '\r', '\n'], start);
        var value = end < 0 ? message[start..] : message[start..end];
        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var exitStatus) ? exitStatus : null;
    }

    private static string RunGitInDirectory(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start 'git'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {stderrTask.Result}{stdoutTask.Result}");
        return stdoutTask.Result;
    }

    private static void PublishVerifiedMergerEvidence(string beadId, string pullRequest, string head,
        string? mergeCommit, bool diagnosticsEnabled)
    {
        RunManualPilotMergerStep(beadId, "publish-verified-merge-evidence", head,
            () => Run("bd", ["comments", "add", beadId,
                $"nightweaver-merge-v1 pull_request={pullRequest} checked_head={head} required_checks=passed merge_method=squash merge_commit={mergeCommit}"]));
        if (diagnosticsEnabled)
            WriteManualPilotMergerDiagnostic(beadId, "invocation", "completed", head, null, null, null);
    }

    private static void WriteAtomicText(string path, string value)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, value, new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void WriteFlushedText(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var bytes = new UTF8Encoding(false).GetBytes(value);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static JsonElement ReadPullRequest(string pullRequest)
    {
        var json = Run("gh", ["pr", "view", pullRequest, "--json", "state,baseRefName,headRefName,headRefOid,url"]);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunAllowFailure(
        string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string ResolveStartedMarkerPath(string beadId)
    {
        var startedDirectory = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_STARTED_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(startedDirectory))
            return Path.Combine(startedDirectory, beadId + ".started");

        return Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_STARTED_FILE")
            ?? throw new InvalidOperationException("Pool fixture started-file path is missing.");
    }

    private static void WriteStartedMarker(string path, string beadId) =>
        File.WriteAllText(path, beadId, new UTF8Encoding(false));

    private static void AppendStageLaunchRecord(string path, string beadId) =>
        ManualPilotEvidenceLog.AppendLine(path,
            $"{beadId}|{Environment.ProcessId}|{Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks}");

    private static void RejectToImplement(string beadId, string stage, int repairCycles, string finding)
    {
        Run("bd", ["comments", "add", beadId, finding]);
        Run("bd", ["update", beadId, "--remove-label", stage, "--add-label", "stage:implement",
            "--set-metadata", $"repair_cycles={repairCycles}"]);
    }

    private static JsonElement ReadFixtureBead(string beadId)
    {
        using var document = JsonDocument.Parse(Run("bd", ["show", beadId, "--json"]));
        var root = document.RootElement;
        return (root.ValueKind == JsonValueKind.Array ? root[0] : root).Clone();
    }

    private static int ReadPilotRepairLimit()
    {
        var configPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PILOT_CONFIG")
            ?? throw new InvalidOperationException("The manual pilot Project configuration path is missing.");
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        return config.RootElement.GetProperty("limits").GetProperty("max_repair_cycles").GetInt32();
    }

    private static void AbortRebaseIfActive()
    {
        foreach (var state in new[] { "rebase-merge", "rebase-apply" })
        {
            var path = Run("git", ["rev-parse", "--git-path", state]).Trim();
            if (Directory.Exists(path) || File.Exists(path))
                Run("git", ["rebase", "--abort"]);
        }
    }

    private static string Run(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}: {stderrTask.Result}{stdoutTask.Result}");
        return stdoutTask.Result;
    }

    private static FixtureProcessResult RunCapturingExitCode(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return new(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static void SpawnProcess(string mode, params string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture process path is unavailable.");
        var fixtureDll = Assembly.GetExecutingAssembly().Location;
        var launchArguments = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { fixtureDll, mode }.Concat(arguments)
            : new[] { mode }.Concat(arguments);
        var commandLineText = string.Join(' ', new[] { executable }.Concat(launchArguments).Select(QuoteArgument));
        var commandLine = Marshal.StringToHGlobalUni(commandLineText);
        try
        {
            var startup = new StartupInfo
            {
                Cb = (uint)Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardInput = GetStdHandle(-10),
                StandardOutput = GetStdHandle(-11),
                StandardError = GetStdHandle(-12)
            };
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                    CreateNoWindow, IntPtr.Zero, Environment.CurrentDirectory, ref startup, out var info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create descendant fixture.");
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var result = new StringBuilder().Append('"');
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void WaitForFile(string path)
        => WaitForFile(path, TimeSpan.FromSeconds(15));

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed > timeout) throw new TimeoutException($"Fixture handshake timed out: {path}");
            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Cb;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
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
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
