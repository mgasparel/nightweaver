using System.Diagnostics;
using System.Text.Json;

// Opt-in, bounded native boundary check. No tracker, browser or normal-suite work.
internal static class SettlementCheck
{
    internal static async Task<int> RunAsync(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Use a new check directory.");
        Directory.CreateDirectory(directory);
        var clock = Stopwatch.StartNew();
        var results = new List<object>();
        foreach (var boundary in new[] { "before-admission", "before-launch", "after-launch", "live-child", "legacy-gap" })
        {
            var trace = Path.Combine(directory, boundary);
            Directory.CreateDirectory(trace);
            Process? observer = boundary == "before-admission" ? null : Start(["--observe", trace]);
            Process? wrapper = null;
            Process? child = null;
            try
            {
                if (observer is not null) await UntilAsync(() => File.Exists(Path.Combine(trace, "observer.ready")));
                var marker = Path.Combine(trace, "child.marker");
                if (boundary == "legacy-gap")
                {
                    File.WriteAllText(Path.Combine(trace, "legacy.pending"), JsonSerializer.Serialize(new
                    {
                        pid = Environment.ProcessId,
                        processStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime()
                    }));
                }
                else
                {
                    var start = Info(["--fixture-child", marker]);
                    // Use an ordinary shim invocation whose delegated executable is
                    // this same binary in its fixture-child mode.
                    start.Environment["NIGHTWEAVER_PROBE_BD"] = Environment.ProcessPath;
                    start.Environment["NIGHTWEAVER_PROBE_TRACE"] = trace;
                    start.Environment["NIGHTWEAVER_PROBE_NATIVE_PATH"] = Environment.GetEnvironmentVariable("PATH");
                    start.Environment["NIGHTWEAVER_PROBE_PAUSE"] = boundary == "before-launch" ? boundary : "after-launch";
                    start.Environment["NIGHTWEAVER_PROBE_CHECK_WRAPPER"] = "1";
                    wrapper = Process.Start(start)!;
                    await UntilAsync(() => Directory.GetFiles(trace, boundary == "before-admission" ? "*.pending" : "*.paused").Length == 1);
                    if (boundary is not ("before-launch" or "before-admission"))
                    {
                        await UntilAsync(() => File.Exists(marker));
                        child = Process.GetProcessById(int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture));
                        _ = child.SafeHandle;
                    }
                    if (Directory.GetFiles(trace, "*.launched").Length != 0)
                        throw new InvalidOperationException("Check missed the launch-record boundary.");
                    wrapper.Kill(entireProcessTree: boundary != "live-child");
                    await wrapper.WaitForExitAsync();
                    await UntilAsync(() => NativeSettlement.WaitForSingleObject(wrapper.SafeHandle, 0) == 0);
                    if (child is not null && boundary != "live-child")
                        await UntilAsync(() => NativeSettlement.WaitForSingleObject(child.SafeHandle, 0) == 0);
                }
                observer ??= Start(["--observe", trace]);
                await UntilAsync(() => File.Exists(Path.Combine(trace, "observer.ready")));
                File.WriteAllText(Path.Combine(trace, "observer.stop"), "stop");
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var expected = boundary is "live-child" or "legacy-gap" ? 1 : 0;
                if (observer.ExitCode != expected) throw new InvalidOperationException("Unexpected observer result: " + boundary);
                var observedFile = Directory.GetFiles(trace, "*.observed").Single();
                using var observed = JsonDocument.Parse(File.ReadAllText(observedFile));
                var containment = observed.RootElement.GetProperty("containment");
                if (containment.GetProperty("settled").GetBoolean() != (expected == 0))
                    throw new InvalidOperationException("Unexpected containment result.");
                if (boundary is "before-launch" or "before-admission" && File.Exists(marker))
                    throw new InvalidOperationException("Child launched before the launch gate.");
                if (boundary == "before-admission" && !containment.GetProperty("provedNonLaunch").GetBoolean())
                    throw new InvalidOperationException("Missing non-launch proof.");
                // On the negative live-child case, closing the last Job handle
                // contains cleanup to this exact fixture and kills its child.
                if (child is not null)
                    await UntilAsync(() => NativeSettlement.WaitForSingleObject(child.SafeHandle, 0) == 0);
                results.Add(new { boundary, observerExit = observer.ExitCode, passed = true,
                    childStarted = child is not null, childSettled = child is not null ? true : (bool?)null });
            }
            finally
            {
                if (wrapper is { HasExited: false }) { wrapper.Kill(true); await wrapper.WaitForExitAsync(); }
                if (child is not null && NativeSettlement.WaitForSingleObject(child.SafeHandle, 0) != 0)
                { child.Kill(true); await child.WaitForExitAsync(); }
                File.WriteAllText(Path.Combine(trace, "observer.stop"), "stop");
                if (observer is not null)
                {
                    await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    observer.Dispose();
                }
                wrapper?.Dispose(); child?.Dispose();
            }
        }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            schema = "nightweaver-probe-settlement-check-v1", results, passed = true,
            elapsedMs = clock.Elapsed.TotalMilliseconds
        }));
        return 0;
    }

    private static ProcessStartInfo Info(string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("NIGHTWEAVER_PROBE_CHECK_WRAPPER");
        return start;
    }
    private static Process Start(string[] arguments) => Process.Start(Info(arguments))!;

    private static async Task UntilAsync(Func<bool> ready)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Settlement check boundary was not reached.");
            await Task.Delay(5);
        }
    }
}
