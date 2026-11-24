using System.Diagnostics;
using System.Text.Json;

// Independent QA observer. The runner starts it outside the Web process tree.
// It holds identity-checked native handles, so killing a shim cannot erase the
// settlement observation. It never kills a process or reads command payloads.
internal static class ProcessObserver
{
    internal static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var entries = new Dictionary<string, Entry>();
        var errors = new List<string>();
        File.WriteAllText(Path.Combine(directory, "observer.ready"), "ready");
        while (true)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.pending"))
            {
                if (entries.Count >= 10000 && !entries.ContainsKey(file))
                {
                    if (errors.Count == 0) errors.Add("Process observation capacity exceeded.");
                    continue;
                }
                if (!entries.TryGetValue(file, out var entry))
                {
                    var value = Read(file);
                    if (value is null) continue;
                    using var document = value;
                    var root = document.RootElement;
                    var job = root.TryGetProperty("jobName", out var name) ? ProbeJob.Open(name.GetString()!) : null;
                    entry = new Entry(file, Open(root), job);
                    entries.Add(file, entry);
                    if (job is not null)
                        File.WriteAllText(Path.ChangeExtension(file, ".admitted"), "observer holds containing Job");
                }
                var launched = Path.ChangeExtension(file, ".launched");
                if (entry.Child is null && File.Exists(launched))
                {
                    using var value = Read(launched);
                    if (value is not null) entry.Child = Open(value.RootElement);
                }
                entry.Wrapper.Poll();
                entry.Child?.Poll();
            }
            if (File.Exists(Path.Combine(directory, "observer.stop"))) break;
            await Task.Delay(10);
        }
        foreach (var entry in entries.Values)
        {
            entry.Wrapper.Poll();
            entry.Child?.Poll();
            var active = entry.Job?.ActiveProcesses;
            var admitted = File.Exists(Path.ChangeExtension(entry.File, ".admitted"));
            // A new-protocol wrapper cannot launch before observer admission.
            // Legacy records have no such guarantee and must remain unknown.
            using var pending = Read(entry.File);
            var nonLaunch = entry.Job is null && !admitted &&
                pending?.RootElement.TryGetProperty("jobName", out _) == true;
            var settled = active == 0 || nonLaunch;
            if (!settled) errors.Add(Path.GetFileName(entry.File) + ": process containment settlement is unproved.");
            // Native handles signal only after rundown. No missing observation
            // becomes a successful settlement, and no byte count is reconstructed.
            File.WriteAllText(Path.ChangeExtension(entry.File, ".observed"), JsonSerializer.Serialize(new
            {
                schema = "nightweaver-tool-observation-v1",
                wrapper = entry.Wrapper.Record(), child = entry.Child?.Record(),
                containment = new { activeProcesses = active, provedNonLaunch = nonLaunch, settled },
                observedUtc = DateTimeOffset.UtcNow
            }));
            entry.Wrapper.Process?.Dispose();
            entry.Child?.Process?.Dispose();
            entry.Job?.Dispose();
        }
        File.WriteAllText(Path.Combine(directory, "observer.summary"), JsonSerializer.Serialize(new
        {
            schema = "nightweaver-tool-observer-v1", count = entries.Count, errors,
            note = "Independent native-handle observations; absent values remain unknown."
        }));
        return errors.Count == 0 ? 0 : 1;
    }

    private static JsonDocument? Read(string file)
    {
        try { return JsonDocument.Parse(File.ReadAllText(file)); }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static ObservedProcess Open(JsonElement identity)
    {
        var pid = identity.GetProperty("pid").GetInt32();
        var birth = identity.GetProperty("processStartedUtc").GetDateTimeOffset();
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            _ = process.SafeHandle;
            if (process.StartTime.ToUniversalTime() != birth.UtcDateTime)
                throw new InvalidOperationException("Process identity changed.");
            return new ObservedProcess(pid, process, null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            return new ObservedProcess(pid, null, "Native handle unavailable or process identity changed.");
        }
    }

    private sealed record Entry(string File, ObservedProcess Wrapper, ProbeJob? Job)
    {
        internal ObservedProcess? Child { get; set; }
    }

    private sealed class ObservedProcess(int pid, Process? process, string? unavailable)
    {
        internal Process? Process { get; } = process;
        private DateTimeOffset? settledByUtc;
        private int? exitCode;
        internal void Poll()
        {
            if (Process is null || settledByUtc is not null) return;
            if (NativeSettlement.WaitForSingleObject(Process.SafeHandle, 0) != 0) return;
            settledByUtc = DateTimeOffset.UtcNow;
            // This is the observed process exit, not a missing shim final result.
            exitCode = Process.ExitCode;
        }
        internal object Record() => new { pid, settledByUtc, exitCode, unavailable };
    }
}
