using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Nightweaver.Inspection;

namespace Nightweaver.ActivityFeed;

internal enum ActivityKind
{
    InvocationStarted, InvocationCompleted, PlanningApproved, PlanningPublished,
    DeliveryApproved, ChangesRequired, QaAdmitted, MergeVerified, BeadClosed,
    StateObserved, PauseObserved, PauseRemovalObserved, ResumeConfirmed,
    RecoveryStarted, RecoveryCompleted, RecoveryFailed, HealthChanged,
    WaitStarted, WaitChanged, WaitEnded, CleanupFailed, EvidenceUnavailable,
    RecordsLost, RuntimeReplaced, HistoryGap
}
internal enum ActivitySource { Invocation, Publication, Admission, MergeVerifier, Inventory, Attention, Recovery, Health, Pool, Journal }
internal enum ActivityStrength { Unknown, Observed, Confirmed, Verified }
internal enum ActivityPhase { Planning, Delivery, Qa, Merger, Runtime }
internal enum ActivityRole { Planner, PlanChallenger, Implementer, Reviewer, Qa, Merger }
internal enum ActivityReason
{
    None, Unsupported, MissingEvidence, ConflictingEvidence, StaleSource, SourceFailed,
    NeedsHuman, ProductDecision, CombinedPause, Capacity, Backpressure, Blocked,
    Deferred, Maintenance, Unhealthy, Recovered, QueueFull, DiskFailure, PartialTail,
    CursorExpired, Retention, ClientEviction, Replaced, RuntimeStarting, RuntimeRunning,
    RuntimeDraining, RuntimeStopped, RuntimeFailed, UnknownOwner
}
internal enum ActivityFreshness { Unknown, Fresh, Stale, Unavailable }
internal enum ActivityConnection { Connecting, Connected, Disconnected }
internal enum ActivityDetail { Comment, Invocation, Bead, Recovery, Health }

internal static class ActivityLimits
{
    internal const int ProducerQueue = 512;
    internal const int RecordBytes = 4096;
    internal const int SegmentBytes = 1024 * 1024;
    internal const int Segments = 8;
    internal const int RetainedRecords = 4096;
    internal const int PageRecords = 100;
    internal const int BatchBytes = 256 * 1024;
    internal const int ClientRecords = 500;
    internal const int IdentifierCharacters = 128;
    internal const int LinkCharacters = 512;
    internal static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(60);
}

// Inputs come from trusted, typed source adapters, never from Agent prose or journal hints.
// A source is an attribution boundary, not a substitute for guarded workflow verification.
internal sealed record ActivityEvidence(ActivityKind Kind, ActivitySource Source, ActivityPhase Phase,
    Guid Cause, string SourceReference)
{
    public string? Bead { get; init; }
    public string? Invocation { get; init; }
    public DateTimeOffset? SourceTime { get; init; }
    public ActivityFreshness Freshness { get; init; } = ActivityFreshness.Unknown;
    public ActivityReason Reason { get; init; }
    public int? Count { get; init; }
    public int? Limit { get; init; }
    public ActivityDetail? Detail { get; init; }
    public ActivityRole? Role { get; init; }
    public MergerFacts? Merge { get; init; }
}

// Both sides are supplied only after the existing live verifier or historical
// DigestMergeEvidence contract has accepted them. This model checks their agreement too.
internal sealed record MergerFacts(string Bead, string PullRequest, string CheckedHead,
    string MergeCommit, string ReportReference, string VerificationReference, bool RequiredChecksPassed);
internal sealed record ActivityMergeProof(MergerFacts Report, MergerFacts Confirmation);
internal readonly record struct ActivityPosition(Guid Journal, long Offset, Guid Runtime, long Sequence);

