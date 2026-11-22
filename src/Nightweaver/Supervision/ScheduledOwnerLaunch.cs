namespace Nightweaver.Supervision;

/// <summary>Caller holds start.lock through consumption and owner publication.</summary>
internal static class ScheduledOwnerLaunch
{
    internal static async Task ConsumeAsync(SupervisionStore store, string attemptId, long generation,
        CancellationToken token)
    {
        var read = store.Read();
        var state = read.State;
        if (read.Availability != SupervisionAvailability.Available || state is null || state.Suppressed
            || state.Generation != generation || state.PendingAttempt is not { } attempt
            || attempt.Id != attemptId || attempt.Generation != generation || attempt.LaunchConsumed)
            throw new InvalidOperationException("Scheduled owner request is absent, stale, suppressed or already consumed.");
        var consumed = attempt with { LaunchConsumed = true };
        var saved = await store.SaveAsync(state.Revision, state with { PendingAttempt = consumed }, token).ConfigureAwait(false);
        var confirmed = store.Read();
        if (confirmed.Availability != SupervisionAvailability.Available || confirmed.State?.Revision != saved.Revision
            || confirmed.State.PendingAttempt != consumed || confirmed.State.Suppressed)
            throw new InvalidDataException("Scheduled owner consumption is unconfirmed; no owner start authorized.");
    }

    internal static async Task RequestAsync(SupervisionStore store, ScheduledTaskDefinition definition,
        ISupervisionScheduler scheduler, string attemptId, Func<CancellationToken, Task<bool>> permit,
        CancellationToken token, string urls = "http://127.0.0.1:5000")
    {
        var state = store.Read().State ?? throw new InvalidDataException("Supervision unavailable.");
        if (!Nightweaver.Cli.BackgroundCommandParser.IsLoopbackUrl(urls))
            throw new InvalidDataException("Scheduled owner requires a loopback URL.");
        var task = scheduler.Read(definition.Name) ?? throw new InvalidOperationException("Owner task is absent.");
        definition.RequireOwned(scheduler, task);
        if (!task.Enabled || state.PendingAttempt is not { } attempt || attempt.Id != attemptId
            || attempt.LaunchConsumed || attempt.Generation != state.Generation || state.Suppressed
            || !await permit(token).ConfigureAwait(false))
            throw new InvalidOperationException("Scheduled owner launch is not authorized.");
        var fresh = store.Read();
        if (fresh.Availability != SupervisionAvailability.Available || fresh.State?.Revision != state.Revision
            || fresh.State.Generation != attempt.Generation || fresh.State.Suppressed)
            throw new InvalidOperationException("Scheduled owner intent changed before the request.");
        // Run is a request, never readiness evidence. Do not replay an uncertain reply.
        scheduler.Run(definition.Name, [attempt.Id, attempt.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), urls]);
    }
}
