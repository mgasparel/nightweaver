using System.Diagnostics;
using System.Runtime.InteropServices;
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
        Process? observedWriter = null;
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
                {
                    var owner = owners.First(owner => owner.GetProperty("ProcessObservation")
                        .GetProperty("ExecutablePath").GetString()
                        ?.EndsWith("bd.exe", StringComparison.OrdinalIgnoreCase) == true);
                    observedWriter = Process.GetProcessById(owner.GetProperty("Pid").GetInt32());
                    _ = observedWriter.SafeHandle;
                    Assert.Equal(owner.GetProperty("StartFileTime").GetInt64(), observedWriter.StartTime.ToFileTimeUtc());
                    break;
                }
                await Task.Delay(20, lockDeadline.Token);
            }
        }
        finally
        {
            try
            {
                await lifetime.CancelAsync();
                try
                {
                    var result = await writer;
                    Assert.True(result.ExitCode == 0,
                        $"exit={result.ExitCode}; stderr={result.StandardError}; stdout={result.StandardOutput}");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }

                if (observedWriter is not null)
                {
                    // Pin the exact Restart Manager owner generation, not a later PID reuse.
                    Assert.Equal(0u, WaitForSingleObject(observedWriter.SafeHandle, 0));
                    using var unlocked = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
            finally { observedWriter?.Dispose(); }
        }

        target.Dispose();
        Assert.False(Directory.Exists(target.RootPath));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint milliseconds);
}
