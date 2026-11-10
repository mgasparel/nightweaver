using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class NativeBeadsCleanupTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task CancelingAStartedBeadsWriteReleasesTheEmbeddedDoltLockBeforeFixtureDisposal()
    {
        using var target = new AgentStageTargetFixture();
        var lockPath = Assert.Single(Directory.GetFiles(
            Path.Combine(target.Path, ".beads", "embeddeddolt"), "LOCK", SearchOption.AllDirectories));
        var input = Path.Combine(target.RootPath, "import.jsonl");
        File.WriteAllLines(input, Enumerable.Range(0, 1500).Select(number => JsonSerializer.Serialize(new
        {
            id = $"stage-probe{number}",
            title = $"Native lock writer probe {number}",
            status = "open",
            issue_type = "task",
            priority = 2
        })));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var lockDeadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        lockDeadline.CancelAfter(TimeSpan.FromSeconds(12));
        var request = new ProcessRequest("bd", ["--sandbox", "import", input, "--json"],
            target.Path, target.CreateRuntimeEnvironment(), TimeSpan.FromSeconds(30));
        var writer = new SystemProcessRunner().RunAsync(request, lifetime.Token);
        try
        {
            while (true)
            {
                if (writer.IsCompleted)
                {
                    var result = await writer;
                    Assert.Fail($"bd exited before the fixture observed the Dolt lock (exit {result.ExitCode}). {result.StandardError}");
                }
                using var report = JsonDocument.Parse(WindowsFileLockDiagnostics.Capture(lockPath));
                var owners = report.RootElement.GetProperty("Owners").EnumerateArray();
                if (owners.Any(owner =>
                    owner.GetProperty("ProcessObservation").GetProperty("ExecutablePath").GetString()
                        ?.EndsWith("bd.exe", StringComparison.OrdinalIgnoreCase) == true))
                    break;
                await Task.Delay(20, lockDeadline.Token);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                var result = await writer;
                Assert.True(result.ExitCode == 0, result.StandardError);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        target.Dispose();
        Assert.False(Directory.Exists(target.RootPath));
    }
}
