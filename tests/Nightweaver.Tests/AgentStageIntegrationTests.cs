using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

[CollectionDefinition("Agent stage native integration", DisableParallelization = true)]
public sealed class AgentStageNativeIntegration { }

[Collection("Agent stage native integration")]
public sealed class AgentStageIntegrationTests
{
    [Fact]
    public async Task OperationalRetryRunsRealAgentJobAfterResettingTheSameClaimedWorktree()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var bead = target.CreateBead("Operational retry integration", "task", "stage:implement");

        var outcome = await target.RunRetryAgentAsync(bead, AgentRole.Implementer, ["nonzero-dirty", "review"], verifyResetSentinels: true);

        Assert.True(outcome.Succeeded, outcome.StageResult.Failure?.Reason);
        Assert.Equal(2, outcome.AttemptCount);
        Assert.Equal("stage:review", target.ReadState(bead).Stage);
        Assert.Equal(4, target.ReadState(bead).RepairCycles);
        var beadSnapshot = target.ReadBead(bead);
        Assert.Equal("in_progress", beadSnapshot.Status);
        Assert.Equal("nightweaver:demo", beadSnapshot.Assignee);

        var worktree = target.WorktreePath(bead);
        Assert.Equal("fixture base\n", File.ReadAllText(System.IO.Path.Combine(worktree, "README.md")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.False(File.Exists(System.IO.Path.Combine(worktree, "retry-untracked.txt")));
        Assert.Equal("ignored sentinel\n", File.ReadAllText(System.IO.Path.Combine(worktree, ".nightweaver-ignored", "keep.bin")));
        Assert.Equal(2, Directory.GetFiles(System.IO.Path.Combine(target.Host.LogsRoot(ProjectSlug.Parse("demo")), bead), "*.log").Length);
        Assert.True(File.Exists(outcome.StageResult.LogFilePath), outcome.StageResult.LogFilePath);
        var retryCommands = target.LastRetryRequests.Where(request => request.FileName == "git"
            && request.WorkingDirectory == worktree && request.Arguments.SequenceEqual(["reset", "--hard", "HEAD"])).ToArray();
        Assert.Equal(2, retryCommands.Length); // initial provision plus one same-worktree pre-retry reset
        Assert.Equal(2, target.LastRetryRequests.Count(request => request.FileName == "git"
            && request.WorkingDirectory == worktree && request.Arguments.SequenceEqual(["clean", "-fd"])));
    }

    [Fact]
    public async Task PlanningRetryKeepsWarmProvisionedWorktreeDetachedAndRetainsClaim()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var bead = target.CreateBead("Planning retry integration", "feature", "stage:plan");

        var outcome = await target.RunRetryAgentAsync(bead, AgentRole.Planner, ["failure", "plan-challenge"]);

        Assert.True(outcome.Succeeded, outcome.StageResult.Failure?.Reason);
        Assert.Equal(2, outcome.AttemptCount);
        Assert.Equal("stage:plan-challenge", target.ReadState(bead).Stage);
        Assert.Equal(4, target.ReadState(bead).RepairCycles);
        Assert.Equal("nightweaver:demo", target.ReadBead(bead).Assignee);
        Assert.Equal("", target.CurrentBranch(bead));
        Assert.Equal(2, Directory.GetFiles(System.IO.Path.Combine(target.Host.LogsRoot(ProjectSlug.Parse("demo")), bead), "*.log").Length);
    }

    [Fact]
    public async Task DuplicateRetryFailureUsesRealClassifierAndEscalatorThenCleansDeliveryEnvironment()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var bead = target.CreateBead("Duplicate retry escalation integration", "task", "stage:implement");

        var outcome = await target.RunRetryAgentAsync(bead, AgentRole.Implementer,
            ["failure", "failure", "review"], classifierAction: "duplicate");

