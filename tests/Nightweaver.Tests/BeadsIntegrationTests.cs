using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class BeadsIntegrationTests
{
    [Fact]
    public async Task LargePublicationMetadataFileMergesThroughNativeGuardedUpdates()
    {
        using var target = new BeadsFixture("publication repo with spaces");
        using var host = new TempDirectory();
        const string actor = "nightweaver:demo";
        var id = target.ControlBeadId;
        target.Run("bd", ["update", id, "--status", "in_progress", "--assignee", actor,
            "--set-metadata", "operator=preserve"]);
        var graph = JsonSerializer.Serialize(new[] { new { description = new string('x', 60000) } });
        using var file = InvocationRuntimeFiles.CreatePlanningPublicationMetadataFile(new(host.Path), ProjectSlug.Parse("demo"), id);
        await JsonSerializer.SerializeAsync(file.Stream, new { publication_revision = "revision", publication_graph = graph },
            cancellationToken: TestContext.Current.CancellationToken);
        await file.Stream.FlushAsync(TestContext.Current.CancellationToken);
        // Keep the same immutable, readable file pinned across both native writes.
        BeadSnapshot? published = null;
        foreach (var status in new[] { "in_progress", "open" })
        {
            var request = BeadsCommand.CreateWrite(target.Path, ["update", id, "--metadata", "@" + file.Stream.Name,
                "--status", status, "--assignee", status == "open" ? "" : actor,
                "--if-status", "in_progress", "--if-assignee", actor, "--json"], actor);
            Assert.True(request.Arguments.Sum(a => a.Length + 3) < 2000);
            published = BeadsReader.ParsePublicationResult(target.Run("bd", request.Arguments), id);
        }
        var bead = BeadsReader.Parse(target.Run("bd", BeadsCommand.CreateShow(target.Path, id).Arguments)).ById[id];
        Assert.Equal("open", bead.Status);
        Assert.Null(bead.Assignee);
        Assert.Equal(graph, PlanningPublisher.Metadata(bead, "publication_graph"));
        Assert.Equal("revision", PlanningPublisher.Metadata(bead, "publication_revision"));
        Assert.Equal("preserve", PlanningPublisher.Metadata(bead, "operator"));
        Assert.NotNull(published);
        Assert.False(string.IsNullOrWhiteSpace(PlanningPublisher.Version(published)));
        Assert.Equal(PlanningPublisher.Version(published), PlanningPublisher.Version(bead));
        // The release receipt response contains a comment, not a Product version.
        // Native comments must leave the Product version unchanged for continuation.
        target.Run("bd", ["comments", "add", id, "Retain release evidence"]);
        var afterComment = BeadsReader.Parse(target.Run("bd", BeadsCommand.CreateShow(target.Path, id).Arguments)).ById[id];
        Assert.Equal(PlanningPublisher.Version(bead), PlanningPublisher.Version(afterComment));
    }

    [Fact]
    public async Task RealInventoryReadsAllClaimsAndLeavesBothTrackersAndHostUntouched()
    {
        using var target = new BeadsFixture("target repo with spaces");
        using var other = new BeadsFixture("foreign tracker");
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var config = temp.WriteFile("host/projects/demo.json", JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = target.Path } }));
        var owned = target.Create("Owned paused claim", "nightweaver:demo", "in_progress");
        var foreign = target.Create("Human claim", "human", "in_progress");
        var unassigned = target.Create("Unassigned claim", status: "in_progress");
        var otherProject = target.Create("Other project", "nightweaver:elsewhere", "in_progress");
        target.Run("bd", ["update", owned, "--add-label", "paused", "--add-label", "planned", "--metadata", "{\"recovery\":\"retain\"}"]);
        target.Run("bd", ["dep", "add", owned, foreign]);
        target.Run("bd", ["comments", "add", owned, "Retain this comment"]);
        target.Create("Closed control", "nightweaver:demo", "closed");
        target.Create("Hidden gate", "nightweaver:demo", "in_progress", "gate");
        var deferred = target.Create("Deferred control");
        target.Run("bd", ["defer", deferred, "--until", "+1h"]);
        var controlTitles = Enumerable.Range(0, 51).Select(index => $"Control {index}").ToArray();
        var controlsFile = temp.WriteFile("inventory-controls.jsonl", string.Join('\n', controlTitles.Select(title => JsonSerializer.Serialize(new
        {
            title,
            issue_type = "task",
            status = "open",
            priority = 2,
            created_at = "2025-01-01T00:00:00Z",
            labels = Array.Empty<string>()
        }))));
        using (JsonDocument.Parse(target.Run("bd", ["import", controlsFile, "--json"]))) { }
        var late = target.Create("Owned beyond default limit", "nightweaver:demo", "in_progress");
        var decoy = other.Create("Must not inspect this", "nightweaver:demo", "in_progress");

        var inventoryArgs = BeadsCommand.Create(target.Path).Arguments;
        var logicalBefore = target.Run("bd", inventoryArgs);
        var importedControls = BeadsReader.Parse(logicalBefore).ById.Values
            .Where(bead => bead.Payload.GetProperty("title").GetString()!.StartsWith("Control ", StringComparison.Ordinal))
            .ToArray();
        var importedControlTitles = importedControls
            .Select(bead => bead.Payload.GetProperty("title").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(controlTitles.Order(StringComparer.Ordinal), importedControlTitles);
        Assert.All(importedControls, bead =>
        {
            Assert.Equal("open", bead.Status);
            Assert.Equal("task", bead.IssueType);
        });
        // Compare native semantics on blocked, paused, foreign/owned, closed,
        // gate and >50-row controls before measuring mutation-free inspection.
        var inventory = BeadsReader.Parse(logicalBefore);
        var readyJson = target.Run("bd", BeadsCommand.CreateLifecycleReadiness(target.Path).Arguments);
        var nativeReady = BeadsReader.Parse(target.Run("bd", BeadsCommand.CreateUnassignedReadiness(target.Path).Arguments));
        var recoveryReady = BeadsReader.Parse(target.Run("bd", BeadsCommand.CreateRecoverableReadiness(target.Path, "nightweaver:demo").Arguments));
        var compareRunner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, readyJson, "")));
        var combined = await new BeadsReader(compareRunner).ReadReadinessAsync(new(target.Path, "main"), inventory,
            "nightweaver:demo", TestContext.Current.CancellationToken);
        Assert.Equal(nativeReady.ById.Keys.Order(), combined.NativeUnassignedReady.Order());
        Assert.Equal(recoveryReady.ById.Keys.Order(), combined.RecoverableOwnedReady.Order());
        Assert.Single(compareRunner.Requests);
        var commentsBefore = target.Run("bd", ["--readonly", "--sandbox", "comments", owned, "--json"]);
        var treeBefore = Snapshot.FileTree(target.Path);
        var otherBefore = Snapshot.FileTree(other.Path);
        var hostBefore = Snapshot.FileTree(host);
        var runner = new RecordingProcessRunner((request, token) =>
        {
            Assert.True(request.FileName == "git" || request.FileName == "bd");
            if (request.FileName == "bd")
                Assert.True(request.Arguments.SequenceEqual(inventoryArgs)
                    || request.Arguments.SequenceEqual(BeadsCommand.CreateLifecycleReadiness(target.Path).Arguments));
            var environment = new Dictionary<string, string?>(target.Environment)
            {
                ["BEADS_DIR"] = Path.Combine(other.Path, ".beads"), ["BEADS_DB"] = Path.Combine(other.Path, ".beads", "beads.db"),
                ["BD_DB"] = "wrong", ["BEADS_DOLT_SERVER_DATABASE"] = "wrong", ["BEADS_DOLT_DATA_DIR"] = other.Path,
                ["BEADS_MEM_STATS"] = Path.Combine(host, "forbidden-stats"), ["BEADS_MEM_PROFILE"] = Path.Combine(host, "forbidden-profile"),
                ["GIT_DIR"] = Path.Combine(other.Path, ".git")
            };
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            return new SystemProcessRunner().RunAsync(request with { Environment = environment }, token);
        });
        async Task<string> DryRun(string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await NightweaverApp.RunAsync(args, new(host, caller, output, error), runner, CancellationToken.None);
            Assert.Equal("", error.ToString());
            Assert.Equal(0, code);
            return output.ToString();
        }
        var first = await DryRun(["--project", "demo", "--dry-run"]);
        var second = await DryRun(["--config", config, "--dry-run"]);
        Assert.Equal(first, second);
        var plan = Nightweaver.Planning.StartupPlanner.Plan("nightweaver:demo", BeadsReader.Parse(logicalBefore));
        Assert.Equal(3, plan.Releases.Count);
        foreach (var release in plan.Releases) Assert.Contains($"\"{release.BeadId}\": in_progress -> open", first, StringComparison.Ordinal);
        Assert.Contains($"\"{late}\": in_progress -> open", first, StringComparison.Ordinal);
        Assert.Contains($"\"{foreign}\": foreign_in_progress_claim", first, StringComparison.Ordinal);
        Assert.Contains($"\"{unassigned}\": unassigned_in_progress_claim", first, StringComparison.Ordinal);
        Assert.Contains($"\"{otherProject}\": foreign_in_progress_claim", first, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{decoy}\":", first, StringComparison.Ordinal);
        Assert.Equal(10, runner.Requests.Count);
        Assert.Equal(treeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(otherBefore, Snapshot.FileTree(other.Path));
        Assert.Equal(hostBefore, Snapshot.FileTree(host));
        Assert.Equal(logicalBefore, target.Run("bd", inventoryArgs));
        Assert.Equal(commentsBefore, target.Run("bd", ["--readonly", "--sandbox", "comments", owned, "--json"]));
        Assert.Equal(treeBefore, Snapshot.FileTree(target.Path));

        // A failing measured inspection also preserves both snapshots and emits no report.
        File.WriteAllText(Path.Combine(target.Path, ".beads", "metadata.json"), "{broken");
        var brokenBefore = Snapshot.FileTree(target.Path);
        var failureOutput = new StringWriter();
        var failureError = new StringWriter();
        var failure = await NightweaverApp.RunAsync(["--project", "demo", "--dry-run"], new(host, caller, failureOutput, failureError), runner, CancellationToken.None);
        Assert.Equal(1, failure);
        Assert.Equal("", failureOutput.ToString());
        Assert.Contains("bd --readonly --sandbox", failureError.ToString(), StringComparison.Ordinal);
        Assert.Equal(brokenBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(otherBefore, Snapshot.FileTree(other.Path));
        Assert.Equal(hostBefore, Snapshot.FileTree(host));
    }

    [Fact]
    public async Task MarkerWithoutDatabaseFailsWithoutCreatingGateFiles()
    {
        using var repository = new GitFixture();
        var before = Snapshot.FileTree(repository.Path);
        var runner = new RecordingProcessRunner((_, _) => throw new InvalidOperationException("must not launch bd"));
        await Assert.ThrowsAsync<ToolExecutionException>(() => new BeadsReader(runner).ReadSnapshotAsync(new(repository.Path, "main"), CancellationToken.None));
        Assert.Empty(runner.Requests);
        Assert.Equal(before, Snapshot.FileTree(repository.Path));
    }

    [Theory]
    [InlineData("missing-gate")]
    [InlineData("server")]
    [InlineData("env-routing")]
    public async Task UnpreparedEmbeddedBackendFailsBeforeAnyProcessOrMutation(string failure)
    {
        using var repository = new BeadsApplicationFixture();
        if (failure == "missing-gate") File.Delete(Path.Combine(repository.Path, ".beads", "embeddeddolt.gate.lock"));
        if (failure == "server") File.WriteAllText(Path.Combine(repository.Path, ".beads", "metadata.json"), "{\"backend\":\"dolt\",\"dolt_mode\":\"server\"}");
        if (failure == "env-routing") File.WriteAllText(Path.Combine(repository.Path, ".beads", ".env"), "export BEADS_DIR=somewhere-else");
        var before = Snapshot.FileTree(repository.Path);
        var runner = new RecordingProcessRunner((_, _) => throw new InvalidOperationException("must refuse before bd creates a lock"));
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => new BeadsReader(runner).ReadSnapshotAsync(new(repository.Path, "main"), CancellationToken.None));
        Assert.Contains("mutation-free inspection", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
        Assert.Equal(before, Snapshot.FileTree(repository.Path));
    }
}