// No public constructor or init properties: all serialized records pass the projector.
internal sealed class ActivityRecord
{
    internal ActivityRecord(ActivityPosition position, DateTimeOffset observedAt, ActivityEvidence evidence,
        ActivityStrength strength, string message)
    {
        Position = position; ObservedAt = observedAt; Kind = evidence.Kind; Source = evidence.Source;
        Phase = evidence.Phase; Cause = evidence.Cause; SourceReference = evidence.SourceReference;
        Bead = evidence.Bead; Invocation = evidence.Invocation; SourceTime = evidence.SourceTime;
        Freshness = evidence.Freshness; Reason = evidence.Reason; Count = evidence.Count;
        Limit = evidence.Limit; Detail = evidence.Detail; Role = evidence.Role; Merge = evidence.Merge;
        Strength = strength; Message = message;
    }
    public int Schema { get; } = 1;
    public ActivityPosition Position { get; }
    public DateTimeOffset ObservedAt { get; }
    public ActivityKind Kind { get; }
    public ActivitySource Source { get; }
    public ActivityPhase Phase { get; }
    public Guid Cause { get; }
    public string SourceReference { get; }
    public string? Bead { get; }
    public string? Invocation { get; }
    public DateTimeOffset? SourceTime { get; }
    public ActivityFreshness Freshness { get; }
    public ActivityReason Reason { get; }
    public int? Count { get; }
    public int? Limit { get; }
    public ActivityDetail? Detail { get; }
    public ActivityRole? Role { get; }
    public MergerFacts? Merge { get; }
    public ActivityStrength Strength { get; }
    public string Message { get; }
}

