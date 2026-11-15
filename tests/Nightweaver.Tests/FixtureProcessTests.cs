using System.Diagnostics;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class FixtureProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedLaunchCannotEnterNativeInheritableHandleWindow(bool existingProcess)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC")!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "exit /b 0" }
        };
        using var attempted = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        Task<Process?> launch;
        bool enteredWindow;
        lock (WindowsProcessLauncher.ProcessCreationGate)
        {
            launch = Task.Factory.StartNew(() =>
            {
                attempted.Set();
                try
                {
                    if (!existingProcess) return FixtureProcess.Start(startInfo);
                    var process = new Process { StartInfo = startInfo };
                    try { Assert.True(FixtureProcess.Start(process)); return process; }
                    catch { process.Dispose(); throw; }
                }
                finally { completed.Set(); }
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            // Give an unguarded launch time to enter CreateProcess while the
            // native owner still has inheritable writers. This is a contention
            // observation, not a delay added to the production launch path.
            enteredWindow = completed.Wait(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }
        using var child = await launch;
        Assert.NotNull(child);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, child.ExitCode);
        Assert.False(enteredWindow, "Managed fixture launched while native pipe writers could be inherited.");
    }
}
