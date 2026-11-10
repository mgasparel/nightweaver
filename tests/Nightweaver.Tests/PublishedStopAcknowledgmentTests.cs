using System.Text;

namespace Nightweaver.Tests;

public sealed class PublishedStopAcknowledgmentTests
{
    private const string Marker = "nightweaver: graceful stop accepted; draining current turns.";

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ExitedRootAcceptsOnlyFreshAcknowledgmentAndOtherwiseReportsNativeExit(bool hasAcknowledgment, bool acknowledgmentIsStale)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"nightweaver-stop-ack-{Guid.NewGuid():N}")).FullName;
        try
        {
            var log = Path.Combine(root, "runtime.log");
            using var runtime = WindowsConsoleProcess.Start(
                Environment.GetEnvironmentVariable("COMSPEC")!, ["/c", "exit", "7"], root,
                new Dictionary<string, string?>(), Path.Combine(root, "process.log"));
            Assert.Equal(7, await runtime.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            File.WriteAllText(log, hasAcknowledgment ? Marker : "no acknowledgment", new UTF8Encoding(false));
            var offset = acknowledgmentIsStale ? new FileInfo(log).Length : 0;
            Task Observe() => PublishedContinuousStopIntegrationTests.WaitForGracefulStopAcceptedAsync(
                runtime, log, offset, 0, DateTime.UtcNow.AddSeconds(10), TestContext.Current.CancellationToken);
            if (hasAcknowledgment && !acknowledgmentIsStale)
                await Observe();
            else
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(Observe);
                Assert.Contains("before acknowledging graceful stop (exit 7)", failure.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
