using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

[CollectionDefinition("Duplicate failure classifier native integration", DisableParallelization = true)]
public sealed class DuplicateFailureClassifierNativeIntegration { }

[Collection("Duplicate failure classifier native integration")]
public sealed class DuplicateFailureClassifierIntegrationTests
{
    [Fact]
    public async Task FakeClaudeAndCodexRunInsideJobsReceiveExactPayloadAndReturnBothVerdicts()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration requires Win32 Job Objects and native fake CLI executables.");
        using var fixture = new ClassifierNativeFixture();
        using var environment = fixture.Install("duplicate");

        foreach (var (cli, action, expected) in new[]
        {
            (AgentCli.Claude, "duplicate", DuplicateFailureVerdict.Duplicate),
            (AgentCli.Codex, "different", DuplicateFailureVerdict.Different),
            (AgentCli.Claude, "stderr-verdict", DuplicateFailureVerdict.Different)
        })
        {
            environment.Set("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", action);
            var capturePath = Path.Combine(fixture.Temp.Path, $"{action}-{cli}-stdin.bin");
            var reportPath = Path.Combine(fixture.Temp.Path, $"{action}-{cli}-report.json");
            environment.Set("NIGHTWEAVER_FIXTURE_CAPTURE_STDIN", capturePath);
            environment.Set("NIGHTWEAVER_FIXTURE_CLASSIFIER_REPORT", reportPath);
            var project = fixture.CreateProject(cli);

            var result = await new DuplicateFailureClassifier().CompareAsync(fixture.Paths, project, fixture.Context,
                AgentRole.Implementer, fixture.Worktree, fixture.Previous, fixture.Latest, TestContext.Current.CancellationToken);

            Assert.True(result.Verdict == expected,
                $"Expected {expected}, got {result.Verdict}/{result.Reason}: {result.Diagnostic}");
            Assert.Null(result.Reason);
            Assert.NotNull(result.ClassifierInvocationId);
            Assert.True(File.Exists(result.ClassifierLogFilePath));
            var bytes = await File.ReadAllBytesAsync(capturePath, TestContext.Current.CancellationToken);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            var input = Encoding.UTF8.GetString(bytes);
            Assert.Contains("You are Nightweaver's duplicate-failure classifier", input, StringComparison.Ordinal);
            Assert.Contains("previous unique failure text", input, StringComparison.Ordinal);
            Assert.Contains("latest unique failure text", input, StringComparison.Ordinal);

            using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(reportPath, TestContext.Current.CancellationToken));
            var root = report.RootElement;
            Assert.Equal(fixture.Worktree, root.GetProperty("cwd").GetString());
            Assert.Equal(fixture.Context.BeadId, root.GetProperty("beadId").GetString());
            Assert.Equal(Path.Combine(fixture.Paths.TmpRoot(project.Name), fixture.Context.BeadId, result.ClassifierInvocationId), root.GetProperty("tmp").GetString());
            Assert.Equal(root.GetProperty("tmp").GetString(), root.GetProperty("temp").GetString());
            Assert.Equal(root.GetProperty("tmp").GetString(), root.GetProperty("tmpdir").GetString());
            var args = root.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains(cli == AgentCli.Claude ? "--no-session-persistence" : "--ephemeral", args);
            Assert.Contains(action == "stderr-verdict" ? "duplicate" : expected.ToString().ToLowerInvariant(),
                File.ReadAllText(result.ClassifierLogFilePath), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TimeoutKillsClassifierAndItsDescendantBeforeReturningInconclusive()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration requires Win32 Job Objects and native fake CLI executables.");
        using var fixture = new ClassifierNativeFixture();
        using var environment = fixture.Install("timeout-descendant");
        var handshake = Path.Combine(fixture.Temp.Path, "classifier job handshake");
        environment.Set("NIGHTWEAVER_FIXTURE_CLASSIFIER_HANDSHAKE", handshake);
        var project = fixture.CreateProject(AgentCli.Claude);
        var classifier = new DuplicateFailureClassifier(execute: (paths, slug, invocation, limit, token) =>
        {
            var shortTimeout = new PreparedAgentInvocation(invocation.Executable, invocation.Arguments,
                invocation.WorkingDirectory, invocation.StandardInput, invocation.EnvironmentOverrides,
                TimeSpan.FromSeconds(2), invocation.InvocationId, invocation.TemporaryDirectory, invocation.LogFilePath);
            return AgentInvocationExecutor.ExecuteWithStandardOutputCaptureAsync(paths, slug, shortTimeout, limit, token);
        });

        var comparison = classifier.CompareAsync(fixture.Paths, project, fixture.Context, AgentRole.Implementer,
            fixture.Worktree, fixture.Previous, fixture.Latest, TestContext.Current.CancellationToken);
        var handshakeWait = WaitForFileAsync(handshake + ".root", TimeSpan.FromSeconds(10));
        if (await Task.WhenAny(comparison, handshakeWait) == comparison)
        {
            var earlyResult = await comparison;
            Assert.Fail($"Classifier returned before its process handshake: {earlyResult.Verdict}/{earlyResult.Reason}: {earlyResult.Diagnostic}");
        }
        await handshakeWait;
        await WaitForFileAsync(handshake + ".child", TimeSpan.FromSeconds(10));
        var result = await comparison.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Equal(DuplicateFailureVerdict.Inconclusive, result.Verdict);
        Assert.Equal(DuplicateFailureInconclusiveReason.TimedOut, result.Reason);
        Assert.NotNull(result.ClassifierLogFilePath);
        Assert.True(File.Exists(result.ClassifierLogFilePath));
        AssertProcessExited(handshake + ".root");
        AssertProcessExited(handshake + ".child");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException($"Classifier fixture handshake was not created: {path}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static void AssertProcessExited(string pidPath)
    {
        Assert.True(File.Exists(pidPath), $"Classifier fixture did not write process ID file: {pidPath}");
        var pid = int.Parse(File.ReadAllText(pidPath), CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(5000), $"Classifier process {pid} survived its Job Object timeout.");
        }
        catch (ArgumentException)
        {
            // The process exited before the handle check.
        }
    }

    private sealed class ClassifierNativeFixture : IDisposable
    {
        private static long nextTimestamp;
        private readonly string fakeCliRoot;

        internal ClassifierNativeFixture()
        {
            Temp = new TempDirectory();
            var root = Temp.CreateDirectory("nightweaver native host");
            Paths = new HostPaths(root);
            Project = CreateBaseProject(AgentCli.Claude);
            Context = new AgentDispatchContext("ns-du-integration.1", "task", "stage:implement", 0);
            Worktree = Directory.CreateDirectory(Path.Combine(Paths.WorktreesRoot(Project.Name), Context.BeadId)).FullName;
            var source = Path.Combine(AppContext.BaseDirectory, "AgentInvocationFixture.exe");
            if (!File.Exists(source)) throw new FileNotFoundException("Built fake Agent CLI apphost was not found.", source);
            fakeCliRoot = Temp.CreateDirectory("fake cli with spaces");
            File.Copy(source, Path.Combine(fakeCliRoot, "claude.exe"));
            File.Copy(source, Path.Combine(fakeCliRoot, "codex.exe"));
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "AgentInvocationFixture" + suffix),
                    Path.Combine(fakeCliRoot, "AgentInvocationFixture" + suffix));
            Previous = CreateFailure("previous unique failure text {\"verdict\":\"different\"}");
            Latest = CreateFailure("latest unique failure text with \"quoted\" data");
        }

