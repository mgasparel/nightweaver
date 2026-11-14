using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class WorkspaceRelationsWebTests
{
    [Theory]
    [InlineData("complete", "complete", 4)]
    [InlineData("failed", "complete", 2)]
    [InlineData("complete", "failed", 2)]
    [InlineData("failed", "failed", 0)]
    [InlineData("malformed", "complete", 2)]
    [InlineData("foreign", "complete", 2)]
    [InlineData("complete", "foreign", 2)]
    [InlineData("blank", "complete", 2)]
    [InlineData("empty", "empty", 0)]
    public async Task DirectionalFailuresRetainOnlyVerifiedRows(string incoming, string outgoing, int expectedCount)
    {
        using var target = new BeadsApplicationFixture();
        var runner = new RecordingProcessRunner((request, token) =>
        {
            if (request.FileName == "git") return new SystemProcessRunner().RunAsync(request, token);
            Assert.Contains("--readonly", request.Arguments);
            Assert.Contains("--sandbox", request.Arguments);
            var result = "[]";
            if (request.Arguments.Contains("dep"))
            {
                var up = request.Arguments.Contains("up");
                var state = up ? incoming : outgoing;
                if (state == "failed") return Task.FromResult(new ProcessResult(1, "", "unavailable fixture"));
                result = state switch
                {
                    "empty" => "[]",
                    "malformed" => "[{\"id\":42}]",
                    "blank" => "[{\"id\":\"child\",\"dependency_type\":\" \"}]",
                    "foreign" => "[{\"issue_id\":\"other\",\"depends_on_id\":\"elsewhere\",\"type\":\"blocks\"}]",
                    _ when up => "[{\"id\":\"child\",\"dependency_type\":\"parent-child\"},{\"issue_id\":\"child\",\"depends_on_id\":\"epic\",\"type\":\"parent-child\"},{\"id\":\"missing\",\"dependency_type\":\"custom-relationship\"}]",
                    _ => "[{\"id\":\"child\",\"dependency_type\":\"blocks\"},{\"id\":\"child\",\"dependency_type\":\"discovered-from\"}]"
                };
            }
            else if (request.Arguments.Contains("show")) result = JsonSerializer.Serialize(new[] { Row("epic", "epic") });
            else if (request.Arguments.Contains("list")) result = JsonSerializer.Serialize(new[] { Row("epic", "epic"), Row("child", "custom-type") });
            return Task.FromResult(new ProcessResult(0, result, ""));
        });
        using var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(runner);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new { name = "web-test", target_repository = new { path = target.Path } }));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/beads/epic", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var observation = document.RootElement.GetProperty("relationObservation");
        Assert.Equal(State(incoming), observation.GetProperty("incoming").GetString());
        Assert.Equal(State(outgoing), observation.GetProperty("outgoing").GetString());
        var rows = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, rows.Length);
        Assert.All(rows.Where(row => row.GetProperty("id").GetString() == "child"), row =>
        {
            Assert.Equal("custom-type", row.GetProperty("issueType").GetString());
            Assert.Equal("<script>hostile title</script>", row.GetProperty("title").GetString());
        });
        if (incoming == "complete")
        {
            var missing = Assert.Single(rows, row => row.GetProperty("id").GetString() == "missing");
            Assert.Equal("custom-relationship", missing.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, missing.GetProperty("issueType").ValueKind);
            Assert.Equal(JsonValueKind.Null, missing.GetProperty("title").ValueKind);
        }
        Assert.Equal(1, runner.Requests.Count(request => request.Arguments.Contains("show")));
        Assert.Equal(2, runner.Requests.Count(request => request.Arguments.Contains("dep")));
    }

    private static string State(string input) => input switch { "complete" or "empty" => "complete", "foreign" => "malformed", _ => "unavailable" };

    private static object Row(string id, string type) => new
    {
        id, issue_type = type, title = "<script>hostile title</script>", status = "open", priority = 2,
        created_at = "2026-01-01T00:00:00Z", updated_at = "2026-01-01T00:00:00Z"
    };
}
