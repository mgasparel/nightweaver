using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class CleanupWebTests
{
    [Fact]
    public async Task HttpRetryGuardsQueueAndOwnerAcknowledgementRemainSeparateFromDisposal()
    {
        using var target = new BeadsApplicationFixture();
        var protectedBead = false;
        var runner = new RecordingProcessRunner((request, token) => request.FileName == "git"
            ? new SystemProcessRunner().RunAsync(request, token)
            : Task.FromResult(new ProcessResult(0, protectedBead
                ? """[{"id":"done","status":"closed","issue_type":"task","priority":1,"labels":["needs-human"],"created_at":"2026-11-13T00:00:00Z"}]"""
                : """[{"id":"done","status":"closed","issue_type":"task","priority":1,"labels":[],"created_at":"2026-11-13T00:00:00Z"}]""", "")));
        using var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>(); services.AddSingleton<IProcessRunner>(runner);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new { name = "web-test", target_repository = new { path = target.Path } }));
        using var client = factory.CreateClient();
        var token = TestContext.Current.CancellationToken;
        var paths = new HostPaths(factory.HostRoot);
        var project = ProjectConfigLoader.Load(factory.ConfigPath, null, paths);
        var repository = await new TargetRepositoryValidator(runner).ValidateAsync(project.TargetRepository, project.ConfigFilePath, token);
        var store = new CompletedEnvironmentStore(paths, project.Name, repository);
        var record = store.Create("done", WorktreePurpose.Delivery, null) with { Status = CleanupStatus.NeedsAttention, Diagnostic = "policy denied" };
        await store.WriteAsync(record, token);
        await using var telemetry = new FactoryTelemetryPublisher(paths, project, repository, project.Concurrency.MaxConcurrentDelivery, 1);
        await telemetry.StartAsync(token);
        var gate = new ContinuousStopGate();
        using var acceptance = new CleanupRetryAcceptance(paths, project, telemetry.RuntimeId, gate);
        var body = JsonSerializer.Serialize(new { runtimeId = telemetry.RuntimeId, revision = CleanupRetryInbox.Revision(record), requestId = Guid.NewGuid().ToString("N") });
        async Task Post(string content, HttpStatusCode expected, string query = "")
        {
            using var response = await client.PostAsync("/api/cleanup/done/retry" + query, new StringContent(content, Encoding.UTF8, "application/json"), token);
            Assert.True(expected == response.StatusCode, $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync(token)}");
        }
        await Post(body, HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-Nightweaver-Request", "console-v1");
        client.DefaultRequestHeaders.Add("Origin", "http://foreign.invalid");
        await Post(body, HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        foreach (var invalid in new[] { "{}", "[]", "{", body[..^1] + ",\"path\":\"C:/arbitrary\"}", body[..^1] + ",\"revision\":\"duplicate\"}" })
            await Post(invalid, HttpStatusCode.BadRequest);
        await Post(body, HttpStatusCode.BadRequest, "?force=true");
        await Post(body.Replace(CleanupRetryInbox.Revision(record), new string('0', 64), StringComparison.Ordinal), HttpStatusCode.Conflict);
        protectedBead = true; await Post(body, HttpStatusCode.Conflict); protectedBead = false;
        var inbox = new CleanupRetryInbox(paths, project, repository);
        Assert.Empty(inbox.Read());
        await Post(body, HttpStatusCode.Accepted); await Post(body, HttpStatusCode.Accepted);
        Assert.Equal("queued", Assert.Single(inbox.Read()).Outcome);
        Assert.Equal(record, Assert.Single(store.Read().Obligations));
        gate.RequestStop(); await Post(body, HttpStatusCode.Conflict);
        Assert.Single(inbox.Read());
        foreach (var query in new[] { "page=0", "page=1&page=2", "status=unknown", "path=ignored" })
        {
            using var response = await client.GetAsync("/api/cleanup?" + query, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var read = await client.GetAsync("/api/cleanup", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
        Assert.False(json.RootElement.GetProperty("canRetry").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("counts").GetProperty("needs-attention").GetInt32());
    }
}
