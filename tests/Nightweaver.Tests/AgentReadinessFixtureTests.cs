using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class AgentReadinessFixtureTests
{
    [Fact]
    public async Task ConcurrentAgentsPublishReadinessWithTheSharedStartedMarkerInUse()
    {
        using var directory = new TempDirectory();
        var startedDirectory = directory.CreateDirectory("started");
        var readyDirectory = directory.CreateDirectory("ready");
        var sharedMarker = Path.Combine(directory.Path, "agent.started");
        using var occupiedMarker = new FileStream(sharedMarker, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var agents = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var executable = Path.Combine(AppContext.BaseDirectory, "AgentInvocationFixture.exe");
        var runner = new SystemProcessRunner();
        var beadIds = new[] { "fixture-first", "fixture-second" };
        var invocations = beadIds.Select(beadId => runner.RunAsync(
            new ProcessRequest(executable, [], directory.Path, new Dictionary<string, string?>
            {
                ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
                ["NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION"] = null,
                ["NIGHTWEAVER_INVOCATION_PURPOSE"] = null,
                ["NIGHTWEAVER_BEAD_ID"] = beadId,
                ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = sharedMarker,
                ["NIGHTWEAVER_FIXTURE_STARTED_DIRECTORY"] = startedDirectory,
                ["NIGHTWEAVER_FIXTURE_READY_DIRECTORY"] = readyDirectory,
                ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = Path.Combine(directory.Path, "release")
            }, TimeSpan.FromSeconds(20)), agents.Token)).ToArray();

        using var readinessDeadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        readinessDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Task.WhenAll(beadIds.Zip(invocations, async (beadId, invocation) =>
            {
                var ready = Path.Combine(readyDirectory, beadId + ".ready");
                while (!File.Exists(ready))
                {
                    if (invocation.IsCompleted)
                    {
                        var result = await invocation;
                        Assert.Fail($"Fixture Agent {beadId} exited before readiness (exit {result.ExitCode}). {result.StandardError}");
                    }
                    await Task.Delay(20, readinessDeadline.Token);
                }

                Assert.Equal(beadId, File.ReadAllText(Path.Combine(startedDirectory, beadId + ".started")));
            }));
        }
        finally
        {
            await agents.CancelAsync();
            try { await Task.WhenAll(invocations); }
            catch (OperationCanceledException) when (agents.IsCancellationRequested) { }
        }
    }
}
