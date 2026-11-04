using System.Diagnostics;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class MergerOutcomeVerifierTests
{
    [Fact]
    public async Task AcceptsOnlyANewStructuredFindingConfirmedByTheCurrentRequiredCheck()
    {
        using var fixture = new VerifierFixture();

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("fixture checks", result.Finding?.Subject);
        Assert.Equal(fixture.Head, result.Finding?.CheckedHead);
    }

    [Fact]
    public async Task RejectsAValidFindingThatPredatesTheCurrentInvocation()
    {
        using var fixture = new VerifierFixture();
        fixture.Invocation = fixture.Invocation with
        {
            PreInvocationCommentIds = new HashSet<string>(["prior", "current"], StringComparer.Ordinal)
        };

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("from this invocation", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMalformedCurrentFinding()
    {
        using var fixture = new VerifierFixture();
        fixture.Comments = [new("current", "{ malformed", DateTimeOffset.UtcNow)];

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No structured actionable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("workflow")]
    public async Task RejectsChangedBeadOwnerOrStage(string change)
    {
        using var fixture = new VerifierFixture();
        if (change == "owner") fixture.Runner.Assignee = "human:receiver";
        else fixture.Runner.Stage = "stage:review";

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("current Bead state or owner", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAReportedCheckThatPassedForTheCurrentHead()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.CheckFailed = false;

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No required check", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsPullRequestHeadMismatchAndActiveRebase()
    {
        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.AdvertisedHead = new string('d', 40);
            var result = await fixture.VerifyAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("open retained branch and current checked HEAD", result.Diagnostic, StringComparison.Ordinal);
        }

        using (var fixture = new VerifierFixture())
        {
            Directory.CreateDirectory(Path.Combine(fixture.Worktree, ".git", "rebase-merge"));
            var result = await fixture.VerifyAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("active rebase", result.Diagnostic, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RejectsObservationForDifferentRetainedBranch()
    {
        using var fixture = new VerifierFixture();
        fixture.Invocation = fixture.Invocation with
        {
            Observations = fixture.Invocation.Observations! with { BranchName = "nightweaver/other" }
        };

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("retained branch", result.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class VerifierFixture : IDisposable
    {
        internal const string BeadId = "ns-ver.1";
        internal const string Owner = "nightweaver:demo";
        private readonly TempDirectory temp = new();
        private readonly ProjectConfig project;
        private readonly ValidatedTargetRepository repository;
        private readonly AgentStageSession session;
        private readonly AgentOutcomeClassificationResult classification =
            new(AgentOutcomeChoice.ChangesRequired, "fixture", "The current required check failed.");
        private readonly string invocationId = "20261104T1200000000000Z-merger";

        internal VerifierFixture()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Settled Merger log verification requires Windows file handles.");
            var host = new HostPaths(temp.CreateDirectory("host"));
            var worktrees = host.WorktreesRoot(ProjectSlug.Parse("demo"));
            Worktree = Directory.CreateDirectory(Path.Combine(worktrees, BeadId)).FullName;
            RunGit(Worktree, "init", "--initial-branch=main");
            RunGit(Worktree, "config", "user.name", "Verifier Fixture");
            RunGit(Worktree, "config", "user.email", "verifier@example.invalid");
            File.WriteAllText(Path.Combine(Worktree, "README.md"), "fixture\n");
            RunGit(Worktree, "add", "README.md");
            RunGit(Worktree, "commit", "-m", "fixture base");
            RunGit(Worktree, "checkout", "-b", $"nightweaver/{BeadId}");
            Head = RunGit(Worktree, "rev-parse", "HEAD");

            project = new ProjectConfig(ProjectSlug.Parse("demo"), "fixture.json", new("target", "main"),
                new(1, 1, 1), new(3, 1), new(AgentCli.Codex, null, "low", 1),
                new RoleSettings(AgentRoles.All.ToDictionary(role => role,
                    _ => new AgentSettings(AgentCli.Codex, null, "low", 1))));
            repository = new ValidatedTargetRepository(temp.CreateDirectory("target"), "main");
            var state = new BeadWorkflowState(BeadId, "task", "in_progress", "stage:merge", false, false, false, 0);
            session = new AgentStageSession(host, project, repository, BeadId, AgentRole.Merger,
                WorktreePurpose.Delivery, new ProvisionedWorktree(Worktree, Head), state, Owner,
                new AgentDispatchContext(BeadId, "task", "stage:merge", 0), PreserveInheritedChanges: true);

            var branch = $"nightweaver/{BeadId}";
            PullRequest = $"https://example.invalid/pull/{Uri.EscapeDataString(branch)}";
            var details = $"Required check 'fixture checks' failed for synchronized HEAD {Head}; inspect the fixture failure.";
            var finding = JsonSerializer.Serialize(new
            {
                schema = "nightweaver-merger-rejection-v1",
                kind = "checks",
                pull_request = PullRequest,
                checked_head = Head,
                subject = "fixture checks",
                summary = "The required fixture check failed.",
                details
            });
            Comments = [new("prior", finding, DateTimeOffset.UnixEpoch), new("current", finding, DateTimeOffset.UnixEpoch.AddSeconds(1))];
            var logPath = Path.Combine(host.LogsRoot(project.Name), BeadId, invocationId + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, $"Merger rejection evidence: fixture checks; {details}\n");
            var observations = new AgentStageObservations(state, state, Owner, Owner, AgentExecutionStatus.Exited,
                0, branch, Head, string.Empty, true);
            Invocation = new AgentStageResult(AgentRole.Merger, BeadId, invocationId, logPath, null, [], state,
                observations, classification, new HashSet<string>(["prior"], StringComparer.Ordinal));
            Runner = new VerifierProcessRunner(Head, branch, PullRequest);
        }

        internal string Worktree { get; }
        internal string Head { get; }
        internal string PullRequest { get; }
        internal VerifierProcessRunner Runner { get; }
        internal AgentStageResult Invocation { get; set; }
        internal IReadOnlyList<BeadCommentSnapshot> Comments { get; set; }

        internal Task<MergerRejectionVerification> VerifyAsync() =>
            new MergerOutcomeVerifier(Runner).VerifyRejectionAsync(
                new HostPaths(Path.Combine(temp.Path, "host")), project, session, Invocation,
                classification, Comments, Owner, CancellationToken.None);

        public void Dispose() => temp.Dispose();

        private static string RunGit(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = workingDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(workingDirectory, "missing-global-config");
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}{output}");
            return output.Trim();
        }
    }

    private sealed class VerifierProcessRunner(string head, string branch, string pullRequest) : IProcessRunner
    {
        private readonly SystemProcessRunner system = new();
        internal string Status { get; set; } = "in_progress";
        internal string Assignee { get; set; } = VerifierFixture.Owner;
        internal string Stage { get; set; } = "stage:merge";
        internal bool CheckFailed { get; set; } = true;
        internal string AdvertisedHead { get; set; } = head;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.FileName == "git") return system.RunAsync(request, cancellationToken);
            if (request.FileName == "bd")
            {
                if (request.Arguments.Contains("comments", StringComparer.Ordinal))
                    return Task.FromResult(Json(Array.Empty<object>()));
                return Task.FromResult(Json(new object[] { new
                {
                    id = VerifierFixture.BeadId, status = Status, issue_type = "task", priority = 2,
                    created_at = "2026-01-01T00:00:00Z", assignee = Assignee,
                    labels = new[] { Stage }, metadata = new { repair_cycles = 0 }
                } }));
            }
            if (request.FileName == "gh" && request.Arguments.Count >= 2
                && request.Arguments[0] == "pr" && request.Arguments[1] == "view")
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    state = "OPEN", headRefName = branch, headRefOid = AdvertisedHead, url = pullRequest
                }), string.Empty));
            if (request.FileName == "gh" && request.Arguments.Count >= 2
                && request.Arguments[0] == "pr" && request.Arguments[1] == "checks")
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new { name = "fixture checks", state = CheckFailed ? "FAILURE" : "SUCCESS", bucket = CheckFailed ? "fail" : "pass" }
                }), string.Empty));
            throw new InvalidOperationException($"Unexpected verifier command: {request.FileName} {string.Join(' ', request.Arguments)}");
        }

        private static ProcessResult Json(object value) => new(0, JsonSerializer.Serialize(value), string.Empty);
    }
}
