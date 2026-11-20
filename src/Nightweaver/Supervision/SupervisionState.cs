using System.Text.Json.Serialization;

namespace Nightweaver.Supervision;

// This model contains no Bead identifiers, claims, stages or Delivery repair accounting.
internal sealed record SupervisionIdentity(string Root, string ConfigPath, string GitCommonDirectory, string UserSid);
internal sealed record SupervisionOwner(int ProcessId, DateTimeOffset StartedUtc, string OwnerId, string? RuntimeId);
internal enum SupervisionHealth { Unknown, Healthy, Failed, Draining, Gap }
internal sealed record SupervisionObservation(string Epoch, long MonotonicTicks, DateTimeOffset Utc,
    SupervisionHealth Health, SupervisionOwner? Owner);
internal sealed record SupervisionAttempt(string Id, long Generation, int Number, DateTimeOffset ReservedUtc,
    string Epoch, SupervisionOwner? PreviousOwner);
internal sealed record SupervisionIncident(string Id, DateTimeOffset Utc, string Reason, string? EvidenceId);

internal sealed record SupervisionState(
    int Version,
    SupervisionIdentity Identity,
    long Revision,
    bool Enabled,
    bool DesiredRunning,
    long Generation,
    string? MaintenanceToken,
    int Attempts,
    bool Exhausted,
    DateTimeOffset? NextDueUtc,
    SupervisionAttempt? PendingAttempt,
    SupervisionObservation? LastObservation,
    int MatchingFailures,
    string? Attention,
    SupervisionIncident? LastIncident,
    SupervisionIncident? UnresolvedIncident,
    SupervisionIncident[] History,
    long DiscardedHistory)
{
    [JsonIgnore]
    internal bool Suppressed => !Enabled || !DesiredRunning || MaintenanceToken is not null;

    internal static SupervisionState Manual(SupervisionIdentity identity) =>
        new(1, identity, 0, false, false, 0, null, 0, false, null, null, null, 0, null, null, null, [], 0);
}

/// <summary>Pure transitions only. A reserved value is not launch authority until durably committed.
/// Repository exclusion and fresh native owner checks remain the consumer's responsibility.</summary>
internal static class SupervisionPolicy
{
    internal const int MaximumAttempts = 3;
    internal const int MaximumHistory = 32;
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan TickDeadline = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan SchedulerActionLimit = TimeSpan.FromSeconds(240);
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(75);
    internal static readonly TimeSpan ReconciliationBudget = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan PersistenceBudget = TimeSpan.FromSeconds(10);

    // Delay before attempt 1, 2 and 3; due time rounds up to an eligible tick.
    internal static TimeSpan Backoff(int completedAttempts) => completedAttempts switch
    {
        0 => TimeSpan.FromSeconds(30),
        1 => TimeSpan.FromMinutes(2),
        2 => TimeSpan.FromMinutes(10),
        _ => throw new InvalidOperationException("Recovery attempt budget exhausted.")
    };

    internal static SupervisionState Observe(SupervisionState state, SupervisionObservation observation)
    {
        SupervisionValidation.Validate(state);
        SupervisionValidation.Validate(observation);
        var previous = state.LastObservation;
        if (previous is not null && (observation.Utc < previous.Utc
            || observation.Epoch == previous.Epoch && observation.MonotonicTicks < previous.MonotonicTicks))
            return state with { Attention = "clock-discontinuity", MatchingFailures = 0 };

        var contiguous = previous is not null && previous.Epoch == observation.Epoch
            && observation.MonotonicTicks - previous.MonotonicTicks >= TickInterval.Ticks
            && observation.MonotonicTicks - previous.MonotonicTicks <= TickInterval.Ticks * 2
            && (observation.Utc - previous.Utc - TimeSpan.FromTicks(observation.MonotonicTicks - previous.MonotonicTicks)).Duration()
                <= TimeSpan.FromSeconds(5);
        // Sub-minute duplicate probes cannot manufacture repeated failures. Large gaps, suspension,
        // new processes and reboot require fresh qualification, but do not refund attempts or due time.
        var failures = observation.Health == SupervisionHealth.Failed
            ? contiguous && previous!.Health == SupervisionHealth.Failed && previous.Owner == observation.Owner
                ? Math.Min(2, state.MatchingFailures + 1) : 1
            : 0;
        var due = state.NextDueUtc;
        if (failures == 2 && due is null && state.Attempts < MaximumAttempts
            && state.PendingAttempt is null && !state.Suppressed && state.Attention is null)
            due = observation.Utc + Backoff(state.Attempts);
        return state with { LastObservation = observation, MatchingFailures = failures, NextDueUtc = due };
    }

