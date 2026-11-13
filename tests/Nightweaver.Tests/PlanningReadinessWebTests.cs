using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Inspection;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class PlanningReadinessWebTests
{
    [Fact]
    public async Task BoundaryRejectsInvalidOriginsSnapshotsAndUnavailableProductsWithoutWriting()
    {
        using var target = new BeadsApplicationFixture();
        var current = Bead();
        var writes = 0;
        var refuseGuard = false;
        var runner = new RecordingProcessRunner((request, token) =>
        {
            if (request.FileName == "git") return new SystemProcessRunner().RunAsync(request, token);
            if (request.Arguments.Contains("update"))
            {
                writes++;
                Assert.Contains("--if-status", request.Arguments);
                Assert.Contains("--if-assignee", request.Arguments);
                Assert.Contains("--status", request.Arguments);
                Assert.Contains("--assignee", request.Arguments);
                if (refuseGuard) return Task.FromResult(new ProcessResult(13, "", "claim changed"));
                throw new InvalidOperationException("Rejected requests must never write.");
            }
            return Task.FromResult(new ProcessResult(0, "[" + current.Payload.GetRawText() + "]", ""));
        });
        using var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(runner);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new { name = "web-test", target_repository = new { path = target.Path } }));
        using var client = factory.CreateClient();
        var expected = PlanningReadiness.Project(current)!.Expected;
        async Task AssertPostAsync(string body, HttpStatusCode status, string suffix = "")
        {
            using var response = await client.PostAsync("/api/beads/product/ready-for-planning" + suffix,
                new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
        var valid = JsonSerializer.Serialize(new { expected });
        await AssertPostAsync(valid, HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-Nightweaver-Request", "console-v1");
        client.DefaultRequestHeaders.Add("Origin", "https://foreign.invalid");
        await AssertPostAsync(valid, HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        await AssertPostAsync(valid, HttpStatusCode.BadRequest, "?force=true");
        foreach (var body in new[] { "{}", "[]", "{", "{\"expected\":3}", "{\"expected\":\"prose approved\"}", valid[..^1] + ",\"action\":\"force\"}", valid[..^1] + ",\"expected\":\"duplicate\"}" })
            await AssertPostAsync(body, HttpStatusCode.BadRequest);
        foreach (var unavailable in new[]
        {
            Bead(status: "closed"), Bead(status: "in_progress", assignee: "nightweaver:other"),
            Bead(assignee: "human"), Bead(labels: ["needs-human"]), Bead(labels: ["product-decision-required"]),
            Bead(labels: ["planned"]), Bead(labels: ["ready-for-planning"]), Bead(labels: ["stage:merge"]),
            Bead(labels: ["stage:plan", "stage:plan-challenge"]), Bead(type: "task"), Bead(priority: 1)
        })
        {
            current = unavailable;
            await AssertPostAsync(valid, HttpStatusCode.Conflict);
        }
        Assert.Equal(0, writes);
        current = Bead();
        refuseGuard = true;
        await AssertPostAsync(valid, HttpStatusCode.Conflict);
        Assert.Equal(1, writes);
        Assert.DoesNotContain("ready-for-planning", current.Labels);
    }

    private static BeadSnapshot Bead(string status = "open", string? assignee = null, string[]? labels = null,
        string type = "feature", int priority = 2)
        => BeadsReader.Parse(JsonSerializer.Serialize(new[] { new
        {
            id = "product", status, assignee, issue_type = type, priority,
            labels = labels ?? [], created_at = "2026-01-01T00:00:00Z", description = "Ready for Planning"
        } })).ById["product"];
}
