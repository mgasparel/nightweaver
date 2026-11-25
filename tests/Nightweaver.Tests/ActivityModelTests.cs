using System.Text;
using System.Text.Json;
using Nightweaver.ActivityFeed;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class ActivityModelTests
{
    private static readonly GitHubRepositoryIdentity Repository = new("github.com", "owner", "repo");
    private static readonly Guid Journal = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Runtime = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Cause = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T00:00:00Z");
    private static ActivityPosition Position(long offset = 1) => new(Journal, offset, Runtime, offset);
    private static ActivityEvidence Evidence(ActivityKind kind) => new(kind, ActivityProjector.Rule(kind).Source,
        kind is ActivityKind.PlanningApproved or ActivityKind.PlanningPublished ? ActivityPhase.Planning
            : kind == ActivityKind.QaAdmitted ? ActivityPhase.Qa : kind == ActivityKind.MergeVerified ? ActivityPhase.Merger : ActivityPhase.Delivery,
        Cause, "receipt-1")
    {
        Bead = "ns-example", Invocation = "20261125T000000Z-implementer", SourceTime = Now.AddMinutes(-1),
        Freshness = ActivityFreshness.Fresh
    };
    private static MergerFacts Facts => new("ns-example", "https://github.com/owner/repo/pull/42",
        new('a', 40), new('b', 40), "report-1", "receipt-1", true);
    private static ActivityRecord Project(ActivityKind kind, long offset = 1) => Assert.IsType<ActivityRecord>(
        ActivityProjector.Project(Position(offset), Now, Evidence(kind), Repository,
            kind == ActivityKind.MergeVerified ? new(Facts, Facts) : null));

    public static TheoryData<string> Kinds => new(Enum.GetNames<ActivityKind>());

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryEventHasFixedAttributableBoundedSchema(string name)
    {
        var kind = Enum.Parse<ActivityKind>(name);
        var record = Project(kind);
        var rule = ActivityProjector.Rule(kind);
        Assert.Equal(rule.Source, record.Source);
        Assert.Equal(rule.Strength, record.Strength);
        Assert.Equal(rule.Message, record.Message);
        var bytes = ActivityProjector.Serialize(record);
        Assert.Equal(record.Kind, ActivityProjector.Deserialize(bytes, Repository).Kind);
        Assert.InRange(bytes.Length, 1, ActivityLimits.RecordBytes);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(1, json.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(kind.ToString(), json.RootElement.GetProperty("kind").GetString());
        Assert.Equal("receipt-1", json.RootElement.GetProperty("sourceReference").GetString());
        Assert.Equal(Runtime, json.RootElement.GetProperty("position").GetProperty("runtime").GetGuid());
    }

    [Fact]
    public void StartCompletionApprovalQaMergeAndClosureAreSeparateFacts()
    {
        var kinds = new[] { ActivityKind.InvocationStarted, ActivityKind.InvocationCompleted, ActivityKind.PlanningApproved,
            ActivityKind.DeliveryApproved, ActivityKind.QaAdmitted, ActivityKind.MergeVerified, ActivityKind.BeadClosed };
        var records = kinds.Select(k => Project(k)).ToArray();
        Assert.Equal(kinds.Length, records.Select(r => r.Message).Distinct().Count());
        Assert.Single(records, r => r.Strength == ActivityStrength.Verified);
        Assert.Null(Project(ActivityKind.BeadClosed).Merge);
        foreach (var source in Enum.GetValues<ActivitySource>().Where(s => s != ActivitySource.MergeVerifier))
            Assert.Null(ActivityProjector.Project(Position(), Now, Evidence(ActivityKind.MergeVerified) with { Source = source }, Repository, new(Facts, Facts)));
        Assert.Contains("Resume is not confirmed", Project(ActivityKind.PauseRemovalObserved).Message);
        Assert.Contains("Merge evidence is separate", Project(ActivityKind.CleanupFailed).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAndConflictingMergeEvidenceStayUnknown(bool conflict)
    {
        var record = ActivityProjector.Project(Position(), Now, Evidence(ActivityKind.MergeVerified), Repository,
            conflict ? new(Facts, Facts with { MergeCommit = new('c', 40) }) : null)!;
        Assert.Equal(ActivityKind.EvidenceUnavailable, record.Kind);
        Assert.Equal(ActivityStrength.Unknown, record.Strength);
        Assert.Null(record.Merge);
        Assert.Equal(conflict ? ActivityReason.ConflictingEvidence : ActivityReason.MissingEvidence, record.Reason);
        Assert.Equal(record.Reason, ActivityProjector.Deserialize(ActivityProjector.Serialize(record), Repository).Reason);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo/pull/42")]
    [InlineData("https://secret@github.com/owner/repo/pull/42")]
    [InlineData("https://github.com/owner/repo/pull/42?token=SECRET_SENTINEL")]
    [InlineData("https://github.com/owner/repo/pull/42#SECRET_SENTINEL")]
    [InlineData("https://github.com/other/repo/pull/42")]
    [InlineData("https://evil.example/owner/repo/pull/42")]
    [InlineData("https://github.com/owner/repo/pull/%34%32")]
    [InlineData("https://github.com/owner/repo/pull/042")]
    [InlineData("https://github.com:443/owner/repo/pull/42")]
    [InlineData("https://github.com/owner/secret/../repo/pull/42")]
    [InlineData("https://github.com/owner/repo/pull/42\\SECRET_SENTINEL")]
    public void UnsafeLinksCannotEnterRecords(string link)
    {
        var facts = Facts with { PullRequest = link };
        var record = ActivityProjector.Project(Position(), Now, Evidence(ActivityKind.MergeVerified), Repository, new(facts, facts))!;
        Assert.Equal(ActivityStrength.Unknown, record.Strength);
        Assert.Null(record.Merge);
        Assert.DoesNotContain("SECRET_SENTINEL", Encoding.UTF8.GetString(ActivityProjector.Serialize(record)));
    }

    [Fact]
    public void MalformedOrForeignMergeProofCannotVerify()
    {
        MergerFacts[] invalid = [Facts with { Bead = "other" }, Facts with { RequiredChecksPassed = false },
            Facts with { CheckedHead = "stage:merge" }, Facts with { MergeCommit = "process-exit-0" },
            Facts with { ReportReference = "receipt-1" }, Facts with { VerificationReference = "other" },
            Facts with { ReportReference = "SECRET_SENTINEL\nprivate" }];
        foreach (var facts in invalid)
        {
            var record = ActivityProjector.Project(Position(), Now, Evidence(ActivityKind.MergeVerified), Repository, new(facts, facts))!;
            Assert.Equal(ActivityStrength.Unknown, record.Strength);
            Assert.Null(record.Merge);
            Assert.DoesNotContain("SECRET_SENTINEL", Encoding.UTF8.GetString(ActivityProjector.Serialize(record)));
        }
    }

    [Theory]
    [InlineData("Stale")]
    [InlineData("Unknown")]
    [InlineData("Unavailable")]
    public void StaleOrUnavailableSourcesCannotConfirmOutcomes(string name)
    {
        var freshness = Enum.Parse<ActivityFreshness>(name);
        foreach (var kind in new[] { ActivityKind.MergeVerified, ActivityKind.DeliveryApproved, ActivityKind.ResumeConfirmed, ActivityKind.BeadClosed })
        {
            var record = ActivityProjector.Project(Position(), Now, Evidence(kind) with { Freshness = freshness }, Repository, new(Facts, Facts))!;
            Assert.Equal(ActivityStrength.Unknown, record.Strength);
            Assert.Equal(ActivityReason.StaleSource, record.Reason);
        }
    }

    [Theory]
    [InlineData(0, "Fresh")]
    [InlineData(60, "Fresh")]
    [InlineData(61, "Stale")]
    [InlineData(-1, "Unknown")]
    public void FreshnessUsesMonotonicAgeAndDoesNotUseConnection(int seconds, string name)
    {
        var expected = Enum.Parse<ActivityFreshness>(name);
        Assert.Equal(expected, ActivityProjector.Freshness(TimeSpan.FromSeconds(seconds), false));
        Assert.Equal(ActivityFreshness.Unavailable, ActivityProjector.Freshness(TimeSpan.Zero, true));
        Assert.Equal(ActivityFreshness.Unknown, ActivityProjector.Freshness(null, false));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void IdentifierBoundsRejectRatherThanTruncate(int length, bool accepted)
    {
        var input = Evidence(ActivityKind.StateObserved) with { Bead = new string('a', length) };
        Assert.Equal(accepted, ActivityProjector.Project(Position(), Now, input, Repository) is not null);
    }

    [Theory]
    [InlineData("ns-\ud83d\ude00")]
    [InlineData("ns-\u202eprivate")]
    [InlineData("ns-e\u0301")]
    [InlineData("ns-\ud800")]
    [InlineData("ns-SECRET_SENTINEL\nprivate")]
    [InlineData("../SECRET_SENTINEL")]
    [InlineData("<script>SECRET_SENTINEL</script>")]
    public void UnicodeControlsPathsAndFreeTextAreNotIdentifiers(string value)
    {
        var input = Evidence(ActivityKind.StateObserved);
        Assert.Null(ActivityProjector.Project(Position(), Now, input with { Bead = value }, Repository));
        Assert.Null(ActivityProjector.Project(Position(), Now, input with { SourceReference = value }, Repository));
        Assert.Null(ActivityProjector.Project(Position(), Now, input with { Invocation = value }, Repository));
    }

    [Fact]
    public void MissingAndInvalidFieldsNeverDefaultToSuccess()
    {
        var input = Evidence(ActivityKind.InvocationStarted);
        ActivityEvidence[] invalid = [input with { Invocation = null }, input with { Bead = null },
            input with { Kind = (ActivityKind)999 }, input with { Phase = (ActivityPhase)999 },
            input with { Reason = (ActivityReason)999 }, input with { Source = (ActivitySource)999 },
            input with { Freshness = (ActivityFreshness)999 }, input with { Detail = (ActivityDetail)999 },
            input with { Role = (ActivityRole)999 },
            input with { Count = -1 }, input with { Limit = 1000000001 }, input with { Cause = Guid.Empty },
            input with { Merge = Facts }];
        foreach (var bad in invalid) Assert.Null(ActivityProjector.Project(Position(), Now, bad, Repository));
        Assert.Null(ActivityProjector.Project(Position() with { Sequence = 0 }, Now, input, Repository));
        Assert.Null(ActivityProjector.Project(Position() with { Runtime = Guid.Empty }, Now, input, Repository));
        Assert.Null(ActivityProjector.Project(Position(), Now, Evidence(ActivityKind.QaAdmitted) with { Phase = ActivityPhase.Delivery }, Repository));
    }

    [Fact]
    public void UnsupportedDetailsHaveOnlyAFixedCodeAndTypedReference()
    {
        var record = ActivityProjector.Project(Position(), Now, Evidence(ActivityKind.HealthChanged) with
        { Reason = ActivityReason.Unsupported, Detail = ActivityDetail.Health, Count = 1, Limit = 3 }, Repository)!;
        var json = Encoding.UTF8.GetString(ActivityProjector.Serialize(record));
        Assert.Contains("Unsupported", json);
        Assert.Contains("Health", json);
        foreach (var field in new[] { "prompt", "exception", "environment", "commandLine", "configuration", "transcript", "guidance" })
            Assert.DoesNotContain(field, json);
    }

    [Fact]
    public void DuplicateLateAndClockChangedInputsKeepJournalOrderAndAnchor()
    {
        var initial = ActivityProjector.Project(Position(), Now, Evidence(ActivityKind.StateObserved) with
        { ObservedPublication = ActivityKind.DeliveryApproved }, Repository)!;
        var confirmed = ActivityProjector.Project(Position(2), Now.AddHours(-2), Evidence(ActivityKind.DeliveryApproved) with
        { SourceTime = Now.AddDays(-2) }, Repository)!;
        var late = ActivityProjector.Project(Position(3), Now.AddHours(-3), Evidence(ActivityKind.InvocationStarted) with
        { SourceTime = null, Cause = Guid.NewGuid() }, Repository)!;
        var state = ActivityReplay.Apply(ActivityReplayResult.Empty, [initial, initial, confirmed, late]);
        Assert.Equal(2, state.Rows.Count);
        Assert.Equal(initial.Position, state.Rows[0].Anchor);
        Assert.Same(confirmed, state.Rows[0].Evidence);
        Assert.Same(late, state.Rows[1].Evidence);
        Assert.Equal(3, state.Cursor!.Value.Offset);
        Assert.Null(late.SourceTime);
        var replay = ActivityReplay.Apply(state, [initial, confirmed, late]);
        Assert.Equal(state.Rows, replay.Rows);
        Assert.Equal(3, replay.Suppressed);
        Assert.Throws<InvalidDataException>(() => ActivityReplay.Apply(state, [Project(ActivityKind.HealthChanged)]));
    }

    [Fact]
    public void RuntimeReplacementDoesNotInventPriorTransitions()
    {
        var old = Project(ActivityKind.InvocationCompleted);
        var next = ActivityProjector.Project(Position(2) with { Runtime = Guid.NewGuid(), Sequence = 1 }, Now,
            Evidence(ActivityKind.StateObserved) with { Cause = Guid.NewGuid() }, Repository)!;
        var state = ActivityReplay.Apply(ActivityReplayResult.Empty, [old, next]);
        Assert.Equal(ActivityKind.StateObserved, state.Rows[1].Evidence.Kind);
        Assert.Equal(ActivityStrength.Observed, state.Rows[1].Evidence.Strength);
        Assert.Throws<InvalidDataException>(() => ActivityReplay.Apply(state,
            [ActivityProjector.Project(Position(3) with { Journal = Guid.NewGuid() }, Now, Evidence(ActivityKind.HistoryGap), Repository)!]));
    }

    [Fact]
    public void ReplayAndDedupMemoryStayFiniteAndReportEviction()
    {
        var state = ActivityReplayResult.Empty;
        for (var page = 0; page < 6; page++)
        {
            var batch = Enumerable.Range(page * 100 + 1, 100).Select(i => ActivityProjector.Project(Position(i), Now,
                Evidence(ActivityKind.StateObserved) with { Cause = Guid.NewGuid() }, Repository)!).ToArray();
            state = ActivityReplay.Apply(state, batch);
        }
        Assert.Equal(500, state.Rows.Count);
        Assert.Equal(500, state.Receipts.Count);
        Assert.Equal(100, state.Evicted);
        Assert.Equal(101, state.Rows[0].Anchor.Offset);
        Assert.Throws<InvalidDataException>(() => ActivityReplay.Apply(state, Enumerable.Repeat(Project(ActivityKind.StateObserved), 101).ToArray()));
        Assert.Throws<InvalidDataException>(() => ActivityReplay.Apply(state, [Project(ActivityKind.StateObserved)]));
    }

    [Fact]
    public void MaximumSafeFieldsFitTheUtf8RecordBudgetWithFraming()
    {
        var record = ActivityProjector.Project(Position(long.MaxValue), Now, Evidence(ActivityKind.MergeVerified) with
        {
            Bead = new string('a', 128), Invocation = new string('b', 128), SourceReference = new string('c', 128),
            Count = 1000000000, Limit = 1000000000, Detail = ActivityDetail.Comment
        }, Repository, new(Facts with { Bead = new string('a', 128), VerificationReference = new string('c', 128), ReportReference = new string('d', 128) },
            Facts with { Bead = new string('a', 128), VerificationReference = new string('c', 128), ReportReference = new string('d', 128) }))!;
        Assert.Equal(ActivityStrength.Verified, record.Strength);
        Assert.InRange(ActivityProjector.Serialize(record).Length + 1, 1, ActivityLimits.RecordBytes);
    }

    [Fact]
    public void ConflictingCausalFactsRequireExplicitUnavailableEvidence()
    {
        var first = Project(ActivityKind.MergeVerified);
        var otherFacts = Facts with { MergeCommit = new('c', 40) };
        var conflicting = ActivityProjector.Project(Position(2), Now, Evidence(ActivityKind.MergeVerified), Repository, new(otherFacts, otherFacts))!;
        var state = ActivityReplay.Apply(ActivityReplayResult.Empty, [first]);
        Assert.Throws<InvalidDataException>(() => ActivityReplay.Apply(state, [conflicting]));
        var gap = Project(ActivityKind.HistoryGap, 2);
        Assert.Equal(2, ActivityReplay.Apply(state, [gap]).Rows.Count);
    }

    [Fact]
    public void LaterInventoryCannotDowngradeConfirmationOrLoseCursorProgress()
    {
        var confirmed = Project(ActivityKind.DeliveryApproved);
        var observed = ActivityProjector.Project(Position(2), Now, Evidence(ActivityKind.StateObserved) with
        { ObservedPublication = ActivityKind.DeliveryApproved }, Repository)!;
        var state = ActivityReplay.Apply(ActivityReplayResult.Empty, [confirmed, observed]);
        Assert.Single(state.Rows);
        Assert.Same(confirmed, state.Rows[0].Evidence);
        Assert.Equal(observed.Position, state.Cursor);
        var next = ActivityReplay.Apply(state, [Project(ActivityKind.InvocationStarted, 3)]);
        Assert.Equal(2, next.Rows.Count);
        Assert.Equal(3, next.Cursor!.Value.Offset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InventoryAndDistinctEventsKeepEveryFactWithSharedCause(bool inventoryFirst)
    {
        ActivityKind[] kinds = [ActivityKind.InvocationStarted, ActivityKind.InvocationCompleted,
            ActivityKind.RecoveryStarted, ActivityKind.RecoveryCompleted, ActivityKind.WaitChanged,
            ActivityKind.HealthChanged, ActivityKind.RecordsLost];
        var ordered = inventoryFirst ? kinds.Prepend(ActivityKind.StateObserved) : kinds.Append(ActivityKind.StateObserved);
        var records = ordered.Select((kind, index) => Project(kind, index + 1)).ToArray();
        var state = ActivityReplay.Apply(ActivityReplayResult.Empty, records);
        Assert.Equal(records.Length, state.Rows.Count);
        Assert.Equal(0, state.Suppressed);
        Assert.Equal(records.Select(r => r.Kind), state.Rows.Select(r => r.Evidence.Kind));
        Assert.Equal(records.Select(r => r.Position), state.Rows.Select(r => r.Anchor));
        Assert.Equal(records[^1].Position, state.Cursor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InventorySuppressesOnlyItsExactPublication(bool inventoryFirst)
    {
        var inventory = Evidence(ActivityKind.StateObserved) with { ObservedPublication = ActivityKind.DeliveryApproved };
        ActivityEvidence[] inputs = inventoryFirst
            ? [inventory, Evidence(ActivityKind.DeliveryApproved), Evidence(ActivityKind.ChangesRequired), Evidence(ActivityKind.InvocationCompleted)]
            : [Evidence(ActivityKind.ChangesRequired), Evidence(ActivityKind.DeliveryApproved), inventory, Evidence(ActivityKind.InvocationCompleted)];
        var records = inputs.Select((input, index) => ActivityProjector.Project(Position(index + 1), Now, input, Repository)!).ToArray();
        var state = ActivityReplay.Apply(ActivityReplayResult.Empty, records);
        Assert.Equal(3, state.Rows.Count);
        Assert.Equal(1, state.Suppressed);
        var approval = Assert.Single(state.Rows, row => row.Evidence.Kind == ActivityKind.DeliveryApproved);
        Assert.Equal(inventoryFirst ? records[0].Position : records[1].Position, approval.Anchor);
        Assert.Equal(ActivityStrength.Confirmed, approval.Evidence.Strength);
        Assert.Contains(state.Rows, row => row.Evidence.Kind == ActivityKind.ChangesRequired);
        Assert.Contains(state.Rows, row => row.Evidence.Kind == ActivityKind.InvocationCompleted);
        Assert.Equal(records[^1].Position, state.Cursor);
    }

    [Fact]
    public void UnboundInventoryCannotSuppressAPublication()
    {
        var state = ActivityReplay.Apply(ActivityReplayResult.Empty,
            [Project(ActivityKind.StateObserved), Project(ActivityKind.DeliveryApproved, 2)]);
        Assert.Equal(2, state.Rows.Count);
        Assert.Equal(0, state.Suppressed);
    }

    [Fact]
    public void PublicationBindingRejectsOtherKindsAndInvalidValues()
    {
        foreach (var kind in Enum.GetValues<ActivityKind>().Append((ActivityKind)999))
        {
            var input = Evidence(ActivityKind.StateObserved) with { ObservedPublication = kind };
            var record = ActivityProjector.Project(Position(), Now, input, Repository);
            var allowed = kind is ActivityKind.PlanningApproved or ActivityKind.PlanningPublished
                or ActivityKind.DeliveryApproved or ActivityKind.ChangesRequired or ActivityKind.BeadClosed;
            Assert.Equal(allowed, record is not null);
            if (record is not null)
            {
                Assert.Equal(kind, record.ObservedPublication);
                Assert.Contains($"\"observedPublication\":\"{kind}\"", Encoding.UTF8.GetString(ActivityProjector.Serialize(record)));
            }
        }
        var nonInventory = Evidence(ActivityKind.InvocationCompleted) with { ObservedPublication = ActivityKind.DeliveryApproved };
        Assert.Null(ActivityProjector.Project(Position(), Now, nonInventory, Repository));
    }
}
