using System.Diagnostics;
using System.Globalization;

namespace Nightweaver.Tests;

public sealed class BackgroundTerminalLifetimeTests
{
    [Theory]
    [InlineData("terminal")]
    [InlineData("standalone")]
    public async Task DetachedOwnerSurvivesClosingItsLaunchingTerminalJob(string mode)
    {
        using var directory = new TempDirectory();
        var ready = Path.Combine(directory.Path, "owner.ready");
        var executable = Path.Combine(AppContext.BaseDirectory, "ExecutionCrashHost.exe");
        using var terminal = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--terminal-owner-probe", mode, ready },
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Process? owner = null;
        Process? child = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready))
            {
                Assert.False(terminal.HasExited, "Terminal probe exited before its owner became ready.");
                await Task.Delay(20, deadline.Token);
            }
            owner = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready, deadline.Token), CultureInfo.InvariantCulture));
            _ = owner.SafeHandle;
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready + ".child", deadline.Token), CultureInfo.InvariantCulture));
            _ = child.SafeHandle;
            Assert.False(owner.HasExited);
            Assert.False(child.HasExited);

            // Closing a terminal kills its session job, not merely its shell.
            // Killing only this root closes its last job handle and triggers that policy.
            terminal.Kill(entireProcessTree: false);
            await terminal.WaitForExitAsync(deadline.Token);
            await Task.Delay(200, deadline.Token);
            Assert.False(owner.HasExited, "Closing the launching terminal killed the detached background owner.");
            Assert.False(child.HasExited, "Closing the launching terminal killed the background owner's child.");
            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!terminal.HasExited) terminal.Kill(entireProcessTree: false);
            await terminal.WaitForExitAsync(cleanupDeadline.Token);
            if (owner is not null)
            {
                if (!owner.HasExited) owner.Kill(entireProcessTree: true);
                await owner.WaitForExitAsync(cleanupDeadline.Token);
                owner.Dispose();
            }
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(cleanupDeadline.Token);
                child.Dispose();
            }
        }
    }

    [Fact]
    public async Task CallerThatForbidsBreakawayDoesNotStartAnAttachedOwner()
    {
        using var directory = new TempDirectory();
        var ready = Path.Combine(directory.Path, "owner.ready");
        using var terminal = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ExecutionCrashHost.exe"))
        {
            ArgumentList = { "--terminal-owner-probe", "restricted", ready },
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var error = terminal.StandardError.ReadToEndAsync(deadline.Token);
            await terminal.WaitForExitAsync(deadline.Token);
            Assert.Contains("detachment-error=5:", await error);
            Assert.DoesNotContain("detached-owner=", await terminal.StandardOutput.ReadToEndAsync(deadline.Token));
            Assert.False(File.Exists(ready));
        }
        finally
        {
            if (!terminal.HasExited) terminal.Kill(entireProcessTree: false);
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await terminal.WaitForExitAsync(cleanupDeadline.Token);
        }
    }
}