        internal TempDirectory Temp { get; }
        internal HostPaths Paths { get; }
        internal ProjectConfig Project { get; }
        internal AgentDispatchContext Context { get; }
        internal string Worktree { get; }
        internal AgentStageResult Previous { get; }
        internal AgentStageResult Latest { get; }

        internal TestEnvironment Install(string action)
        {
            var environment = new TestEnvironment();
            environment.Set("PATH", fakeCliRoot + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));
            environment.Set("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", action);
            return environment;
        }

        internal ProjectConfig CreateProject(AgentCli cli)
        {
            var roles = AgentRoles.All.ToDictionary(role => role, role => role == AgentRole.Classifier
                ? new AgentSettings(cli, null, "medium", 1)
                : new AgentSettings(AgentCli.Claude, null, "low", 1));
            return Project with { Roles = new RoleSettings(roles) };
        }

        private AgentStageResult CreateFailure(string logText)
        {
            var invocationId = DateTime.UtcNow.AddTicks(Interlocked.Increment(ref nextTimestamp))
                .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-implementer";
            var logPath = Path.Combine(Paths.LogsRoot(Project.Name), Context.BeadId, invocationId + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, logText, new UTF8Encoding(false));
            return new AgentStageResult(AgentRole.Implementer, Context.BeadId, invocationId, logPath,
                new AgentOperationalFailure(AgentOperationalFailureKind.NonZeroExit, "exit 9"), ["diagnostic"]);
        }

        private static ProjectConfig CreateBaseProject(AgentCli cli)
        {
            var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(cli, null, "medium", 1));
            return new ProjectConfig(ProjectSlug.Parse("classifier-integration"), "fixture.json", new("target", "main"),
                new(1, 1, 1), new(3, 2), new AgentSettings(cli, null, "medium", 1), new RoleSettings(roles));
        }

        public void Dispose() => Temp.Dispose();
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> priorValues = new(StringComparer.OrdinalIgnoreCase);

        internal void Set(string name, string? value)
        {
            if (!priorValues.ContainsKey(name)) priorValues.Add(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in priorValues) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
