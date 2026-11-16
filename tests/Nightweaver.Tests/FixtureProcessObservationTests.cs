using System.Diagnostics;
using Nightweaver.Tests.AgentInvocationFixture;
using Xunit.Sdk;

namespace Nightweaver.Tests;

public sealed class FixtureProcessObservationTests
{
    [Fact]
    public void PidHandshakeIsInvisibleUntilItsContentsAreClosed()
    {
        using var directory = new TempDirectory();
        var marker = Path.Combine(directory.Path, "child.pid");
        var observedWriter = false;
        FixturePidMarker.Publish(marker, 12345, beforeWrite: () =>
        {
            observedWriter = true;
            // The timeout consumer treats existence as permission to kill the
            // child. An empty or partially written PID must not be observable.
            Assert.False(File.Exists(marker));
        });
        Assert.True(observedWriter);
        using var settled = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = new StreamReader(settled);
        Assert.Equal("12345", reader.ReadToEnd());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ObservationRetainsVerifiedExitAndRejectsAnotherGeneration(bool launchRecord, bool wrongStart)
    {
        using var directory = new TempDirectory();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC")!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p fixture_probe= & exit /b 7");
        using var owner = FixtureProcess.Start(start)!;
        var ticks = owner.StartTime.ToUniversalTime().Ticks + (wrongStart ? 1 : 0);
        var identity = Path.Combine(directory.Path, "agent.pid");
        File.WriteAllText(identity, $"{owner.Id}|{ticks}");
        Process Observe() => launchRecord
            ? PublishedContinuousStopIntegrationTests.ReadFixtureProcessFromLaunchRecord($"bead|{owner.Id}|{ticks}").Process
            : PublishedContinuousStopIntegrationTests.ReadFixtureProcess(identity);
        try
        {
            if (wrongStart)
            {
                Assert.Throws<EqualException>(() => { using var invalid = Observe(); });
                Assert.False(owner.HasExited, "Rejecting another generation must only release the observer handle.");
            }
            else
            {
                using var observed = Observe();
                owner.StandardInput.Close();
                Assert.True(owner.WaitForExit(10_000));
                Assert.Equal(7, owner.ExitCode);
                owner.Dispose();
                Assert.True(observed.HasExited);
                Assert.Equal(7, observed.ExitCode);
            }
        }
        finally
        {
            // The positive case already disposed its launcher, while the rejection case still owns a live child.
            if (wrongStart)
            {
                owner.StandardInput.Close();
                Assert.True(owner.WaitForExit(10_000));
            }
        }
    }
}
