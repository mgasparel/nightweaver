using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;
using static Nightweaver.Tests.OperationalTelemetryTests;

namespace Nightweaver.Tests;

[Collection("Agent invocation executor")]
public sealed class FirstPathTelemetryTests : AgentStageIntegrationTestSupport
{
    private const string RuntimeId = "0123456789abcdef0123456789abcdef";
    private const string InvocationId = "20261119T1200000000000Z-merger";
    private const string Canary = "PRIVATE_PROMPT_SOURCE_TRANSCRIPT_EXCEPTION";

    [Theory]
    [InlineData("confirmed", 1, "queued")]
    [InlineData("lost", 1, "queued")]
    [InlineData("unconfirmed", 0, null)]
    [InlineData("guard", 0, null)]
    [InlineData("cleanup-failed", 1, "failed")]
    [InlineData("cancel-after-close", 1, "cancelled")]
    [InlineData("already-closed", 0, null)]
    public async Task OnlyConfirmedOwnedClosureCountsBeforeIndependentHousekeeping(string mode, int expected, string? cleanup)
    {
        await using var receiver = await Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1), _ => { });
        using var scope = telemetry.EnterRuntime(RuntimeId);
        using var measurements = new Measurements();
        using var temp = new TempDirectory();
        var host = new HostPaths(temp.CreateDirectory("host"));
        var repository = new ValidatedTargetRepository(temp.CreateDirectory("target"), "main");
        var slug = ProjectSlug.Parse("demo");
        var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
        var project = new ProjectConfig(slug, "fixture.json", new(repository.Path, "main"), new(1, 1, 1), new(3, 1),
            settings, new RoleSettings(AgentRoles.All.ToDictionary(role => role, _ => settings)));
        var initial = new BeadWorkflowState("ns-proof", "task", "in_progress", "stage:merge", false, false, false, 0);
        var session = new AgentStageSession(host, project, repository, initial.Id, AgentRole.Merger, WorktreePurpose.Delivery,
            new(Path.Combine(host.WorktreesRoot(slug), initial.Id), new string('a', 40)), initial, "owner",
            new(initial.Id, "task", "stage:merge", 0), true);
        var invocation = new AgentStageResult(AgentRole.Merger, initial.Id, InvocationId, "local.log", null, [],
            Observations: new(initial, initial, "owner", "owner", AgentExecutionStatus.Exited, 0,
                "nightweaver/ns-proof", new string('a', 40), "", true));
        var evidence = new MergerVerifiedMergeEvidence("https://example.invalid/pull/123", new string('a', 40), new string('b', 40), "squash", "current");
        var closed = mode == "already-closed";
        var comments = new List<string>();
        var closeWrites = 0;
        var readsAfterClose = 0;
        object Row() => new { id = initial.Id, issue_type = "task", status = closed ? "closed" : "in_progress",
            assignee = closed ? null : "owner", priority = 2, created_at = "2026-11-19T00:00:00Z", labels = new[] { "stage:merge" } };
        var runner = new RecordingProcessRunner((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            var args = request.Arguments;
            string json;
            if (args.Contains("comments"))
            {
                if (args.Contains("add")) comments.Add(args[^1]);
                json = JsonSerializer.Serialize(comments.Select((text, i) => new { id = i.ToString(), issue_id = initial.Id, text, author = "owner", created_at = "2026-11-19T00:00:00Z" }));
            }
            else if (args.Contains("update"))
            {
                closeWrites++;
                if (mode == "guard") return Task.FromResult(new ProcessResult(13, "", "guard changed"));
                closed = mode != "unconfirmed";
                return Task.FromResult(new ProcessResult(mode == "lost" ? 1 : 0, mode == "lost" ? "" : JsonSerializer.Serialize(new[] { Row() }), ""));
            }
            else if (args.Contains("dep")) json = "[]";
            else
            {
                if (closeWrites > 0 && args.Contains("show")) readsAfterClose++;
                json = JsonSerializer.Serialize(new[] { Row() });
            }
            return Task.FromResult(new ProcessResult(0, json, ""));
        });
        var queue = new CallbackQueue(() =>
        {
            Assert.Equal(1, measurements.Merges); // success precedes even cancelled cleanup
            if (mode == "cancel-after-close") throw new OperationCanceledException();
            if (mode == "cleanup-failed") throw new IOException(Canary);
        });
        var finalizer = new MergerCompletionFinalizer(runner, queue);
        if (mode == "cancel-after-close")
            await Assert.ThrowsAsync<OperationCanceledException>(() => finalizer.CompleteAsync(session, invocation, "owner", evidence, CancellationToken.None));
        else if (mode == "already-closed")
            await Assert.ThrowsAsync<AgentStageInputException>(() => finalizer.CompleteAsync(session, invocation, "owner", evidence, CancellationToken.None));
        else
        {
            var result = await finalizer.CompleteAsync(session, invocation, "owner", evidence, CancellationToken.None);
            Assert.Equal(expected == 1, result.Succeeded);
        }
        Assert.Equal(expected, measurements.Merges);
        Assert.Equal(mode is "lost" or "unconfirmed" ? 1 : 0, readsAfterClose);
        Assert.Equal(cleanup, measurements.Cleanup);
        if (expected == 1)
        {
            // Repeated callback and startup housekeeping cannot count the closed task again.
            await Assert.ThrowsAsync<AgentStageInputException>(() => finalizer.CompleteAsync(session, invocation, "owner", evidence, CancellationToken.None));
            Assert.False(session.TryRecordMergeTelemetry());
            Assert.False((session with { Role = AgentRole.Merger }).TryRecordMergeTelemetry());
            Assert.Equal(1, measurements.Merges);
        }
    }

    [Fact]
    public async Task NativeInvocationAndIndependentlyVerifiedClosureExportCorrelatedSignals()
    {
        var forward = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIRST_PATH_OTLP");
        await using var receiver = await Receiver.StartAsync(forward: forward is null ? null : new Uri(forward));
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1), _ => { }, metricIntervalMilliseconds: 100);
        using var scope = telemetry.EnterRuntime(RuntimeId);
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("Disposable telemetry proof", "task", "stage:merge");
        var project = target.CreateProject(AgentCli.Codex);
        target.RunTarget("bd", ["update", beadId, "--status", "in_progress", "--assignee", project.RecoveryActor]);
        var repository = new ValidatedTargetRepository(target.Path, "main")
        {
            OriginUrl = AgentStageTargetFixture.FixtureOriginUrl,
            OriginRepository = new("github.com", "nightweaver-fixture", "target")
        };
        var system = new SystemProcessRunner();
        var localOrigin = target.RunTarget("git", ["remote", "get-url", "origin"]).Trim();
        var worktree = await new WorktreeProvisioner(system).ProvisionAsync(target.Host, project.Name, repository, beadId,
            WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var temporary = Path.Combine(target.Host.TmpRoot(project.Name), beadId, InvocationId);
        var prepared = new PreparedAgentInvocation("codex", ["inspect"], worktree.Path, Canary,
            new Dictionary<string, string> { ["NIGHTWEAVER_BEAD_ID"] = beadId, ["TMP"] = temporary,
                ["TEMP"] = temporary, ["TMPDIR"] = temporary, ["NIGHTWEAVER_INVOCATION_ID"] = InvocationId,
                ["NIGHTWEAVER_LOCAL_CHECK_OUTPUT"] = "terminal" }, TimeSpan.FromSeconds(20), InvocationId, temporary,
            Path.Combine(target.Host.LogsRoot(project.Name), beadId, InvocationId + ".log"), purpose: "merger");
        var execution = await AgentInvocationExecutor.ExecuteAsync(target.Host, project.Name, prepared,
            new FakeAgentLauncher(target.AgentFixtureExecutablePath), TestContext.Current.CancellationToken);
        Assert.Equal(0, execution.ExitCode);
        var state = BeadWorkflowState.Parse(target.ReadBead(beadId));
        var session = new AgentStageSession(target.Host, project, repository, beadId, AgentRole.Merger, WorktreePurpose.Delivery,
            worktree, state, project.RecoveryActor, new(beadId, "task", "stage:merge", 0), true);
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.FileName == "git" && request.Arguments.SequenceEqual(["config", "--get", "remote.origin.url"]))
                return new ProcessResult(0, AgentStageTargetFixture.FixtureOriginUrl, "");
            if (request.FileName == "git" && request.Arguments[0] == "fetch")
                return await system.RunAsync(GitCommand.Create(["fetch", localOrigin, "main"], request.WorkingDirectory), token);
            return await target.RunWithFixturePullRequestAsync(system, request, token);
        });
        var observed = await new AgentPostExitVerifier(runner, new BeadsReader(runner)).VerifyAsync(repository, worktree.Path,
            AgentRole.Merger, state, project.RecoveryActor, worktree.HeadSha, execution, TestContext.Current.CancellationToken);
        Assert.True(observed.Succeeded, observed.Failure?.Reason);
        observed = observed with { PreInvocationCommentIds = new HashSet<string>() };
        var comment = $"nightweaver-merge-v1 pull_request={AgentStageTargetFixture.FixturePullRequestUrl} checked_head={worktree.HeadSha} required_checks=passed merge_method=squash merge_commit={worktree.HeadSha}";
        var verified = await new MergerOutcomeVerifier(runner).VerifyApprovalAsync(session, observed,
            new(AgentOutcomeChoice.Approved, "fixture", "Controlled local merged fixture."), [new("current", comment, DateTimeOffset.UtcNow)],
            project.RecoveryActor, TestContext.Current.CancellationToken);
        Assert.True(verified.Succeeded, verified.Diagnostic);
        var completion = await new MergerCompletionFinalizer(runner, new CallbackQueue(() => { })).CompleteAsync(session, observed,
            project.RecoveryActor, verified.Evidence!, TestContext.Current.CancellationToken);
        Assert.True(completion.Succeeded, completion.Diagnostic);
        Assert.Equal("closed", target.ReadBead(beadId).Status);
        await telemetry.StopAsync(false);
        var records = receiver.Captures.ToArray();
        foreach (var record in records) Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(record.Bytes));
        Assert.All(records.Where(r => r.Signal is "traces" or "logs"),
            r => Assert.True(r.Bytes.Length <= TelemetryAdmission.RecordBytes));
        foreach (var signal in new[] { "traces", "logs" })
        {
            var details = records.Where(r => r.Signal == signal).SelectMany(DecodeRecords)
                .Select(r => Attributes(r, signal == "traces" ? 9 : 6)).ToArray();
            foreach (var operation in new[] { "invocation", "verified_merge" })
            {
                var tags = Assert.Single(details, d => d["operation"] == operation);
                Assert.Equal(beadId, tags["bead.id"]);
                Assert.Equal(RuntimeId, tags["runtime.id"]);
                Assert.Equal(InvocationId, tags["invocation.id"]);
            }
        }
        var metrics = records.Where(r => r.Signal == "metrics").SelectMany(DecodeRecords).ToArray();
        foreach (var name in new[] { "nightweaver.invocation.duration", "nightweaver.invocation.attempts", "nightweaver.verified_merge", "nightweaver.merge.cleanup" })
            Assert.Contains(metrics, m => m.Only(1).String == name);
        foreach (var record in records.Where(r => r.Signal == "metrics"))
        {
            Assert.DoesNotContain(beadId, Encoding.UTF8.GetString(record.Bytes));
            Assert.DoesNotContain(RuntimeId, Encoding.UTF8.GetString(record.Bytes));
            Assert.DoesNotContain(InvocationId, Encoding.UTF8.GetString(record.Bytes));
        }
        if (Environment.GetEnvironmentVariable("NIGHTWEAVER_FIRST_PATH_EVIDENCE") is { } output)
        {
            Directory.CreateDirectory(output);
            for (var i = 0; i < records.Length; i++) await File.WriteAllBytesAsync(Path.Combine(output, $"{i}-{records[i].Signal}.protobuf"), records[i].Bytes, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(output, "signals.json"), JsonSerializer.Serialize(new
                { beadId, runtimeId = RuntimeId, invocationId = InvocationId, checkedHead = worktree.HeadSha, mergeCommit = worktree.HeadSha,
                    outcome = "verified_merge", cleanup = "queued", captures = records.Select(r => new { r.Signal, size = r.Bytes.Length }) }), TestContext.Current.CancellationToken);
        }
    }

    internal static IEnumerable<Message> DecodeRecords(Capture capture)
    {
        var resource = Message.Parse(Message.Parse(capture.Bytes).Only(1).Bytes);
        return resource.Fields.Where(f => f.Number == 2).SelectMany(f => Message.Parse(f.Bytes).Fields)
            .Where(f => f.Number == 2).Select(f => Message.Parse(f.Bytes));
    }

    private sealed class CallbackQueue(Action callback) : ICompletedEnvironmentQueue
    {
        public Task EnqueueAsync(AgentStageSession session, MergerVerifiedMergeEvidence evidence, CancellationToken token)
        { callback(); return Task.CompletedTask; }
    }

    private sealed class FakeAgentLauncher(string executable) : IAgentProcessLauncher
    {
        public string ResolveExecutable(string name, IReadOnlyDictionary<string, string> environment) => executable;
        public WindowsProcessLauncher.WindowsAgentProcess Launch(string path, IReadOnlyList<string> arguments,
            string workingDirectory, IReadOnlyDictionary<string, string> environment) =>
            WindowsProcessLauncher.Launch(path, arguments, workingDirectory, environment);
    }

    internal sealed class Measurements : IDisposable
    {
        private readonly MeterListener listener = new();
        internal long Merges;
        internal string? Cleanup;
        internal Measurements()
        {
            listener.InstrumentPublished = (instrument, observer) =>
            { if (instrument.Meter.Name == OperationalTelemetry.ScopeName) observer.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "nightweaver.verified_merge") Merges += value;
                if (instrument.Name == "nightweaver.merge.cleanup") foreach (var tag in tags) Cleanup = (string?)tag.Value;
            });
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }
}