        Assert.Equal(OperationalRetryStatus.Escalated, outcome.Status);
        Assert.Equal(2, outcome.AttemptCount);
        Assert.Equal(NeedsHumanCause.DuplicateFailure, outcome.EscalationCause);
        Assert.Equal(DuplicateFailureVerdict.Duplicate, outcome.LastClassification!.Verdict);
        var state = target.ReadState(bead);
        Assert.Equal("stage:implement", state.Stage);
        Assert.True(state.NeedsHuman);
        var beadSnapshot = target.ReadBead(bead);
        Assert.Equal("open", beadSnapshot.Status);
        Assert.Null(beadSnapshot.Assignee);
        Assert.False(Directory.Exists(target.WorktreePath(bead)));
        Assert.False(Directory.Exists(System.IO.Path.Combine(target.Host.TmpRoot(ProjectSlug.Parse("demo")), bead)));
        Assert.Equal(1, GitFixture.RunGit(target.Path, ["show-ref", "--verify", "--quiet", $"refs/heads/nightweaver/{bead}"], allowFailure: true));
        var logs = Directory.GetFiles(System.IO.Path.Combine(target.Host.LogsRoot(ProjectSlug.Parse("demo")), bead), "*.log");
        Assert.Equal(3, logs.Length);
        Assert.Contains(outcome.StageResult.LogFilePath, logs, StringComparer.Ordinal);
        var comments = target.ReadComments(bead);
        using var commentsJson = JsonDocument.Parse(comments);
        var diagnosticText = StringValues(commentsJson.RootElement)
            .Single(value => value.Contains("nightweaver-needs-human-v1", StringComparison.Ordinal));
        using var diagnostic = JsonDocument.Parse(diagnosticText);
        Assert.Equal("duplicate-failure", diagnostic.RootElement.GetProperty("cause").GetString());
        Assert.Equal(System.IO.Path.Combine(target.Host.LogsRoot(ProjectSlug.Parse("demo")), bead),
            diagnostic.RootElement.GetProperty("log_directory").GetString());
        Assert.Contains("attempts completed: 2", diagnostic.RootElement.GetProperty("latest_findings").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, target.LastRetryLifecycleCount);
    }

    [Fact]
    public async Task DeliverySlotKeepsOneClaimAndPersistentWorktreeAcrossEverySupportedEntryStage()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var entries = new[]
        {
            (Label: (string?)null, Roles: new[] { AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Qa }),
            (Label: (string?)"stage:implement", Roles: new[] { AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Qa }),
            (Label: (string?)"stage:review", Roles: new[] { AgentRole.Reviewer, AgentRole.Qa }),
            (Label: (string?)"stage:qa", Roles: new[] { AgentRole.Qa })
        };

        foreach (var entry in entries)
        {
            var bead = entry.Label is null
                ? target.CreateBead($"Delivery slot {Guid.NewGuid():N}", "task")
                : target.CreateBead($"Delivery slot {Guid.NewGuid():N}", "task", entry.Label);
            var observations = System.IO.Path.Combine(target.RootPath, $"{bead}-observations.jsonl");
            var actions = entry.Roles.ToDictionary(role => role, role => role switch
            {
                AgentRole.Implementer => entry.Label is null ? "slot-implement-unlabeled" : "slot-implement",
                AgentRole.Reviewer => "slot-review",
                AgentRole.Qa => "slot-qa",
                _ => throw new InvalidOperationException($"Unexpected Delivery role {role}.")
            });

            var outcome = await target.RunDeliverySlotAsync(bead, actions, observations);

            var failureLog = outcome.LastStage is { } lastStage && File.Exists(lastStage.StageResult.LogFilePath)
                ? File.ReadAllText(lastStage.StageResult.LogFilePath)
                : "(no invocation log)";
            Assert.True(outcome.MergeReady,
                $"{outcome.Disposition}: {outcome.Diagnostic}; last role {outcome.LastRole}; "
                + $"retry status {outcome.LastStage?.Status}; failure {outcome.LastStage?.StageResult.Failure?.Kind}: {outcome.LastStage?.StageResult.Failure?.Reason}; "
                + $"logs {outcome.LastStage?.StageResult.LogFilePath}\n{failureLog}");
            Assert.Equal(entry.Roles, target.LastSlotRoles);
            var claimWrites = AgentStageTargetFixture.ClaimWrites(target.LastSlotRequests, bead);
            Assert.Single(claimWrites, request => AgentStageTargetFixture.ClaimWriteStatus(request) == "open");
            Assert.Single(claimWrites, request => AgentStageTargetFixture.ClaimWriteStatus(request) == "in_progress");
            var final = target.ReadBead(bead);
            Assert.Equal("open", final.Status);
            Assert.Null(final.Assignee);
            Assert.Equal("stage:merge", target.ReadState(bead).Stage);

            var records = File.ReadAllLines(observations)
                .Select(line => JsonSerializer.Deserialize<SlotObservation>(line)!)
                .ToArray();
            Assert.Equal(entry.Roles.Length, records.Length);
            Assert.Equal(entry.Roles, records.Select(record => record.Role).ToArray());
            Assert.All(records, record =>
            {
                Assert.Equal("in_progress", record.Status);
                Assert.Equal("nightweaver:demo", record.Assignee);
                Assert.Equal(target.WorktreePath(bead), record.Worktree);
            });
            Assert.Equal($"nightweaver/{bead}", records[^1].Branch);
            Assert.All(records, record => Assert.Equal(records[0].Worktree, record.Worktree));
            var expectedInputStages = entry.Label switch
            {
                null or "stage:implement" => new[] { entry.Label, "stage:review", "stage:qa" },
                "stage:review" => new[] { "stage:review", "stage:qa" },
                _ => new[] { "stage:qa" }
            };
            Assert.Equal(expectedInputStages, records.Select(record => record.Stage).ToArray());

            var worktree = target.WorktreePath(bead);
            Assert.Equal($"nightweaver/{bead}", target.CurrentBranch(bead));
            var status = target.RunTarget("git", ["status", "--porcelain"], worktree);
            Assert.True(status.Length == 0,
                $"Entry {entry.Label ?? "(unlabeled)"} left worktree dirty:\n{status}"
                + target.RunTarget("git", ["diff", "--", ".gitignore", "README.md"], worktree));
            if (entry.Roles.Contains(AgentRole.Implementer))
            {
                Assert.True(File.Exists(System.IO.Path.Combine(worktree, "delivery-slot.txt")));
                Assert.True(File.Exists(System.IO.Path.Combine(worktree, ".nightweaver-ignored", "slot-sentinel.bin")));
                Assert.Contains("slot fixture implementation", target.RunTarget("git", ["log", "-1", "--format=%s"], worktree), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task DistinctRetryFailuresExhaustDefaultBudgetAfterThreeNativeAttempts()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var bead = target.CreateBead("Retry exhaustion integration", "task", "stage:implement");

        var outcome = await target.RunRetryAgentAsync(bead, AgentRole.Implementer,
            ["failure", "failure", "failure"], classifierAction: "different");

        Assert.Equal(OperationalRetryStatus.Escalated, outcome.Status);
        Assert.Equal(3, outcome.AttemptCount);
        Assert.Equal(NeedsHumanCause.OperationalRetriesExhausted, outcome.EscalationCause);
        Assert.Equal(DuplicateFailureVerdict.Different, outcome.LastClassification!.Verdict);
        Assert.Equal(3, target.LastRetryLifecycleCount);
        var state = target.ReadState(bead);
        Assert.Equal("stage:implement", state.Stage);
        Assert.True(state.NeedsHuman);
        Assert.Equal("open", target.ReadBead(bead).Status);
        Assert.Null(target.ReadBead(bead).Assignee);
        Assert.False(Directory.Exists(target.WorktreePath(bead)));
        Assert.False(Directory.Exists(System.IO.Path.Combine(target.Host.TmpRoot(ProjectSlug.Parse("demo")), bead)));
        Assert.Equal(1, GitFixture.RunGit(target.Path, ["show-ref", "--verify", "--quiet", $"refs/heads/nightweaver/{bead}"], allowFailure: true));
        var logs = Directory.GetFiles(System.IO.Path.Combine(target.Host.LogsRoot(ProjectSlug.Parse("demo")), bead), "*.log");
        Assert.Equal(4, logs.Length); // three lifecycle invocations and one comparison
    }

    [Fact]
    public async Task RealGitBdAndWindowsJobsRunPlanningDeliveryAndAllOperationalClassifications()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        using var foreign = new BeadsFixture("foreign tracker");
        var foreignBefore = Snapshot.FileTree(foreign.Path);
        target.SeedRootSentinels();
        var rootBefore = target.CaptureRootState();

        var planner = target.CreateBead("Planning transition", "feature", "stage:plan");
        var planning = await target.RunAgentAsync(planner, AgentRole.Planner, "plan-challenge", AgentCli.Claude);
        AssertSuccess(planning, "plan-challenge");
        Assert.Equal("stage:plan-challenge", target.ReadState(planner).Stage);

        var challenger = target.CreateBead("Plan challenge transition", "feature", "stage:plan-challenge");
        var challenged = await target.RunAgentAsync(challenger, AgentRole.PlanChallenger, "challenge", AgentCli.Codex);
        AssertSuccess(challenged, "challenge");
        Assert.Equal("stage:plan-approved", target.ReadState(challenger).Stage);

        var approved = target.CreateBead("Materialized plan", "bug", "stage:plan-approved");
        var materialized = await target.RunAgentAsync(approved, AgentRole.Planner, "planned-materialization", AgentCli.Claude);
        AssertSuccess(materialized, "planned-materialization");
        Assert.True(target.ReadState(approved).Planned);
        var links = await new BeadsReader(new SystemProcessRunner()).ReadUpDependenciesAsync(
            new(target.Path, "main"), approved, TestContext.Current.CancellationToken);
        Assert.Contains(links, link => link.DependsOnId == approved && link.Type == "discovered-from"
            && target.ReadBead(link.IssueId) is { IssueType: "task", Status: "open" });

        var delivery = target.CreateBead("Delivery transition", "task", "stage:implement");
        var delivered = await target.RunAgentAsync(delivery, AgentRole.Implementer, "review-commit", AgentCli.Codex);
        AssertSuccess(delivered, "review-commit");
        Assert.Equal("stage:review", target.ReadState(delivery).Stage);

        var reviewId = target.CreateBead("Reviewer transition", "task", "stage:review");
        var reviewed = await target.RunAgentAsync(reviewId, AgentRole.Reviewer, "review-qa", AgentCli.Claude);
        AssertSuccess(reviewed, "review-qa");
        Assert.Equal("stage:qa", target.ReadState(reviewId).Stage);

        var qaId = target.CreateBead("QA transition", "task", "stage:qa");
        var qa = await target.RunAgentAsync(qaId, AgentRole.Qa, "qa-merge", AgentCli.Codex);
        AssertSuccess(qa, "qa-merge");
        Assert.Equal("stage:merge", target.ReadState(qaId).Stage);

        var merger = target.CreateBead("Merger closure", "task", "stage:merge");
        var merged = await target.RunAgentAsync(merger, AgentRole.Merger, "close-commit", AgentCli.Codex);
        AssertSuccess(merged, "close-commit");
        Assert.Equal("closed", target.ReadState(merger).Status);

        var rejectionId = target.CreateBead("Correct repair increment", "task", "stage:review");
        var rejection = await target.RunAgentAsync(rejectionId, AgentRole.Reviewer, "reject-with-increment", AgentCli.Claude);
        AssertSuccess(rejection, "reject-with-increment");
        Assert.Equal(1, target.ReadState(rejectionId).RepairCycles);

        var nonzeroId = target.CreateBead("Nonzero exit", "task", "stage:implement");
        var nonzero = await target.RunAgentAsync(nonzeroId, AgentRole.Implementer, "review-nonzero", AgentCli.Codex);
        AssertFailure(nonzero, AgentOperationalFailureKind.NonZeroExit);
        Assert.Contains("19", nonzero.Failure!.Reason, StringComparison.Ordinal);

        var timeoutId = target.CreateBead("Timeout", "task", "stage:implement");
        var timedOut = await target.RunAgentAsync(timeoutId, AgentRole.Implementer, "timeout", AgentCli.Codex);
        AssertFailure(timedOut, AgentOperationalFailureKind.TimedOut);
        Assert.Contains(timedOut.Diagnostics, diagnostic => diagnostic.StartsWith("IllegalBeadTransition:", StringComparison.Ordinal));

        var dirtyId = target.CreateBead("Dirty worktree", "task", "stage:implement");
        var dirty = await target.RunAgentAsync(dirtyId, AgentRole.Implementer, "review-dirty", AgentCli.Codex);
        AssertFailure(dirty, AgentOperationalFailureKind.DirtyWorktree);
        Assert.Contains("fixture-dirty.txt", dirty.Failure!.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(System.IO.Path.Combine(target.WorktreePath(dirtyId), "fixture-dirty.txt")),
            "Post-exit failure must retain the dirty worktree for diagnosis.");

        var headId = target.CreateBead("Forbidden read-only commit", "feature", "stage:plan");
        var headChanged = await target.RunAgentAsync(headId, AgentRole.Planner, "plan-commit", AgentCli.Claude);
        AssertFailure(headChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var challengerHeadId = target.CreateBead("Forbidden challenger commit", "feature", "stage:plan-challenge");
        var challengerHeadChanged = await target.RunAgentAsync(challengerHeadId, AgentRole.PlanChallenger, "challenge-commit", AgentCli.Codex);
        AssertFailure(challengerHeadChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var reviewerHeadId = target.CreateBead("Forbidden reviewer commit", "task", "stage:review");
        var reviewerHeadChanged = await target.RunAgentAsync(reviewerHeadId, AgentRole.Reviewer, "review-qa-commit", AgentCli.Claude);
        AssertFailure(reviewerHeadChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var qaHeadId = target.CreateBead("Forbidden QA commit", "task", "stage:qa");
        var qaHeadChanged = await target.RunAgentAsync(qaHeadId, AgentRole.Qa, "qa-merge-commit", AgentCli.Codex);
        AssertFailure(qaHeadChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var noOpId = target.CreateBead("Illegal no-op", "task", "stage:implement");
        var noOp = await target.RunAgentAsync(noOpId, AgentRole.Implementer, "noop", AgentCli.Codex);
        AssertFailure(noOp, AgentOperationalFailureKind.IllegalBeadTransition);

        var missingCounterId = target.CreateBead("Missing repair increment", "task", "stage:review");
        var missingCounter = await target.RunAgentAsync(missingCounterId, AgentRole.Reviewer, "reject-without-increment", AgentCli.Claude);
        AssertFailure(missingCounter, AgentOperationalFailureKind.IllegalBeadTransition);
        Assert.Contains("repair_cycles", missingCounter.Failure!.Reason, StringComparison.Ordinal);

        Assert.Equal(rootBefore, target.CaptureRootState());
        Assert.Equal(foreignBefore, Snapshot.FileTree(foreign.Path));
    }

    private static void AssertSuccess(AgentStageResult result, string action)
    {
        Assert.True(result.Succeeded, result.Failure?.Reason);
        Assert.True(File.Exists(result.LogFilePath), result.LogFilePath);
        Assert.Contains($"fixture action completed: {action}", File.ReadAllText(result.LogFilePath), StringComparison.Ordinal);
    }

    private static void AssertFailure(AgentStageResult result, AgentOperationalFailureKind expected)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure!.Kind);
        Assert.True(File.Exists(result.LogFilePath), result.LogFilePath);
    }

    private sealed record SlotObservation(string BeadId, AgentRole Role, string? Stage, string Status, string? Assignee,
        string Worktree, string Branch, string Head, string Action);

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString()!;
            yield break;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var value in StringValues(item)) yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var value in StringValues(property.Value)) yield return value;
        }
    }

    private sealed class AgentStageTargetFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private long nextRun;

        internal AgentStageTargetFixture()
        {
            TargetPath = temp.CreateDirectory("target repository with spaces");
            OriginPath = System.IO.Path.Combine(temp.Path, "origin.git");
            Directory.CreateDirectory(OriginPath);
            Run("git", ["init", "--bare", OriginPath], temp.Path);
            Run("git", ["init", "-b", "main"], TargetPath);
            Run("git", ["config", "core.autocrlf", "false"], TargetPath);
            Run("git", ["config", "user.name", "Nightweaver Fixture"], TargetPath);
            Run("git", ["config", "user.email", "fixture@example.invalid"], TargetPath);
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "README.md"), "fixture base\n", new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(TargetPath, ".gitignore"), ".nightweaver-ignored/\n", new UTF8Encoding(false));
            Run("git", ["add", "README.md", ".gitignore"], TargetPath);
            Run("git", ["commit", "-m", "fixture base"], TargetPath);
            Run("git", ["remote", "add", "origin", OriginPath], TargetPath);
            Run("git", ["push", "--set-upstream", "origin", "main"], TargetPath);

            Host = new HostPaths(temp.CreateDirectory("nightweaver host"));
            Directory.CreateDirectory(System.IO.Path.Combine(Host.Root, "prompts"));
            foreach (var file in new[] { "planner.md", "plan-challenger.md", "implementer.md", "reviewer.md", "qa.md", "merger.md" })
                File.WriteAllText(System.IO.Path.Combine(Host.Root, "prompts", file), $"fixture prompt for {file}\n", new UTF8Encoding(false));

            FakeCliRoot = temp.CreateDirectory("fake native agent cli");
            CopyFixtureCli("AgentInvocationFixture.exe");
            CopyFixtureCli("AgentInvocationFixture.dll");
            CopyFixtureCli("AgentInvocationFixture.deps.json");
            CopyFixtureCli("AgentInvocationFixture.runtimeconfig.json");

            UserHome = temp.CreateDirectory("isolated bd user");
            BeadsEnvironment = new Dictionary<string, string?>(BeadsCommand.Create(TargetPath).Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["HOME"] = UserHome,
                ["USERPROFILE"] = UserHome,
                ["APPDATA"] = UserHome,
                ["XDG_CONFIG_HOME"] = UserHome,
                ["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(UserHome, "gitconfig"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_AUTHOR_NAME"] = "Nightweaver Fixture",
                ["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid",
                ["GIT_COMMITTER_NAME"] = "Nightweaver Fixture",
                ["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid"
            };
            Run("bd", ["init", "--prefix", "stage", "--non-interactive", "--skip-agents", "--skip-hooks"], TargetPath, BeadsEnvironment);
            _ = CreateBead("Control", "task");
        }

        private string TargetPath { get; }
        private string OriginPath { get; }
        internal string RootPath => temp.Path;
        internal HostPaths Host { get; }
        private string FakeCliRoot { get; }
        private string UserHome { get; }
        private IReadOnlyDictionary<string, string?> BeadsEnvironment { get; }
        internal IReadOnlyList<ProcessRequest> LastRetryRequests { get; private set; } = [];
        internal int LastRetryLifecycleCount { get; private set; }
        internal IReadOnlyList<ProcessRequest> LastSlotRequests { get; private set; } = [];
        internal IReadOnlyList<AgentRole> LastSlotRoles { get; private set; } = [];

        internal string Path => TargetPath;
        internal string RunTarget(string tool, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
            Run(tool, arguments, workingDirectory ?? TargetPath, BeadsEnvironment);
        internal string WorktreePath(string beadId) => System.IO.Path.Combine(Host.WorktreesRoot(ProjectSlug.Parse("demo")), beadId);
        internal string CurrentBranch(string beadId) => Run("git", ["branch", "--show-current"], WorktreePath(beadId)).Trim();

        internal void SeedRootSentinels()
        {
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "README.md"), "root checkout edit to preserve\n", new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "root-untracked-sentinel.txt"), "preserve me\n", new UTF8Encoding(false));
        }

        internal string CreateBead(string title, string type, params string[] labels)
        {
            var created = Run("bd", ["create", "--title", title, "--type", type, "--json"], TargetPath, BeadsEnvironment);
            using var document = JsonDocument.Parse(created);
            var id = document.RootElement.GetProperty("id").GetString()!;
            if (labels.Length > 0)
            {
                var update = new List<string> { "update", id };
                foreach (var label in labels) { update.Add("--add-label"); update.Add(label); }
                Run("bd", update, TargetPath, BeadsEnvironment);
            }
            return id;
        }

        internal BeadWorkflowState ReadState(string beadId) => BeadWorkflowState.Parse(ReadBead(beadId));

        internal BeadSnapshot ReadBead(string beadId)
        {
            var inventory = BeadsReader.Parse(Run("bd", BeadsCommand.Create(TargetPath).Arguments, TargetPath, BeadsEnvironment));
            return inventory.ById[beadId];
        }

        internal string ReadComments(string beadId) => Run("bd",
            ["--readonly", "--sandbox", "comments", beadId, "--json"], TargetPath, BeadsEnvironment);

        internal async Task<AgentStageResult> RunAgentAsync(string beadId, AgentRole role, string action, AgentCli cli)
        {
            var runId = Interlocked.Increment(ref nextRun).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var pidFile = System.IO.Path.Combine(temp.Path, "pids", runId + ".txt");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pidFile)!);
            var project = CreateProject(cli);
            var runner = new SystemProcessRunner();
            var preparer = new AgentInvocationPreparer();
            async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig projectConfig, AgentRole invocationRole,
                AgentDispatchContext context, string worktreePath, CancellationToken token)
            {
                var invocation = await preparer.PrepareAsync(hostPaths, projectConfig, invocationRole, context, worktreePath, token);
                var executable = AgentCliNames.ToConfigValue(cli);
                Assert.Equal(executable, invocation.Executable);
                var canonical = System.IO.Path.GetFullPath(System.IO.Path.Combine(hostPaths.WorktreesRoot(projectConfig.Name), context.BeadId));
                Assert.Equal(canonical, worktreePath);
                Assert.Equal(canonical, invocation.WorkingDirectory);
                var branch = Run("git", ["branch", "--show-current"], worktreePath).Trim();
                if (invocationRole is AgentRole.Planner or AgentRole.PlanChallenger)
                    Assert.Equal("", branch);
                else
                    Assert.Equal($"nightweaver/{context.BeadId}", branch);
                return invocation;
            }

            Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
                PreparedAgentInvocation invocation, CancellationToken token)
            {
                var timeout = action == "timeout" ? TimeSpan.FromMilliseconds(350) : invocation.Timeout;
                var actual = new PreparedAgentInvocation(invocation.Executable, invocation.Arguments, invocation.WorkingDirectory,
                    invocation.StandardInput, invocation.EnvironmentOverrides, timeout, invocation.InvocationId,
                    invocation.TemporaryDirectory, invocation.LogFilePath);
                return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, actual, token);
            }

            var coordinator = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
            var oldAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION");
            var oldPidFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE");
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            AgentStageResult result;
            try
            {
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", action);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", pidFile);
                Environment.SetEnvironmentVariable("PATH", FakeCliRoot + System.IO.Path.PathSeparator + (oldPath ?? ""));
                result = await coordinator.RunAsync(Host, project, new(TargetPath, "main"), beadId, role,
                    TestContext.Current.CancellationToken);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", oldAction);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", oldPidFile);
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
            AssertFixtureExited(pidFile);
            return result;
        }

        internal async Task<OperationalRetryOutcome> RunRetryAgentAsync(
            string beadId,
            AgentRole role,
            IReadOnlyList<string> actions,
            bool verifyResetSentinels = false,
            string? classifierAction = null)
        {
            const string actor = "nightweaver:demo";
            var claimEnvironment = new Dictionary<string, string?>(BeadsEnvironment, StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = actor
            };
            Run("bd", ["update", beadId, "--claim"], TargetPath, claimEnvironment);
            Run("bd", ["update", beadId, "--set-metadata", "repair_cycles=4"], TargetPath, claimEnvironment);

            var project = CreateProject(AgentCli.Codex);
            var systemRunner = new SystemProcessRunner();
            var runner = new RecordingProcessRunner((request, token) => systemRunner.RunAsync(request, token));
            var preparer = new AgentInvocationPreparer();
            var pidFiles = new List<string>();
            var actionIndex = 0;
            async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig projectConfig, AgentRole invocationRole,
                AgentDispatchContext context, string worktreePath, CancellationToken token)
            {
                var invocation = await preparer.PrepareAsync(hostPaths, projectConfig, invocationRole, context, worktreePath, token);
                var canonical = System.IO.Path.GetFullPath(System.IO.Path.Combine(hostPaths.WorktreesRoot(projectConfig.Name), context.BeadId));
                Assert.Equal(canonical, worktreePath);
                Assert.Equal(canonical, invocation.WorkingDirectory);
                return invocation;
            }

            Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
                PreparedAgentInvocation invocation, CancellationToken token)
            {
                var index = Interlocked.Increment(ref actionIndex) - 1;
                Assert.True(index < actions.Count, "Retry coordinator launched more native Agents than the test provided actions.");
                var pidFile = System.IO.Path.Combine(temp.Path, "retry-pids", index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pidFile)!);
                pidFiles.Add(pidFile);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", actions[index]);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", pidFile);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", null);
                if (index == 1 && verifyResetSentinels)
                {
                    Assert.Equal("fixture base\n", File.ReadAllText(System.IO.Path.Combine(invocation.WorkingDirectory, "README.md"))
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
                    Assert.False(File.Exists(System.IO.Path.Combine(invocation.WorkingDirectory, "retry-untracked.txt")));
                    Assert.True(File.Exists(System.IO.Path.Combine(invocation.WorkingDirectory, ".nightweaver-ignored", "keep.bin")));
                }
                return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation, token);
            }

            var oldActor = Environment.GetEnvironmentVariable("BD_ACTOR");
            var oldAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION");
            var oldPidFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE");
            var oldClassifierAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION");
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("BD_ACTOR", actor);
                Environment.SetEnvironmentVariable("PATH", FakeCliRoot + System.IO.Path.PathSeparator + (oldPath ?? ""));
                var stageCoordinator = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
                DuplicateFailureClassifier? classifier = null;
                if (classifierAction is not null)
                {
                    classifier = new DuplicateFailureClassifier(execute: async (hostPaths, projectSlug, invocation, limit, token) =>
                    {
                        var priorAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION");
                        Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", classifierAction);
                        try
                        {
                            return await AgentInvocationExecutor.ExecuteWithStandardOutputCaptureAsync(
                                hostPaths, projectSlug, invocation, limit, token);
                        }
                        finally
                        {
                            Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", priorAction);
                        }
                    });
                }
                var retryCoordinator = new OperationalRetryCoordinator(runner, stages: stageCoordinator, classifier: classifier);
                var outcome = await retryCoordinator.RunAsync(Host, project, new(TargetPath, "main"), beadId, role, actor,
                    TestContext.Current.CancellationToken);
                LastRetryRequests = runner.Requests.ToArray();
                Assert.True(pidFiles.Count <= actions.Count);
                LastRetryLifecycleCount = pidFiles.Count;
                foreach (var pidFile in pidFiles) AssertFixtureExited(pidFile);
                return outcome;
            }
            finally
            {
                Environment.SetEnvironmentVariable("BD_ACTOR", oldActor);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", oldAction);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", oldPidFile);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", oldClassifierAction);
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
        }

        internal async Task<DeliverySlotOutcome> RunDeliverySlotAsync(
            string beadId,
            Dictionary<AgentRole, string> actions,
            string observationPath)
        {
            const string actor = "nightweaver:demo";
            var project = CreateProject(AgentCli.Codex);
            var systemRunner = new SystemProcessRunner();
            var runner = new RecordingProcessRunner((request, token) => systemRunner.RunAsync(request, token));
            var preparer = new AgentInvocationPreparer();
            var invocationRoles = new Dictionary<string, AgentRole>(StringComparer.Ordinal);
            var pidFiles = new List<string>();
            var roles = new List<AgentRole>();
            long sequence = 0;

            async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig projectConfig,
                AgentRole role, AgentDispatchContext context, string worktreePath, CancellationToken token)
            {
                var invocation = await preparer.PrepareAsync(hostPaths, projectConfig, role, context, worktreePath, token);
                var canonical = System.IO.Path.GetFullPath(System.IO.Path.Combine(hostPaths.WorktreesRoot(projectConfig.Name), context.BeadId));
                Assert.Equal(canonical, worktreePath);
                Assert.Equal(canonical, invocation.WorkingDirectory);
                Assert.Equal($"nightweaver/{context.BeadId}", Run("git", ["branch", "--show-current"], worktreePath).Trim());
                invocationRoles.Add(invocation.InvocationId, role);
                return invocation;
            }

            Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
                PreparedAgentInvocation invocation, CancellationToken token)
            {
                var role = invocationRoles[invocation.InvocationId];
                if (!actions.TryGetValue(role, out var action))
                    throw new InvalidOperationException($"No fixture action was provided for {role}.");
                roles.Add(role);
                var pidFile = System.IO.Path.Combine(temp.Path, "slot-pids", Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pidFile)!);
                pidFiles.Add(pidFile);
                AppendSlotObservation(observationPath, beadId, role, invocation.WorkingDirectory, action);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", action);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", pidFile);
                return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation, token);
            }

            var oldActor = Environment.GetEnvironmentVariable("BD_ACTOR");
            var oldAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION");
            var oldPidFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE");
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("BD_ACTOR", actor);
                Environment.SetEnvironmentVariable("PATH", FakeCliRoot + System.IO.Path.PathSeparator + (oldPath ?? ""));
                var stages = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
                var retries = new OperationalRetryCoordinator(runner, stages: stages);
                var slotRunner = new DeliveryRoleRunner((paths, config, repository, requestedBead, role, expectedAssignee, token, progress) =>
                    retries.RunAsync(paths, config, repository, requestedBead, role, expectedAssignee, token, progress));
                var slot = new DeliverySlotCoordinator(runner, slotRunner);
                var outcome = await slot.RunAsync(Host, project, new(TargetPath, "main"), beadId,
                    TestContext.Current.CancellationToken);
                LastSlotRequests = runner.Requests.ToArray();
                LastSlotRoles = roles.ToArray();
                foreach (var pidFile in pidFiles) AssertFixtureExited(pidFile);
                return outcome;
            }
            finally
            {
                Environment.SetEnvironmentVariable("BD_ACTOR", oldActor);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", oldAction);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", oldPidFile);
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
        }

        private void AppendSlotObservation(string path, string beadId, AgentRole role, string worktreePath, string action)
        {
            var bead = ReadBead(beadId);
            var state = BeadWorkflowState.Parse(bead);
            var observation = new SlotObservation(beadId, role, state.Stage, bead.Status, bead.Assignee,
                worktreePath, Run("git", ["branch", "--show-current"], worktreePath).Trim(),
                Run("git", ["rev-parse", "HEAD"], worktreePath).Trim(), action);
            File.AppendAllText(path, JsonSerializer.Serialize(observation) + Environment.NewLine, new UTF8Encoding(false));
        }

        internal static ProcessRequest[] ClaimWrites(IReadOnlyList<ProcessRequest> requests, string beadId) =>
            requests.Where(request => request.FileName == "bd"
                && request.Arguments.ToList().IndexOf("update") is var updateIndex
                && updateIndex >= 0 && updateIndex + 1 < request.Arguments.Count && request.Arguments[updateIndex + 1] == beadId
                && request.Arguments.Contains("--if-assignee", StringComparer.Ordinal)
                && request.Arguments.Contains("--if-status", StringComparer.Ordinal)).ToArray();

        internal static string? ClaimWriteStatus(ProcessRequest request)
        {
            var index = request.Arguments.ToList().IndexOf("--if-status");
            return index >= 0 && index + 1 < request.Arguments.Count ? request.Arguments[index + 1] : null;
        }

        internal string CaptureRootState()
        {
            var indexPath = System.IO.Path.Combine(TargetPath, ".git", "index");
            using var index = File.OpenRead(indexPath);
            return string.Join("\n",
                Run("git", ["rev-parse", "HEAD"], TargetPath).Trim(),
                Run("git", ["symbolic-ref", "--short", "HEAD"], TargetPath).Trim(),
                Convert.ToHexString(SHA256.HashData(index)),
                Run("git", ["status", "--porcelain"], TargetPath),
                File.ReadAllText(System.IO.Path.Combine(TargetPath, "README.md")),
                File.ReadAllText(System.IO.Path.Combine(TargetPath, "root-untracked-sentinel.txt")));
        }

        private ProjectConfig CreateProject(AgentCli cli)
        {
            var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(cli, null, "low", 1));
            return new(ProjectSlug.Parse("demo"), "fixture.json", new(TargetPath, "main"), new(1, 1, 1), new(3, 2),
                new(cli, null, "low", 1), new RoleSettings(roles));
        }

        private void CopyFixtureCli(string name)
        {
            var source = System.IO.Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(source)) throw new FileNotFoundException($"Native Agent fixture component '{name}' was not built.", source);
            File.Copy(source, System.IO.Path.Combine(FakeCliRoot, name), overwrite: true);
            if (name == "AgentInvocationFixture.exe")
            {
                File.Copy(source, System.IO.Path.Combine(FakeCliRoot, "claude.exe"), overwrite: true);
                File.Copy(source, System.IO.Path.Combine(FakeCliRoot, "codex.exe"), overwrite: true);
            }
        }

        private static void AssertFixtureExited(string pidFile)
        {
            Assert.True(File.Exists(pidFile), $"The native fixture did not create its process handshake: {pidFile}");
            var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(process.WaitForExit(5000), $"Fixture process {pid} survived executor completion.");
            }
            catch (ArgumentException)
            {
                // The process exited before this check.
            }
        }

        private static string Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            if (environment is not null)
            {
                foreach (var pair in environment)
                {
                    if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
                    else startInfo.Environment[pair.Key] = pair.Value;
                }
            }
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(workingDirectory, "missing-global-config");
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdoutTask, stderrTask);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}: {stderrTask.Result}{stdoutTask.Result}");
            return stdoutTask.Result;
        }

        public void Dispose() => temp.Dispose();
    }
}
