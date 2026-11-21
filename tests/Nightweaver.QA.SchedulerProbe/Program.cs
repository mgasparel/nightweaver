using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Supervision;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length != 3 || args[0] is not ("proof" or "action" or "child" or "cleanup")
    || !Path.IsPathFullyQualified(args[1]) || !Directory.Exists(args[1]) || !Guid.TryParseExact(args[2], "N", out _))
    throw new ArgumentException("Expected proof|action|child <existing-absolute-evidence-directory> <nonce-N-guid>.");
var mode = args[0];
var directory = Path.GetFullPath(args[1]);
var nonce = args[2];
var prefix = Path.Combine(directory, nonce);
var executable = Environment.ProcessPath!;
using var user = WindowsIdentity.GetCurrent();
var sid = user.User!.Value;
var timer = Stopwatch.StartNew();
async Task Wait(Func<bool> predicate, int seconds)
{
    var clock = Stopwatch.StartNew();
    while (!predicate())
    {
        if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Marker proof deadline expired.");
        await Task.Delay(200);
    }
}
if (mode == "child")
{
    File.WriteAllText(prefix + ".child", JsonSerializer.Serialize(new { nonce, sid, pid = Environment.ProcessId,
        startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime() }));
    await Wait(() => File.Exists(prefix + ".release"), 60);
    return;
}
if (mode == "action")
{
    File.WriteAllText(prefix + ".action-started", JsonSerializer.Serialize(new { nonce, sid, pid = Environment.ProcessId,
        startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime() }));
    try
    {
        using var child = WindowsProcessLauncher.LaunchDetached(executable, ["child", directory, nonce], directory);
        await Wait(() => File.Exists(prefix + ".child"), 10);
        File.WriteAllText(prefix + ".action", JsonSerializer.Serialize(new { nonce, sid, pid = Environment.ProcessId,
            startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(), childPid = child.ProcessId }));
    }
    catch (Exception exception) { File.WriteAllText(prefix + ".action-failure.txt", exception.ToString()); Environment.ExitCode = 1; }
    return;
}
var identity = new SupervisionIdentity(directory, Path.Combine(directory, "unused.json"), directory, sid);
if (mode == "cleanup")
{
    var retained = System.Xml.Linq.XDocument.Load(prefix + ".definition.xml");
    var ns = ScheduledTaskDefinition.Namespace;
    var trigger = retained.Descendants(ns + "TimeTrigger").Single();
    var exact = ScheduledTaskDefinition.Build(identity, executable, ["action", directory, nonce], directory,
        DateTimeOffset.Parse(trigger.Element(ns + "StartBoundary")!.Value, System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(trigger.Element(ns + "EndBoundary")!.Value, System.Globalization.CultureInfo.InvariantCulture), nonce);
    using var cleanupScheduler = new WindowsSupervisionScheduler();
    var existing = cleanupScheduler.Read(exact.Name);
    if (existing is not null)
    {
        exact.RequireOwned(cleanupScheduler, existing);
        if (existing.Running || existing.Enabled) throw new InvalidDataException("Retained fixture must be disabled and stopped for cleanup.");
        cleanupScheduler.Delete(exact.Name);
    }
    if (cleanupScheduler.Read(exact.Name) is not null) throw new InvalidDataException("Removal unconfirmed.");
    Console.WriteLine("Confirmed exact retained disabled marker removal: " + exact.Name);
    return;
}
var definition = ScheduledTaskDefinition.Build(identity, executable, ["action", directory, nonce], directory,
    DateTimeOffset.UtcNow.AddSeconds(10), DateTimeOffset.UtcNow.AddMinutes(2), nonce);
using var scheduler = new WindowsSupervisionScheduler();
Process? childProcess = null;
var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
    assertions++;
}
try
{
    // Validate the production shape without registering any recovery action.
    var productionShape = ScheduledTaskDefinition.Build(identity, executable,
        ["background", "tick", "--config", identity.ConfigPath], directory);
    scheduler.Validate(productionShape.Xml, sid);
    Check(scheduler.Read(definition.Name) is null, "Nonce collision; foreign task retained.");
    scheduler.Validate(definition.Xml, sid);
    File.WriteAllText(prefix + ".definition.xml", definition.Xml);
    scheduler.Create(definition.Name, definition.Xml, sid);
    var registered = scheduler.Read(definition.Name)!;
    definition.RequireOwned(scheduler, registered);
    Check(!registered.Enabled, "Fixture must register disabled.");
    File.WriteAllText(prefix + ".registered.xml", registered.Xml);
    scheduler.SetEnabled(definition.Name, true);
    Check(scheduler.Read(definition.Name)!.Enabled, "Enable not confirmed.");
    // No Run call: the real time trigger must launch this marker.
    await Wait(() =>
    {
        var current = scheduler.Read(definition.Name)!;
        File.WriteAllText(prefix + ".last-status.json", JsonSerializer.Serialize(current));
        if (File.Exists(prefix + ".action-failure.txt")) throw new InvalidOperationException(File.ReadAllText(prefix + ".action-failure.txt"));
        return File.Exists(prefix + ".action") && !current.Running;
    }, 90);
    var action = JsonDocument.Parse(File.ReadAllText(prefix + ".action"));
    var child = JsonDocument.Parse(File.ReadAllText(prefix + ".child"));
    Check(action.RootElement.GetProperty("sid").GetString() == sid, "Action SID mismatch.");
    Check(child.RootElement.GetProperty("sid").GetString() == sid, "Child SID mismatch.");
    Check(action.RootElement.GetProperty("nonce").GetString() == nonce, "Action nonce mismatch.");
    childProcess = Process.GetProcessById(child.RootElement.GetProperty("pid").GetInt32());
    Check(childProcess.StartTime.ToUniversalTime() == child.RootElement.GetProperty("startedUtc").GetDateTime(), "Child creation identity changed.");
    Check(!childProcess.HasExited, "Detached child did not survive completed scheduler action.");
    Check(scheduler.Read(definition.Name)!.LastResult == 0, "Scheduler action failed.");
    Console.WriteLine(JsonSerializer.Serialize(new { task = definition.Name, sid, nonce, action = action.RootElement,
        child = child.RootElement, assertions, elapsedSeconds = timer.Elapsed.TotalSeconds }));
}
catch (Exception exception)
{
    File.WriteAllText(prefix + ".failure.txt", exception.ToString());
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
finally
{
    // Each mutation requires the exact retained definition, never name-only removal.
    var remaining = scheduler.Read(definition.Name);
    if (remaining is not null)
    {
        definition.RequireOwned(scheduler, remaining);
        scheduler.SetEnabled(definition.Name, false);
        definition.RequireOwned(scheduler, scheduler.Read(definition.Name)!);
        scheduler.Delete(definition.Name);
    }
    File.WriteAllText(prefix + ".release", nonce);
    if (childProcess is not null)
    {
        await childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(NativeWait.WaitForSingleObject(childProcess.Handle, 10000) == 0, "Detached child native settlement unavailable.");
        childProcess.Dispose();
    }
    Check(scheduler.Read(definition.Name) is null, "Fixture removal not confirmed.");
    File.WriteAllText(prefix + ".cleanup.json", JsonSerializer.Serialize(new { task = definition.Name, absent = true,
        childSettled = childProcess is not null, assertions, elapsedSeconds = timer.Elapsed.TotalSeconds }));
}

internal static class NativeWait
{
    [DllImport("kernel32.dll")]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
