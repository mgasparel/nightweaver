using System.Text;
using Nightweaver.Tests.ExecutionCrashHost;

namespace Nightweaver.Tests;

public sealed class StreamReadinessTests
{
    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.Read)]
    public async Task JsonReadinessSurvivesAnOpenWriter(FileShare sharing)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "holder.json");
        Task<Report> readiness;
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, sharing))
        {
            writer.Write(Encoding.UTF8.GetBytes("{\"ProcessId\":"));
            writer.Flush();
            readiness = AgentStreamCleanupCoordinator.WaitForJsonAsync<Report>(path, TimeSpan.FromSeconds(1));
            // Hold the writer through multiple polls; a fault is also completion.
            await Task.Delay(40, TestContext.Current.CancellationToken);
            Assert.False(readiness.IsCompleted);
            writer.Write(Encoding.UTF8.GetBytes("12345}"));
        }
        Assert.Equal(12345, (await readiness).ProcessId);
    }

    [Fact]
    public async Task SharingNeverExtendsTheReadinessDeadline()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "producer.json");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            AgentStreamCleanupCoordinator.WaitForJsonAsync<Report>(path, TimeSpan.FromMilliseconds(60)));
    }

    public sealed record Report(int ProcessId);
}