    internal static SupervisionState Reserve(SupervisionState state, SupervisionObservation observation, string attemptId)
    {
        // Only the exact most recently persisted observation can qualify a reservation. Consumers
        // commit Observe first and retain serialization through reserve/launch reconciliation.
        SupervisionValidation.Validate(state);
        SupervisionValidation.ValidateId(attemptId);
        if (state.Suppressed || state.Attention is not null || state.Exhausted || state.PendingAttempt is not null
            || state.Attempts >= MaximumAttempts || state.MatchingFailures != 2
            || state.LastObservation != observation || observation.Health != SupervisionHealth.Failed
            || state.NextDueUtc is null || observation.Utc < state.NextDueUtc)
            throw new InvalidOperationException("Recovery is not eligible for reservation.");
        return state with
        {
            Attempts = state.Attempts + 1,
            NextDueUtc = null,
            PendingAttempt = new(attemptId, state.Generation, state.Attempts + 1, observation.Utc,
                observation.Epoch, observation.Owner),
            MatchingFailures = 0
        };
    }

    internal static SupervisionState SetIntent(SupervisionState state, bool enabled, bool desiredRunning, string? maintenanceToken)
    {
        SupervisionValidation.Validate(state);
        if (maintenanceToken is not null) SupervisionValidation.ValidateId(maintenanceToken);
        return state with { Enabled = enabled, DesiredRunning = desiredRunning, MaintenanceToken = maintenanceToken,
            Generation = checked(state.Generation + 1), MatchingFailures = 0, LastObservation = null };
    }

    internal static SupervisionState Settle(SupervisionState state, string attemptId, DateTimeOffset utc,
        bool confirmedRunningOrAbsent, SupervisionIncident incident)
    {
        SupervisionValidation.Validate(state);
        if (state.PendingAttempt is not { } pending || pending.Id != attemptId || utc < pending.ReservedUtc
            || incident.Utc != utc)
            throw new InvalidOperationException("Attempt settlement does not match the retained reservation.");
        SupervisionValidation.Validate(incident);
        // Unknown process settlement never permits another attempt; retain the reservation and incident.
        var next = AddIncident(state, incident);
        return next with
        {
            PendingAttempt = confirmedRunningOrAbsent ? null : pending,
            Attention = confirmedRunningOrAbsent && state.Attention == "unresolved-attempt" ? null
                : confirmedRunningOrAbsent ? state.Attention : state.Attention ?? "unresolved-attempt",
            UnresolvedIncident = confirmedRunningOrAbsent ? null : state.UnresolvedIncident ?? incident,
            Exhausted = state.Attempts == MaximumAttempts,
            NextDueUtc = confirmedRunningOrAbsent && state.Attempts < MaximumAttempts ? utc + Backoff(state.Attempts) : null,
            MatchingFailures = 0,
            LastObservation = null
        };
    }

    // No automatic reset: transient readiness, health observations, logon and reboot cannot refund budget.
    internal static SupervisionState Reset(SupervisionState state)
    {
        SupervisionValidation.Validate(state);
        if (state.PendingAttempt is not null)
            throw new InvalidOperationException("Reconcile the unresolved attempt before resetting recovery.");
        return state with { Generation = checked(state.Generation + 1), Attempts = 0, Exhausted = false,
            NextDueUtc = null, Attention = null, LastObservation = null, MatchingFailures = 0 };
    }

    private static SupervisionState AddIncident(SupervisionState state, SupervisionIncident incident)
    {
        var history = state.History.Append(incident).ToArray();
        var discarded = Math.Max(0, history.Length - MaximumHistory);
        return state with { LastIncident = incident, History = history.Skip(discarded).ToArray(),
            DiscardedHistory = checked(state.DiscardedHistory + discarded) };
    }
}
