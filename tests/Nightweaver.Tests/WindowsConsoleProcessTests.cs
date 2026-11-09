using System.Text.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using Nightweaver.Execution;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

public sealed class WindowsConsoleProcessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] StdioExpectedArguments = ["argument with spaces", "quoted\"argument"];
    private static readonly string[] NoLogExpectedArguments = ["no-log argument with spaces", "quoted\"no-log"];

    [Fact]
    public async Task ConsoleHelperDoesNotRetainAnUnrelatedWriterInALiveChild()
    {
        Assert.True(OperatingSystem.IsWindows(), "This probe requires Windows anonymous pipes and CreateProcess inheritance.");

        var root = Path.Combine(Path.GetTempPath(), $"nightweaver-console-inheritance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "probe-report.json");
        try
        {
            var result = await RunCoordinatorAsync(root);
            Assert.True(File.Exists(reportPath),
                $"The probe coordinator did not retain its report. exit={result.ExitCode}; stdout={result.StandardOutput}; stderr={result.StandardError}; artifacts={root}");
            var report = JsonSerializer.Deserialize<NativeHandleProbeReport>(File.ReadAllText(reportPath),
                JsonOptions);
            Assert.NotNull(report);
            Assert.Null(report.Error);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(result.Nonce, report.Nonce);
            Assert.True(result.JobEmptyConfirmed);

            var openWriter = Assert.IsType<NativePipePeek>(report.NoChildWriterOpen);
            Assert.True(openWriter.Success, Describe("no-child open writer", openWriter, root));
            Assert.Equal(0u, openWriter.TotalBytesAvailable);

            var closedWriter = Assert.IsType<NativePipePeek>(report.NoChildWriterClosed);
            Assert.False(closedWriter.Success, Describe("no-child closed writer", closedWriter, root));
            Assert.Equal(109, closedWriter.Error);

            var nonInheritable = Assert.IsType<NativeHandleChildObservation>(report.NonInheritableChild);
            Assert.False(nonInheritable.WriterWasInheritable);
            AssertChildIdentity(nonInheritable.Ready, result.Nonce, "noninheritable child", root);
            Assert.True(nonInheritable.WriterClosedBeforePeek, DescribeChild(nonInheritable, root));
            Assert.True(nonInheritable.SameChildAliveBeforePeek, DescribeChild(nonInheritable, root));
            Assert.True(nonInheritable.SameChildAliveAfterPeek, DescribeChild(nonInheritable, root));
            AssertPeekIsBrokenPipe(nonInheritable.WhileAlive, "noninheritable child while alive", root);
            Assert.True(nonInheritable.ChildExited, DescribeChild(nonInheritable, root));
            Assert.Equal(0, nonInheritable.ExitCode);
            AssertPeekIsBrokenPipe(nonInheritable.AfterExit, "noninheritable child after exit", root);
            Assert.Null(nonInheritable.CleanupError);
            Assert.True(nonInheritable.ChildGoneAfterCleanup, DescribeChild(nonInheritable, root));

            var inherited = Assert.IsType<NativeHandleChildObservation>(report.InheritableChild);
            Assert.True(inherited.WriterWasInheritable);
            AssertChildIdentity(inherited.Ready, result.Nonce, "inheritable child", root);
            Assert.True(inherited.WriterClosedBeforePeek, DescribeChild(inherited, root));
            Assert.True(inherited.SameChildAliveBeforePeek, DescribeChild(inherited, root));
            Assert.True(inherited.SameChildAliveAfterPeek, DescribeChild(inherited, root));
            Assert.True(inherited.ChildExited, DescribeChild(inherited, root));
            Assert.Equal(0, inherited.ExitCode);
            AssertPeekIsBrokenPipe(inherited.AfterExit, "inheritable child after exit", root);
            Assert.Null(inherited.CleanupError);
            Assert.True(inherited.ChildGoneAfterCleanup, DescribeChild(inherited, root));

            var whileAlive = Assert.IsType<NativePipePeek>(inherited.WhileAlive);
            AssertPeekIsBrokenPipe(whileAlive, "inheritable child while alive", root);

            var evidenceDirectory = GetSuccessEvidenceDirectory(result.Nonce);
            Assert.False(Directory.Exists(evidenceDirectory), $"Evidence destination already exists: {evidenceDirectory}");
            Directory.CreateDirectory(evidenceDirectory);
            foreach (var path in Directory.EnumerateFiles(root))
                File.Copy(path, Path.Combine(evidenceDirectory, Path.GetFileName(path)));
            Assert.True(File.Exists(Path.Combine(evidenceDirectory, "probe-report.json")), evidenceDirectory);
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Preserve the report and helper logs on a RED or infrastructure failure.
            throw;
        }
    }

    [Fact]
    public async Task ProbeFailureRetainsPartialObservationsAndBothCleanupOutcomes()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), $"nightweaver-console-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var failedProbeRoot = Path.Combine(root, "probe");
        Directory.CreateDirectory(failedProbeRoot);
        var probeResult = await RunCoordinatorAsync(failedProbeRoot, "fail-after-peek");
        Assert.Equal(2, probeResult.ExitCode);
        Assert.True(probeResult.JobEmptyConfirmed);
        var report = JsonSerializer.Deserialize<NativeHandleProbeReport>(
            File.ReadAllText(Path.Combine(failedProbeRoot, "probe-report.json")), JsonOptions);
        Assert.NotNull(report);
        Assert.Contains("Injected probe infrastructure failure", report.Error);
        Assert.True(report.NoChildWriterOpen?.Success);
        Assert.False(report.NoChildWriterClosed?.Success);
        Assert.Equal(109, report.NoChildWriterClosed?.Error);
        var partial = Assert.IsType<NativeHandleChildObservation>(report.NonInheritableChild);
        Assert.True(partial.Ready?.ProcessId > 0, JsonSerializer.Serialize(partial));
        Assert.True(partial.SameChildAliveBeforePeek, JsonSerializer.Serialize(partial));
        Assert.True(partial.WriterClosedBeforePeek, JsonSerializer.Serialize(partial));
        Assert.True(partial.SameChildAliveAfterPeek, JsonSerializer.Serialize(partial));
        AssertPeekIsBrokenPipe(partial.WhileAlive, "failure-path live child", failedProbeRoot);
        Assert.Contains("release/wait", partial.CleanupError);
        Assert.True(partial.ChildExited, JsonSerializer.Serialize(partial));
        Assert.True(partial.ChildGoneAfterCleanup, JsonSerializer.Serialize(partial));

        var outerFailureRoot = Path.Combine(root, "outer-drain");
        Directory.CreateDirectory(outerFailureRoot);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunCoordinatorAsync(outerFailureRoot, injectDrainFailure: true));
        Assert.Contains("Injected coordinator stdout drain failure", failure.Message);
        var cleanup = JsonSerializer.Deserialize<OuterCoordinatorCleanupReport>(
            File.ReadAllText(Path.Combine(outerFailureRoot, "outer-cleanup.json")), JsonOptions);
        Assert.NotNull(cleanup);
        Assert.Contains("Injected coordinator stdout drain failure", cleanup.OriginalFailure);
        Assert.Null(cleanup.CleanupFailure);
        Assert.True(cleanup.JobEmptyConfirmed);
    }

    [Fact]
    public async Task ConsoleHelperPreservesStreamsArgumentsEnvironmentAndFailureCleanup()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), $"nightweaver-console-semantics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        const string input = "redirected stdin payload; EOF follows";
        var result = await RunCoordinatorAsync(root, "semantics", input);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.JobEmptyConfirmed);

        var report = JsonSerializer.Deserialize<NativeHelperSemanticsReport>(
            File.ReadAllText(Path.Combine(root, "semantics-report.json")), JsonOptions);
        Assert.NotNull(report);
        Assert.Null(report.Error);
        Assert.Equal(2, report.MissingExecutableError);
        Assert.Equal(3, report.InvalidLogError);
        Assert.True(report.MissingExecutableLogClosed);
        Assert.True(report.InvalidLogDestinationAbsent);

        Assert.Equal(0, report.StdioExitCode);
        Assert.True(report.StdioChildId > 0);
        Assert.True(report.StdioChildStartTicks > 0);
        Assert.True(report.StdioChildGone);
        Assert.True(report.StdioLogClosed);
        var stdio = Assert.IsType<NativeHelperStdioChildReport>(report.StdioChild);
        Assert.Equal(StdioExpectedArguments, stdio.Arguments);
        Assert.Equal(Path.Combine(root, "work directory with spaces"), stdio.WorkingDirectory);
        Assert.Equal("overridden value", stdio.OverrideValue);
        Assert.Null(stdio.RemovedValue);
        Assert.Equal(input, stdio.StandardInput);
        Assert.Contains($"stdout-sentinel:{result.Nonce}", report.MergedOutput);
        Assert.Contains($"stderr-sentinel:{result.Nonce}", report.MergedOutput);

        var zero = Assert.IsType<NativeHelperInputObservation>(report.ZeroInput);
        Assert.NotEqual(0, zero.RawHandle);
        Assert.True(zero.ConsoleModeSucceeded,
            "With the parent stdin set to zero, Windows supplied the child with the isolated console input handle.");
        var invalid = Assert.IsType<NativeHelperInputObservation>(report.InvalidInput);
        Assert.Equal(-1, invalid.RawHandle);
        Assert.False(invalid.ConsoleModeSucceeded);
        var nonInheritable = Assert.IsType<NativeHelperInputObservation>(report.NonInheritableInput);
        Assert.True(nonInheritable.HandleInformationSucceeded);
        Assert.NotNull(nonInheritable.HandleFlags);
        Assert.True((nonInheritable.HandleFlags.Value & 1) != 0,
            "The child should receive the helper-owned inheritable duplicate of the noninheritable parent input.");
        var console = Assert.IsType<NativeHelperInputObservation>(report.ConsoleInput);
        Assert.True(console.SourceConsoleModeSucceeded, JsonSerializer.Serialize(console));
        Assert.NotEqual(0, console.RawHandle);
        Assert.True(console.HandleInformationSucceeded, JsonSerializer.Serialize(console));

        Assert.Equal(0, report.NoLogExitCode);
        Assert.True(report.NoLogLogAbsent);
        var noLog = Assert.IsType<NativeHelperNoLogChildReport>(report.NoLogChild);
        Assert.Equal(NoLogExpectedArguments, noLog.Arguments);
        Assert.Equal(Path.Combine(root, "work directory with spaces"), noLog.WorkingDirectory);
        Assert.Equal("no-log override", noLog.OverrideValue);
        Assert.Null(noLog.RemovedValue);
    }

    private static async Task<CoordinatorResult> RunCoordinatorAsync(string root, string? scenario = null,
        string? inputText = null, bool injectDrainFailure = false)
    {
        var fixtureAssembly = typeof(FixtureMarker).Assembly.Location;
        var executable = Path.ChangeExtension(fixtureAssembly, ".exe");
        Assert.True(File.Exists(executable), $"The AgentInvocationFixture apphost is missing: {executable}");
        var nonce = Guid.NewGuid().ToString("N");
        var arguments = new List<string> { "--native-handle-probe", root, nonce };
        if (scenario is not null) arguments.Add(scenario);
        var coordinator = WindowsProcessLauncher.Launch(executable,
            arguments, Path.GetDirectoryName(executable)!,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        int exitCode = -1;
        var jobEmptyConfirmed = false;
        Exception? operationFailure = null;
        Exception? cleanupFailure = null;
        var standardOutput = string.Empty;
        var standardError = string.Empty;
        try
        {
            stdoutTask = ReadToEndAsync(coordinator.StandardOutput);
            stderrTask = ReadToEndAsync(coordinator.StandardError);
            if (inputText is not null)
                await coordinator.StandardInput.WriteAsync(Encoding.UTF8.GetBytes(inputText));
            coordinator.StandardInput.Dispose();
            exitCode = checked((int)await coordinator.WaitForRootExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken));
            if (injectDrainFailure)
                throw new InvalidOperationException("Injected coordinator stdout drain failure.");
            standardOutput = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            standardError = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            try
            {
                await coordinator.TerminateAndConfirmEmptyAsync(TimeSpan.FromSeconds(5));
                jobEmptyConfirmed = true;
            }
            catch (Exception exception) { cleanupFailure = CombineFailures(cleanupFailure, exception); }
            try { coordinator.Dispose(); }
            catch (Exception exception) { cleanupFailure = CombineFailures(cleanupFailure, exception); }
            try
            {
                WriteOuterCleanupReport(root, new OuterCoordinatorCleanupReport(operationFailure?.ToString(),
                    cleanupFailure?.ToString(), jobEmptyConfirmed));
            }
            catch (Exception exception) { cleanupFailure = CombineFailures(cleanupFailure, exception); }
        }

        if (operationFailure is not null)
        {
            if (cleanupFailure is not null)
                throw new AggregateException("Coordinator operation and owned-job cleanup both failed.",
                    operationFailure, cleanupFailure);
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        return new CoordinatorResult(exitCode, standardOutput, standardError, nonce, jobEmptyConfirmed);
    }

    private static void WriteOuterCleanupReport(string root, OuterCoordinatorCleanupReport report) =>
        File.WriteAllText(Path.Combine(root, "outer-cleanup.json"), JsonSerializer.Serialize(report));

    private static Exception CombineFailures(Exception? prior, Exception next) => prior is null
        ? next
        : new AggregateException("Multiple owned-job cleanup failures occurred.", prior, next);

    private static string GetSuccessEvidenceDirectory(string nonce) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "TestResults", "ns-s7k-console-probe", nonce));

    private static async Task<string> ReadToEndAsync(FileStream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static void AssertPeekIsBrokenPipe(NativePipePeek? observation, string phase, string root)
    {
        var peek = Assert.IsType<NativePipePeek>(observation);
        Assert.False(peek.Success, Describe(phase, peek, root));
        Assert.Equal(109, peek.Error);
    }

    private static void AssertChildIdentity(NativeHandleChildReady? observation, string nonce, string phase, string root)
    {
        var ready = Assert.IsType<NativeHandleChildReady>(observation);
        Assert.Equal(nonce, ready.Nonce);
        Assert.True(ready.ProcessId > 0, $"{phase} reported no process id. artifacts={root}");
        Assert.True(ready.StartTimeUtcTicks > 0, $"{phase} reported no process start identity. artifacts={root}");
    }

    private static string DescribeChild(NativeHandleChildObservation child, string root) =>
        $"mode={child.Mode}; ready={JsonSerializer.Serialize(child.Ready)}; writerClosed={child.WriterClosedBeforePeek}; " +
        $"aliveBefore={child.SameChildAliveBeforePeek}; whileAlive={JsonSerializer.Serialize(child.WhileAlive)}; " +
        $"aliveAfterPeek={child.SameChildAliveAfterPeek}; exited={child.ChildExited}; exitCode={child.ExitCode}; " +
            $"afterExit={JsonSerializer.Serialize(child.AfterExit)}; cleanup={child.CleanupError}; goneAfterCleanup={child.ChildGoneAfterCleanup}; " +
            $"log={child.ChildLogPath}; artifacts={root}";

    private static string Describe(string phase, NativePipePeek peek, string root) =>
        $"{phase}: success={peek.Success}; error={peek.Error}; bytesRead={peek.BytesRead}; " +
        $"available={peek.TotalBytesAvailable}; bytesLeft={peek.BytesLeftThisMessage}; artifacts={root}";

    private sealed record CoordinatorResult(int ExitCode, string StandardOutput, string StandardError,
        string Nonce, bool JobEmptyConfirmed);
    private sealed record OuterCoordinatorCleanupReport(string? OriginalFailure, string? CleanupFailure,
        bool JobEmptyConfirmed);
    private sealed record NativeHelperSemanticsReport(string Nonce, int? MissingExecutableError,
        int? InvalidLogError, bool MissingExecutableLogClosed, bool InvalidLogDestinationAbsent,
        NativeHelperStdioChildReport? StdioChild, int? StdioChildId, long StdioChildStartTicks,
        int? StdioExitCode, bool StdioChildGone, string? MergedOutput, bool StdioLogClosed,
        NativeHelperInputObservation? ZeroInput, NativeHelperInputObservation? InvalidInput,
        NativeHelperInputObservation? NonInheritableInput, NativeHelperInputObservation? ConsoleInput,
        int? NoLogExitCode, NativeHelperNoLogChildReport? NoLogChild, bool NoLogLogAbsent, string? Error);
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
    private sealed record NativeHandleChildObservation(string Mode, bool WriterWasInheritable,
        NativeHandleChildReady? Ready, bool WriterClosedBeforePeek, bool SameChildAliveBeforePeek,
        NativePipePeek? WhileAlive, bool SameChildAliveAfterPeek, bool ChildExited, int? ExitCode,
        NativePipePeek? AfterExit, string ChildLogPath, string? CleanupError, bool ChildGoneAfterCleanup);
    private sealed record NativeHandleProbeReport(string Nonce, NativePipePeek? NoChildWriterOpen,
        NativePipePeek? NoChildWriterClosed, NativeHandleChildObservation? NonInheritableChild,
        NativeHandleChildObservation? InheritableChild, string? Error);
}
