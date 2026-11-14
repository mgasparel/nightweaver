using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class AgentStageDeliveryIntegrationTests : AgentStageIntegrationTestSupport
{

    [Fact]
    public async Task DeliverySlotKeepsOneClaimAndPersistentWorktreeThroughMergeHandoff()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var entries = new[]
        {
            // Entry-stage selection is covered by DeliverySlotCoordinatorTests.
            (Label: (string?)null, Roles: new[] { AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Qa })
        };

        var entryBeads = target.CreateBeads(entries.Select(entry =>
            (Title: $"Delivery slot {entry.Label ?? "(unlabeled)"}", Type: "task",
                Labels: entry.Label is { } label ? new[] { label } : Array.Empty<string>())).ToArray());
        foreach (var entry in entries)
        {
            var bead = entryBeads[$"Delivery slot {entry.Label ?? "(unlabeled)"}"];
            if (entry.Label is "stage:review" or "stage:qa") target.SeedRemoteDeliveryBranch(bead);
            var observations = System.IO.Path.Combine(target.RootPath, $"{bead}-observations.jsonl");
            var actions = entry.Roles.Select(role => (role, Action: role switch
            {
                AgentRole.Implementer => entry.Label is null ? "slot-implement-unlabeled" : "slot-implement",
                AgentRole.Reviewer => "slot-review",
                AgentRole.Qa => "slot-qa",
                _ => throw new InvalidOperationException($"Unexpected Delivery role {role}.")
            })).ToArray();

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
            var inProgressWrites = claimWrites.Where(request => AgentStageTargetFixture.ClaimWriteStatus(request) == "in_progress").ToArray();
            var expectedInProgressWrites = entry.Roles.Length;
            Assert.True(inProgressWrites.Length == expectedInProgressWrites, string.Join(Environment.NewLine,
                claimWrites.Select(request => string.Join(' ', request.Arguments))));
            var final = target.ReadBead(bead);
            Assert.Equal("open", final.Status);
            Assert.Null(final.Assignee);
            Assert.Equal("stage:merge", BeadWorkflowState.Parse(final).Stage);

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
                Assert.Equal(3 - record.RepairCycles, record.RemainingRepairBudget);
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
            var diff = status.Length == 0 ? string.Empty
                : target.RunTarget("git", ["diff", "--", ".gitignore", "README.md"], worktree);
            Assert.True(status.Length == 0,
                $"Entry {entry.Label ?? "(unlabeled)"} left worktree dirty:\n{status}{diff}");
            if (entry.Roles.Contains(AgentRole.Implementer))
            {
                Assert.True(File.Exists(System.IO.Path.Combine(worktree, "delivery-slot.txt")));
                Assert.True(File.Exists(System.IO.Path.Combine(worktree, ".nightweaver-ignored", "slot-sentinel.bin")));
                Assert.Contains("slot fixture implementation", target.RunTarget("git", ["log", "-1", "--format=%s"], worktree), StringComparison.Ordinal);
            }
        }
    }

    private static string? ArgumentAfter(ProcessRequest request, string option)
    {
        var index = request.Arguments.ToList().IndexOf(option);
        return index >= 0 && index + 1 < request.Arguments.Count ? request.Arguments[index + 1] : null;
    }
}

public sealed class PlanningNativeTurnTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task NativeApprovedGraphPublishesAndEntersDeliveryWithoutAnotherAgentTurn()
    {
        using var target = new AgentStageTargetFixture();
        var product = target.CreateBead("Requested behavior", "feature", "ready-for-planning");
        var before = target.ReadInventory().ById.Count;
        var draft = await target.RunRetryAgentAsync(product, AgentRole.Planner, ["planning-draft"]);
        Assert.True(draft.Succeeded, ReadInvocationLog(draft.StageResult.LogFilePath));
        Assert.Equal("stage:plan-challenge", draft.StageResult.VerifiedState?.Stage);
        var review = await target.RunRetryAgentAsync(product, AgentRole.PlanChallenger, ["planning-approve"]);
        Assert.True(review.Succeeded, ReadInvocationLog(review.StageResult.LogFilePath));
        Assert.Equal("stage:plan-approved", review.StageResult.VerifiedState?.Stage);
        Assert.NotNull(review.StageResult.PlanningApproval);
        Assert.True(review.StageResult.PublicationReceipt?.ClaimReleased);
        Assert.Equal(before, target.ReadInventory().ById.Count);
        Assert.Contains("planning_approval", target.ReadComments(product), StringComparison.Ordinal);
        Assert.True(Directory.Exists(target.WorktreePath(product)));
        var publication = await target.RunRetryAgentAsync(product, AgentRole.Planner, []);
        Assert.True(publication.Succeeded, publication.StageResult.Failure?.Reason);
        Assert.True(publication.StageResult.VerifiedState?.Planned);
        Assert.Equal(0, target.LastRetryLifecycleCount);
        var generated = target.ReadInventory().ById.Values.Where(b => PlanningPublisher.Metadata(b, "publication_product") == product).ToArray();
        Assert.Equal(2, generated.Length);
        var task = Assert.Single(generated, b => PlanningPublisher.Metadata(b, "publication_entry") == "slice-1");
        var dependent = Assert.Single(generated, b => PlanningPublisher.Metadata(b, "publication_entry") == "slice-2");
        Assert.DoesNotContain("plan-unpublished", task.Labels);
        Assert.True(PlanningPublicationGate.Allows(task, target.ReadInventory()), target.ReadBead(product).Payload.GetRawText() + task.Payload.GetRawText());
        var ready = BeadsReader.Parse(target.RunTarget("bd", ["ready", "--unassigned", "--limit", "0", "--json"]));
        Assert.Contains(task.Id, ready.ById.Keys);
        Assert.DoesNotContain(dependent.Id, ready.ById.Keys);
        // Cross the publication-to-Delivery boundary with a real Implementer.
        // Full Reviewer/QA claim continuity is exercised by the separate native
        // DeliverySlotKeepsOneClaimAndPersistentWorktreeThroughMergeHandoff case.
        var delivery = await target.RunRetryAgentAsync(task.Id, AgentRole.Implementer, ["slot-implement-unlabeled"]);
        Assert.True(delivery.Succeeded, delivery.StageResult.Failure?.Reason);
        Assert.Equal("stage:review", delivery.StageResult.VerifiedState?.Stage);
        Assert.Equal(1, target.LastRetryLifecycleCount);
        Assert.Equal($"nightweaver/{task.Id}", target.CurrentBranch(task.Id));
        Assert.True(File.Exists(Path.Combine(target.WorktreePath(task.Id), "delivery-slot.txt")));
    }
}

