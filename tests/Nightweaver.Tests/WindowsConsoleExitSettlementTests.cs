using System.Diagnostics;

namespace Nightweaver.Tests;

public sealed class WindowsConsoleExitSettlementTests
{
    [Fact]
    public async Task NativeWaitHonorsTimeoutAndCancellationBeforeARepeatWait()
    {
        using var directory = new TempDirectory();
        var executable = Path.Combine(AppContext.BaseDirectory, "AgentInvocationFixture.exe");
        using var runtime = WindowsConsoleProcess.Start(executable, ["hold"], directory.Path,
            new Dictionary<string, string?>
            {
                ["NIGHTWEAVER_FIXTURE_ACTION"] = null,
                ["NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION"] = null,
                ["NIGHTWEAVER_INVOCATION_PURPOSE"] = null
            }, Path.Combine(directory.Path, "held.log"));
        await Assert.ThrowsAsync<TimeoutException>(() => runtime.WaitAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
        Assert.False(runtime.HasExited);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pendingWait = runtime.WaitAsync(TimeSpan.FromSeconds(10), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingWait);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.WaitAsync(TimeSpan.FromSeconds(10), cancelled.Token));
        Assert.False(runtime.HasExited);
        runtime.KillEntireProcessTree();
        var exitCode = await runtime.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(runtime.HasExited);
        Assert.Equal(exitCode, runtime.ExitCode);
    }

    [Fact]
    public async Task WaitingAfterManagedExitStillWaitsForTheNativeProcessObject()
    {
        using var directory = new TempDirectory();
        var executable = Path.Combine(AppContext.BaseDirectory, "AgentInvocationFixture.exe");
        for (var iteration = 0; iteration < 32; iteration++)
        {
            using var runtime = WindowsConsoleProcess.Start(executable, ["close-stdin"], directory.Path,
                new Dictionary<string, string?>
                {
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = null,
                    ["NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION"] = null,
                    ["NIGHTWEAVER_INVOCATION_PURPOSE"] = null
                }, Path.Combine(directory.Path, $"child-{iteration}.log"));
            using var observer = Process.GetProcessById(runtime.Id);
            _ = observer.SafeHandle;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!observer.HasExited)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                Assert.True(DateTime.UtcNow < deadline, "The fixture child did not report termination status.");
                await Task.Yield();
            }
            Assert.Equal(0, await runtime.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(observer.WaitForExit(0),
                $"WaitAsync returned before the native process object became signaled; iteration={iteration}.");
        }
    }
}
