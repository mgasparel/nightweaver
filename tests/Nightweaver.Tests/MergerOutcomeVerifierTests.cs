using System.Diagnostics;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class MergerOutcomeVerifierTests
{
    [Fact]
    public void PullRequestQueryUsesFieldsSupportedByTheInstalledGhCli()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh", UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("pr");
        startInfo.ArgumentList.Add("view");
        startInfo.ArgumentList.Add("--help");
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        var help = output + error;
        Assert.Contains("JSON FIELDS", help, StringComparison.Ordinal);
        Assert.Contains("--repo [HOST/]OWNER/REPO", help, StringComparison.Ordinal);
        foreach (var field in new[] { "state", "baseRefName", "headRefName", "headRefOid", "url", "mergedAt", "mergeCommit" })
            Assert.Contains(field, help, StringComparison.Ordinal);
        Assert.DoesNotContain("baseRepository", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsOnlyANewStructuredFindingConfirmedByTheCurrentRequiredCheck()
    {
        using var fixture = new VerifierFixture();

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("fixture checks", result.Finding?.Subject);
        Assert.Equal(fixture.Head, result.Finding?.CheckedHead);
        Assert.Equal(2, fixture.Runner.PullRequestViewCount);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task AcceptsLocalBuildFailureWithoutAFailedRemoteCheck()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.CheckFailed = false;
        fixture.SetLocalCheckFinding("dotnet build", 1);

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("local", result.Finding?.CheckSource);
        Assert.Equal(1, result.Finding?.LocalExitCode);
        Assert.Equal(1, fixture.Runner.PullRequestViewCount);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task AcceptsLocalTestFailureWithACompletedNonzeroTranscript()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 1);

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("dotnet test", result.Finding?.Subject);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task RejectsLocalCheckMarkerWithoutFramedNonzeroTranscript()
    {
        using var fixture = new VerifierFixture();
        const string details = "error CS1002: expected ';' in Fixture.cs";
        const string summary = "Local build failed for the checked HEAD.";
        fixture.SetFinding("checks", "dotnet build", summary, details, "local", 1,
            $"A prior report mentioned Build FAILED. and 1 Error(s). {summary} {details}");

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("complete local check transcript", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task RejectsLocalTranscriptThatContradictsStructuredExitCode()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet build", 1, transcriptExitCode: 0);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("nonzero exit", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsLocalTranscriptWithLaterSuccessfulTerminalResult()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet build", 1, trailingOutput: "Build succeeded.");

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("no unambiguous nonzero build/test failure summary", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/Nightweaver.cs")]
    public async Task AcceptsRootLevelAndNestedConflictPaths(string subject)
    {
        using var fixture = new VerifierFixture();
        fixture.SetFinding("conflict", subject, "A repository conflict requires implementation repair.",
            $"CONFLICT: both branches changed {subject} and the rebase was aborted.",
            transcript: $"CONFLICT: both branches changed {subject} and the rebase was aborted.");

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Theory]
    [InlineData("../README.md")]
    [InlineData("src/../README.md")]
    [InlineData("/README.md")]
    [InlineData("C:\\README.md")]
    public async Task RejectsAbsoluteOrTraversalConflictPaths(string subject)
    {
        using var fixture = new VerifierFixture();
        fixture.SetFinding("conflict", subject, "A repository conflict requires implementation repair.",
            $"CONFLICT: both branches changed {subject} and the rebase was aborted.",
            transcript: $"CONFLICT: both branches changed {subject} and the rebase was aborted.");

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No structured actionable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsRemoteCheckFailureWhenPullRequestHeadChangesDuringObservation()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.HeadAfterChecks = new string('d', 40);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("identity or head changed", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Runner.PullRequestViewCount);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
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

    [Fact]
    public async Task AcceptsApprovalOnlyAfterCurrentMergeEvidencePassingChecksAndDefaultBranchReachability()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(fixture.PullRequest, result.Evidence?.PullRequest);
        Assert.Equal(fixture.Head, result.Evidence?.CheckedHead);
        Assert.Equal(new string('b', 40), result.Evidence?.MergeCommit);
        Assert.Equal("current", result.Evidence?.SourceCommentId);
        Assert.Equal(2, fixture.Runner.PullRequestViewCount);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
        Assert.True(fixture.Runner.FetchedDefaultBranch);
        Assert.Equal("github.com/example/project", fixture.Runner.PullRequestRepository);
        Assert.Equal("github.com/example/project", fixture.Runner.ChecksRepository);
    }

    [Fact]
    public async Task RejectsApprovalWhenReturnedPRUrlIdentifiesAForeignRepository()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.Runner.ReportedPullRequestUrl = "https://github.com/attacker/other/pull/123";
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("does not match", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Runner.PullRequestViewCount);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
        Assert.Equal("github.com/example/project", fixture.Runner.PullRequestRepository);
    }

    [Fact]
    public async Task AcceptsReturnedPRUrlForTheCapturedSameRepository()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("https://github.com/example/project/pull/123", result.Evidence?.PullRequest);
        Assert.Equal("github.com/example/project", fixture.Runner.PullRequestRepository);
    }

    [Fact]
    public async Task RejectsEvidenceForAForeignRepositoryPRUrlBeforeQueryingGitHub()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.SetMergeEvidence(pullRequest: "https://github.com/attacker/other/pull/123");

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("does not identify the validated Target Repository", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Runner.PullRequestViewCount);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task RejectsTargetOriginChangedDuringRequiredCheckVerification()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.Runner.OriginUrlAfterChecks = "https://github.com/attacker/other.git";
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("origin changed while PR checks", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
        Assert.False(fixture.Runner.FetchedDefaultBranch);
    }

    [Fact]
    public async Task RejectsApprovalFromHistoricalEvidenceOrChangedClaim()
    {
        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.SetMergeEvidence();
            fixture.Invocation = fixture.Invocation with
            {
                PreInvocationCommentIds = new HashSet<string>(["prior", "current"], StringComparer.Ordinal)
            };
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("current invocation", result.Diagnostic, StringComparison.Ordinal);
            Assert.Equal(0, fixture.Runner.PullRequestViewCount);
        }

        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = false;
            fixture.Runner.Assignee = "human:receiver";
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("state or claim owner", result.Diagnostic, StringComparison.Ordinal);
            Assert.Equal(0, fixture.Runner.PullRequestViewCount);
        }
    }

    [Fact]
    public async Task RejectsApprovalWhenPullRequestOrCurrentHeadChecksAreNotVerified()
    {
        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = false;
            fixture.Runner.BaseBranch = "release";
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("identity, configured base", result.Diagnostic, StringComparison.Ordinal);
        }

        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = false;
            fixture.Runner.HeadAfterChecks = new string('d', 40);
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("changed while required checks", result.Diagnostic, StringComparison.Ordinal);
        }

        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = true;
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("Required pull request checks", result.Diagnostic, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RejectsMergeCommitThatIsNotReachableFromConfiguredDefaultBranch()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.Runner.MergeCommitReachable = false;
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("not reachable", result.Diagnostic, StringComparison.Ordinal);
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
            repository = new ValidatedTargetRepository(temp.CreateDirectory("target"), "main")
            {
                OriginUrl = "https://github.com/example/project.git",
                OriginRepository = new("github.com", "example", "project")
            };
            var state = new BeadWorkflowState(BeadId, "task", "in_progress", "stage:merge", false, false, false, 0);
            session = new AgentStageSession(host, project, repository, BeadId, AgentRole.Merger,
                WorktreePurpose.Delivery, new ProvisionedWorktree(Worktree, Head), state, Owner,
                new AgentDispatchContext(BeadId, "task", "stage:merge", 0), PreserveInheritedChanges: true);

            var branch = $"nightweaver/{BeadId}";
            PullRequest = "https://github.com/example/project/pull/123";
            var logPath = Path.Combine(host.LogsRoot(project.Name), BeadId, invocationId + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var observations = new AgentStageObservations(state, state, Owner, Owner, AgentExecutionStatus.Exited,
                0, branch, Head, string.Empty, true);
            Invocation = new AgentStageResult(AgentRole.Merger, BeadId, invocationId, logPath, null, [], state,
                observations, classification, new HashSet<string>(["prior"], StringComparer.Ordinal));
            Runner = new VerifierProcessRunner(Head, branch, PullRequest);
            SetFinding("checks", "fixture checks", "The required fixture check failed.",
                $"Required check 'fixture checks' failed for synchronized HEAD {Head}; inspect the fixture failure.", "remote",
                transcript: $"Merger rejection evidence: fixture checks; Required check 'fixture checks' failed for synchronized HEAD {Head}; inspect the fixture failure.");
        }

        internal string Worktree { get; }
        internal string Head { get; }
        internal string PullRequest { get; }
        internal VerifierProcessRunner Runner { get; }
        internal AgentStageResult Invocation { get; set; }
        internal IReadOnlyList<BeadCommentSnapshot> Comments { get; set; } = [];
        private string LogText { get; set; } = string.Empty;

        internal void SetLocalCheckFinding(string command, int exitCode, int? transcriptExitCode = null,
            string trailingOutput = "")
        {
            var summary = $"Local {command} failed for the checked HEAD.";
            var details = command == "dotnet test"
                ? "Failed: LocalCheckFailureTests.RejectsInvalidInput [1 ms]"
                : "error CS1002: expected ';' in Fixture.cs";
            var reportedExitCode = transcriptExitCode ?? exitCode;
            var envelope = JsonSerializer.Serialize(new { command, checked_head = Head, exit_code = reportedExitCode });
            var terminal = command == "dotnet test"
                ? "Test run summary: Failed!\ntotal: 1\nfailed: 1"
                : "Build FAILED.\n1 Error(s)";
            var transcript = $"NIGHTWEAVER_LOCAL_CHECK_V1 {envelope}\n{terminal}\n{summary}\n{details}\n{trailingOutput}\nEND_NIGHTWEAVER_LOCAL_CHECK_V1\n";
            SetFinding("checks", command, summary, details, "local", exitCode, transcript);
        }

        internal void SetFinding(string kind, string subject, string summary, string details,
            string? checkSource = null, int? localExitCode = null, string? transcript = null)
        {
            var fields = new Dictionary<string, object?>
            {
                ["schema"] = "nightweaver-merger-rejection-v1",
                ["kind"] = kind,
                ["pull_request"] = PullRequest,
                ["checked_head"] = Head,
                ["subject"] = subject,
                ["summary"] = summary,
                ["details"] = details
            };
            if (checkSource is not null) fields["check_source"] = checkSource;
            if (localExitCode is not null) fields["local_exit_code"] = localExitCode.Value;
            var finding = JsonSerializer.Serialize(fields);
            Comments = [new("prior", finding, DateTimeOffset.UnixEpoch), new("current", finding, DateTimeOffset.UnixEpoch.AddSeconds(1))];
            LogText = transcript ?? $"Merger rejection evidence: {subject}; {details}\n";
            File.WriteAllText(Invocation.LogFilePath, LogText);
        }

        internal void SetMergeEvidence(string? pullRequest = null, string? checkedHead = null,
            string mergeMethod = "squash", string? mergeCommit = null)
        {
            var comment = $"nightweaver-merge-v1 pull_request={pullRequest ?? PullRequest} checked_head={checkedHead ?? Head} required_checks=passed merge_method={mergeMethod} merge_commit={mergeCommit ?? new string('b', 40)}";
            Comments = [new("prior", comment, DateTimeOffset.UnixEpoch), new("current", comment, DateTimeOffset.UnixEpoch.AddSeconds(1))];
        }

        internal Task<MergerRejectionVerification> VerifyAsync() =>
            new MergerOutcomeVerifier(Runner).VerifyRejectionAsync(
                new HostPaths(Path.Combine(temp.Path, "host")), project, session, Invocation,
                classification, Comments, Owner, CancellationToken.None);

        internal Task<MergerApprovalVerification> VerifyApprovalAsync() =>
            new MergerOutcomeVerifier(Runner).VerifyApprovalAsync(session, Invocation,
                new(AgentOutcomeChoice.Approved, "fixture", "The merged result is confirmed."),
                Comments, Owner, CancellationToken.None);

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
        internal string OriginUrl { get; set; } = "https://github.com/example/project.git";
        internal string? OriginUrlAfterChecks { get; set; }
        internal string ReportedPullRequestUrl { get; set; } = pullRequest;
        internal string? PullRequestRepository { get; private set; }
        internal string? ChecksRepository { get; private set; }
        internal string Status { get; set; } = "in_progress";
        internal string Assignee { get; set; } = VerifierFixture.Owner;
        internal string Stage { get; set; } = "stage:merge";
        internal bool CheckFailed { get; set; } = true;
        internal string AdvertisedHead { get; set; } = head;
        internal string? HeadAfterChecks { get; set; }
        internal string BaseBranch { get; set; } = "main";
        internal bool ApprovalMode { get; set; }
        internal bool MergeCommitReachable { get; set; } = true;
        internal bool FetchedDefaultBranch { get; private set; }
        internal int PullRequestViewCount { get; private set; }
        internal int RequiredChecksCount { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.FileName == "git")
            {
                if (request.Arguments.SequenceEqual(["config", "--get", "remote.origin.url"], StringComparer.Ordinal))
                {
                    var origin = OriginUrlAfterChecks is not null && RequiredChecksCount > 0
                        ? OriginUrlAfterChecks : OriginUrl;
                    return Task.FromResult(new ProcessResult(0, origin, string.Empty));
                }
                if (request.Arguments.Count == 3 && request.Arguments[0] == "fetch"
                    && request.Arguments[1] == OriginUrl && request.Arguments[2] == "main")
                {
                    FetchedDefaultBranch = true;
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
                }
                if (request.Arguments.Count == 4 && request.Arguments[0] == "merge-base"
                    && request.Arguments[1] == "--is-ancestor")
                    return Task.FromResult(new ProcessResult(MergeCommitReachable ? 0 : 1, string.Empty, string.Empty));
                return system.RunAsync(request, cancellationToken);
            }
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
            {
                PullRequestViewCount++;
                var jsonIndex = request.Arguments.ToList().IndexOf("--json");
                if (jsonIndex < 0 || jsonIndex + 1 >= request.Arguments.Count)
                    return Task.FromResult(new ProcessResult(1, string.Empty, "gh pr view requires --json fields in this fixture."));
                var requestedFields = request.Arguments[jsonIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var supportedFields = new HashSet<string>(
                    ["state", "baseRefName", "headRefName", "headRefOid", "url", "mergedAt", "mergeCommit"],
                    StringComparer.Ordinal);
                var unsupportedField = requestedFields.FirstOrDefault(field => !supportedFields.Contains(field));
                if (unsupportedField is not null)
                    return Task.FromResult(new ProcessResult(1, string.Empty, $"Unknown JSON field: {unsupportedField}"));
                var repoIndex = request.Arguments.ToList().IndexOf("--repo");
                PullRequestRepository = repoIndex >= 0 && repoIndex + 1 < request.Arguments.Count
                    ? request.Arguments[repoIndex + 1] : null;
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    state = ApprovalMode ? "MERGED" : "OPEN",
                    baseRefName = BaseBranch,
                    headRefName = branch,
                    headRefOid = AdvertisedHead,
                    url = ReportedPullRequestUrl,
                    mergedAt = ApprovalMode ? "2026-11-04T20:00:00Z" : null,
                    mergeCommit = ApprovalMode ? new { oid = new string('b', 40) } : null
                }), string.Empty));
            }
            if (request.FileName == "gh" && request.Arguments.Count >= 2
                && request.Arguments[0] == "pr" && request.Arguments[1] == "checks")
            {
                var repoIndex = request.Arguments.ToList().IndexOf("--repo");
                ChecksRepository = repoIndex >= 0 && repoIndex + 1 < request.Arguments.Count
                    ? request.Arguments[repoIndex + 1] : null;
                RequiredChecksCount++;
                if (HeadAfterChecks is not null) AdvertisedHead = HeadAfterChecks;
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[]
                {
                    new { name = "fixture checks", state = CheckFailed ? "FAILURE" : "SUCCESS", bucket = CheckFailed ? "fail" : "pass" }
                }), string.Empty));
            }
            throw new InvalidOperationException($"Unexpected verifier command: {request.FileName} {string.Join(' ', request.Arguments)}");
        }

        private static ProcessResult Json(object value) => new(0, JsonSerializer.Serialize(value), string.Empty);
    }
}
