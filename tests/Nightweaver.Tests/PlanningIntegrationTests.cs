using System.Text.Json;
using System.Reflection;
using Nightweaver;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class PlanningIntegrationTests
{
    private const string Actor = "nightweaver:demo";

    [Fact]
    public async Task RealBdProjectionMatchesActualClaimReleaseAndPlanningSelectionWithoutDryRunMutation()
    {
        using var target = new BeadsFixture("planning target with spaces");
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var config = temp.WriteFile("host/projects/demo.json", JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = target.Path },
            concurrency = new { max_concurrent_planning = 2, max_active_plan_beads = 5 }
        }));
        var unassignedProduct = target.Create("Unassigned product", type: "feature");
        target.Run("bd", ["update", unassignedProduct, "--add-label", "stage:plan"]);
        var ownedReadyProduct = target.Create("Owned ready product", Actor, "in_progress", "bug");
        target.Run("bd", ["update", ownedReadyProduct, "--add-label", "stage:plan-challenge"]);
        var ownedBlockedProduct = target.Create("Owned blocked product", Actor, "in_progress", "feature");
        target.Run("bd", ["update", ownedBlockedProduct, "--add-label", "stage:plan"]);
        var blocker = target.Create("Open blocker");
        target.Run("bd", ["dep", "add", ownedBlockedProduct, blocker]);

        var inventoryArguments = BeadsCommand.Create(target.Path).Arguments;
        var inventoryJson = target.Run("bd", inventoryArguments);
        var inventory = BeadsReader.Parse(inventoryJson);
        var realRunner = RealRunner(target);
        var readiness = await new BeadsReader(realRunner).ReadReadinessAsync(new(target.Path, "main"), inventory, Actor, CancellationToken.None);
        var startup = StartupPlanner.Plan(Actor, inventory);
        var predicted = PlanningPoolPlanner.Plan(startup, readiness, 2, 5);
        Assert.Contains(ownedReadyProduct, readiness.RecoverableOwnedReady);
        Assert.DoesNotContain(ownedBlockedProduct, readiness.RecoverableOwnedReady);
        Assert.Contains(unassignedProduct, readiness.NativeUnassignedReady);
        Assert.Equal(new[] { unassignedProduct, ownedReadyProduct }.Order(StringComparer.Ordinal), predicted.Selected.Select(candidate => candidate.Bead.Id).Order(StringComparer.Ordinal));
        Assert.Contains(ownedReadyProduct, predicted.Readiness.RecoveredReadyBeadIds);
        Assert.DoesNotContain(ownedBlockedProduct, predicted.Readiness.ReadyBeadIds);

        var appDirectory = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NightweaverAppDir").Value!;
        foreach (var file in Directory.EnumerateFiles(appDirectory)) File.Copy(file, Path.Combine(host, Path.GetFileName(file)));
        var executable = Path.Combine(host, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var targetBeforeDryRuns = Snapshot.FileTree(target.Path);
        var hostBeforeDryRuns = Snapshot.FileTree(host);
        var runner = RealRunner(target);
        async Task<string> DryRun()
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var result = await NightweaverApp.RunAsync(["--config", config, "--dry-run"], new(host, caller, output, error), runner, CancellationToken.None);
            Assert.Equal(0, result);
            Assert.Equal("", error.ToString());
            return output.ToString();
        }

        var first = await DryRun();
        var second = await DryRun();
        Assert.Equal(first, second);
        var executableResult = await new SystemProcessRunner().RunAsync(
            new(executable, ["--config", config, "--dry-run"], caller, target.Environment, TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal(0, executableResult.ExitCode);
        Assert.Equal("", executableResult.StandardError);
        Assert.Equal(first, executableResult.StandardOutput);
        Assert.Contains($"{JsonSerializer.Serialize(ownedReadyProduct)}; role=plan_challenger; source=hypothetical_post_release", first, StringComparison.Ordinal);
        Assert.DoesNotContain($"{JsonSerializer.Serialize(ownedBlockedProduct)}; role=", first, StringComparison.Ordinal);
        Assert.Contains("Active Plan Beads\n  count: 2\n  threshold: 5\n  backpressure: inactive", first, StringComparison.Ordinal);
        Assert.Equal(inventoryJson, target.Run("bd", inventoryArguments));
        Assert.Equal(targetBeforeDryRuns, Snapshot.FileTree(target.Path));
        Assert.Equal(hostBeforeDryRuns, Snapshot.FileTree(host));

        // The oracle mutation is outside the measured dry-run interval.
        foreach (var release in startup.Releases)
            target.Run("bd", ["update", release.BeadId, "--status", "open", "--assignee", "", "--force"]);
        var actualReadyJson = target.Run("bd", BeadsCommand.CreateUnassignedReadiness(target.Path).Arguments);
        var actualReady = BeadsReader.Parse(actualReadyJson).ById.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(predicted.Readiness.ReadyBeadIds.Order(StringComparer.Ordinal), actualReady.Order(StringComparer.Ordinal));

        var actualInventory = BeadsReader.Parse(target.Run("bd", inventoryArguments));
        var actualStartup = StartupPlanner.Plan(Actor, actualInventory);
        var actualReadiness = BeadsReadiness.Create(actualReady, []);
        var actualPlan = PlanningPoolPlanner.Plan(actualStartup, actualReadiness, 2, 5);
        Assert.Equal(predicted.Selected.Select(candidate => candidate.Bead.Id), actualPlan.Selected.Select(candidate => candidate.Bead.Id));
        Assert.Contains(ownedReadyProduct, actualPlan.Readiness.NativeUnassignedReady);
        Assert.DoesNotContain(ownedBlockedProduct, actualPlan.Readiness.ReadyBeadIds);
    }

    private static RecordingProcessRunner RealRunner(BeadsFixture target) => new((request, token) =>
    {
        Assert.True(request.FileName is "git" or "bd");
        var environment = new Dictionary<string, string?>(target.Environment);
        foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
        return new SystemProcessRunner().RunAsync(request with { Environment = environment }, token);
    });
}
