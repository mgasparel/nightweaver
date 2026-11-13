namespace Nightweaver.Execution;

internal static class CleanupRetryPolicy
{
    private static readonly string[] DenialWords = ["denied", "permission", "policy", "not permitted", "access"];
    private static readonly string[] TransientWords = ["sharing violation", "being used by another process", "resource busy", "temporarily unavailable", "unable to unlink"];

    internal static bool IsDue(CompletedEnvironmentObligation record, DateTimeOffset now) =>
        record.Status == CleanupStatus.Pending && (record.Attempts == 0 || record.CycleAttempts == 0)
        || record.Status == CleanupStatus.RetryScheduled && record.NextAttempt <= now;

    internal static CompletedEnvironmentObligation Begin(CompletedEnvironmentObligation record, DateTimeOffset now) => record with
    {
        Attempts = checked(record.Attempts + 1), CycleAttempts = checked((record.CycleAttempts ?? record.Attempts) + 1),
        LastAttempt = now, Status = CleanupStatus.Pending, NextAttempt = null, ResolvedAt = null, Diagnostic = null
    };

    internal static CompletedEnvironmentObligation Fail(CompletedEnvironmentObligation record, string reason,
        bool transient, DateTimeOffset now)
    {
        var delay = (record.CycleAttempts ?? record.Attempts) switch
        {
            1 => TimeSpan.FromSeconds(30), 2 => TimeSpan.FromMinutes(2), 3 => TimeSpan.FromMinutes(10), _ => (TimeSpan?)null
        };
        var retry = transient && delay is not null;
        return record with { Status = retry ? CleanupStatus.RetryScheduled : CleanupStatus.NeedsAttention,
            NextAttempt = retry ? now + delay!.Value : null, ResolvedAt = null,
            Diagnostic = Limit(reason), LastFailure = Limit(reason),
            LastDenial = transient ? record.LastDenial : Limit(reason) };
    }

    // Only known sharing/busy failures are automatic. Unknown tool/policy failures fail closed.
    internal static bool IsTransient(Exception exception) => exception is IOException io
        && (io.HResult & 0xffff) is 32 or 33 or 16 or 11;

    internal static bool IsTransientGitFailure(string reason) =>
        !DenialWords.Any(word => reason.Contains(word, StringComparison.OrdinalIgnoreCase))
        && TransientWords
            .Any(word => reason.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string Limit(string reason) => reason.Length <= 384 ? reason : reason[..384];
}