public abstract class AgentStageIntegrationTestSupport
{
    private protected static void AssertSuccess(AgentStageResult result, string action)
    {
        Assert.True(result.Succeeded,
            $"{action}: {result.Failure?.Reason}; before={result.Observations?.BeforeState}; observed={result.Observations?.ObservedState}; "
            + $"beforeAssignee={result.Observations?.BeforeAssignee ?? "(none)"}; observedAssignee={result.Observations?.ObservedAssignee ?? "(none)"}");
        Assert.True(File.Exists(result.LogFilePath), result.LogFilePath);
        Assert.Contains($"fixture action completed: {action}", File.ReadAllText(result.LogFilePath), StringComparison.Ordinal);
    }

    private protected static void AssertFailure(AgentStageResult result, AgentOperationalFailureKind expected)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure!.Kind);
        Assert.True(File.Exists(result.LogFilePath), result.LogFilePath);
    }

    private protected static string ReadInvocationLog(string? path) =>
        path is not null && File.Exists(path) ? File.ReadAllText(path) : "(no invocation log)";

    private protected sealed record SlotObservation(string BeadId, AgentRole Role, string? Stage, int RepairCycles,
        int RemainingRepairBudget, string Status, string? Assignee, string Worktree, string Branch, string Head, string Action);

    private protected static IEnumerable<string> StringValues(JsonElement element)
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

    private protected sealed class AgentStageTargetFixture : IDisposable
    {
        internal const string FixtureOriginUrl = "https://github.com/nightweaver-fixture/target.git";
        internal const string FixtureRepositoryName = "nightweaver-fixture/target";
        internal const string FixturePullRequestUrl = "https://github.com/nightweaver-fixture/target/pull/123";
        private readonly TempDirectory temp = new();
        private long nextRun;
        private static readonly Lazy<AgentStageTargetFixture> Template = new(() =>
        {
            var template = new AgentStageTargetFixture(initializeTemplate: true);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => template.Dispose();
            return template;
        });

        internal AgentStageTargetFixture() : this(initializeTemplate: false) { }

        private AgentStageTargetFixture(bool initializeTemplate)
        {
            TargetPath = temp.CreateDirectory("target repository with spaces");
            OriginPath = System.IO.Path.Combine(temp.Path, "origin.git");
            if (initializeTemplate)
            {
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
            }
            else
            {
                var template = Template.Value;
                TestDirectoryTemplate.Copy(template.RootPath, temp.Path);
                var configPath = System.IO.Path.Combine(TargetPath, ".git", "config");
                File.WriteAllText(configPath, File.ReadAllText(configPath)
                    .Replace(template.OriginPath.Replace('\\', '/'), OriginPath.Replace('\\', '/'), StringComparison.Ordinal)
                    .Replace(template.OriginPath.Replace("\\", "\\\\", StringComparison.Ordinal),
                        OriginPath.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
            }

            Host = new HostPaths(temp.CreateDirectory("nightweaver host"));
            Directory.CreateDirectory(System.IO.Path.Combine(Host.Root, "prompts"));
            foreach (var file in new[] { "planner.md", "plan-challenger.md", "implementer.md", "reviewer.md", "qa.md", "merger.md" })
                File.WriteAllText(System.IO.Path.Combine(Host.Root, "prompts", file), $"fixture prompt for {file}\n", new UTF8Encoding(false));

            FakeCliRoot = temp.CreateDirectory("fake native agent cli");
            CopyFixtureCli("AgentInvocationFixture.exe");
            CopyFixtureCli("AgentInvocationFixture.dll");
            CopyFixtureCli("AgentInvocationFixture.deps.json");
            CopyFixtureCli("AgentInvocationFixture.runtimeconfig.json");
            InheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

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
            if (initializeTemplate)
            {
                Run("bd", ["init", "--prefix", "stage", "--non-interactive", "--skip-agents", "--skip-hooks"], TargetPath, BeadsEnvironment);
                ControlBeadId = CreateBead("Control", "task");
            }
            else
            {
                ControlBeadId = Template.Value.ControlBeadId;
            }
        }

        private string TargetPath { get; }
        internal string ControlBeadId { get; }
        private string OriginPath { get; }
        internal string RootPath => temp.Path;
        internal HostPaths Host { get; }
        private string FakeCliRoot { get; }
        private string InheritedPath { get; }
        private string UserHome { get; }
        private IReadOnlyDictionary<string, string?> BeadsEnvironment { get; }
        internal IReadOnlyList<ProcessRequest> LastRetryRequests { get; private set; } = [];
        internal int LastRetryLifecycleCount { get; private set; }
        internal IReadOnlyList<ProcessRequest> LastSlotRequests { get; private set; } = [];
        internal AgentRole[] LastSlotRoles { get; private set; } = [];
        internal bool FailOutcomePublishResponseAfterCommitOnce { get; set; }
        internal bool FixtureRequiredCheckFails { get; set; }
        internal bool LoseVerifiedTaskCloseResponseAfterCommitOnce { get; set; }
        internal bool ClaimMergerAfterQaHandoffResponseLostOnce { get; set; }
        internal bool MergerClaimedAfterQaHandoffResponseLost { get; private set; }
        internal bool LoseDeliveryAcquireResponseAfterCommitOnce { get; set; }
        internal bool LoseDeliveryReleaseResponseAfterCommitOnce { get; set; }
        internal string? FailDeliveryReleaseForBeadBeforeCommitOnce { get; set; }
        internal bool FailOwnershipRereadAfterLostDeliveryAcquireOnce { get; set; }
        private bool failNextInventoryAfterLostDeliveryAcquire;
        internal TaskCompletionSource<bool>? DeliveryAcquireCommitted { get; set; }
        internal TaskCompletionSource<bool>? ContinueAfterDeliveryAcquireCommit { get; set; }
        internal string? ChangeOutcomeStateAfterEvidenceComment { get; set; }

        internal string Path => TargetPath;
        internal string RunTarget(string tool, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
            Run(tool, arguments, workingDirectory ?? TargetPath, BeadsEnvironment);
        internal string AgentFixtureExecutablePath => System.IO.Path.Combine(FakeCliRoot, "AgentInvocationFixture.exe");

        internal IReadOnlyDictionary<string, string?> CreateRuntimeEnvironment(
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            var environment = new Dictionary<string, string?>(BeadsEnvironment, StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = FakeCliRoot + System.IO.Path.PathSeparator + InheritedPath
            };
            if (overrides is not null)
                foreach (var (key, value) in overrides)
                {
                    if (value is null) environment.Remove(key);
                    else environment[key] = value;
                }
            return environment;
        }

        internal void SendConsoleBreak(int processId)
        {
            var result = Run(AgentFixtureExecutablePath,
                ["--nightweaver-send-control-break", processId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                TargetPath, BeadsEnvironment);
            if (result.Length > 0)
                throw new InvalidOperationException($"Console Ctrl+Break helper failed for process {processId}: {result}");
        }

        internal void ConfigureHostedFixtureOriginWithLocalRewrite()
        {
            var localOriginUrl = OriginPath.Replace('\\', '/');
            Run("git", ["config", "remote.origin.url", FixtureOriginUrl], TargetPath, BeadsEnvironment);
            Run("git", ["config", "remote.origin.pushurl", localOriginUrl], TargetPath, BeadsEnvironment);
            Run("git", ["config", $"url.{localOriginUrl}.insteadOf", FixtureOriginUrl], TargetPath, BeadsEnvironment);
        }

        internal async Task<ProcessResult> RunWithFixturePullRequestAsync(IProcessRunner inner, ProcessRequest request,
            CancellationToken cancellationToken)
        {
            if (request.FileName == "bd" && LoseVerifiedTaskCloseResponseAfterCommitOnce
                && request.Arguments.Contains("update", StringComparer.Ordinal)
                && ArgumentAfter(request, "--status") == "closed"
                && ArgumentAfter(request, "--if-status") == "in_progress")
            {
                var committed = await inner.RunAsync(request, cancellationToken);
                if (committed.ExitCode == 0)
                {
                    LoseVerifiedTaskCloseResponseAfterCommitOnce = false;
                    return new ProcessResult(1, string.Empty, "simulated lost verified-close response after commit");
                }
                return committed;
            }

            if (request.FileName == "bd" && IsInventoryRead(request) && failNextInventoryAfterLostDeliveryAcquire)
            {
                failNextInventoryAfterLostDeliveryAcquire = false;
                return new ProcessResult(1, string.Empty, "simulated unavailable ownership reread after lost acquire response");
            }

            if (request.FileName == "bd" && LoseDeliveryAcquireResponseAfterCommitOnce && IsDeliveryClaimAcquire(request))
            {
                var committed = await inner.RunAsync(request, cancellationToken);
                if (committed.ExitCode == 0)
                {
                    LoseDeliveryAcquireResponseAfterCommitOnce = false;
                    failNextInventoryAfterLostDeliveryAcquire = FailOwnershipRereadAfterLostDeliveryAcquireOnce;
                    FailOwnershipRereadAfterLostDeliveryAcquireOnce = false;
                    DeliveryAcquireCommitted?.TrySetResult(true);
                    if (ContinueAfterDeliveryAcquireCommit is { } continueSignal)
                        await continueSignal.Task.WaitAsync(cancellationToken);
                    return new ProcessResult(1, string.Empty, "simulated lost Delivery acquire response after commit");
                }
                return committed;
            }

            if (request.FileName == "bd" && LoseDeliveryReleaseResponseAfterCommitOnce && IsDeliveryClaimRelease(request))
            {
                var committed = await inner.RunAsync(request, cancellationToken);
                if (committed.ExitCode == 0)
                {
                    LoseDeliveryReleaseResponseAfterCommitOnce = false;
                    return new ProcessResult(1, string.Empty, "simulated lost Delivery release response after commit");
                }
                return committed;
            }

            if (request.FileName == "bd" && FailDeliveryReleaseForBeadBeforeCommitOnce is { } failedBeadId
                && IsDeliveryClaimRelease(request)
                && request.Arguments.Contains(failedBeadId, StringComparer.Ordinal))
            {
                FailDeliveryReleaseForBeadBeforeCommitOnce = null;
                return new ProcessResult(1, string.Empty, $"simulated required claim finalization failure for '{failedBeadId}'");
            }

            if (request.FileName == "bd" && request.Arguments.Contains("comments", StringComparer.Ordinal)
                && request.Arguments.Contains("add", StringComparer.Ordinal)
                && request.Arguments[^1].Contains("nightweaver-delivery-outcome-v1", StringComparison.Ordinal))
            {
                var commentResult = await inner.RunAsync(request, cancellationToken);
                if (commentResult.ExitCode == 0 && ChangeOutcomeStateAfterEvidenceComment is { } changeKind)
                {
                    var beadId = request.Arguments[^2];
                    if (changeKind == "owner")
                        Run("bd", ["update", beadId, "--assignee", "human@example.invalid", "--force", "--json"], TargetPath, BeadsEnvironment);
                    else if (changeKind == "workflow")
                        Run("bd", ["update", beadId, "--remove-label", "stage:implement", "--add-label", "stage:qa", "--json"], TargetPath, BeadsEnvironment);
                    else
                        throw new InvalidOperationException($"Unknown publication race '{changeKind}'.");
                    ChangeOutcomeStateAfterEvidenceComment = null;
                }
                return commentResult;
            }
            var losesQaHandoffResponse = request.FileName == "bd" && ClaimMergerAfterQaHandoffResponseLostOnce
                && request.Arguments.Contains("update", StringComparer.Ordinal)
                && request.Arguments.Contains("--add-label", StringComparer.Ordinal)
                && request.Arguments.Contains("stage:merge", StringComparer.Ordinal);
            var losesImplementerResponse = request.FileName == "bd" && FailOutcomePublishResponseAfterCommitOnce
                && request.Arguments.Contains("update", StringComparer.Ordinal)
                && request.Arguments.Contains("--add-label", StringComparer.Ordinal)
                && request.Arguments.Contains("stage:review", StringComparer.Ordinal);
            if (losesQaHandoffResponse || losesImplementerResponse)
            {
                var committed = await inner.RunAsync(request, cancellationToken);
                if (committed.ExitCode == 0)
                {
                    if (losesQaHandoffResponse)
                    {
                        var args = request.Arguments.ToList();
                        if (ArgumentAfter(request, "--status") != "open"
                            || ArgumentAfter(request, "--assignee") != string.Empty
                            || ArgumentAfter(request, "--if-status") != "in_progress"
                            || ArgumentAfter(request, "--if-assignee") != "nightweaver:demo")
                            throw new InvalidOperationException("QA stage publication did not atomically release the current Delivery claim.");
                        var updateIndex = args.IndexOf("update");
                        var beadId = args[updateIndex + 1];
                        var receiver = Run("bd", ["--actor", "nightweaver:merger", "--dolt-auto-commit", "on", "update", beadId,
                            "--if-status", "open", "--if-assignee", string.Empty, "--status", "in_progress",
                            "--assignee", "nightweaver:merger", "--json"], TargetPath, BeadsEnvironment);
                        if (!receiver.Contains("in_progress", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Receiving Merger claim failed after the atomic QA publication: {receiver}");
                        ClaimMergerAfterQaHandoffResponseLostOnce = false;
                        MergerClaimedAfterQaHandoffResponseLost = true;
                    }
                    else FailOutcomePublishResponseAfterCommitOnce = false;
                    return new ProcessResult(1, string.Empty, "simulated lost publication response after commit");
                }
                return committed;
            }
            if (request.FileName != "gh") return await inner.RunAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var branch = Run("git", ["branch", "--show-current"], request.WorkingDirectory, BeadsEnvironment).Trim();
            var head = Run("git", ["rev-parse", "HEAD"], request.WorkingDirectory, BeadsEnvironment).Trim();
            if (request.Arguments.Count >= 2 && request.Arguments[0] == "pr" && request.Arguments[1] == "checks")
            {
                var failed = FixtureRequiredCheckFails;
                var checks = JsonSerializer.Serialize(new[]
                {
                    new { name = "fixture checks", state = failed ? "FAILURE" : "SUCCESS", bucket = failed ? "fail" : "pass" }
                });
                return new ProcessResult(0, checks, string.Empty);
            }
            var remoteMainOutput = Run("git", ["ls-remote", "origin", "refs/heads/main"], request.WorkingDirectory, BeadsEnvironment);
            var remoteMain = remoteMainOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var merged = string.Equals(remoteMain, head, StringComparison.OrdinalIgnoreCase);
            var response = JsonSerializer.Serialize(new
            {
                state = merged ? "MERGED" : "OPEN",
                baseRefName = "main",
                headRefName = branch,
                headRefOid = head,
                url = FixturePullRequestUrl,
                baseRepository = new
                {
                    nameWithOwner = FixtureRepositoryName,
                    url = "https://github.com/nightweaver-fixture/target"
                },
                mergedAt = merged ? "2026-11-04T20:00:00Z" : null,
                mergeCommit = merged ? new { oid = remoteMain } : null
            });
            return new ProcessResult(0, response, string.Empty);
        }
        internal string WorktreePath(string beadId) => System.IO.Path.Combine(Host.WorktreesRoot(ProjectSlug.Parse("demo")), beadId);
        internal string CurrentBranch(string beadId) => Run("git", ["branch", "--show-current"], WorktreePath(beadId)).Trim();

        internal void SeedRootSentinels()
        {
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "README.md"), "root checkout edit to preserve\n", new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "root-untracked-sentinel.txt"), "preserve me\n", new UTF8Encoding(false));
        }

        internal async Task SeedDirtyDeliveryEnvironmentAsync(string beadId)
        {
            var project = CreateProject(AgentCli.Codex);
            var worktree = await new WorktreeProvisioner(new SystemProcessRunner()).ProvisionAsync(Host, project.Name,
                new ValidatedTargetRepository(TargetPath, "main"), beadId, WorktreePurpose.Delivery,
                TestContext.Current.CancellationToken);
            File.WriteAllText(System.IO.Path.Combine(worktree.Path, "README.md"), "inherited tracked work\n", new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(worktree.Path, "inherited-untracked.txt"), "inherited untracked work\n", new UTF8Encoding(false));
            var temporary = System.IO.Path.Combine(Host.TmpRoot(project.Name), beadId, "old");
            Directory.CreateDirectory(temporary);
            File.WriteAllText(System.IO.Path.Combine(temporary, "sentinel.txt"), "old invocation", new UTF8Encoding(false));
        }

        internal string CreateBead(string title, string type, params string[] labels)
        {
            var arguments = new List<string> { "create", "--title", title, "--type", type, "--json" };
            if (labels.Length > 0) arguments.AddRange(["--labels", string.Join(',', labels)]);
            var created = Run("bd", arguments, TargetPath, BeadsEnvironment);
            using var document = JsonDocument.Parse(created);
            var id = document.RootElement.GetProperty("id").GetString()!;
            return id;
        }

        internal IReadOnlyDictionary<string, string> CreateBeads(
            params (string Title, string Type, string[] Labels)[] specifications)
        {
            var seedPath = System.IO.Path.Combine(temp.Path, $"beads-seed-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(seedPath, string.Join('\n', specifications.Select(specification => JsonSerializer.Serialize(new
            {
                title = specification.Title,
                issue_type = specification.Type,
                labels = specification.Labels,
                status = "open",
                priority = 2
            }))));
            try
            {
                Run("bd", ["import", seedPath, "--json"], TargetPath, BeadsEnvironment);
                var titles = specifications.Select(specification => specification.Title).ToHashSet(StringComparer.Ordinal);
                return BeadsReader.Parse(Run("bd", BeadsCommand.Create(TargetPath).Arguments, TargetPath, BeadsEnvironment))
                    .ById.Values.Where(bead => titles.Contains(bead.Payload.GetProperty("title").GetString()!))
                    .ToDictionary(bead => bead.Payload.GetProperty("title").GetString()!, bead => bead.Id, StringComparer.Ordinal);
            }
            finally
            {
                File.Delete(seedPath);
            }
        }

        internal BeadWorkflowState ReadState(string beadId) => BeadWorkflowState.Parse(ReadBead(beadId));

        internal void SetRepairCycles(string beadId, int count) =>
            Run("bd", ["update", beadId, "--set-metadata", $"repair_cycles={count}"], TargetPath, BeadsEnvironment);

        internal void AddComment(string beadId, string text) =>
            Run("bd", ["comments", "add", beadId, text], TargetPath, BeadsEnvironment);

        internal string SeedRemoteDeliveryBranch(string beadId)
        {
            var branch = $"nightweaver/{beadId}";
            Run("git", ["branch", branch, "main"], TargetPath, BeadsEnvironment);
            Run("git", ["push", "origin", branch], TargetPath, BeadsEnvironment);
            Run("git", ["branch", "-D", branch], TargetPath, BeadsEnvironment);
            return Run("git", ["--git-dir", OriginPath, "rev-parse", $"refs/heads/{branch}"], TargetPath, BeadsEnvironment).Trim();
        }

        internal string RemoteBranchSha(string beadId) => Run("git",
            ["--git-dir", OriginPath, "rev-parse", $"refs/heads/nightweaver/{beadId}"], TargetPath, BeadsEnvironment).Trim();

        internal BeadsInventory ReadInventory() =>
            BeadsReader.Parse(Run("bd", BeadsCommand.Create(TargetPath).Arguments, TargetPath, BeadsEnvironment));

        internal BeadSnapshot ReadBead(string beadId) => ReadInventory().ById[beadId];

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
                // Invocation time includes native process startup; allow the fixture to publish its PID
                // before the timeout so the kill assertion stays reliable under full-suite load.
                var timeout = action == "timeout" ? TimeSpan.FromSeconds(5) : invocation.Timeout;
                var actual = new PreparedAgentInvocation(invocation.Executable, invocation.Arguments, invocation.WorkingDirectory,
                    invocation.StandardInput, invocation.EnvironmentOverrides, timeout, invocation.InvocationId,
                    invocation.TemporaryDirectory, invocation.LogFilePath);
                return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, actual,
                    CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
                        ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidFile
                    }), token);
            }

            var coordinator = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
            var result = await coordinator.RunAsync(Host, project, new(TargetPath, "main"), beadId, role,
                TestContext.Current.CancellationToken);
            AssertFixtureExited(pidFile);
            return result;
        }

        internal async Task<OperationalRetryOutcome> RunRetryAgentAsync(
            string beadId,
            AgentRole role,
            IReadOnlyList<string> actions,
            bool verifyResetSentinels = false,
            string? classifierAction = null,
            bool verifyRetainedSentinels = false)
        {
            const string actor = "nightweaver:demo";
            var claimEnvironment = new Dictionary<string, string?>(BeadsEnvironment, StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = actor
            };
            Run("bd", ["update", beadId, "--claim", "--set-metadata", "repair_cycles=4"], TargetPath, claimEnvironment);

            var project = CreateProject(AgentCli.Codex);
            var systemRunner = new SystemProcessRunner();
            var runner = new RecordingProcessRunner((request, token) => RunWithFixturePullRequestAsync(systemRunner, request, token));
            var preparer = new AgentInvocationPreparer();
            var pidFiles = new List<string>();
            string? latestAction = null;
            string? latestPidFile = null;
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

            async Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
                PreparedAgentInvocation invocation, CancellationToken token)
            {
                var index = Interlocked.Increment(ref actionIndex) - 1;
                Assert.True(index < actions.Count, "Retry coordinator launched more native Agents than the test provided actions.");
                var pidFile = System.IO.Path.Combine(temp.Path, "retry-pids", index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pidFile)!);
                pidFiles.Add(pidFile);
                latestAction = actions[index];
                latestPidFile = pidFile;
                if (index == 1 && verifyResetSentinels)
                {
                    Assert.Equal("fixture base\n", File.ReadAllText(System.IO.Path.Combine(invocation.WorkingDirectory, "README.md"))
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
                    Assert.False(File.Exists(System.IO.Path.Combine(invocation.WorkingDirectory, "retry-untracked.txt")));
                    Assert.True(File.Exists(System.IO.Path.Combine(invocation.WorkingDirectory, ".nightweaver-ignored", "keep.bin")));
                }
                if (index == 1 && verifyRetainedSentinels)
                {
                    Assert.Equal("inherited tracked work\n", File.ReadAllText(System.IO.Path.Combine(invocation.WorkingDirectory, "README.md"))
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
                    Assert.Equal("inherited untracked work\n", File.ReadAllText(System.IO.Path.Combine(invocation.WorkingDirectory, "inherited-untracked.txt"))
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
                    Assert.Equal("retain failed attempt evidence\n", File.ReadAllText(System.IO.Path.Combine(invocation.WorkingDirectory, "attempt-untracked.txt"))
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
                    Assert.Equal("old invocation", File.ReadAllText(System.IO.Path.Combine(hostPaths.TmpRoot(ProjectSlug.Parse("demo")), beadId,
                        "old", "sentinel.txt")));
                }
                try
                {
                    return await AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                        CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["BD_ACTOR"] = actor,
                            ["NIGHTWEAVER_FIXTURE_ACTION"] = latestAction,
                            ["NIGHTWEAVER_FIXTURE_PID_FILE"] = latestPidFile,
                            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION"] = null
                        }), token);
                }
                catch (AgentInvocationExecutionException exception) when (exception.InnerException is BeadEnvironmentException)
                {
                    pidFiles.Remove(pidFile);
                    throw;
                }
            }

            var stageCoordinator = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
            DuplicateFailureClassifier? classifier = null;
            if (classifierAction is not null)
            {
                classifier = new DuplicateFailureClassifier(execute: (hostPaths, projectSlug, invocation, limit, token) =>
                {
                    return AgentInvocationExecutor.ExecuteWithStandardOutputCaptureAsync(hostPaths, projectSlug,
                        invocation, limit, CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["BD_ACTOR"] = actor,
                            ["NIGHTWEAVER_FIXTURE_ACTION"] = latestAction
                                ?? throw new InvalidOperationException("Classifier launched before a stage action."),
                            ["NIGHTWEAVER_FIXTURE_PID_FILE"] = latestPidFile
                                ?? throw new InvalidOperationException("Classifier launched before a stage PID file."),
                            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION"] = classifierAction
                        }), token);
                });
            }
            var retryCoordinator = new OperationalRetryCoordinator(runner, stages: stageCoordinator, classifier: classifier,
                outcomeClassifier: FixtureOutcomeClassifier.Create());
            OperationalRetryOutcome outcome;
            try
            {
                outcome = await retryCoordinator.RunAsync(Host, project, new(TargetPath, "main"), beadId, role, actor,
                    TestContext.Current.CancellationToken);
            }
            catch
            {
                LastRetryRequests = runner.Requests.ToArray();
                LastRetryLifecycleCount = pidFiles.Count;
                throw;
            }
            LastRetryRequests = runner.Requests.ToArray();
            Assert.True(pidFiles.Count <= actions.Count);
            LastRetryLifecycleCount = pidFiles.Count;
            foreach (var pidFile in pidFiles) AssertFixtureExited(pidFile);
            return outcome;
        }

        internal async Task<DeliverySlotOutcome> RunDeliverySlotAsync(
            string beadId,
            IReadOnlyList<(AgentRole Role, string Action)> actions,
            string observationPath,
            int maxRepairCycles = 3,
            bool verifyRepairContinuity = false)
        {
            const string actor = "nightweaver:demo";
            var project = CreateProject(AgentCli.Codex, maxRepairCycles);
            var systemRunner = new SystemProcessRunner();
            var runner = new RecordingProcessRunner((request, token) => RunWithFixturePullRequestAsync(systemRunner, request, token));
            var preparer = new AgentInvocationPreparer();
            var invocations = new Dictionary<string, (AgentRole Role, string Branch)>(StringComparer.Ordinal);
            var pidFiles = new List<string>();
            var roles = new List<AgentRole>();
            long sequence = 0;
            var actionIndex = 0;

            async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig projectConfig,
                AgentRole role, AgentDispatchContext context, string worktreePath, CancellationToken token)
            {
                var invocation = await preparer.PrepareAsync(hostPaths, projectConfig, role, context, worktreePath, token);
                var canonical = System.IO.Path.GetFullPath(System.IO.Path.Combine(hostPaths.WorktreesRoot(projectConfig.Name), context.BeadId));
                Assert.Equal(canonical, worktreePath);
                Assert.Equal(canonical, invocation.WorkingDirectory);
                var branch = Run("git", ["branch", "--show-current"], worktreePath).Trim();
                Assert.Equal($"nightweaver/{context.BeadId}", branch);
                if (verifyRepairContinuity && roles.Count > 0)
                {
                    Assert.True(File.Exists(System.IO.Path.Combine(worktreePath, ".nightweaver-ignored", "slot-sentinel.bin")),
                        "The ignored sentinel must survive role provisioning and repair cycles.");
                    Assert.Contains("slot fixture implementation", Run("git", ["log", "--format=%s"], worktreePath),
                        StringComparison.Ordinal);
                }
                // Execute follows preparation without a native action in between.
                // Reuse this observed branch when recording the invocation input.
                invocations.Add(invocation.InvocationId, (role, branch));
                return invocation;
            }

            Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
                PreparedAgentInvocation invocation, CancellationToken token)
            {
                var (role, branch) = invocations[invocation.InvocationId];
                var index = Volatile.Read(ref actionIndex);
                var consumesNextAction = index < actions.Count && actions[index].Role == role;
                if (consumesNextAction)
                {
                    Interlocked.Increment(ref actionIndex);
                }
                else if (index == 0 || actions[index - 1].Role != role)
                {
                    throw new InvalidOperationException($"No fixture action matches invocation {index} ({role}); next action is "
                        + (index < actions.Count ? actions[index].Role.ToString() : "(none)"));
                }
                else
                {
                    index--;
                }
                var step = actions[index];
                var action = step.Action;
                roles.Add(role);
                var pidFile = System.IO.Path.Combine(temp.Path, "slot-pids", Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pidFile)!);
                pidFiles.Add(pidFile);
                var budgetLine = invocation.StandardInput.Split('\n').Single(line => line.StartsWith("Remaining repair budget: ", StringComparison.Ordinal));
                var remainingRepairBudget = int.Parse(budgetLine["Remaining repair budget: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                AppendSlotObservation(observationPath, beadId, role, invocation.WorkingDirectory, remainingRepairBudget, action, branch);
                return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                    CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["BD_ACTOR"] = actor,
                        ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
                        ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidFile
                    }), token);
            }

            var stages = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
            var retries = new OperationalRetryCoordinator(runner, stages: stages,
                outcomeClassifier: FixtureOutcomeClassifier.Create());
            var slotRunner = new DeliveryRoleRunner((paths, config, repository, requestedBead, role, expectedAssignee,
                expectedState, token, progress) =>
                retries.RunAsync(paths, config, repository, requestedBead, role, expectedAssignee, token, progress,
                    expectedState));
            var slot = new DeliverySlotCoordinator(runner, slotRunner);
            var outcome = await slot.RunAsync(Host, project, new(TargetPath, "main"), beadId,
                TestContext.Current.CancellationToken);
            LastSlotRequests = runner.Requests.ToArray();
            LastSlotRoles = roles.ToArray();
            foreach (var pidFile in pidFiles) AssertFixtureExited(pidFile);
            return outcome;
        }

        internal FixtureEnvironmentOverlayProcessLauncher CreateProcessLauncher(
            IReadOnlyDictionary<string, string?> environmentOverrides)
        {
            var overrides = new Dictionary<string, string?>(environmentOverrides, StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = FakeCliRoot + System.IO.Path.PathSeparator + InheritedPath
            };
            return new FixtureEnvironmentOverlayProcessLauncher(overrides);
        }

        internal sealed class FixtureEnvironmentOverlayProcessLauncher(
            IReadOnlyDictionary<string, string?> overrides) : IAgentProcessLauncher
        {
            public string ResolveExecutable(string name, IReadOnlyDictionary<string, string> environment) =>
                WindowsProcessLauncher.ResolveExecutable(name, MergeEnvironment(environment));

            public WindowsProcessLauncher.WindowsAgentProcess Launch(string executable, IReadOnlyList<string> arguments,
                string workingDirectory, IReadOnlyDictionary<string, string> environment) =>
                WindowsProcessLauncher.Launch(executable, arguments, workingDirectory, MergeEnvironment(environment));

            private Dictionary<string, string> MergeEnvironment(IReadOnlyDictionary<string, string> environment)
            {
                var merged = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
                foreach (var (name, value) in overrides)
                {
                    var existing = merged.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) merged.Remove(existing);
                    if (value is not null) merged[name] = value;
                }
                return merged;
            }
        }

        private void AppendSlotObservation(string path, string beadId, AgentRole role, string worktreePath,
            int remainingRepairBudget, string action, string branch)
        {
            var bead = ReadBead(beadId);
            var state = BeadWorkflowState.Parse(bead);
            var observation = new SlotObservation(beadId, role, state.Stage, state.RepairCycles, remainingRepairBudget,
                bead.Status, bead.Assignee, worktreePath, branch,
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

        private static string? ArgumentAfter(ProcessRequest request, string option)
        {
            var index = request.Arguments.ToList().IndexOf(option);
            return index >= 0 && index + 1 < request.Arguments.Count ? request.Arguments[index + 1] : null;
        }

        private static bool IsInventoryRead(ProcessRequest request) =>
            request.Arguments.Contains("list", StringComparer.Ordinal)
            && request.Arguments.Contains("--all", StringComparer.Ordinal);

        private static bool IsDeliveryClaimAcquire(ProcessRequest request) =>
            request.Arguments.Contains("update", StringComparer.Ordinal)
            && ArgumentAfter(request, "--if-status") == "open"
            && ArgumentAfter(request, "--if-assignee") == string.Empty
            && ArgumentAfter(request, "--status") == "in_progress"
            && request.Arguments.Contains("--assignee", StringComparer.Ordinal)
            && !request.Arguments.Contains("--add-label", StringComparer.Ordinal);

        private static bool IsDeliveryClaimRelease(ProcessRequest request) =>
            request.Arguments.Contains("update", StringComparer.Ordinal)
            && ArgumentAfter(request, "--if-status") == "in_progress"
            && ArgumentAfter(request, "--if-assignee") is not null
            && ArgumentAfter(request, "--status") == "open"
            && !request.Arguments.Contains("--add-label", StringComparer.Ordinal);

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

        internal ProjectConfig CreateProject(AgentCli cli, int maxRepairCycles = 3, int maxConcurrentDelivery = 1, int timeoutMinutes = 1)
        {
            var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(cli, null, "low", timeoutMinutes));
            return new(ProjectSlug.Parse("demo"), "fixture.json", new(TargetPath, "main"), new(1, maxConcurrentDelivery, 1), new(maxRepairCycles, 2),
                new(cli, null, "low", timeoutMinutes), new RoleSettings(roles));
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
                File.Copy(source, System.IO.Path.Combine(FakeCliRoot, "gh.exe"), overwrite: true);
            }
        }

        private static void AssertFixtureExited(string pidFile)
        {
            Assert.True(File.Exists(pidFile), $"The native fixture did not create its process handshake: {pidFile}");
            var identity = File.ReadAllText(pidFile).Split('|', StringSplitOptions.TrimEntries);
            Assert.Equal(2, identity.Length);
            var pid = int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture);
            var startTime = long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                var actualStartTime = process.StartTime.ToUniversalTime().Ticks;
                if (actualStartTime != startTime) return;
                Assert.True(process.WaitForExit(5000), $"Fixture process {pid} started at {new DateTime(startTime, DateTimeKind.Utc):O} survived executor completion.");
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

        internal void PreserveForFailure() => temp.PreserveForFailure();

        public void Dispose() => temp.Dispose();
    }
}
