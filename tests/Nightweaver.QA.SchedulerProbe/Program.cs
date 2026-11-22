using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Supervision;

// This explicit native proof is excluded from the normal suite. It never opens a live Project.
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length < 3 || args[0] is not ("proof" or "tick" or "owner" or "child")
    || !Path.IsPathFullyQualified(args[1]) || !Guid.TryParseExact(args[2], "N", out _))
    throw new ArgumentException("Expected proof|tick|owner|child <isolated-root> <nonce> [attempt generation].");
var mode = args[0];
var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
var nonce = args[2];
var executable = Environment.ProcessPath!;
if (!executable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Copy the probe binaries into the isolated root before execution.");
var paths = new HostPaths(root);
var slug = ProjectSlug.Parse("probe");
using var user = WindowsIdentity.GetCurrent();
var identity = new SupervisionIdentity(root, Path.Combine(root, "probe.json"), Path.Combine(root, "repository", ".git"), user.User!.Value);
var store = new SupervisionStore(paths, slug, identity);
var token = CancellationToken.None;
var clock = Stopwatch.StartNew();
var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
    assertions++;
}
void Write(string name, object value) => File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(value));
async Task Wait(Func<bool> predicate, int seconds = 30)
{
    var wait = Stopwatch.StartNew();
    while (!predicate())
    {
        if (wait.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Probe deadline: " + seconds);
        await Task.Delay(100);
    }
}
if (mode == "child")
{
    Write("child.json", new { pid = Environment.ProcessId, started = Process.GetCurrentProcess().StartTime.ToUniversalTime() });
    await Task.Delay(TimeSpan.FromSeconds(90));
    return;
}
if (mode == "proof")
{
    Directory.CreateDirectory(identity.GitCommonDirectory);
    using (var git = Process.Start(new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
        ArgumentList = { "init", "--initial-branch=main", Path.GetDirectoryName(identity.GitCommonDirectory)! } })!)
    {
        await git.WaitForExitAsync();
        Check(git.ExitCode == 0, "Fixture repository initialization failed.");
    }
    File.WriteAllText(identity.ConfigPath, "{\"fixture\":true}");
    Write(BackgroundDeploymentManifest.FileName, new { schema_version = 1, build_version = "scheduler-probe",
        cli_executable = "bin/Nightweaver.QA.SchedulerProbe.exe", web_executable = "bin/Nightweaver.QA.SchedulerProbe.exe" });
    File.WriteAllText(Path.Combine(root, "trigger.txt"), DateTimeOffset.UtcNow.AddSeconds(10).ToString("O"));
}
var start = DateTimeOffset.Parse(File.ReadAllText(Path.Combine(root, "trigger.txt")), System.Globalization.CultureInfo.InvariantCulture);
var tickDefinition = ScheduledTaskDefinition.Build(identity, executable, ["tick", root, nonce], root,
    start, start.AddMinutes(2), nonce);
var ownerDefinition = ScheduledTaskDefinition.Build(identity, executable,
    ["owner", root, nonce, "$(Arg0)", "$(Arg1)", "$(Arg2)"], root, fixtureNonce: nonce, owner: true);
using var scheduler = new WindowsSupervisionScheduler();

if (mode == "owner")
{
    try
    {
        if (args.Length != 6 || args[5] != "http://127.0.0.1:5000") throw new InvalidDataException("Owner request lacks reservation identity or URL.");
        Check(BackgroundDeploymentManifest.Load(root).ResolveCliExecutable(root) == executable, "Installed executable changed.");
        using var command = await BackgroundOwnerApplication.AcquireScheduledCommandAsync(paths, slug, token);
        using var ownerLock = BackgroundFileLock.AcquireOwner(paths, slug);
        var task = scheduler.Read(ownerDefinition.Name)!;
        ownerDefinition.RequireOwned(scheduler, task);
        if (!task.Enabled) throw new InvalidDataException("Owner task disabled.");
        await ScheduledOwnerLaunch.ConsumeAsync(store, args[3], long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), token);
        ProcessLifetimeTargetRepositoryLock.Acquire(new(Path.GetDirectoryName(identity.GitCommonDirectory)!, "main")
            { GitCommonDirectory = identity.GitCommonDirectory });
        var process = Process.GetCurrentProcess();
        var record = new BackgroundOwnerRecord(1, "running", args[3], process.Id, process.StartTime.ToUniversalTime(),
            root, identity.ConfigPath, slug.Value, Path.GetDirectoryName(identity.GitCommonDirectory), null,
            "http://127.0.0.1:5000", "healthy", "live", "fixture", "fixture", null)
            { ContainmentVersion = 1, GitCommonDirectory = identity.GitCommonDirectory };
        using var job = WindowsProcessLauncher.AttachCurrentProcessToKillOnCloseJob(BackgroundContainment.Name(record));
        using var child = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            ArgumentList = { "child", root, nonce }
        })!;
        await BackgroundOwnerRecord.WriteAsync(BackgroundOwnerRecord.PathFor(paths, slug), record, token);
        Write("owner-" + process.Id + ".json", record);
        command.Dispose();
        await Wait(() => store.Read().State?.Suppressed == true, 60);
        Write("owner-stopped.json", new { process.Id, utc = DateTimeOffset.UtcNow });
        // Owner exit closes the named kill-on-close Job. The runner checks exact child settlement.
    }
    catch (Exception exception)
    {
        Write("owner-rejected-" + Environment.ProcessId + ".json", new { error = exception.ToString() });
        Environment.ExitCode = 1;
    }
    return;
}

