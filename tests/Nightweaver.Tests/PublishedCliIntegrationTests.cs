using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nightweaver;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

[CollectionDefinition("Published CLI integration")]
public sealed class PublishedCliTestGroup : ICollectionFixture<PublishedCliArtifactFixture>
{
    public const string Name = "Published CLI integration";
}

[Collection(PublishedCliTestGroup.Name)]
public sealed class PublishedCliIntegrationTests(PublishedCliArtifactFixture published)
{
    private const string Actor = "nightweaver:demo";
    private static readonly string[] ScrubbedGitVariables =
    [ "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES" ];

    [Fact]
    public async Task PublishedApphostSelectorsAreDeterministicAndPreserveCompleteTargetAndHostState()
    {
        using var fixture = new StartupSweepFixture(published.PublishDirectory);
        var target = fixture.Target;
        var ids = SeedRepresentativeScenario(fixture);
        WriteProjectConfig(fixture.Config, target.Path, "claude", maxActivePlanBeads: 50, maxConcurrentPlanning: 2, maxConcurrentDelivery: 2);

        var baseline = CaptureState(fixture, published, ids.CommentedBeads);
        var inventory = BeadsReader.Parse(baseline.Logical.Inventory);
        var nativeReady = BeadsReader.Parse(baseline.Logical.NativeReadiness).ById.Keys.ToHashSet(StringComparer.Ordinal);
        var recoverableReady = BeadsReader.Parse(baseline.Logical.RecoverableReadiness).ById.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.True(ActivePlanCount(inventory) < 50, "The representative scenario must select Planning below its configured threshold.");
        Assert.All(ids.PlanningCandidates.Where(id => id != ids.RecoveredProduct), id => Assert.Contains(id, nativeReady));
        Assert.Contains(ids.RecoveredProduct, recoverableReady);
        Assert.Contains(ids.RecoveredTask, recoverableReady);
        Assert.Contains(ids.DiscoveredChild, nativeReady);
        Assert.DoesNotContain(ids.BlockedChild, nativeReady);
        Assert.DoesNotContain(ids.BlockedChild, recoverableReady);
        Assert.DoesNotContain(ids.ForeignClaim, nativeReady);
        Assert.DoesNotContain(ids.UnassignedClaim, nativeReady);
        Assert.DoesNotContain(ids.ForeignClaim, recoverableReady);
        Assert.DoesNotContain(ids.UnassignedClaim, recoverableReady);
        Assert.True(HasDependency(baseline.Logical.Dependencies, ids.DiscoveredChild, ids.PlannedParent, "discovered-from"));
        Assert.True(HasDependency(baseline.Logical.Dependencies, ids.BlockedChild, ids.Blocker, "blocks"));

        var runner = CreateRecordingGuard(target, published);
        var inProcessOutput = new StringWriter();
        var inProcessError = new StringWriter();
        var inProcessCode = await NightweaverApp.RunAsync(
            ["--project", "demo", "--dry-run"],
            new(fixture.Host, fixture.Caller, inProcessOutput, inProcessError), runner, CancellationToken.None);
        Assert.Equal(0, inProcessCode);
        Assert.Equal(string.Empty, inProcessError.ToString());
        AssertRecordedAllowlistWasUsed(runner.Requests, target.Path);
        AssertStateUnchanged(baseline, CaptureState(fixture, published, ids.CommentedBeads));

        var launchEnvironment = published.EnvironmentWithAgentTraps(target.Environment);
        var projectFirst = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, baseline, ids.CommentedBeads,
            ["--project", "demo", "--dry-run"]);
        var projectSecond = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, baseline, ids.CommentedBeads,
            ["--project", "demo", "--dry-run"]);
        var relativeConfig = Path.GetRelativePath(fixture.Caller, fixture.Config);
        var configFirst = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, baseline, ids.CommentedBeads,
            ["--config", relativeConfig, "--dry-run"]);
        var configSecond = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, baseline, ids.CommentedBeads,
            ["--config", relativeConfig, "--dry-run"]);
        var absoluteConfig = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, baseline, ids.CommentedBeads,
            ["--config", fixture.Config, "--dry-run"]);

        AssertByteIdentical(projectFirst.StandardOutput, projectSecond.StandardOutput);
        AssertByteIdentical(projectFirst.StandardOutput, configFirst.StandardOutput);
        AssertByteIdentical(projectFirst.StandardOutput, configSecond.StandardOutput);
        AssertByteIdentical(projectFirst.StandardOutput, absoluteConfig.StandardOutput);
        AssertByteIdentical(projectFirst.StandardOutput, Encoding.UTF8.GetBytes(inProcessOutput.ToString()));
        var report = projectFirst.StandardOutputText;
        Assert.Contains("Nightweaver dry run: hypothetical startup snapshot; no actions are performed.", report, StringComparison.Ordinal);
        Assert.Contains("agent_defaults: cli=claude", report, StringComparison.Ordinal);
        Assert.Contains("detached HEAD", report, StringComparison.Ordinal);
        Assert.Contains("locked_worktree", report, StringComparison.Ordinal);
        Assert.Contains("source=hypothetical_post_release", report, StringComparison.Ordinal);
        AssertReportCandidateSource(report, ids.RecoveredProduct, "planner", "hypothetical_post_release");
        AssertReportCandidateSource(report, ids.RecoveredTask, "reviewer", "hypothetical_post_release");
        Assert.DoesNotContain($"{JsonSerializer.Serialize(ids.BlockedChild)}; role=", report, StringComparison.Ordinal);
        Assert.DoesNotContain($"{JsonSerializer.Serialize(ids.ForeignClaim)}; role=", report, StringComparison.Ordinal);
        Assert.DoesNotContain($"{JsonSerializer.Serialize(ids.UnassignedClaim)}; role=", report, StringComparison.Ordinal);

        var planCandidates = ids.PlanningCandidates.Select(id => CandidateExpectation.From(inventory.ById[id], PlanningRoleFor(inventory.ById[id])))
            .OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray();
        var deliveryCandidates = ids.DeliveryCandidates.Select(id => CandidateExpectation.From(inventory.ById[id], DeliveryRoleFor(inventory.ById[id])))
            .OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray();
        AssertCandidatePool(report, "Planning selected\n", "\nPlanning waiting\n", "Planning waiting\n", "\nPlanning excluded diagnostics\n", planCandidates, 2);
        AssertCandidatePool(report, "Delivery selected (capacity=2)\n", "\nDelivery waiting\n", "Delivery waiting\n", "\nMerger selected (capacity=1)\n", deliveryCandidates, 2);
        AssertCandidatePool(report, "Merger selected (capacity=1)\n", "\nMerger waiting\n", "Merger waiting\n", "\nDelivery and Merger excluded task-stage diagnostics\n", [CandidateExpectation.From(inventory.ById[ids.MergerCandidate], "merger")], 1);
        foreach (var beadId in fixture.StaleIds)
            Assert.Contains($"{JsonSerializer.Serialize(beadId)}:", Section(report, "Worktree removals (hypothetical)\n", "\nRetained worktrees\n"), StringComparison.Ordinal);
        foreach (var beadId in fixture.OrphanIds)
            Assert.Contains($"{JsonSerializer.Serialize(beadId)}:", Section(report, "Orphan tmp deletions (hypothetical)\n", "\nStartup diagnostics\n"), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(fixture.Host, "tmp", "demo", "unexpected-file"), "not a directory");
        var failureBaseline = CaptureState(fixture, published, ids.CommentedBeads);
        var lateFailure = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, failureBaseline, ids.CommentedBeads,
            ["--project", "demo", "--dry-run"], expectedExitCode: 1, expectedError: "runtime directory inspection");
        Assert.Empty(lateFailure.StandardOutput);
        Assert.Contains("runtime directory inspection", lateFailure.StandardErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishedApphostReportsEveryCandidatePastTheDefaultPageLimitForBothSelectors()
    {
        using var fixture = new StartupSweepFixture(published.PublishDirectory);
        var target = fixture.Target;
        target.Run("bd", ["close", target.ControlBeadId]);
        target.Run("bd", ["update", fixture.ActiveIds[0], "--status", "closed", "--force"]);
        target.Run("bd", ["update", fixture.ActiveIds[1], "--status", "closed", "--force"]);

        var seedSpecs = CreatePaginationSeedSpecs();
        using var seedFileDirectory = new TempDirectory();
        var seedFile = seedFileDirectory.WriteFile("pagination.jsonl", string.Join('\n', seedSpecs.Select(spec => JsonSerializer.Serialize(new
        {
            title = spec.Title,
            issue_type = spec.IssueType,
            status = "open",
            priority = spec.Priority,
            created_at = spec.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            labels = spec.Labels
        }))));
        using (JsonDocument.Parse(target.Run("bd", ["import", seedFile, "--json"]))) { }

        var blocker = target.Create("Pagination blocker", type: "feature");
        target.Run("bd", ["update", blocker, "--add-label", "planned"]);
        var blockedProduct = target.Create("Pagination blocked product", type: "bug");
        target.Run("bd", ["dep", "add", blockedProduct, blocker]);
        var assignedProduct = target.Create("Pagination assigned product", "human", type: "feature");
        var pausedProduct = target.Create("Pagination paused product", type: "feature");
        target.Run("bd", ["update", pausedProduct, "--add-label", "needs-human"]);
        var blockedTask = target.Create("Pagination blocked task");
        target.Run("bd", ["dep", "add", blockedTask, blocker]);
        var assignedTask = target.Create("Pagination assigned task", "human");
        target.Run("bd", ["update", assignedTask, "--add-label", "stage:review"]);
        var pausedTask = target.Create("Pagination paused task");
        target.Run("bd", ["update", pausedTask, "--add-label", "stage:qa", "--add-label", "needs-human"]);

        var inventoryJson = target.Run("bd", BeadsCommand.Create(target.Path).Arguments);
        var inventory = BeadsReader.Parse(inventoryJson);
        var idsByTitle = inventory.ById.Values.ToDictionary(bead => bead.Payload.GetProperty("title").GetString()!, bead => bead.Id, StringComparer.Ordinal);
        var expectedByPool = seedSpecs.GroupBy(spec => spec.Pool).ToDictionary(
            group => group.Key,
            group => group.Select(spec => CandidateExpectation.From(inventory.ById[idsByTitle[spec.Title]], spec.Role))
                .OrderBy(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.CreatedAt)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray());
        var nativeReadiness = BeadsReader.Parse(target.Run("bd", BeadsCommand.CreateUnassignedReadiness(target.Path).Arguments));
        var allExpectedIds = expectedByPool.Values.SelectMany(items => items).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(allExpectedIds, id => Assert.Contains(id, nativeReadiness.ById.Keys));
        Assert.DoesNotContain(blockedProduct, nativeReadiness.ById.Keys);
        Assert.DoesNotContain(blockedTask, nativeReadiness.ById.Keys);
        Assert.DoesNotContain(assignedProduct, nativeReadiness.ById.Keys);
        Assert.DoesNotContain(assignedTask, nativeReadiness.ById.Keys);

        target.Run("bd", ["comments", "add", expectedByPool[CandidatePool.Planning][0].Id, "pagination preservation comment"]);
        target.Run("bd", ["comments", "add", expectedByPool[CandidatePool.Delivery][0].Id, "delivery preservation comment"]);
        target.Run("bd", ["comments", "add", expectedByPool[CandidatePool.Merger][0].Id, "merge preservation comment"]);
        var commentIds = expectedByPool.Values.Select(items => items[0].Id).ToArray();
        WriteProjectConfig(fixture.Config, target.Path, "codex", maxActivePlanBeads: 1000);

        var baseline = CaptureState(fixture, published, commentIds);
        var launchEnvironment = published.EnvironmentWithAgentTraps(target.Environment);
        var projectRun = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, baseline, commentIds,
            ["--project", "demo", "--dry-run"]);
        var configRun = await RunMeasuredApphostAsync(fixture, published, launchEnvironment, baseline, commentIds,
            ["--config", Path.GetRelativePath(fixture.Caller, fixture.Config), "--dry-run"]);
        AssertByteIdentical(projectRun.StandardOutput, configRun.StandardOutput);

        var report = projectRun.StandardOutputText;
        Assert.Contains("agent_defaults: cli=codex", report, StringComparison.Ordinal);
        var planning = expectedByPool[CandidatePool.Planning];
        var delivery = expectedByPool[CandidatePool.Delivery];
        var merger = expectedByPool[CandidatePool.Merger];
        Assert.Equal(120, planning.Length);
        Assert.Equal(120, delivery.Length);
        Assert.Equal(3, merger.Length);
        AssertCandidatePool(report, "Planning selected\n", "\nPlanning waiting\n", "Planning waiting\n", "\nPlanning excluded diagnostics\n", planning, 2);
        AssertCandidatePool(report, "Delivery selected (capacity=3)\n", "\nDelivery waiting\n", "Delivery waiting\n", "\nMerger selected (capacity=1)\n", delivery, 3);
        AssertCandidatePool(report, "Merger selected (capacity=1)\n", "\nMerger waiting\n", "Merger waiting\n", "\nDelivery and Merger excluded task-stage diagnostics\n", merger, 1);
        Assert.DoesNotContain(JsonSerializer.Serialize(blockedProduct) + "; role=", report, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(assignedProduct) + "; role=", report, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(pausedProduct) + "; role=", report, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(blockedTask) + "; role=", report, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(assignedTask) + "; role=", report, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(pausedTask) + "; role=", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningThresholdDoesNotBlockDeliveryOrMergerInCodexConfiguration()
    {
        using var fixture = new StartupSweepFixture(published.PublishDirectory);
        var target = fixture.Target;
        var planningCandidate = target.Create("Threshold planning candidate", type: "feature");
        target.Run("bd", ["update", planningCandidate, "--add-label", "stage:plan"]);
        var deliveryCandidate = target.Create("Threshold delivery candidate");
        target.Run("bd", ["update", deliveryCandidate, "--add-label", "stage:implement"]);
        var mergerCandidate = target.Create("Threshold merger candidate");
        target.Run("bd", ["update", mergerCandidate, "--add-label", "stage:merge"]);

        var inventory = BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments));
        var activeCount = ActivePlanCount(inventory);
        Assert.True(activeCount > 0);
        WriteProjectConfig(fixture.Config, target.Path, "codex", maxActivePlanBeads: activeCount);
        var comments = Array.Empty<string>();
        var baseline = CaptureState(fixture, published, comments);
        var environment = published.EnvironmentWithAgentTraps(target.Environment);

        var first = await RunMeasuredApphostAsync(fixture, published, environment, baseline, comments,
            ["--project", "demo", "--dry-run"]);
        var second = await RunMeasuredApphostAsync(fixture, published, environment, baseline, comments,
            ["--project", "demo", "--dry-run"]);
        AssertByteIdentical(first.StandardOutput, second.StandardOutput);
        var report = first.StandardOutputText;
        Assert.Contains("agent_defaults: cli=codex", report, StringComparison.Ordinal);
        Assert.Contains($"Active Plan Beads\n  count: {activeCount}\n  threshold: {activeCount}\n  backpressure: active", report, StringComparison.Ordinal);
        Assert.Contains($"{JsonSerializer.Serialize(planningCandidate)}; role=planner; reason=backpressure", report, StringComparison.Ordinal);
        Assert.Contains($"{JsonSerializer.Serialize(deliveryCandidate)}; role=implementer;", report, StringComparison.Ordinal);
        Assert.Contains($"{JsonSerializer.Serialize(mergerCandidate)}; role=merger;", report, StringComparison.Ordinal);
        Assert.Contains("Delivery selected (capacity=3)", report, StringComparison.Ordinal);
        Assert.Contains("Merger selected (capacity=1)", report, StringComparison.Ordinal);
    }

    private static RepresentativeIds SeedRepresentativeScenario(StartupSweepFixture fixture)
    {
        var target = fixture.Target;
        var planning = new List<string>();
        planning.Add(CreateLabeled("Planning feature", "feature", ["stage:plan"]));
        planning.Add(CreateLabeled("Planning bug challenge", "bug", ["stage:plan-challenge"]));
        planning.Add(CreateLabeled("Planning feature approved", "feature", ["stage:plan-approved"]));
        planning.Add(CreateLabeled("Planning bug default stage", "bug", []));

        var recoveredProduct = target.Create("Recovered planning product", Actor, "in_progress", "feature");
        target.Run("bd", ["update", recoveredProduct, "--add-label", "stage:plan"]);
        planning.Add(recoveredProduct);

        var implementer = CreateLabeled("Delivery implementer", "task", ["stage:implement"]);
        var reviewer = CreateLabeled("Delivery reviewer", "task", ["stage:review"]);
        var qa = CreateLabeled("Delivery QA", "task", ["stage:qa"]);
        var recoveredTask = target.Create("Recovered delivery reviewer", Actor, "in_progress", "task");
        target.Run("bd", ["update", recoveredTask, "--add-label", "stage:review"]);

        var plannedParent = target.Create("Planned Product parent", type: "feature");
        target.Run("bd", ["update", plannedParent, "--add-label", "planned"]);
        var discoveredChild = target.Create("Discovered ready child", type: "task");
        target.Run("bd", ["dep", "add", discoveredChild, plannedParent, "--type", "discovered-from"]);

        var blocker = target.Create("Open planned blocker", type: "feature");
        target.Run("bd", ["update", blocker, "--add-label", "planned"]);
        var blockedChild = target.Create("Child blocked by open Product", type: "task");
        target.Run("bd", ["dep", "add", blockedChild, blocker]);

        var mergerCandidate = CreateLabeled("Ready merger candidate", "task", ["stage:merge"]);
        var foreignClaim = target.Create("Foreign in progress control", "human", "in_progress", "task");
        target.Run("bd", ["update", foreignClaim, "--add-label", "stage:implement"]);
        var unassignedClaim = target.Create("Unassigned in progress control", status: "in_progress", type: "task");
        target.Run("bd", ["update", unassignedClaim, "--add-label", "stage:qa"]);
        var otherProjectClaim = target.Create("Other project claim control", "nightweaver:elsewhere", "in_progress", "task");

        foreach (var id in new[] { planning[0], planning[1], recoveredProduct, reviewer, recoveredTask, discoveredChild })
            target.Run("bd", ["comments", "add", id, "preserve this comment during published dry-run"]);

        var trackedPath = Path.Combine(target.Path, "tracked fixture file.txt");
        File.WriteAllText(trackedPath, "committed baseline\n", new UTF8Encoding(false));
        target.Run("git", ["add", "--", "tracked fixture file.txt"]);
        target.Run("git", ["-c", "user.name=Nightweaver Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "track fixture sentinel"]);
        File.WriteAllText(trackedPath, "staged version\n", new UTF8Encoding(false));
        target.Run("git", ["add", "--", "tracked fixture file.txt"]);
        File.WriteAllText(trackedPath, "unstaged worktree version\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(target.Path, "untracked fixture file.txt"), "preserve this untracked file\n", new UTF8Encoding(false));

        return new(planning.AsReadOnly(), [implementer, reviewer, qa, recoveredTask, discoveredChild, target.ControlBeadId],
            recoveredProduct, recoveredTask, discoveredChild, plannedParent, blockedChild, blocker, mergerCandidate,
            foreignClaim, unassignedClaim, [planning[0], planning[1], recoveredProduct, reviewer, recoveredTask, discoveredChild]);

        string CreateLabeled(string title, string type, IReadOnlyList<string> labels)
        {
            var id = target.Create(title, type: type);
            foreach (var label in labels) target.Run("bd", ["update", id, "--add-label", label]);
            return id;
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<PaginationSeedSpec> CreatePaginationSeedSpecs()
    {
        var specs = new List<PaginationSeedSpec>(243);
        var planTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var deliveryTime = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var mergerTime = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var planStages = new[] { ("stage:plan", "planner"), ("stage:plan-challenge", "plan_challenger"), ("stage:plan-approved", "planner") };
        var deliveryStages = new[] { ("stage:implement", "implementer"), ("stage:review", "reviewer"), ("stage:qa", "qa") };

        for (var index = 0; index < 120; index++)
        {
            var (stage, role) = planStages[index % planStages.Length];
            specs.Add(new($"Pagination Product {index:000}", index % 2 == 0 ? "feature" : "bug", [stage], index % 5, planTime.AddSeconds(index), CandidatePool.Planning, role));
        }
        for (var index = 0; index < 120; index++)
        {
            var (stage, role) = deliveryStages[index % deliveryStages.Length];
            specs.Add(new($"Pagination Delivery {index:000}", "task", [stage], index % 5, deliveryTime.AddSeconds(index), CandidatePool.Delivery, role));
        }
        for (var index = 0; index < 3; index++)
            specs.Add(new($"Pagination Merger {index:000}", "task", ["stage:merge"], index, mergerTime.AddSeconds(index), CandidatePool.Merger, "merger"));
        return specs.AsReadOnly();
    }

    private static void WriteProjectConfig(string path, string targetPath, string cli, int maxActivePlanBeads,
        int maxConcurrentPlanning = 2, int maxConcurrentDelivery = 3)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = targetPath },
            concurrency = new
            {
                max_concurrent_planning = maxConcurrentPlanning,
                max_concurrent_delivery = maxConcurrentDelivery,
                max_active_plan_beads = maxActivePlanBeads
            },
            agent_defaults = new { cli }
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static RecordingProcessRunner CreateRecordingGuard(BeadsFixture target, PublishedCliArtifactFixture published) =>
        new(async (request, cancellationToken) =>
        {
            if (request.FileName == "bd")
            {
                Assert.Equal(target.Path, request.WorkingDirectory);
                var allowed = request.Arguments.SequenceEqual(BeadsCommand.Create(target.Path).Arguments)
                    || request.Arguments.SequenceEqual(BeadsCommand.CreateUnassignedReadiness(target.Path).Arguments)
                    || request.Arguments.SequenceEqual(BeadsCommand.CreateRecoverableReadiness(target.Path, Actor).Arguments);
                if (!allowed) throw new InvalidOperationException($"Unexpected bd request reached the recording guard: bd {string.Join(' ', request.Arguments)}");
                Assert.Equal("0", request.Environment["GIT_OPTIONAL_LOCKS"]);
                Assert.Equal("0", request.Environment["GIT_TERMINAL_PROMPT"]);
                Assert.Equal("1", request.Environment["BD_DISABLE_METRICS"]);
                Assert.Equal("true", request.Environment["OTEL_SDK_DISABLED"]);
                Assert.Equal("off", request.Environment["BD_DOLT_AUTO_COMMIT"]);
                Assert.Equal("false", request.Environment["BD_DOLT_AUTO_PUSH"]);
                foreach (var variable in BeadsCommand.ScrubbedVariables) Assert.Null(request.Environment[variable]);
            }
            else if (request.FileName == "git")
            {
                var branchCheck = request.Arguments.SequenceEqual(["check-ref-format", "--branch", "main"]);
                Assert.Equal(branchCheck ? Path.GetTempPath() : target.Path, request.WorkingDirectory);
                var allowed = branchCheck
                    || request.Arguments.SequenceEqual(["rev-parse", "--is-inside-work-tree", "--show-prefix", "--path-format=absolute", "--git-dir", "--git-common-dir"])
                    || request.Arguments.SequenceEqual(["worktree", "list", "--porcelain", "-z"]);
                if (!allowed) throw new InvalidOperationException($"Unexpected Git request reached the recording guard: git {string.Join(' ', request.Arguments)}");
                Assert.Equal("0", request.Environment["GIT_OPTIONAL_LOCKS"]);
                Assert.Equal("0", request.Environment["GIT_TERMINAL_PROMPT"]);
                foreach (var variable in ScrubbedGitVariables)
                    Assert.Null(request.Environment[variable]);
            }
            else
            {
                throw new InvalidOperationException($"Process guard rejected executable '{request.FileName}' before execution");
            }

            Assert.True(request.Timeout > TimeSpan.Zero && request.Timeout <= TimeSpan.FromSeconds(30));
            var environment = published.EnvironmentWithAgentTraps(target.Environment);
            foreach (var (name, value) in request.Environment) environment[name] = value;
            var result = await ProcessTool.RunAsync(request.FileName, request.Arguments, request.WorkingDirectory, environment, request.Timeout);
            return new ProcessResult(result.ExitCode, result.StandardOutputText, result.StandardErrorText);
        });

    private static void AssertRecordedAllowlistWasUsed(IReadOnlyList<ProcessRequest> requests, string targetPath)
    {
        Assert.NotEmpty(requests);
        Assert.Contains(requests, request => request.FileName == "bd" && request.Arguments.SequenceEqual(BeadsCommand.Create(targetPath).Arguments));
        Assert.Contains(requests, request => request.FileName == "bd" && request.Arguments.SequenceEqual(BeadsCommand.CreateUnassignedReadiness(targetPath).Arguments));
        Assert.Contains(requests, request => request.FileName == "bd" && request.Arguments.SequenceEqual(BeadsCommand.CreateRecoverableReadiness(targetPath, Actor).Arguments));
        Assert.Contains(requests, request => request.FileName == "git" && request.Arguments.SequenceEqual(["worktree", "list", "--porcelain", "-z"]));
    }

    private static async Task<RawProcessResult> RunMeasuredApphostAsync(StartupSweepFixture fixture, PublishedCliArtifactFixture published,
        IReadOnlyDictionary<string, string?> environment, StateSnapshot baseline, IReadOnlyList<string> commentBeads, IReadOnlyList<string> arguments,
        int expectedExitCode = 0, string? expectedError = null)
    {
        var executable = Path.Combine(fixture.Host, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");
        var result = await ProcessTool.RunAsync(executable, arguments, fixture.Caller, environment, TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == expectedExitCode,
            $"Expected exit {expectedExitCode}, received {result.ExitCode}. stdout: {result.StandardOutputText}\nstderr: {result.StandardErrorText}");
        if (expectedError is null) Assert.Empty(result.StandardError);
        else
        {
            Assert.Empty(result.StandardOutput);
            Assert.Contains(expectedError, result.StandardErrorText, StringComparison.Ordinal);
        }
        published.AssertNoAgentInvocations();
        AssertPhysicalUnchanged(baseline.Physical, CapturePhysicalState(fixture, published));
        var afterLogical = CaptureLogicalState(fixture.Target, commentBeads);
        AssertLogicalUnchanged(baseline.Logical, afterLogical);
        AssertPhysicalUnchanged(baseline.Physical, CapturePhysicalState(fixture, published));
        return result;
    }

    private static StateSnapshot CaptureState(StartupSweepFixture fixture, PublishedCliArtifactFixture published, IReadOnlyList<string> commentBeads)
    {
        var logical = CaptureLogicalState(fixture.Target, commentBeads);
        var physical = CapturePhysicalState(fixture, published);
        return new(logical, physical);
    }

    private static LogicalSnapshot CaptureLogicalState(BeadsFixture target, IReadOnlyList<string> commentBeads)
    {
        var inventory = target.Run("bd", BeadsCommand.Create(target.Path).Arguments);
        var ids = BeadsReader.Parse(inventory).ById.Keys.Order(StringComparer.Ordinal).ToArray();
        var dependencyArguments = new List<string> { "--readonly", "--sandbox", "dep", "list" };
        dependencyArguments.AddRange(ids);
        dependencyArguments.Add("--json");
        var dependencies = target.Run("bd", dependencyArguments);
        var nativeReadiness = target.Run("bd", BeadsCommand.CreateUnassignedReadiness(target.Path).Arguments);
        var recoverableReadiness = target.Run("bd", BeadsCommand.CreateRecoverableReadiness(target.Path, Actor).Arguments);
        var comments = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in commentBeads.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            comments[id] = CanonicalJson(target.Run("bd", ["--readonly", "--sandbox", "comments", id, "--json"]));
        var worktrees = target.Run("git", ["worktree", "list", "--porcelain", "-z"]);
        var refs = target.Run("git", ["for-each-ref", "--sort=refname", "--format=%(refname)%00%(objectname)%00"]);
        var index = target.Run("git", ["ls-files", "--stage", "-z"]);
        var status = target.Run("git", ["status", "--porcelain=v2", "--branch", "-z"]);
        return new(CanonicalJson(inventory), CanonicalJson(dependencies), CanonicalJson(nativeReadiness), CanonicalJson(recoverableReadiness),
            comments, worktrees, refs, index, status);
    }

    private static SortedDictionary<string, string> CapturePhysicalState(StartupSweepFixture fixture, PublishedCliArtifactFixture published)
    {
        published.AssertNoAgentInvocations();
        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            // Root trees include their ordinary child paths; reparse targets are snapshotted below.
            ["target-root"] = Snapshot.FileTree(fixture.Target.Root),
            ["host-root"] = Snapshot.FileTree(fixture.Host),
            ["caller-root"] = Snapshot.FileTree(fixture.Caller),
            ["external-sentinel-root"] = Snapshot.FileTree(fixture.External),
            ["published-artifact"] = Snapshot.FileTree(published.PublishDirectory),
            ["agent-traps"] = Snapshot.FileTree(published.TrapDirectory),
            ["agent-invocation-record"] = Snapshot.Location(published.TrapRecordPath)
        };
    }

    private static void AssertStateUnchanged(StateSnapshot expected, StateSnapshot actual)
    {
        AssertLogicalUnchanged(expected.Logical, actual.Logical);
        AssertPhysicalUnchanged(expected.Physical, actual.Physical);
    }

    private static void AssertLogicalUnchanged(LogicalSnapshot expected, LogicalSnapshot actual)
    {
        Assert.Equal(expected.Inventory, actual.Inventory);
        Assert.Equal(expected.Dependencies, actual.Dependencies);
        Assert.Equal(expected.NativeReadiness, actual.NativeReadiness);
        Assert.Equal(expected.RecoverableReadiness, actual.RecoverableReadiness);
        Assert.Equal(expected.Comments, actual.Comments);
        Assert.Equal(expected.GitWorktrees, actual.GitWorktrees);
        Assert.Equal(expected.GitRefs, actual.GitRefs);
        Assert.Equal(expected.GitIndex, actual.GitIndex);
        Assert.Equal(expected.GitStatus, actual.GitStatus);
    }

    private static void AssertPhysicalUnchanged(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var path in expected.Keys) Assert.Equal(expected[path], actual[path]);
    }

    private static bool HasDependency(string json, string issueId, string dependsOnId, string type)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        return document.RootElement.EnumerateArray().Any(row =>
        {
            var values = row.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.String)
                .Select(property => property.Value.GetString()).ToArray();
            return values.Contains(issueId, StringComparer.Ordinal) && values.Contains(dependsOnId, StringComparer.Ordinal)
                && values.Contains(type, StringComparer.Ordinal);
        });
    }

    private static string CanonicalJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CanonicalJson(document.RootElement);
    }

    private static string CanonicalJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(',', element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(',', element.EnumerateArray().Select(CanonicalJson).Order(StringComparer.Ordinal)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => throw new InvalidOperationException($"Unsupported JSON value kind {element.ValueKind}")
    };

    private static int ActivePlanCount(BeadsInventory inventory) => inventory.ById.Values.Count(bead =>
        bead.IssueType == "task" && (bead.Status is "open" or "in_progress")
        && !bead.Labels.Contains("product-decision-required", StringComparer.Ordinal)
        && !bead.Labels.Contains("needs-human", StringComparer.Ordinal));

    private static string PlanningRoleFor(BeadSnapshot bead) =>
        bead.Labels.Contains("stage:plan-challenge", StringComparer.Ordinal) ? "plan_challenger" : "planner";

    private static string DeliveryRoleFor(BeadSnapshot bead) =>
        bead.Labels.Contains("stage:review", StringComparer.Ordinal) ? "reviewer"
        : bead.Labels.Contains("stage:qa", StringComparer.Ordinal) ? "qa" : "implementer";

    private static void AssertCandidatePool(string report, string selectedStart, string selectedEnd, string waitingStart, string waitingEnd,
        IReadOnlyList<CandidateExpectation> expected, int selectedCount)
    {
        var selected = ParseCandidates(Section(report, selectedStart, selectedEnd));
        var waiting = ParseCandidates(Section(report, waitingStart, waitingEnd));
        Assert.Equal(selectedCount, selected.Length);
        Assert.Equal(expected.Count - selectedCount, waiting.Length);
        Assert.Empty(selected.Select(candidate => candidate.Id).Intersect(waiting.Select(candidate => candidate.Id), StringComparer.Ordinal));
        var actual = selected.Concat(waiting).OrderBy(candidate => candidate.Rank).ToArray();
        Assert.Equal(expected.Count, actual.Length);
        Assert.Equal(Enumerable.Range(1, actual.Length), actual.Select(candidate => candidate.Rank));
        Assert.Equal(expected.Take(selectedCount).Select(candidate => candidate.Id), selected.OrderBy(candidate => candidate.Rank).Select(candidate => candidate.Id));
        Assert.Equal(expected.Skip(selectedCount).Select(candidate => candidate.Id), waiting.OrderBy(candidate => candidate.Rank).Select(candidate => candidate.Id));
        Assert.Equal(expected.Select(candidate => candidate.Id), actual.Select(candidate => candidate.Id));
        Assert.Equal(expected.Select(candidate => candidate.Role), actual.Select(candidate => candidate.Role));
        Assert.Equal(actual.Length, actual.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count());
        if (expected.Count > 100)
        {
            Assert.Equal(expected[50].Id, actual[50].Id);
            Assert.Equal(expected[100].Id, actual[100].Id);
            Assert.Equal(expected[^1].Id, actual[^1].Id);
        }
    }

    private static void AssertReportCandidateSource(string report, string beadId, string role, string source)
    {
        var expectedPrefix = $"{JsonSerializer.Serialize(beadId)}; role={role};";
        var candidateLine = report.Split('\n').SingleOrDefault(line => line.Contains(expectedPrefix, StringComparison.Ordinal));
        Assert.NotNull(candidateLine);
        Assert.Contains($"source={source}", candidateLine, StringComparison.Ordinal);
    }

    private static ReportedCandidate[] ParseCandidates(string section)
    {
        var matches = Regex.Matches(section, "^  rank=(?<rank>[0-9]+) \\\"(?<id>[^\\\"]+)\\\"; role=(?<role>[a-z_]+);", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return matches.Select(match => new ReportedCandidate(
            int.Parse(match.Groups["rank"].Value, System.Globalization.CultureInfo.InvariantCulture),
            match.Groups["id"].Value,
            match.Groups["role"].Value)).ToArray();
    }

    private static string Section(string value, string startMarker, string endMarker)
    {
        var start = value.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing report section marker '{startMarker.Trim()}'");
        start += startMarker.Length;
        var end = value.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Missing report section marker '{endMarker.Trim()}'");
        return value[start..end];
    }

    private static void AssertByteIdentical(byte[] expected, byte[] actual) => Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));

    private sealed record RepresentativeIds(IReadOnlyList<string> PlanningCandidates, IReadOnlyList<string> DeliveryCandidates,
        string RecoveredProduct, string RecoveredTask, string DiscoveredChild, string PlannedParent, string BlockedChild,
        string Blocker, string MergerCandidate, string ForeignClaim, string UnassignedClaim, IReadOnlyList<string> CommentedBeads);

    private sealed record PaginationSeedSpec(string Title, string IssueType, IReadOnlyList<string> Labels, int Priority,
        DateTimeOffset CreatedAt, CandidatePool Pool, string Role);

    private sealed record CandidateExpectation(string Id, string Role, int Priority, DateTimeOffset CreatedAt)
    {
        internal static CandidateExpectation From(BeadSnapshot bead, string role) => new(bead.Id, role, bead.Priority, bead.CreatedAt);
    }

    private sealed record ReportedCandidate(int Rank, string Id, string Role);
    private sealed record LogicalSnapshot(string Inventory, string Dependencies, string NativeReadiness, string RecoverableReadiness,
        IReadOnlyDictionary<string, string> Comments, string GitWorktrees, string GitRefs, string GitIndex, string GitStatus);
    private sealed record StateSnapshot(LogicalSnapshot Logical, IReadOnlyDictionary<string, string> Physical);
    private enum CandidatePool { Planning, Delivery, Merger }
}
