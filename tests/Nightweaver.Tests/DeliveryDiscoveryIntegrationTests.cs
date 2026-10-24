using System.Reflection;
using System.Text.Json;
using Nightweaver;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class DeliveryDiscoveryIntegrationTests
{
    private const string Actor = "nightweaver:demo";

    [Fact]
    public async Task RealGitAndBdDryRunRoutesBothTaskPoolsIndependentlyAndDoesNotMutateEitherRepository()
    {
        using var target = new BeadsFixture("delivery target with spaces");
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var config = temp.WriteFile("host/projects/demo.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = target.Path },
            concurrency = new { max_concurrent_delivery = 2, max_active_plan_beads = 1 }
        }));

        var implement = CreateTask("Unlabeled implementer with unrelated label", labels: ["ready-for-agent"]);
        var review = CreateTask("Reviewer task", labels: ["stage:review"]);
        var qa = CreateTask("QA task", labels: ["stage:qa"]);
        var firstMerge = CreateTask("First merger task", labels: ["stage:merge"], priority: 0);
        var secondMerge = CreateTask("Second merger task", labels: ["stage:merge"], priority: 1);
        var recovered = CreateTask("Recovered review task", Actor, "in_progress", labels: ["stage:review"]);
        var foreign = CreateTask("Foreign claim", "human", "in_progress", labels: ["stage:implement"]);
        var blocker = target.Create("Open dependency blocker", type: "feature");
        var blockedRecovery = CreateTask("Blocked recovered task", Actor, "in_progress", labels: ["stage:qa"]);
        target.Run("bd", ["dep", "add", blockedRecovery, blocker]);
        var unknown = CreateTask("Unknown stage task", labels: ["stage:plan"]);
        var multiple = CreateTask("Conflicting stage task", labels: ["stage:merge", "stage:review"]);
        var decisionPaused = CreateTask("Decision paused task", labels: ["product-decision-required"]);
        var humanPaused = CreateTask("Human paused task", labels: ["needs-human"]);
        var plannedParent = target.Create("Planned product parent", type: "feature");
        target.Run("bd", ["update", plannedParent, "--add-label", "planned"]);
        var provenanceChild = CreateTask("Task with discovered-from provenance");
        target.Run("bd", ["dep", "add", provenanceChild, plannedParent, "--type", "discovered-from"]);
        var planningCandidate = target.Create("Planning remains backpressured", type: "feature");
        target.Run("bd", ["update", planningCandidate, "--add-label", "stage:plan"]);

        string CreateTask(string title, string? assignee = null, string status = "open", string[]? labels = null, int? priority = null)
        {
            var id = target.Create(title, assignee, status);
            if (priority is not null) target.Run("bd", ["update", id, "--priority", priority.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            foreach (var label in labels ?? []) target.Run("bd", ["update", id, "--add-label", label]);
            return id;
        }

        var inventoryArguments = BeadsCommand.Create(target.Path).Arguments;
        var inventoryJson = target.Run("bd", inventoryArguments);
        var inventory = BeadsReader.Parse(inventoryJson);
        var runner = RealRunner(target);
        var readiness = await new BeadsReader(runner).ReadReadinessAsync(
            new ValidatedTargetRepository(target.Path, "main"), inventory, Actor, CancellationToken.None);
        var startup = StartupPlanner.Plan(Actor, inventory);
        var snapshot = PostSweepReadinessProjector.Project(startup, readiness);
        var delivery = DeliveryDiscoveryPlanner.Plan(snapshot, 2);
        var planning = PlanningPoolPlanner.Plan(snapshot, 2, 1);

        Assert.True(planning.BackpressureActive);
        Assert.Contains(planning.Waiting, candidate => candidate.Candidate.Bead.Id == planningCandidate);
        Assert.Contains(provenanceChild, readiness.NativeUnassignedReady);
        Assert.Contains(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == provenanceChild);
        Assert.Contains(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == recovered && candidate.RecoveredAfterClaimRelease);
        Assert.Contains(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == implement && candidate.Role == AgentRole.Implementer);
        Assert.Contains(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == review && candidate.Role == AgentRole.Reviewer);
        Assert.Contains(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == qa && candidate.Role == AgentRole.Qa);
        Assert.DoesNotContain(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == foreign);
        Assert.DoesNotContain(readiness.RecoverableOwnedReady, id => id == blockedRecovery);
        Assert.DoesNotContain(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == blockedRecovery);
        Assert.Equal(2, delivery.MergerSelected.Count + delivery.MergerWaiting.Count);
        Assert.Equal([firstMerge], delivery.MergerSelected.Select(candidate => candidate.Bead.Id));
        Assert.Equal([secondMerge], delivery.MergerWaiting.Select(candidate => candidate.Bead.Id));
        Assert.Equal(new[] { multiple, unknown }.Order(StringComparer.Ordinal), delivery.Excluded.Select(item => item.BeadId));
        Assert.Equal(2, delivery.DeliverySelected.Count);
        Assert.Equal(4, delivery.DeliveryWaiting.Count);
        Assert.DoesNotContain(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == decisionPaused);
        Assert.DoesNotContain(delivery.DeliverySelected.Concat(delivery.DeliveryWaiting), candidate => candidate.Bead.Id == humanPaused);

        var appDirectory = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NightweaverAppDir").Value!;
        foreach (var file in Directory.EnumerateFiles(appDirectory)) File.Copy(file, Path.Combine(host, Path.GetFileName(file)));
        var executable = Path.Combine(host, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var targetBefore = Snapshot.FileTree(target.Path);
        var hostBefore = Snapshot.FileTree(host);
        var statusBefore = Snapshot.GitStatus(target.Path);
        async Task<string> DryRun()
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await NightweaverApp.RunAsync(["--config", config, "--dry-run"], new(host, caller, output, error), runner, CancellationToken.None);
            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error.ToString());
            return output.ToString();
        }

        var first = await DryRun();
        var second = await DryRun();
        Assert.Equal(first, second);
        Assert.Contains("Planning selected\n  (none)\n\nPlanning waiting", first, StringComparison.Ordinal);
        Assert.Contains("Delivery selected (capacity=2)", first, StringComparison.Ordinal);
        Assert.Contains("Delivery waiting", first, StringComparison.Ordinal);
        Assert.Contains("Merger selected (capacity=1)", first, StringComparison.Ordinal);
        Assert.Contains("reason=waiting_for_capacity", first, StringComparison.Ordinal);
        Assert.Contains("unknown_stage", first, StringComparison.Ordinal);
        Assert.Contains("multiple_stages", first, StringComparison.Ordinal);
        Assert.Contains($"{JsonSerializer.Serialize(recovered)}; role=reviewer; priority=2;", first, StringComparison.Ordinal);
        Assert.Contains("source=hypothetical_post_release", first, StringComparison.Ordinal);
        Assert.Contains($"{JsonSerializer.Serialize(provenanceChild)}; role=implementer;", first, StringComparison.Ordinal);
        Assert.DoesNotContain($"{JsonSerializer.Serialize(blockedRecovery)}; role=", first, StringComparison.Ordinal);
        Assert.DoesNotContain($"{JsonSerializer.Serialize(foreign)}; role=", first, StringComparison.Ordinal);

        var executableResult = await new SystemProcessRunner().RunAsync(
            new(executable, ["--config", config, "--dry-run"], caller, target.Environment, TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal(0, executableResult.ExitCode);
        Assert.Equal(string.Empty, executableResult.StandardError);
        Assert.Equal(first, executableResult.StandardOutput);

        Assert.All(runner.Requests, request => Assert.True(request.FileName is "git" or "bd"));
        Assert.Equal(inventoryJson, target.Run("bd", inventoryArguments));
        Assert.Equal(targetBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(hostBefore, Snapshot.FileTree(host));
        Assert.Equal(statusBefore, Snapshot.GitStatus(target.Path));

    }

    private static RecordingProcessRunner RealRunner(BeadsFixture target) => new((request, token) =>
    {
        Assert.True(request.FileName is "git" or "bd");
        var environment = new Dictionary<string, string?>(target.Environment);
        foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
        return new SystemProcessRunner().RunAsync(request with { Environment = environment }, token);
    });
}
