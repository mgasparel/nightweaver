using System.Security.Cryptography;
using System.Text.Json;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Supervision;

namespace Nightweaver.Upgrade;

// The inventory adapter must establish the complete root-wide consumer set and
// source capability from implementation evidence, never inventory capability strings.
internal sealed record UpgradeLifecycleInventory(UpgradeScope Scope, BackgroundOwnerRecord[] Owners,
    bool SourceBoundarySupported);
internal sealed record UpgradeLifecycleOperations(
    Func<CancellationToken, Task<UpgradeLifecycleInventory>> Inventory,
    Func<string, CancellationToken, Task<SupervisionLifecycle?>> OpenLifecycle,
    Func<BackgroundOwnerRecord, string, string?, CancellationToken, Task<bool>> Drain,
    Func<BackgroundOwnerRecord, OwnerProcessObservation> Observe,
    Func<string, bool?> OwnerActive);

/// <summary>Internal boundary for the replacement adapter. No public mutation command or automatic restart.</summary>
internal sealed class UpgradeLifecycle(HostPaths paths, UpgradeJournal journal, UpgradeLifecycleOperations operations,
    TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    internal static async Task<bool> RequestDrainAsync(HostPaths paths, BackgroundOwnerRecord owner, string operationId,
        string? maintenanceToken, CancellationToken token)
    {
        var response = await BackgroundControlProtocol.SendAsync(BackgroundIdentity.PipeName(paths, ProjectSlug.Parse(owner.Project)),
            new("upgrade", owner.RunId, Maintenance: true, UpgradeOperationId: operationId, MaintenanceToken: maintenanceToken),
            SupervisionPolicy.ProbeBudget, token).ConfigureAwait(false);
        return response.Accepted && response.Owner is { } current && OwnerEvidence(current) == OwnerEvidence(owner);
    }

    // Native operations stay replaceable for deterministic command/store race tests.
    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left, PreparedRelease.JsonOptions)
        == JsonSerializer.Serialize(right, PreparedRelease.JsonOptions);

    internal async Task<UpgradeProgress> BeginDrainAsync(long expectedRevision, CancellationToken token)
    {
        using var manager = UpgradeInterlock.AcquireManager(paths);
        var progress = Read(expectedRevision);
        if (progress.Phase != UpgradePhase.Prepared || progress.PendingOperation is not null)
            throw new InvalidOperationException("Upgrade is not prepared for drain.");
        using var commands = AcquireCommands(progress.Scope);
        using var admission = UpgradeInterlock.AcquireAdmission(paths);
        using var configurations = PinConfigurations(progress.Scope);
        var inventory = await InspectAsync(progress, token).ConfigureAwait(false);
        UpgradeInterlock.RequireStartupAllowed(paths);
        var maintenance = new List<UpgradeMaintenance>();
        // Precheck all stores before reserving. A later partial failure retains suppression.
        foreach (var project in progress.Scope.Projects)
        {
            var lifecycle = await operations.OpenLifecycle(project.Name, token).ConfigureAwait(false);
            var read = lifecycle?.Read();
            RequireNoPendingAttempt(read);
            if (read?.Availability == SupervisionAvailability.Unavailable || read?.State?.MaintenanceToken is not null)
                throw new InvalidOperationException("Supervision is unavailable or maintenance is already active.");
        }
        var reservation = Reservation(progress);
        await UpgradeInterlock.ReserveAsync(paths, reservation, token).ConfigureAwait(false);
        foreach (var project in progress.Scope.Projects)
        {
            var lifecycle = await operations.OpenLifecycle(project.Name, token).ConfigureAwait(false);
            var read = lifecycle?.Read();
            RequireNoPendingAttempt(read);
            if (read?.Availability == SupervisionAvailability.Unavailable)
                throw new InvalidDataException("Supervision became unavailable; retain the upgrade boundary.");
            if (read?.State is not null)
                read = await lifecycle!.ApplyAsync(SupervisionLifecycleAction.EnterMaintenance, null, null, token).ConfigureAwait(false);
            maintenance.Add(new(project.Name, read?.State?.MaintenanceToken, read?.State?.Generation ?? 0, read?.State?.Revision ?? 0)
            {
                StartRequested = read?.State is { Enabled: true, DesiredRunning: true }
                    || read?.State is null && operations.OwnerActive(project.Name) == true
                        && inventory.Owners.SingleOrDefault(o => o.Project == project.Name) is { } owner
                        && owner.State != "stopping" && !DeploymentObserver.IsDeliberate(owner.StopReason)
            });
        }
        var owners = inventory.Owners.Select(OwnerEvidence).ToArray();
        var saved = await journal.SaveAsync(expectedRevision, progress with { Phase = UpgradePhase.Draining,
            Maintenance = maintenance.ToArray(), Owners = progress.Owners.Concat(owners).Distinct().ToArray() }, token).ConfigureAwait(false);
        progress = saved.Progress!;
        // Release admission and command locks before requesting/waiting for drain.
        admission.Dispose();
        commands.Dispose();
        configurations.Dispose();
        foreach (var owner in inventory.Owners)
        {
            var fresh = await InspectAsync(progress, token).ConfigureAwait(false);
            var current = fresh.Owners.SingleOrDefault(o => OwnerEvidence(o) == OwnerEvidence(owner))
                ?? throw new InvalidOperationException("Original owner evidence changed before drain.");
            var active = operations.OwnerActive(owner.Project);
            var observation = operations.Observe(current);
            if (active == false && observation is { State: OwnerProcessState.Exited, DescendantsSettled: true })
                continue;
            var item = progress.Maintenance.Single(m => m.Project == owner.Project);
            if (!await operations.Drain(owner, progress.OperationId, item.Token, token).ConfigureAwait(false))
                throw new InvalidOperationException("Upgrade drain is unconfirmed; retain the boundary and inspect the original owner.");
        }
        return progress;
    }

    internal async Task<UpgradeProgress> ConfirmSettlementAsync(long expectedRevision, CancellationToken token)
    {
        using var manager = UpgradeInterlock.AcquireManager(paths);
        var progress = Read(expectedRevision);
        if (progress.Phase != UpgradePhase.Draining) throw new InvalidOperationException("Upgrade is not draining.");
        using var commands = AcquireCommands(progress.Scope);
        using var admission = UpgradeInterlock.AcquireAdmission(paths);
        using var configurations = PinConfigurations(progress.Scope);
        await ConfirmAsync(progress, token).ConfigureAwait(false);
        return (await journal.SaveAsync(expectedRevision, progress with { Phase = UpgradePhase.Settled }, token).ConfigureAwait(false)).Progress!;
    }

    // A settled phase is historical evidence. Check everything again while the callback runs.
    // The callback must not launch owners or write the journal; the replacement adapter
    // records its separate per-file intent before calling this boundary.
    internal async Task WithMutationAsync(long expectedRevision, Func<CancellationToken, Task> mutate, CancellationToken token)
    {
        using var manager = UpgradeInterlock.AcquireManager(paths);
        var progress = Read(expectedRevision);
        if (progress.Phase is not (UpgradePhase.Settled or UpgradePhase.Replacing or UpgradePhase.RollingBack))
            throw new InvalidOperationException("Upgrade phase does not permit payload mutation.");
        using var commands = AcquireCommands(progress.Scope);
        using var admission = UpgradeInterlock.AcquireAdmission(paths);
        using var configurations = PinConfigurations(progress.Scope);
        using var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(journal.DirectoryPath, "writer.lock"), false);
        progress = Read(expectedRevision);
        await ConfirmAsync(progress, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await mutate(token).ConfigureAwait(false);
    }

    // Explicit reconciled release. Never restores saved running intent or starts an owner.
    internal async Task ReleaseAsync(long expectedRevision, CancellationToken token)
    {
        using var manager = UpgradeInterlock.AcquireManager(paths);
        var progress = Read(expectedRevision);
        if (progress.Phase is not (UpgradePhase.Settled or UpgradePhase.Accepted or UpgradePhase.RolledBack))
            throw new InvalidOperationException("Reconcile the upgrade before release.");
        using var commands = AcquireCommands(progress.Scope);
        using var admission = UpgradeInterlock.AcquireAdmission(paths);
        using var configurations = PinConfigurations(progress.Scope);
        using var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(journal.DirectoryPath, "writer.lock"), false);
        progress = Read(expectedRevision);
        await ConfirmAsync(progress, token).ConfigureAwait(false);
        foreach (var item in progress.Maintenance.Where(m => m.Token is not null))
        {
            var lifecycle = await operations.OpenLifecycle(item.Project, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Supervision installation changed.");
            await lifecycle.ApplyAsync(SupervisionLifecycleAction.LeaveMaintenance, item.Token, item.Revision, token).ConfigureAwait(false);
        }
        await UpgradeInterlock.ReleaseAsync(paths, Reservation(progress), token).ConfigureAwait(false);
    }

    // Resume observes the exact reserved runs and never replays a launch.
    // Command locks exclude ticks and operator stop through a fresh launch.
    internal async Task<UpgradeProgress> ActivateAsync(long expectedRevision,
        Func<AffectedUpgradeProject, string, CancellationToken, Task> start,
        Func<AffectedUpgradeProject, CancellationToken, Task<DeploymentObservation>> observe,
        CancellationToken token, bool resume = false)
    {
        using var manager = UpgradeInterlock.AcquireManager(paths);
        var progress = Read(expectedRevision);
        var reconciling = resume && progress.Phase == UpgradePhase.Verifying;
        if (reconciling && progress.PendingOperation is null)
            throw new InvalidOperationException("Verification operation intent is missing.");
        if (!reconciling && (progress.Phase != UpgradePhase.Installed || progress.PendingOperation is not null))
            throw new InvalidOperationException("Activation requires a confirmed installed package.");
        using var commands = AcquireCommands(progress.Scope);
        using var admission = UpgradeInterlock.AcquireAdmission(paths);
        using var configurations = PinConfigurations(progress.Scope);
        if (!reconciling) await ConfirmAsync(progress, token).ConfigureAwait(false);
        else if (progress.Maintenance.Any(m => !m.StartRequested))
        {
            var stoppedInventory = await InspectAsync(progress, token).ConfigureAwait(false);
            foreach (var project in progress.Scope.Projects.Where(p => !progress.Maintenance.Single(m => m.Project == p.Name).StartRequested))
                RequireStoppedSettlement(progress, project, stoppedInventory);
        }
        var package = PreparedRelease.Inspect(paths.Root, true);
        await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        if (package.Digest != progress.NewPackageDigest
            || PreparedRelease.Inspect(journal.RetainedPackageRoot, false).Digest != progress.OldPackageDigest)
            throw new InvalidDataException("Activation payload or backup changed.");
        var grant = reconciling ? UpgradeInterlock.ReadActivation(paths, Reservation(progress)) : null;
        var launches = grant?.Launches ?? progress.Maintenance.Where(m => m.StartRequested)
            .Select(m => new UpgradeLaunch(m.Project, Guid.NewGuid().ToString("N"))).ToArray();
        if (grant is not null && (grant.PackageDigest != progress.NewPackageDigest
            || !launches.Select(l => l.Project).Order().SequenceEqual(progress.Maintenance.Where(m => m.StartRequested).Select(m => m.Project).Order())))
            throw new InvalidDataException("Activation reservation does not match the operation.");
        var intent = reconciling ? progress.PendingOperation! : Guid.NewGuid().ToString("N");
        if (!reconciling)
        {
            progress = (await journal.SaveAsync(progress.Revision, progress with
            { Phase = UpgradePhase.Verifying, PendingOperation = intent }, token).ConfigureAwait(false)).Progress!;
            await UpgradeInterlock.ActivateAsync(paths, Reservation(progress),
                new(1, progress.OperationId, progress.NewPackageDigest, launches), token).ConfigureAwait(false);
            var maintenance = new List<UpgradeMaintenance>();
            foreach (var item in progress.Maintenance)
            {
                if (item.Token is null) { maintenance.Add(item); continue; }
                var lifecycle = await operations.OpenLifecycle(item.Project, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Supervision installation changed.");
                var state = lifecycle.Read().State;
                if (state?.Generation != item.Generation || state.Revision != item.Revision || state.PendingAttempt is not null)
                    throw new InvalidOperationException("Intent changed before activation.");
                var released = await lifecycle.ApplyAsync(SupervisionLifecycleAction.LeaveMaintenance,
                    item.Token, item.Revision, token).ConfigureAwait(false);
                maintenance.Add(item with { Token = null, Generation = released.State!.Generation, Revision = released.State.Revision });
            }
            progress = (await journal.SaveAsync(progress.Revision, progress with { Maintenance = maintenance.ToArray() }, token)
                .ConfigureAwait(false)).Progress!;
        }
        // The root interlock still excludes every ordinary launch. Only these exact
        // owner runs may acquire payload leases. Do not hold admission while children start.
        admission.Dispose();
        foreach (var launch in reconciling ? [] : launches)
        {
            await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
            await start(progress.Scope.Projects.Single(p => p.Name == launch.Project), launch.RunId, token).ConfigureAwait(false);
        }
        // Stop must remain usable during the health window. Final publication takes
        // fresh command/admission locks and compares the same intent revisions again.
        commands.Dispose();
        await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        var inventory = await InspectAsync(progress, token).ConfigureAwait(false);
        if (inventory.Owners.Any(o => operations.OwnerActive(o.Project) != false && !launches.Contains(new(o.Project, o.RunId))))
            throw new InvalidOperationException("Unexpected replacement owner.");
        var pairs = new List<UpgradeAcceptedPair>();
        var firstTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var confirmedTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var verificationStarted = time.GetTimestamp();
        for (var round = 0; round < 2; round++)
        {
            var previous = pairs.ToArray();
            pairs.Clear();
            if (round == 1 && launches.Length > 0)
                await (wait?.Invoke(TimeSpan.FromSeconds(10), token)
                    ?? Task.Delay(TimeSpan.FromSeconds(10), time, token)).ConfigureAwait(false);
            foreach (var launch in launches)
            {
                var project = progress.Scope.Projects.Single(p => p.Name == launch.Project);
                var remaining = TimeSpan.FromSeconds(60) - time.GetElapsedTime(verificationStarted);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Replacement verification window expired.");
                var observation = await observe(project, token).WaitAsync(remaining, time, token).ConfigureAwait(false);
                var pair = UpgradeAcceptance.Confirm(progress, project, package.BuildVersion, launch.RunId, observation, time.GetUtcNow());
                if (round == 0) firstTimes.Add(project.Name, observation.Utc);
                else
                {
                    if (observation.Utc - firstTimes[project.Name] < TimeSpan.FromSeconds(10))
                        throw new InvalidOperationException("Replacement observations are too close together.");
                    confirmedTimes.Add(project.Name, observation.Utc);
                }
                var owner = observation.Owner!;
                var current = inventory.Owners.SingleOrDefault(o => o.Project == project.Name);
                if (current is null || !DeploymentObserver.SameOwner(owner, current) || operations.OwnerActive(project.Name) != true
                    || operations.Observe(owner).State != OwnerProcessState.Alive)
                    throw new InvalidOperationException("Replacement owner changed during acceptance.");
                pairs.Add(pair);
            }
            if (round == 1 && !previous.SequenceEqual(pairs))
                throw new InvalidOperationException("Replacement pair changed between acceptance observations.");
            if (time.GetElapsedTime(verificationStarted) > TimeSpan.FromSeconds(60))
                throw new TimeoutException("Replacement verification window expired.");
            await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        }
        using var finalCommands = AcquireCommands(progress.Scope);
        using var finalAdmission = UpgradeInterlock.AcquireAdmission(paths);
        await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        inventory = await InspectAsync(progress, token).ConfigureAwait(false);
        foreach (var project in progress.Scope.Projects)
        {
            var expected = pairs.SingleOrDefault(p => p.Owner.Project == project.Name);
            var current = inventory.Owners.SingleOrDefault(o => o.Project == project.Name);
            if (expected is null ? operations.OwnerActive(project.Name) != false
                : current is null || OwnerEvidence(current) != expected.Owner || operations.OwnerActive(project.Name) != true
                    || operations.Observe(current).State != OwnerProcessState.Alive)
                throw new InvalidOperationException("Final replacement native identity is unconfirmed.");
            if (expected is not null && (current!.State != "running" || current.Failure is not null
                || current.StopReason is not null || current.WebHealth != "healthy" || current.RuntimeHealth != "live"))
                throw new InvalidOperationException("Final replacement health is unconfirmed; retain the upgrade interlock.");
            if (expected is null) RequireStoppedSettlement(progress, project, inventory);
        }
        if (PreparedRelease.Inspect(paths.Root, true).Digest != package.Digest)
            throw new InvalidDataException("Installed payload changed during acceptance.");
        if (launches.Length > 0 && (time.GetElapsedTime(verificationStarted) < TimeSpan.FromSeconds(10)
            || time.GetElapsedTime(verificationStarted) > TimeSpan.FromSeconds(60)
            || confirmedTimes.Values.Any(utc => !DeploymentObserver.Fresh(utc, time.GetUtcNow()))))
            throw new InvalidOperationException("Replacement acceptance observations expired before publication.");
        await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        var accepted = progress with { Phase = UpgradePhase.Accepted, PendingOperation = null, LastConfirmedOperation = intent,
            AcceptedPairs = pairs.Select(p => p with { FirstObservedUtc = firstTimes[p.Owner.Project], ConfirmedUtc = confirmedTimes[p.Owner.Project] }).ToArray(),
            Evidence = [.. progress.Evidence, new(Guid.NewGuid().ToString("N"),
                launches.Length == 0 ? UpgradeEvidenceKind.StoppedAcceptance : UpgradeEvidenceKind.RunningAcceptance,
                launches.Length == 0 ? "payload-verified-stopped" : "live-pair-accepted", DateTimeOffset.UtcNow)] };
        progress = (await journal.SaveAsync(progress.Revision, accepted, token).ConfigureAwait(false)).Progress!;
        await UpgradeInterlock.ReleaseAsync(paths, Reservation(progress), token).ConfigureAwait(false);
        return progress;
    }

    internal async Task<UpgradeProgress> ConfirmAcceptedAsync(long revision,
        Func<AffectedUpgradeProject, CancellationToken, Task<DeploymentObservation>> observe, CancellationToken token)
    {
        using var manager = UpgradeInterlock.AcquireManager(paths);
        var progress = Read(revision);
        if (progress.Phase != UpgradePhase.Accepted || progress.PendingOperation is not null || progress.AcceptedPairs is null)
            throw new InvalidOperationException("Accepted operation evidence is required.");
        using var commands = AcquireCommands(progress.Scope);
        using var admission = UpgradeInterlock.AcquireAdmission(paths);
        using var configurations = PinConfigurations(progress.Scope);
        var released = UpgradeInterlock.IsReleased(paths, Reservation(progress));
        if (!released) await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        var inventory = await InspectAsync(progress, token).ConfigureAwait(false);
        foreach (var project in progress.Scope.Projects.Where(p => !progress.AcceptedPairs.Any(pair => pair.Owner.Project == p.Name)))
            RequireStoppedSettlement(progress, project, inventory);
        var package = PreparedRelease.Inspect(paths.Root, true);
        if (package.Digest != progress.NewPackageDigest
            || PreparedRelease.Inspect(journal.RetainedPackageRoot, false).Digest != progress.OldPackageDigest)
            throw new InvalidDataException("Accepted payload or backup changed.");
        // Stop must acquire its command and admission locks while control probes wait.
        admission.Dispose();
        commands.Dispose();
        var observations = new Dictionary<string, DeploymentObservation>(StringComparer.Ordinal);
        foreach (var project in progress.Scope.Projects)
        {
            var pair = progress.AcceptedPairs.SingleOrDefault(p => p.Owner.Project == project.Name);
            var owner = inventory.Owners.SingleOrDefault(o => o.Project == project.Name);
            if (pair is not null)
            {
                var observation = await observe(project, token).WaitAsync(SupervisionPolicy.ProbeBudget, token).ConfigureAwait(false);
                observations.Add(project.Name, observation);
                var current = UpgradeAcceptance.Confirm(progress, project, package.BuildVersion, pair.Owner.RunId, observation, time.GetUtcNow());
                if (current.Owner != pair.Owner || current.Cli != pair.Cli || current.Web != pair.Web
                    || owner is null || OwnerEvidence(owner) != pair.Owner || operations.OwnerActive(project.Name) != true
                    || operations.Observe(owner).State != OwnerProcessState.Alive)
                    throw new InvalidOperationException("Accepted replacement identity is unresolved.");
            }
        }
        // Health can change while a probe is in flight. Check native state again
        // before completing a lost release publication.
        using var finalCommands = AcquireCommands(progress.Scope);
        using var finalAdmission = UpgradeInterlock.AcquireAdmission(paths);
        Read(revision);
        if (!released) await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        inventory = await InspectAsync(progress, token).ConfigureAwait(false);
        foreach (var project in progress.Scope.Projects)
        {
            var expected = progress.AcceptedPairs.SingleOrDefault(p => p.Owner.Project == project.Name);
            var current = inventory.Owners.SingleOrDefault(o => o.Project == project.Name);
            if (expected is null) { RequireStoppedSettlement(progress, project, inventory); continue; }
            if (current is null || OwnerEvidence(current) != expected.Owner || operations.OwnerActive(project.Name) != true
                    || operations.Observe(current).State != OwnerProcessState.Alive || current.State != "running"
                    || current.Failure is not null || current.StopReason is not null
                    || current.WebHealth != "healthy" || current.RuntimeHealth != "live")
                throw new InvalidOperationException("Final accepted native identity or health is unresolved.");
        }
        if (PreparedRelease.Inspect(paths.Root, true).Digest != progress.NewPackageDigest)
            throw new InvalidDataException("Accepted payload changed during observation.");
        if (!released) await RequireActivationIntentAsync(progress, token).ConfigureAwait(false);
        // Recheck all three probe times after inventory, hashing and intent reads.
        foreach (var project in progress.Scope.Projects.Where(p => observations.ContainsKey(p.Name)))
        {
            var pair = progress.AcceptedPairs.Single(p => p.Owner.Project == project.Name);
            UpgradeAcceptance.Confirm(progress, project, package.BuildVersion, pair.Owner.RunId,
                observations[project.Name], time.GetUtcNow());
        }
        if (!released)
        {
            await UpgradeInterlock.ReleaseAsync(paths, Reservation(progress), token).ConfigureAwait(false);
        }
        return progress;
    }

    private void RequireStoppedSettlement(UpgradeProgress progress, AffectedUpgradeProject project,
        UpgradeLifecycleInventory inventory)
    {
        if (operations.OwnerActive(project.Name) != false)
            throw new InvalidOperationException("Stopped acceptance settlement is unresolved.");
        var current = inventory.Owners.SingleOrDefault(o => o.Project == project.Name);
        var originals = progress.Owners.Where(o => o.Project == project.Name).ToArray();
        if (current is not null && !originals.Contains(OwnerEvidence(current)))
            throw new InvalidOperationException("Stopped acceptance has a foreign owner.");
        foreach (var original in originals)
        {
            var owner = inventory.Owners.SingleOrDefault(o => OwnerEvidence(o) == original)
                ?? throw new InvalidOperationException("Original owner evidence is inaccessible or missing.");
            if (operations.Observe(owner) is not { State: OwnerProcessState.Exited, DescendantsSettled: true })
                throw new InvalidOperationException("Original owner and descendant settlement is pending or unavailable.");
        }
    }

    private async Task RequireActivationIntentAsync(UpgradeProgress progress, CancellationToken token)
    {
        Read(progress.Revision);
        UpgradeInterlock.RequireCurrent(paths, Reservation(progress));
        foreach (var item in progress.Maintenance)
        {
            var lifecycle = await operations.OpenLifecycle(item.Project, token).ConfigureAwait(false);
            UpgradeInterlock.RequireActivationAllowed(paths, Reservation(progress), ProjectSlug.Parse(item.Project));
            var read = lifecycle?.Read();
            RequireNoPendingAttempt(read);
            if (read?.Availability == SupervisionAvailability.Unavailable
                || read?.State is { } state && (state.Generation != item.Generation || state.Revision != item.Revision
                    || state.MaintenanceToken != item.Token || item.StartRequested && (!state.Enabled || !state.DesiredRunning))
                || read?.State is null && item.Revision != 0)
                throw new InvalidOperationException("Activation intent changed; retain the upgrade interlock.");
        }
    }

    private async Task ConfirmAsync(UpgradeProgress progress, CancellationToken token)
    {
        UpgradeInterlock.RequireCurrent(paths, Reservation(progress));
        var inventory = await InspectAsync(progress, token).ConfigureAwait(false);
        if (inventory.Owners.Any(o => !progress.Owners.Contains(OwnerEvidence(o))))
            throw new InvalidOperationException("A foreign owner is present; no payload mutation is permitted.");
        foreach (var project in progress.Scope.Projects)
        {
            if (operations.OwnerActive(project.Name) != false)
                throw new InvalidOperationException("Owner settlement is pending or unavailable.");
            var lifecycle = await operations.OpenLifecycle(project.Name, token).ConfigureAwait(false);
            var read = lifecycle?.Read();
            RequireNoPendingAttempt(read);
            var saved = progress.Maintenance.Single(m => m.Project == project.Name);
            if (read?.Availability == SupervisionAvailability.Unavailable
                || read?.State is { } state && (saved.Token is null || state.MaintenanceToken != saved.Token
                    || state.Revision != saved.Revision || state.Generation != saved.Generation)
                || read?.State is null && saved.Token is not null)
                throw new InvalidOperationException("Maintenance token, revision or generation changed; retain the boundary.");
        }
        foreach (var original in progress.Owners)
        {
            // Reconstruct only from a freshly inspected exact original record. Missing
            // records cannot prove native exit or the named containment Job's emptiness.
            var owner = inventory.Owners.SingleOrDefault(o => OwnerEvidence(o) == original)
                ?? throw new InvalidOperationException("Original owner evidence is inaccessible or missing.");
            if (operations.Observe(owner) is not { State: OwnerProcessState.Exited, DescendantsSettled: true })
                throw new InvalidOperationException("Original owner and descendant settlement is pending or unavailable.");
        }
    }

    private static void RequireNoPendingAttempt(SupervisionRead? read)
    {
        if (read?.State?.PendingAttempt is not null)
            throw new InvalidOperationException("A supervision launch is unresolved; retain the reservation and reconcile the exact reserved owner.");
    }

    private async Task<UpgradeLifecycleInventory> InspectAsync(UpgradeProgress progress, CancellationToken token)
    {
        var current = await operations.Inventory(token).ConfigureAwait(false);
        if (!current.SourceBoundarySupported || !Same(current.Scope, progress.Scope) || current.Scope.Root != paths.Root
            || current.Owners.Length > 256 || current.Owners.Select(o => o.Project).Distinct().Count() != current.Owners.Length)
            throw new InvalidOperationException("Upgrade source capability or complete owner inventory is unconfirmed.");
        foreach (var owner in current.Owners)
        {
            var project = progress.Scope.Projects.SingleOrDefault(p => p.Name == owner.Project);
            if (project is null || owner.Root != paths.Root || owner.ConfigPath != project.Configuration
                || owner.ContainmentVersion != 1 || owner.RuntimeId is null || owner.ProcessId <= 0
                || owner.GitCommonDirectory != project.GitCommonDirectory)
                throw new InvalidOperationException("Upgrade owner identity is unconfirmed.");
        }
        return current;
    }

    private UpgradeProgress Read(long revision)
    {
        var read = journal.Read();
        if (read.Availability != UpgradeJournalAvailability.Available || read.Progress?.Revision != revision)
            throw new InvalidOperationException("Upgrade revision is unavailable or changed.");
        return read.Progress;
    }

    internal static UpgradeOwner OwnerEvidence(BackgroundOwnerRecord owner) => new(owner.Project, owner.ProcessId,
        owner.ProcessStartedAtUtc, owner.RunId, owner.RunId, owner.RuntimeId!, BackgroundContainment.Name(owner));
    private static UpgradeReservation Reservation(UpgradeProgress progress) => new(1, progress.Scope.Root, progress.OperationId,
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(progress.Scope, PreparedRelease.JsonOptions))));

    private CommandLocks AcquireCommands(UpgradeScope scope)
    {
        var locks = new CommandLocks();
        try
        {
            foreach (var project in scope.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
                locks.Items.Add(BackgroundFileLock.AcquireStart(paths, ProjectSlug.Parse(project.Name)));
            return locks;
        }
        catch { locks.Dispose(); throw; }
    }

    private static CommandLocks PinConfigurations(UpgradeScope scope)
    {
        var pins = new CommandLocks();
        try
        {
            foreach (var project in scope.Projects)
            {
                pins.Items.Add(InvocationRuntimeFiles.OpenExistingPackageFileForRead(project.Configuration));
                var digest = Convert.ToHexString(SHA256.HashData(PreparedRelease.Read(project.Configuration, 1024 * 1024)));
                if (digest != project.ConfigurationDigest) throw new InvalidDataException("Upgrade configuration changed.");
                pins.Items.Add(InvocationRuntimeFiles.PinExistingRuntimeDirectory(project.GitCommonDirectory));
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    private sealed class CommandLocks : IDisposable
    {
        internal List<IDisposable> Items { get; } = [];
        public void Dispose() { foreach (var item in Items.AsEnumerable().Reverse()) item.Dispose(); Items.Clear(); }
    }
}