internal static class ActivityProjector
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] Serialize(ActivityRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length + 1 > ActivityLimits.RecordBytes) throw new InvalidDataException("Activity record exceeds its byte limit.");
        return bytes;
    }

    internal static ActivityRecord? Project(ActivityPosition position, DateTimeOffset observedAt,
        ActivityEvidence evidence, GitHubRepositoryIdentity repository, ActivityMergeProof? mergeProof = null)
    {
        if (position.Journal == Guid.Empty || position.Runtime == Guid.Empty || position.Offset <= 0 || position.Sequence <= 0
            || evidence.Cause == Guid.Empty || !Enum.IsDefined(evidence.Kind) || !Enum.IsDefined(evidence.Source)
            || !Enum.IsDefined(evidence.Phase) || !Enum.IsDefined(evidence.Freshness) || !Enum.IsDefined(evidence.Reason)
            || evidence.Detail is { } detail && !Enum.IsDefined(detail)
            || evidence.Role is { } role && !Enum.IsDefined(role)
            || !Identifier(evidence.SourceReference) || evidence.Bead is { } bead && !Identifier(bead)
            || evidence.Invocation is { } invocation && !Identifier(invocation)
            || evidence.Count is < 0 or > 1000000000 || evidence.Limit is < 0 or > 1000000000
            || evidence.Merge is not null) return null;

        var rule = Rule(evidence.Kind);
        if (rule.Source != evidence.Source) return null;
        if (evidence.Kind is ActivityKind.PlanningApproved or ActivityKind.PlanningPublished or ActivityKind.DeliveryApproved
            or ActivityKind.ChangesRequired or ActivityKind.QaAdmitted or ActivityKind.MergeVerified or ActivityKind.BeadClosed
            or ActivityKind.StateObserved or ActivityKind.PauseObserved or ActivityKind.PauseRemovalObserved or ActivityKind.ResumeConfirmed
            && evidence.Bead is null) return null;
        if (evidence.Kind is ActivityKind.InvocationStarted or ActivityKind.InvocationCompleted
            && (evidence.Invocation is null || evidence.Bead is null)) return null;
        if (evidence.Kind is ActivityKind.PlanningApproved or ActivityKind.PlanningPublished
            && evidence.Phase != ActivityPhase.Planning) return null;
        if (evidence.Kind == ActivityKind.DeliveryApproved && evidence.Phase != ActivityPhase.Delivery) return null;
        if (evidence.Kind == ActivityKind.QaAdmitted && evidence.Phase != ActivityPhase.Qa) return null;
        if (evidence.Kind == ActivityKind.MergeVerified && evidence.Phase != ActivityPhase.Merger) return null;

        // Freshness qualifies the source, not transport. Old source time alone is not stale.
        if (rule.Strength is ActivityStrength.Confirmed or ActivityStrength.Verified
            && evidence.Freshness != ActivityFreshness.Fresh)
            return Unknown(ActivityReason.StaleSource);

        if (evidence.Kind == ActivityKind.MergeVerified)
        {
            if (mergeProof is null) return Unknown(ActivityReason.MissingEvidence);
            if (mergeProof.Report != mergeProof.Confirmation) return Unknown(ActivityReason.ConflictingEvidence);
            var facts = mergeProof.Confirmation;
            if (facts.Bead != evidence.Bead || !Identifier(facts.Bead)
                || !facts.RequiredChecksPassed || !Hash(facts.CheckedHead) || !Hash(facts.MergeCommit)
                || !Identifier(facts.ReportReference) || !Identifier(facts.VerificationReference)
                || facts.ReportReference == facts.VerificationReference
                || facts.VerificationReference != evidence.SourceReference
                || !SafePullRequest(facts.PullRequest, repository)) return Unknown(ActivityReason.MissingEvidence);
            evidence = evidence with { Merge = facts };
        }
        var record = new ActivityRecord(position, observedAt, evidence, rule.Strength, rule.Message);
        return Serialize(record).Length <= ActivityLimits.RecordBytes ? record : null;

        ActivityRecord Unknown(ActivityReason reason) => new(position, observedAt,
            evidence with { Kind = ActivityKind.EvidenceUnavailable, Reason = reason }, ActivityStrength.Unknown,
            "Activity evidence is unavailable.");
    }

    internal static ActivityFreshness Freshness(TimeSpan? monotonicAge, bool sourceFailed) => sourceFailed
        ? ActivityFreshness.Unavailable : monotonicAge is null || monotonicAge < TimeSpan.Zero
            ? ActivityFreshness.Unknown : monotonicAge <= ActivityLimits.FreshFor ? ActivityFreshness.Fresh : ActivityFreshness.Stale;

    private static bool Identifier(string? value) => value is { Length: > 0 and <= ActivityLimits.IdentifierCharacters }
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool Hash(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    private static bool SafePullRequest(string? value, GitHubRepositoryIdentity repository)
    {
        if (value is null || value.Length > ActivityLimits.LinkCharacters || value.Any(c => !char.IsAscii(c) || char.IsControl(c))
            || value.Contains('\\') || value.Contains('%')
            || GitHubRepositoryIdentity.ParsePullRequestUrl(value) is not { } pr || !pr.Repository.Matches(repository)) return false;
        // Demand canonical syntax. This rejects user info, ports, escaped paths and URI normalization tricks.
        return value == $"https://{repository.CliRepository}/pull/{pr.Number}";
    }

    internal static (ActivitySource Source, ActivityStrength Strength, string Message) Rule(ActivityKind kind) => kind switch
    {
        ActivityKind.InvocationStarted => (ActivitySource.Invocation, ActivityStrength.Observed, "Agent invocation started."),
        ActivityKind.InvocationCompleted => (ActivitySource.Invocation, ActivityStrength.Observed, "Agent invocation completed. Outcome is not confirmed."),
        ActivityKind.PlanningApproved => (ActivitySource.Publication, ActivityStrength.Confirmed, "Planning approval was confirmed."),
        ActivityKind.PlanningPublished => (ActivitySource.Publication, ActivityStrength.Confirmed, "Plan publication was confirmed."),
        ActivityKind.DeliveryApproved => (ActivitySource.Publication, ActivityStrength.Confirmed, "Delivery approval was confirmed."),
        ActivityKind.ChangesRequired => (ActivitySource.Publication, ActivityStrength.Confirmed, "Repair handoff was confirmed."),
        ActivityKind.QaAdmitted => (ActivitySource.Admission, ActivityStrength.Confirmed, "QA admission was confirmed."),
        ActivityKind.MergeVerified => (ActivitySource.MergeVerifier, ActivityStrength.Verified, "Merge verification passed."),
        ActivityKind.BeadClosed => (ActivitySource.Publication, ActivityStrength.Confirmed, "Bead closure was confirmed."),
        ActivityKind.StateObserved => (ActivitySource.Inventory, ActivityStrength.Observed, "Bead state was observed."),
        ActivityKind.PauseObserved => (ActivitySource.Inventory, ActivityStrength.Observed, "Pause labels were observed."),
        ActivityKind.PauseRemovalObserved => (ActivitySource.Inventory, ActivityStrength.Observed, "Pause label removal was observed. Resume is not confirmed."),
        ActivityKind.ResumeConfirmed => (ActivitySource.Attention, ActivityStrength.Confirmed, "Resume or Retry was confirmed."),
        ActivityKind.RecoveryStarted => (ActivitySource.Recovery, ActivityStrength.Observed, "Runtime recovery started."),
        ActivityKind.RecoveryCompleted => (ActivitySource.Recovery, ActivityStrength.Confirmed, "Runtime recovery completed."),
        ActivityKind.RecoveryFailed => (ActivitySource.Recovery, ActivityStrength.Observed, "Runtime recovery failed."),
        ActivityKind.HealthChanged => (ActivitySource.Health, ActivityStrength.Observed, "Runtime health changed."),
        ActivityKind.WaitStarted => (ActivitySource.Pool, ActivityStrength.Observed, "Waiting was observed."),
        ActivityKind.WaitChanged => (ActivitySource.Pool, ActivityStrength.Observed, "Waiting changed."),
        ActivityKind.WaitEnded => (ActivitySource.Pool, ActivityStrength.Observed, "Waiting ended in this observation interval."),
        ActivityKind.CleanupFailed => (ActivitySource.Recovery, ActivityStrength.Observed, "Environment cleanup failed. Merge evidence is separate."),
        ActivityKind.EvidenceUnavailable => (ActivitySource.Journal, ActivityStrength.Unknown, "Activity evidence is unavailable."),
        ActivityKind.RecordsLost => (ActivitySource.Journal, ActivityStrength.Observed, "Activity records were lost."),
        ActivityKind.RuntimeReplaced => (ActivitySource.Journal, ActivityStrength.Observed, "The runtime changed. Earlier activity can be missing."),
        ActivityKind.HistoryGap => (ActivitySource.Journal, ActivityStrength.Observed, "Activity history has a gap."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal sealed record ActivityRow(ActivityPosition Anchor, ActivityRecord Evidence);
internal sealed record ActivityReceipt(ActivityPosition Position, string Hash);
internal sealed record ActivityReplayResult(IReadOnlyList<ActivityRow> Rows, IReadOnlyList<ActivityReceipt> Receipts,
    ActivityPosition? Cursor, int Evicted, int Suppressed)
{
    internal static ActivityReplayResult Empty => new([], [], null, 0, 0);
}

// Pure bounded presentation fold. The writer assigns positions before this is called.
internal static class ActivityReplay
{
    internal static ActivityReplayResult Apply(ActivityReplayResult retained, IReadOnlyList<ActivityRecord> batch)
    {
        if (retained.Rows.Count > ActivityLimits.ClientRecords || retained.Receipts.Count > ActivityLimits.ClientRecords
            || batch.Count > ActivityLimits.PageRecords
            || batch.Sum(r => ActivityProjector.Serialize(r).Length) > ActivityLimits.BatchBytes)
            throw new InvalidDataException("Activity replay exceeds its batch limit.");
        var rows = retained.Rows.ToList();
        var receipts = retained.Receipts.ToList();
        var cursor = retained.Cursor;
        var suppressed = 0;
        var evicted = 0;
        foreach (var record in batch)
        {
            var last = cursor;
            if (last is { } previous && record.Position.Journal != previous.Journal)
                throw new InvalidDataException("A changed journal requires an explicit history reset.");
            var hash = Convert.ToHexString(SHA256.HashData(ActivityProjector.Serialize(record)));
            var same = receipts.FindIndex(r => r.Position.Runtime == record.Position.Runtime
                && r.Position.Sequence == record.Position.Sequence);
            if (same >= 0)
            {
                if (receipts[same].Hash != hash)
                    throw new InvalidDataException("Activity identity has conflicting records.");
                suppressed++; continue;
            }
            // Offset is journal order, independent of source or observation wall clocks.
            if (last is { } tail && record.Position.Offset <= tail.Offset)
                throw new InvalidDataException("Activity replay is out of journal order.");
            cursor = record.Position;
            receipts.Add(new(record.Position, hash));
            if (receipts.Count > ActivityLimits.ClientRecords) receipts.RemoveAt(0);
            same = rows.FindIndex(r => r.Evidence.Position.Runtime == record.Position.Runtime
                && r.Evidence.Cause == record.Cause && r.Evidence.Bead == record.Bead
                && r.Evidence.Phase == record.Phase && r.Evidence.Invocation == record.Invocation
                && (r.Evidence.Kind == record.Kind || r.Evidence.Kind == ActivityKind.StateObserved
                    || record.Kind == ActivityKind.StateObserved));
            if (same >= 0)
            {
                var prior = rows[same].Evidence;
                if (prior.Kind == record.Kind && (prior.Reason != record.Reason || prior.Count != record.Count
                    || prior.Limit != record.Limit || prior.Merge != record.Merge))
                    throw new InvalidDataException("Activity cause has conflicting evidence.");
                if (record.Strength > rows[same].Evidence.Strength)
                    rows[same] = rows[same] with { Evidence = record };
                suppressed++;
            }
            else rows.Add(new(record.Position, record));
            if (rows.Count > ActivityLimits.ClientRecords) { rows.RemoveAt(0); evicted++; }
        }
        return new(rows, receipts, cursor, evicted, suppressed);
    }
}
