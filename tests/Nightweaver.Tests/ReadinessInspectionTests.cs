using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class ReadinessInspectionTests
{
    private const string Actor = "nightweaver:demo";
    private static readonly string[] NativeId = ["native"];
    private static readonly string[] RecoveredId = ["recovered"];

    [Fact]
    public async Task ReadinessRunsBothExactReadOnlyUnlimitedQueriesAndSeparatesMembership()
    {
        using var cancellation = new CancellationTokenSource();
        var native = Row("native", "open", "feature", null, ["stage:plan"]);
        var recovered = Row("recovered", "in_progress", "bug", Actor, ["stage:plan-challenge"]);
        var inventory = BeadsReader.Parse($"[{native},{recovered}]");
        var outputs = new Queue<string>([$"[{native}]", $"[{recovered}]"]);
        var runner = new RecordingProcessRunner((request, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Equal("bd", request.FileName);
            Assert.Equal("target repo with spaces", request.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
            Assert.Equal("--readonly", request.Arguments[0]);
            Assert.Equal("--sandbox", request.Arguments[1]);
            foreach (var variable in BeadsCommand.ScrubbedVariables) Assert.Null(request.Environment[variable]);
            Assert.Equal("1", request.Environment["BD_DISABLE_METRICS"]);
            Assert.Equal("false", request.Environment["BD_IMPORT_AUTO"]);
            return Task.FromResult(new ProcessResult(0, outputs.Dequeue(), ""));
        });

        var readiness = await new BeadsReader(runner).ReadReadinessAsync(new("target repo with spaces", "main"), inventory, Actor, cancellation.Token);

        Assert.Equal(["--readonly", "--sandbox", "ready", "--unassigned", "--limit", "0", "--json"], runner.Requests[0].Arguments);
        Assert.Equal(["--readonly", "--sandbox", "list", "--ready", "--status", "in_progress", "--assignee", Actor, "--limit", "0", "--json"], runner.Requests[1].Arguments);
        Assert.Equal(Actor, runner.Requests[1].Arguments[7]);
        Assert.Equal(NativeId, readiness.NativeUnassignedReady);
        Assert.Equal(RecoveredId, readiness.RecoverableOwnedReady);
        Assert.DoesNotContain("recovered", readiness.NativeUnassignedReady);
        Assert.DoesNotContain("native", readiness.RecoverableOwnedReady);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("status")]
    [InlineData("type")]
    [InlineData("assignee")]
    [InlineData("priority")]
    [InlineData("created_at")]
    [InlineData("labels")]
    public async Task ReadinessRejectsUnknownAndInventoryMismatchedRows(string mismatch)
    {
        var authoritative = Row("native", "open", "feature", null, ["stage:plan"]);
        var inventory = BeadsReader.Parse($"[{authoritative}]");
        var response = mismatch switch
        {
            "unknown" => Row("elsewhere", "open", "feature", null, ["stage:plan"]),
            "status" => Row("native", "in_progress", "feature", null, ["stage:plan"]),
            "type" => Row("native", "open", "bug", null, ["stage:plan"]),
            "assignee" => Row("native", "open", "feature", Actor, ["stage:plan"]),
            "priority" => Row("native", "open", "feature", null, ["stage:plan"], priority: 1),
            "created_at" => Row("native", "open", "feature", null, ["stage:plan"], createdAt: "2026-10-24T00:00:00Z"),
            _ => Row("native", "open", "feature", null, ["stage:other"])
        };
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, $"[{response}]", "")));

        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            new BeadsReader(runner).ReadReadinessAsync(new("target" , "main"), inventory, Actor, CancellationToken.None));

        Assert.Contains(mismatch == "unknown" ? "absent from the complete inventory" : "readiness", exception.Message, StringComparison.Ordinal);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task FailureOfSecondReadinessQueryDoesNotReturnPartialReadiness()
    {
        var row = Row("native", "open", "feature", null, []);
        var inventory = BeadsReader.Parse($"[{row}]");
        var runner = new RecordingProcessRunner((request, _) => Task.FromResult(request.Arguments.Contains("ready") && request.Arguments.Contains("--unassigned")
            ? new ProcessResult(0, $"[{row}]", "") : new ProcessResult(2, "", "query failed")));

        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            new BeadsReader(runner).ReadReadinessAsync(new("target", "main"), inventory, Actor, CancellationToken.None));

        Assert.Contains("--assignee nightweaver:demo", exception.Message, StringComparison.Ordinal);
        Assert.Contains("query failed", exception.StandardError, StringComparison.Ordinal);
        Assert.Equal(2, runner.Requests.Count);
    }

    private static string Row(string id, string status, string type, string? assignee, string[] labels,
        int priority = 2, string createdAt = "2026-10-23T00:00:00Z") => JsonSerializer.Serialize(new
    {
        id, status, issue_type = type, priority, created_at = createdAt, assignee, labels, title = "ignored extra field"
    });
}
