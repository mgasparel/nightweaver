namespace Nightweaver.Supervision;

internal enum ScheduledSupervisionAction { Status, Install, Enable, Disable, Uninstall }
internal sealed record ScheduledSupervisionStatus(string TaskName, string Boundary, ScheduledTaskSnapshot? Registration,
    SupervisionRead Supervision);

/// <summary>Caller holds the same start.lock as manual commands and finite ticks throughout.</summary>
internal sealed class ScheduledSupervision(SupervisionStore store, SupervisionIdentity identity,
    ScheduledTaskDefinition definition, ISupervisionScheduler scheduler)
{
    internal async Task<ScheduledSupervisionStatus> ApplyAsync(ScheduledSupervisionAction action, CancellationToken token)
    {
        var read = store.Read();
        if (read.Availability == SupervisionAvailability.Unavailable)
            throw new InvalidDataException($"Supervision unavailable at '{read.Path}': {read.Diagnostic}");
        var task = scheduler.Read(definition.Name);
        if (task is not null) definition.RequireOwned(scheduler, task);
        if (action == ScheduledSupervisionAction.Status) return Status(task);
        var state = read.State;
        if (action == ScheduledSupervisionAction.Enable)
        {
            if (task is null || state is null) throw new InvalidOperationException("Install disabled supervision before enabling it.");
            if (state.MaintenanceToken is not null || state.PendingAttempt is not null)
                throw new InvalidOperationException("Reconcile the pending attempt and release maintenance before enabling.");
            // Never reset attempts, exhaustion, attention or history here.
            await Save(SupervisionPolicy.SetIntent(state, true, true, null)).ConfigureAwait(false);
            Mutate(() => scheduler.SetEnabled(definition.Name, true), expectedEnabled: true);
            // Existing interactive sessions need a first evaluation without waiting for another logon.
            // A lost Run response is uncertain: do not replay; periodic ticks remain eligible.
            definition.RequireOwned(scheduler, scheduler.Read(definition.Name)
                ?? throw new InvalidOperationException("Scheduler task disappeared before first evaluation."));
            scheduler.Run(definition.Name);
        }
        else
        {
            var suppressed = SupervisionPolicy.SetIntent(state ?? SupervisionState.Manual(identity), false, false, state?.MaintenanceToken);
            // SetIntent advances generation, revoking any reservation's launch authority.
            // Retain its identity for exact settlement; an unknown detached launch cannot be forgotten.
            suppressed = suppressed with { NextDueUtc = null };
            try { await Save(suppressed).ConfigureAwait(false); }
            catch { await store.LatchUncertainStopAsync().ConfigureAwait(false); throw; }
            if (action == ScheduledSupervisionAction.Install)
            {
                scheduler.Validate(definition.Xml, definition.Sid);
                if (task is null) Mutate(() => scheduler.Create(definition.Name, definition.Xml, definition.Sid), false);
                else Mutate(() => scheduler.SetEnabled(definition.Name, false), false);
            }
            else if (action == ScheduledSupervisionAction.Disable && task is not null)
                Mutate(() => scheduler.SetEnabled(definition.Name, false), false);
            else if (action == ScheduledSupervisionAction.Uninstall && task is not null)
                Mutate(() => scheduler.Delete(definition.Name), null);
        }
        return Status(scheduler.Read(definition.Name));

        async Task Save(SupervisionState next)
        {
            var saved = await store.SaveAsync(state?.Revision, next, token).ConfigureAwait(false);
            read = store.Read();
            if (read.State != saved && read.State?.Revision != saved.Revision)
                throw new InvalidDataException("Supervision intent publication not confirmed.");
            state = saved;
        }
        ScheduledSupervisionStatus Status(ScheduledTaskSnapshot? snapshot) =>
            new(definition.Name, "Current-user InteractiveToken logon plus one-minute finite ticks", snapshot, store.Read());
    }

    private void Mutate(Action effect, bool? expectedEnabled)
    {
        var before = scheduler.Read(definition.Name);
        if (before is not null) definition.RequireOwned(scheduler, before);
        Exception? failure = null;
        try { effect(); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        { failure = exception; }
        var after = scheduler.Read(definition.Name);
        if (after is not null) definition.RequireOwned(scheduler, after);
        if (expectedEnabled is null ? after is not null : after?.Enabled != expectedEnabled)
            throw new InvalidOperationException("Scheduler mutation is unconfirmed; durable intent and task evidence retained. Inspect status before retrying.", failure);
    }
}
