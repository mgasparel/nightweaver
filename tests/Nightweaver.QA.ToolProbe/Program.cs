using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Disposable QA PATH shim. It delegates to the exact native executable. It never
// interprets or replaces tool output. One file per process avoids a shared log lock.
var tool = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
var executable = Environment.GetEnvironmentVariable("NIGHTWEAVER_PROBE_" + tool.ToUpperInvariant())
    ?? throw new InvalidOperationException("Missing native tool path.");
var directory = Environment.GetEnvironmentVariable("NIGHTWEAVER_PROBE_TRACE")
    ?? throw new InvalidOperationException("Missing trace directory.");
var clock = Stopwatch.StartNew();
var startedUtc = DateTimeOffset.UtcNow;
var recordId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(directory, recordId + ".pending"), JsonSerializer.Serialize(new
{
    tool, args, startedUtc, pid = Environment.ProcessId
}));
var start = new ProcessStartInfo(executable)
{
    UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true,
    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
};
foreach (var arg in args) start.ArgumentList.Add(arg);
using var process = new Process { StartInfo = start };
var outputBytes = 0L; var errorBytes = 0L;
var exitCode = -1;
double? launchedMs = null;
double? nativeSettledMs = null;
try
{
    process.Start();
    launchedMs = clock.Elapsed.TotalMilliseconds;
    async Task<long> ForwardAsync(Stream input, Stream output)
    {
        var buffer = new byte[8192]; long count = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            await output.FlushAsync(); count += read;
        }
        return count;
    }
    var stdout = ForwardAsync(process.StandardOutput.BaseStream, Console.OpenStandardOutput());
    var stderr = ForwardAsync(process.StandardError.BaseStream, Console.OpenStandardError());
    await process.WaitForExitAsync();
    if (OperatingSystem.IsWindows())
    {
        while (NativeSettlement.WaitForSingleObject(process.SafeHandle, 0) is var state && state != 0)
        {
            if (state != 258) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(1);
        }
    }
    nativeSettledMs = clock.Elapsed.TotalMilliseconds;
    outputBytes = await stdout; errorBytes = await stderr;
    exitCode = process.ExitCode;
}
finally
{
    clock.Stop();
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, recordId + ".json"), JsonSerializer.Serialize(new
    {
        schema = "nightweaver-tool-probe-v1", clockIdentity = recordId,
        clock = "Per-process Stopwatch offsets in milliseconds", tool, args, pid = Environment.ProcessId,
        childPid = launchedMs is null ? (int?)null : process.Id,
        startedUtc, elapsedMs = clock.Elapsed.TotalMilliseconds, launchedMs, nativeSettledMs,
        outputBytes, errorBytes, exitCode,
        gateWaitMs = (double?)null, gateWaitReason = "Shim starts after the application gate.",
        settlement = "Native exit plus drained streams; wrapper and tool startup cost included."
    }));
}
return exitCode;

internal static class NativeSettlement
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