if (mode == "tick")
{
    try
    {
        using var command = BackgroundFileLock.AcquireStart(paths, slug);
        Write("tick-start.json", new { pid = Environment.ProcessId, started = Process.GetCurrentProcess().StartTime.ToUniversalTime() });
        var state = store.Read().State!;
        var previous = state.LastObservation!;
        var observation = new DeploymentObservation(1, previous.Utc.AddMinutes(1), previous.Epoch,
            previous.MonotonicTicks + TimeSpan.TicksPerMinute,
            new(root, identity.ConfigPath, slug.Value, Path.GetDirectoryName(identity.GitCommonDirectory)!, identity.GitCommonDirectory),
            null, DeploymentHealth.Failed, "owner-exited", false, new(OwnerProcessState.Exited, true), null, null);
        var tick = new SupervisionTick(new(store.Read,
            (revision, next, ct) => store.SaveAsync(revision, next, ct),
            _ => Task.FromResult(observation),
            (observed, attempts, _) => Task.FromResult(new RecoveryEvidence(new(Guid.NewGuid().ToString("N"),
                observed, attempts, ResourceIncidentSignal.None, new(IncidentCaptureState.NotRequired, null, null),
                "fixture", "fixture", "fixture"), true)),
            (_, _) => Task.FromResult(false),
            async (id, permit, ct) =>
            {
                await ScheduledOwnerLaunch.RequestAsync(store, ownerDefinition, scheduler, id, permit, ct);
                // Overlapping requests carry the same exact identity. IgnoreNew and consumption both apply.
                await ScheduledOwnerLaunch.RequestAsync(store, ownerDefinition, scheduler, id, permit, ct);
                Write("requested.json", new { id, generation = store.Read().State!.Generation, utc = DateTimeOffset.UtcNow });
            }));
        var result = await tick.RunAsync(token);
        Write("tick-result.json", result);
        Check(result.Outcome == "attempt-pending", "Finite tick did not request an owner.");
    }
    catch (Exception exception) { Write("tick-failure.json", new { error = exception.ToString() }); Environment.ExitCode = 1; }
    return;
}

