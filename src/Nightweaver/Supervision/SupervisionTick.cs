using Nightweaver.Background;

namespace Nightweaver.Supervision;

internal sealed record SupervisionTickResult(string Outcome, SupervisionState? State);

// The caller holds background start.lock for this entire finite operation, including
// final intent checks and native launch. The owner still acquires repository exclusion.
internal sealed record SupervisionTickOperations(
    Func<SupervisionRead> Read,
    Func<long, SupervisionState, CancellationToken, Task<SupervisionState>> Save,
    Func<CancellationToken, Task<DeploymentObservation>> Observe,
    Func<DeploymentObservation, int, CancellationToken, Task<RecoveryEvidence>> Prepare,
    Func<DeploymentObservation, CancellationToken, Task<bool>> Drain,
    Func<string, Func<CancellationToken, Task<bool>>, CancellationToken, Task> Launch);

internal sealed class SupervisionTick(SupervisionTickOperations operations)
{
    internal async Task<SupervisionTickResult> RunAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(SupervisionPolicy.TickDeadline);
        token = deadline.Token;
        var read = operations.Read();
        if (read.Availability == SupervisionAvailability.Manual) return new("manual", null);
        var state = Require(read);
        var observation = await operations.Observe(token).ConfigureAwait(false);
        if (state.PendingAttempt is { } pending)
        {
            // Never replay a launch, even if the previous process died before publishing
            // its owner file. Only the reserved run can acknowledge this attempt.
            var running = observation.Health == DeploymentHealth.Healthy && observation.Owner?.RunId == pending.Id;
            // The prior owner's absence says nothing about a detached launch that has
            // not published its record yet. Keep that crash window unresolved.
            var absent = Settled(observation) && observation.Owner?.RunId == pending.Id;
            var incident = new SupervisionIncident(Guid.NewGuid().ToString("N"), observation.Utc,
                running ? "recovered" : absent ? "start-failed" : "settlement-unknown", null);
            state = await Save(state, SupervisionPolicy.Settle(state, pending.Id, observation.Utc,
                running || absent, incident), token).ConfigureAwait(false);
            return new(running ? "recovered" : absent ? "attempt-settled-absent" : "needs-attention", state);
        }
        if (state.Suppressed) return new("suppressed", state);
        if (state.Exhausted || state.Attention is not null) return new("needs-attention", state);
        state = await Save(state, SupervisionPolicy.Observe(state, observation.ToPolicyObservation()), token).ConfigureAwait(false);
        if (observation.Health != DeploymentHealth.Failed || state.MatchingFailures != 2
            || state.NextDueUtc is null || observation.Utc < state.NextDueUtc)
            return new("observed", state);

        var evidence = await operations.Prepare(observation, state.Attempts, token).ConfigureAwait(false);
        if (evidence.Incident.Capture.State is IncidentCaptureState.Failed or IncidentCaptureState.Pending)
            return new("capture-blocked", state);
        // Capture and probing are async boundaries: no retained intent or owner proof
        // can authorize an effect without another read under command serialization.
        if (!Current(state)) return new("intent-changed", operations.Read().State);
        var confirmed = await operations.Observe(token).ConfigureAwait(false);
        if (confirmed.Health != DeploymentHealth.Failed
            || confirmed.ToPolicyObservation().Owner != observation.ToPolicyObservation().Owner)
            return new("owner-changed", state);
        if (!Current(state)) return new("intent-changed", operations.Read().State);
        if (!Settled(confirmed))
        {
            if (confirmed.Process?.State == OwnerProcessState.Alive && confirmed.OwnerLockHeld == true)
                return new(await operations.Drain(confirmed, token).ConfigureAwait(false)
                    ? "failure-draining" : "drain-unconfirmed", state);
            return new("settlement-unknown", state);
        }
        if (!evidence.ReadyForReconciliation) return new("settlement-unknown", state);
        var reserved = SupervisionPolicy.Reserve(state, state.LastObservation!, Guid.NewGuid().ToString("N"));
        state = await Save(state, reserved, token).ConfigureAwait(false);
        var attempt = state.PendingAttempt!;
        await operations.Launch(attempt.Id, async launchToken =>
        {
            if (!Current(state)) return false;
            var latest = await operations.Observe(launchToken).ConfigureAwait(false);
            return Current(state) && Settled(latest) && latest.Health == DeploymentHealth.Failed
                && latest.ToPolicyObservation().Owner == attempt.PreviousOwner;
        }, token).ConfigureAwait(false);
        // Leave the durable reservation for a fresh tick to reconcile actual native
        // identity and both readiness sources; a launch return/exit code is no proof.
        return new("attempt-pending", operations.Read().State);
    }

    private bool Current(SupervisionState expected)
    {
        var fresh = Require(operations.Read());
        return fresh.Revision == expected.Revision && fresh.Generation == expected.Generation && !fresh.Suppressed;
    }

    private async Task<SupervisionState> Save(SupervisionState before, SupervisionState next, CancellationToken token)
    {
        await operations.Save(before.Revision, next, token).ConfigureAwait(false);
        var confirmed = Require(operations.Read());
        if (confirmed.Revision != before.Revision + 1 || confirmed.Generation != next.Generation
            || confirmed.PendingAttempt != next.PendingAttempt)
            throw new InvalidDataException("Supervision publication unconfirmed; no effect authorized.");
        return confirmed;
    }

    private static SupervisionState Require(SupervisionRead read) => read.Availability == SupervisionAvailability.Available
        && read.State is { } state ? state : throw new InvalidDataException($"Supervision unavailable at '{read.Path}': {read.Diagnostic}");

    private static bool Settled(DeploymentObservation observation) => observation.OwnerLockHeld == false
        && observation.Process is { State: OwnerProcessState.Exited, DescendantsSettled: true };
}
