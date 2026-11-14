using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Nightweaver.Tests.ExecutionCrashHost;

namespace Nightweaver.Tests;

public sealed class BackgroundTerminalLifetimeTests
{
    [Theory]
    [InlineData("terminal")]
    [InlineData("inherited-host")]
    public async Task DetachedOwnerHonorsCallerJobPolicyAndKeepsItsOwnContainment(string mode)
    {
        using var directory = new TempDirectory();
        try
        {
            await RunDetachedOwnerProbeAsync(mode, directory);
        }
        catch
        {
            // Never replace the initiating assertion/setup failure with directory disposal.
            directory.PreserveForFailure();
            throw;
        }
    }

    [Fact]
    public async Task SetupFailureBeforeReadingReadyStillSettlesDetachedOwnerAndChild()
    {
        using var directory = new TempDirectory();
        Process? detachedOwner = null;
        Process? detachedChild = null;
        var injected = new InvalidOperationException("Injected setup failure before reading owner identity.");
        try
        {
            var error = await Record.ExceptionAsync(() => RunDetachedOwnerProbeAsync("terminal", directory, () =>
            {
                var ready = Path.Combine(directory.Path, "owner.ready");
                detachedOwner = Process.GetProcessById(int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture));
                detachedChild = Process.GetProcessById(int.Parse(File.ReadAllText(ready + ".child"), CultureInfo.InvariantCulture));
                _ = detachedOwner.SafeHandle;
                _ = detachedChild.SafeHandle;
                throw injected;
            }));
            Assert.Same(injected, error);
            Assert.NotNull(detachedOwner);
            Assert.NotNull(detachedChild);
            Assert.True(detachedOwner.HasExited, "Setup failure leaked the detached owner.");
            Assert.True(detachedChild.HasExited, "Setup failure leaked the detached child.");
        }
        finally
        {
            // The regression deliberately exercises broken cleanup before the repair.
            // Retain exact process handles so a red run never leaks its own fixture.
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var process in new[] { detachedOwner, detachedChild })
            {
                if (process is null) continue;
                if (!process.HasExited) process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(cleanupDeadline.Token);
                process.Dispose();
            }
        }
    }

    private static async Task RunDetachedOwnerProbeAsync(string mode, TempDirectory directory, Action? onReady = null)
    {
        var ready = Path.Combine(directory.Path, "owner.ready");
        var cleanupName = $"Local\\Nightweaver.TerminalProbe.{Guid.NewGuid():N}";
        using var cleanupJob = TerminalOwnerProbe.CreateCleanupJob(cleanupName);
        var executable = Path.Combine(AppContext.BaseDirectory, "ExecutionCrashHost.exe");
        using var terminal = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--terminal-owner-probe", mode, ready, cleanupName },
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Process? owner = null;
        Process? child = null;
        async Task RunAssertionsAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var callerPolicy = await terminal.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.True(callerPolicy is "caller-policy=allow" or "caller-policy=deny",
                $"The terminal probe did not report its caller's job policy: {callerPolicy}");
            if (mode == "terminal") Assert.Equal("caller-policy=allow", callerPolicy);
            if (callerPolicy == "caller-policy=deny")
            {
                await terminal.WaitForExitAsync(deadline.Token);
                Assert.Equal(5, terminal.ExitCode);
                Assert.Contains("detachment-error=5:", await terminal.StandardError.ReadToEndAsync(deadline.Token));
                Assert.DoesNotContain("detached-owner=", await terminal.StandardOutput.ReadToEndAsync(deadline.Token));
                Assert.False(File.Exists(ready));
                return;
            }
            while (!File.Exists(ready))
            {
                if (terminal.HasExited)
                    Assert.Fail($"Terminal probe exited before its owner became ready: {await terminal.StandardError.ReadToEndAsync(deadline.Token)}");
                await Task.Delay(20, deadline.Token);
            }
            onReady?.Invoke();
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
        Exception? failure = null;
        try
        {
            await RunAssertionsAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (!terminal.HasExited) terminal.Kill(entireProcessTree: false);
                await terminal.WaitForExitAsync(cleanupDeadline.Token);
            }
            finally
            {
                // Always stop detached processes, even when terminal cleanup fails
                // or setup never captured the owner/child identity.
                await TerminalOwnerProbe.StopCleanupJobAsync(cleanupJob, cleanupDeadline.Token);
            }
            if (owner is not null) await owner.WaitForExitAsync(cleanupDeadline.Token);
            if (child is not null) await child.WaitForExitAsync(cleanupDeadline.Token);
        }
        catch (Exception cleanupFailure)
        {
            directory.PreserveForFailure();
            failure = failure is null ? cleanupFailure
                : new AggregateException("Probe failed and cleanup also failed.", failure, cleanupFailure);
        }
        finally
        {
            owner?.Dispose();
            child?.Dispose();
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task CallerThatForbidsBreakawayDoesNotStartAnAttachedOwner()
    {
        using var directory = new TempDirectory();
        var ready = Path.Combine(directory.Path, "owner.ready");
        var cleanupName = $"Local\\Nightweaver.TerminalProbe.{Guid.NewGuid():N}";
        using var cleanupJob = TerminalOwnerProbe.CreateCleanupJob(cleanupName);
        using var terminal = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ExecutionCrashHost.exe"))
        {
            ArgumentList = { "--terminal-owner-probe", "restricted", ready, cleanupName },
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
            try
            {
                await terminal.WaitForExitAsync(cleanupDeadline.Token);
            }
            finally
            {
                await TerminalOwnerProbe.StopCleanupJobAsync(cleanupJob, cleanupDeadline.Token);
            }
        }
    }
}
