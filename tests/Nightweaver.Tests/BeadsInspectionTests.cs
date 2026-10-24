using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class BeadsInspectionTests
{
    private static string Row(string id, string status = "in_progress", string? actor = "nightweaver:demo") => JsonSerializer.Serialize(new
    {
        id, status, issue_type = "custom", priority = 2, created_at = "2026-10-23T00:00:00Z", assignee = actor,
        labels = (string[])["paused", "planned", "stage:delivery"], metadata = new { recovery = "preserve" }, dependencies = new[] { new { depends_on_id = "missing" } }
    });

    [Fact]
    public async Task BoundaryPinsCompleteReadOnlyQueryAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new RecordingProcessRunner((request, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Equal("bd", request.FileName);
            Assert.Equal(["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"], request.Arguments);
            Assert.Equal("target with spaces", request.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
            foreach (var key in (string[])[ "BEADS_DIR", "BEADS_DB", "BD_DB", "BEADS_DOLT_DATA_DIR", "BEADS_DOLT_SERVER_DATABASE", "BEADS_MEM_PROFILE", "BEADS_MEM_STATS", "GIT_DIR", "GIT_WORK_TREE" ]) Assert.Null(request.Environment[key]);
            Assert.False(request.Environment.ContainsKey("BEADS_DOLT_PASSWORD"));
            Assert.False(request.Environment.ContainsKey("BEADS_DOLT_SERVER_HOST"));
            return Task.FromResult(new ProcessResult(0, "[]", ""));
        });
        Assert.Empty((await new BeadsReader(runner).ReadSnapshotAsync(new("target with spaces", "main"), cancellation.Token)).ById);
    }

    public static TheoryData<string> InvalidJson => new()
    {
        "", "[", "null", "{}", "[null]", "[2]", "[{}]",
        "[" + Row("x").Replace("\"issue_type\":\"custom\"", "\"issue_type\":null", StringComparison.Ordinal) + "]",
        "[" + Row("x").Replace("[\"paused\",\"planned\",\"stage:delivery\"]", "[42]", StringComparison.Ordinal) + "]",
        "[" + Row("x") + "," + Row("x") + "]",
        "[" + Row("ok") + ",{}]",
        "[" + Row("x").Replace("\"id\":\"x\"", "\"id\":\"x\",\"id\":\"y\"", StringComparison.Ordinal) + "]",
        "[" + Row("x").Replace("\"priority\":2", "\"priority\":2.5", StringComparison.Ordinal) + "]",
        "[" + Row("x").Replace("\"priority\":2", "\"priority\":5", StringComparison.Ordinal) + "]",
        "[" + Row("x").Replace("2026-10-23T00:00:00Z", "bad", StringComparison.Ordinal) + "]",
        "[" + Row("x").Replace("\"assignee\":\"nightweaver:demo\"", "\"assignee\":42", StringComparison.Ordinal) + "]",
        "[" + Row("x").Replace("[\"paused\",\"planned\",\"stage:delivery\"]", "null", StringComparison.Ordinal) + "]",
        "[" + Row(" ") + "]", "[" + Row("x", " ") + "]"
    };

    [Theory]
    [MemberData(nameof(InvalidJson))]
    public void InvalidInventoryFailsAtomically(string json) => Assert.ThrowsAny<JsonException>(() => BeadsReader.Parse(json));

    [Fact]
    public void PolicyIsExactStableAndPreservesSnapshot()
    {
        var rows = new[] { Row("z"), Row("A"), Row("foreign", actor: "human"), Row("other", actor: "nightweaver:other"),
            Row("case", actor: "Nightweaver:demo"), Row("space", actor: "nightweaver:demo "), Row("null", actor: null), Row("empty", actor: "") }
            .Concat(((string[])["open", "closed", "blocked", "deferred", "custom", "in_PROGRESS"]).Select(status => Row(status, status))).ToArray();
        var inventory = BeadsReader.Parse("[" + string.Join(',', rows) + "]");
        var before = inventory.ById.ToDictionary(pair => pair.Key, pair => pair.Value.Payload.GetRawText());
        var plan = StartupPlanner.Plan("nightweaver:demo", inventory);
        Assert.Same(inventory, plan.Inventory);
        Assert.Equal((string[])["A", "z"], plan.Releases.Select(action => action.BeadId));
        Assert.All(plan.Releases, action => { Assert.Equal("in_progress", action.ExpectedStatus); Assert.Equal("open", action.TargetStatus); Assert.Null(action.TargetAssignee); });
        Assert.Equal((string[])["case", "empty", "foreign", "null", "other", "space"], plan.Diagnostics.Select(diagnostic => diagnostic.BeadId));
        Assert.Equal(2, plan.Diagnostics.Count(diagnostic => diagnostic.Code == StartupDiagnosticCode.UnassignedInProgressClaim));
        Assert.All(inventory.ById, pair => Assert.Equal(before[pair.Key], pair.Value.Payload.GetRawText()));
        Assert.All(inventory.ById.Values, bead => Assert.Equal("preserve", bead.Payload.GetProperty("metadata").GetProperty("recovery").GetString()));
        var reversed = StartupPlanner.Plan("nightweaver:demo", BeadsReader.Parse("[" + string.Join(',', rows.Reverse()) + "]"));
        Assert.Equal(plan.Releases, reversed.Releases);
        Assert.Equal(plan.Diagnostics, reversed.Diagnostics);
        var minimal = BeadsReader.Parse("[{\"id\":\"minimal\",\"status\":\"custom\",\"issue_type\":\"custom\",\"priority\":0,\"created_at\":\"2026-01-01T00:00:00Z\"}]").ById["minimal"];
        Assert.Null(minimal.Assignee);
        Assert.Empty(minimal.Labels);
    }
}

