namespace Nightweaver.ActivityFeed;

// One fixed state per pool. No Bead inventory, diagnostic text or wait duration is retained.
internal sealed record ActivityWaitDetail(ActivityReason Reason, int? Count, int? Limit,
    ActivityFreshness Freshness);

internal sealed class ActivityPoolObservation(ActivityPhase phase, Func<ActivityEvidence, bool> submit,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private ActivityReason? previous;
    private Guid cause;
    private long? lastSuccess;
    private bool unavailable;
    private ActivityWaitDetail current = new(ActivityReason.None, null, null, ActivityFreshness.Unknown);
    internal ActivityWaitDetail Current => lastSuccess is { } last && clock.GetElapsedTime(last) > ActivityLimits.FreshFor
        ? current with { Freshness = ActivityFreshness.Stale } : current;

    internal void Unknown(ActivityReason reason = ActivityReason.SourceFailed)
    {
        current = new(reason, null, null, ActivityFreshness.Unavailable);
        previous = null;
        lastSuccess = null;
        if (!unavailable)
            unavailable = Emit(ActivityKind.EvidenceUnavailable, reason, ActivitySource.Journal, Guid.NewGuid(), null, null,
                ActivityFreshness.Unavailable);
    }

    internal void Observe(ActivityReason reason, int count, int limit)
    {
        if (lastSuccess is { } last && clock.GetElapsedTime(last) > ActivityLimits.FreshFor)
            Unknown(ActivityReason.StaleSource);
        if (unavailable)
        {
            if (!Emit(ActivityKind.HealthChanged, ActivityReason.Recovered, ActivitySource.Health, Guid.NewGuid(), null, null)) return;
            unavailable = false;
        }
        current = new(reason, count, limit, ActivityFreshness.Fresh);
        lastSuccess = clock.GetTimestamp();
        if (previous == reason) return;
        var wasWaiting = previous is not null and not ActivityReason.None;
        if (reason == ActivityReason.None)
        {
            if (!wasWaiting || Emit(ActivityKind.WaitEnded, ActivityReason.None, ActivitySource.Pool, cause, count, limit))
                previous = reason;
            return;
        }
        if (!wasWaiting) cause = Guid.NewGuid();
        if (Emit(wasWaiting ? ActivityKind.WaitChanged : ActivityKind.WaitStarted, reason,
            ActivitySource.Pool, cause, count, limit)) previous = reason;
    }

    private bool Emit(ActivityKind kind, ActivityReason reason, ActivitySource source, Guid identity,
        int? count, int? limit, ActivityFreshness freshness = ActivityFreshness.Fresh) =>
        submit(new(kind, source, phase, identity, "pool-" + phase.ToString().ToLowerInvariant())
        {
            Reason = reason, Count = count <= 1000000000 ? count : null,
            Limit = limit <= 1000000000 ? limit : null, Freshness = freshness, SourceTime = clock.GetUtcNow()
        });
}

internal sealed class ActivityRuntimeObservation(Func<ActivityEvidence, bool> submit, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private ActivityReason? previous;

    internal void Observe(ActivityReason reason)
    {
        if (previous == reason) return;
        if (submit(new(ActivityKind.HealthChanged, ActivitySource.Health, ActivityPhase.Runtime,
            Guid.NewGuid(), "runtime") { Reason = reason, Freshness = ActivityFreshness.Fresh, SourceTime = clock.GetUtcNow() }))
            previous = reason;
    }
}