Process? ownerProcess = null;
Process? childProcess = null;
var created = new List<ScheduledTaskDefinition>();
try
{
    var now = DateTimeOffset.UtcNow;
    var epoch = Guid.NewGuid().ToString("N");
    var state = SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null) with
    {
        MatchingFailures = 2, NextDueUtc = now,
        LastObservation = new(epoch, TimeSpan.TicksPerMinute, now, SupervisionHealth.Failed, null)
    };
    await store.SaveAsync(null, state, token);
    foreach (var definition in new[] { ownerDefinition, tickDefinition })
    {
        Check(scheduler.Read(definition.Name) is null, "Nonce collision; foreign task retained.");
        File.WriteAllText(Path.Combine(root, definition.Name + ".requested.xml"), definition.Xml);
        scheduler.Validate(definition.Xml, identity.UserSid);
        scheduler.Create(definition.Name, definition.Xml, identity.UserSid);
        created.Add(definition);
        var registered = scheduler.Read(definition.Name)!;
        definition.RequireOwned(scheduler, registered);
        File.WriteAllText(Path.Combine(root, definition.Name + ".registered.xml"), registered.Xml);
        scheduler.SetEnabled(definition.Name, true);
    }
    // The timer alone starts the finite tick.
    await Wait(() => File.Exists(Path.Combine(root, "tick-result.json")) && !scheduler.Read(tickDefinition.Name)!.Running, 45);
    Check(!File.Exists(Path.Combine(root, "tick-failure.json")), "Tick failed.");
    await Wait(() => Directory.GetFiles(root, "owner-*.json").Length > 0);
    var record = BackgroundOwnerRecord.TryRead(BackgroundOwnerRecord.PathFor(paths, slug))!;
    Check(record is not null, "Owner publication missing.");
    ownerProcess = Process.GetProcessById(record!.ProcessId);
    Check(ownerProcess.StartTime.ToUniversalTime() == record.ProcessStartedAtUtc.UtcDateTime && !ownerProcess.HasExited,
        "Exact owner did not survive tick exit.");
    Check(Directory.GetFiles(root, "owner-*.json").Length == 1, "Overlapping requests admitted more than one owner.");
    Check(BackgroundFileLock.IsOwnerActive(paths, slug), "Owner lock absent.");
    await Wait(() => File.Exists(Path.Combine(root, "child.json")));
    using var childJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "child.json")));
    childProcess = Process.GetProcessById(childJson.RootElement.GetProperty("pid").GetInt32());
    Check(childProcess.StartTime.ToUniversalTime() == childJson.RootElement.GetProperty("started").GetDateTime(), "Child identity changed.");
    Write("post-tick.json", new { owner = record, tick = scheduler.Read(tickDefinition.Name),
        ownerTask = scheduler.Read(ownerDefinition.Name), child = childJson.RootElement, utc = DateTimeOffset.UtcNow });
    using (var command = BackgroundFileLock.AcquireStart(paths, slug))
    {
        var current = store.Read().State!;
        await store.SaveAsync(current.Revision, SupervisionLifecycle.Transition(current, SupervisionLifecycleAction.Stop), token);
        scheduler.SetEnabled(tickDefinition.Name, false);
        scheduler.SetEnabled(ownerDefinition.Name, false);
    }
    await ownerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Write("settlement-first.json", BackgroundContainment.Read(record));
    await Wait(() => BackgroundContainment.Read(record) is { State: OwnerProcessState.Exited, DescendantsSettled: true }, 10);
    Check(BackgroundContainment.Read(record) is { State: OwnerProcessState.Exited, DescendantsSettled: true }, "Owner Job did not settle.");
    Check(!BackgroundFileLock.IsOwnerActive(paths, slug), "Owner lock retained.");
    Check(store.Read().State is { Suppressed: true, Attempts: 1, PendingAttempt.LaunchConsumed: true }, "Stop or reservation changed.");
    Write("result.json", new { assertions, elapsedSeconds = clock.Elapsed.TotalSeconds, record.RunId, record.ProcessId,
        tickResult = scheduler.Read(tickDefinition.Name)!.LastResult, ownerResult = scheduler.Read(ownerDefinition.Name)!.LastResult });
    Console.WriteLine(File.ReadAllText(Path.Combine(root, "result.json")));
}
catch (Exception exception)
{
    Write("failure.json", new { error = exception.ToString(), assertions, elapsedSeconds = clock.Elapsed.TotalSeconds });
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
finally
{
    using (var command = await BackgroundOwnerApplication.AcquireScheduledCommandAsync(paths, slug, token))
    {
        var state = store.Read().State;
        if (state is not null)
            await store.SaveAsync(state.Revision, SupervisionPolicy.SetIntent(state, false, false, state.MaintenanceToken), token);
        foreach (var definition in created)
        {
            var remaining = scheduler.Read(definition.Name);
            if (remaining is null) continue;
            definition.RequireOwned(scheduler, remaining);
            scheduler.SetEnabled(definition.Name, false);
        }
    }
    await Wait(() => created.All(d => scheduler.Read(d.Name)?.Running != true), 65);
    foreach (var definition in created)
    {
        definition.RequireOwned(scheduler, scheduler.Read(definition.Name)!);
        scheduler.Delete(definition.Name);
        Check(scheduler.Read(definition.Name) is null, "Fixture task removal unconfirmed.");
    }
    Write("cleanup.json", new { tasksAbsent = created.All(d => scheduler.Read(d.Name) is null),
        ownerSettled = ownerProcess?.HasExited, childSettled = childProcess?.HasExited,
        elapsedSeconds = clock.Elapsed.TotalSeconds, assertions });
    ownerProcess?.Dispose();
    childProcess?.Dispose();
}
