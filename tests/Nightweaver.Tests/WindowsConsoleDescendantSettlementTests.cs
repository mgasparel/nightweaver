using System.Diagnostics;

namespace Nightweaver.Tests;

public sealed class WindowsConsoleDescendantSettlementTests : AgentStageIntegrationTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalSettlesNativeBdLockHolderAfterRootExit(bool logOutput)
    {
        using var target = new AgentStageTargetFixture();
        var identity = Path.Combine(target.RootPath, "bd-lock-holder.pid");
        var lockPath = Path.Combine(target.Path, ".beads", "embeddeddolt", "stage", ".dolt", "noms", "LOCK");
        using var runtime = WindowsConsoleProcess.Start(target.AgentFixtureExecutablePath,
            ["spawn-bd-lock-exit", target.Path, identity], target.Path, target.CreateRuntimeEnvironment(),
            logOutput ? Path.Combine(target.RootPath, "runtime.log") : null);
        Process? child = null;
        try
        {
            Assert.Equal(0, await runtime.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            child = Process.GetProcessById(int.Parse(File.ReadAllText(identity), System.Globalization.CultureInfo.InvariantCulture));
            _ = child.SafeHandle;
            Assert.False(child.HasExited, "Runtime root exit must not imply settlement of external bd operations.");
            Assert.Throws<IOException>(() => File.Delete(lockPath));

            runtime.Dispose();

            Assert.True(child.WaitForExit(0), "Console fixture disposal must settle its native bd descendants before repository deletion.");
            File.Delete(lockPath);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
                child.Dispose();
            }
        }
    }
}
