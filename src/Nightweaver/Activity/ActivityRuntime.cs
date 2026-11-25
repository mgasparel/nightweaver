using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.ActivityFeed;

// Runtime-local display evidence. No method reads or writes the tracker.
internal sealed class ActivityRuntime : IDisposable
{
    private static readonly AsyncLocal<ActivityRuntime?> active = new();
    internal static ActivityRuntime? Current => active.Value;
    private readonly ActivityRuntime? previous;
    private readonly Action<ActivityEvidence, ActivityMergeProof?> submit;
    private readonly TimeProvider clock;
    private readonly int inventoryLimit;
    private readonly object gate = new();
    private readonly HashSet<string> receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> receiptOrder = new();
    private readonly Dictionary<string, (ActivityReason Pause, string State)> inventory = new(StringComparer.Ordinal);
    private long? lastInventory;
    private bool inventoryFailed;
    private long inventoryVersion;
    internal long InventoryVersion => Interlocked.Read(ref inventoryVersion);

    internal ActivityRuntime(Action<ActivityEvidence, ActivityMergeProof?> submit, TimeProvider? clock = null, int inventoryLimit = 10000)
    {
        if (inventoryLimit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(inventoryLimit));
        this.inventoryLimit = inventoryLimit;
        this.submit = submit;
        this.clock = clock ?? TimeProvider.System;
        previous = active.Value;
        active.Value = this;
    }

    internal static async Task<ActivityJournal?> StartAsync(HostPaths paths, ProjectConfig project,
        ValidatedTargetRepository repository, string runtime, IProcessRunner runner, CancellationToken token)
    {
        try
        {
            var origin = repository.OriginRepository;
            if (origin is null)
            {
                var result = await runner.RunAsync(GitCommand.Create(["config", "--get", "remote.origin.url"], repository.Path), token);
                origin = result.ExitCode == 0 ? GitHubRepositoryIdentity.ParseRemoteUrl(result.StandardOutput.Trim()) : null;
            }
            // Local repositories can still record progress. No hosted merge URL can
            // match this reserved identity when hosted origin evidence is absent.
            origin ??= new("unavailable.invalid", "unavailable", "unavailable");
            return new(new ActivityJournalFiles(paths, project.Name, repository.Path, origin), origin, Guid.Parse(runtime));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private void Emit(ActivityKind kind, ActivitySource source, ActivityPhase phase, string? bead,
        string reference, string? invocation = null, ActivityRole? role = null,
        ActivityReason reason = ActivityReason.None, int? count = null, ActivityMergeProof? proof = null,
        string? occurrence = null)
    {
        if (bead?.Length > 128 || reference.Length > 128 || invocation?.Length > 128) return;
        // Suppression is finite. Eviction ends duplicate coverage, never workflow authority.
        var key = $"{kind}:{bead}:{phase}:{reference}:{invocation}:{occurrence}";
        lock (gate)
        {
            if (!receipts.Add(key)) return;
            receiptOrder.Enqueue(key);
            if (receiptOrder.Count > ActivityLimits.ClientRecords) receipts.Remove(receiptOrder.Dequeue());
        }
        if (kind == ActivityKind.EvidenceUnavailable) source = ActivitySource.Journal;
        var evidence = new ActivityEvidence(kind, source, phase, Guid.NewGuid(), reference)
        {
            Bead = bead, Invocation = invocation, Role = role, Reason = reason, Count = count,
            SourceTime = source == ActivitySource.Inventory ? null : clock.GetUtcNow(),
            Freshness = ActivityFreshness.Fresh,
            Detail = invocation is null ? ActivityDetail.Bead : ActivityDetail.Invocation
        };
        // A failed display sink must not fail a factory operation.
        try { submit(evidence, proof); }
        catch (Exception) { }
    }

    private static (ActivityPhase Phase, ActivityRole Role)? Role(string role) => role switch
    {
        "planner" => (ActivityPhase.Planning, ActivityRole.Planner),
        "plan_challenger" => (ActivityPhase.Planning, ActivityRole.PlanChallenger),
        "implementer" => (ActivityPhase.Delivery, ActivityRole.Implementer),
        "reviewer" => (ActivityPhase.Delivery, ActivityRole.Reviewer),
        "qa" => (ActivityPhase.Qa, ActivityRole.Qa),
        "merger" => (ActivityPhase.Merger, ActivityRole.Merger), _ => null
    };

    internal void Invocation(PreparedAgentInvocation invocation, string bead, ActivityKind kind,
        ActivityReason reason = ActivityReason.None)
    {
        if (Role(invocation.Purpose) is not { } role) return;
        Emit(kind, ActivitySource.Invocation, role.Phase, bead, invocation.InvocationId,
            invocation.InvocationId, role.Role, reason);
    }

    internal void PreparationFailed(AgentStageSession session)
    {
        if (Role(AgentRoles.ToConfigKey(session.Role)) is not { } role) return;
        Emit(ActivityKind.EvidenceUnavailable, ActivitySource.Journal, role.Phase, session.BeadId,
            "preparation", role: role.Role, reason: ActivityReason.PreparationFailed,
            occurrence: Guid.NewGuid().ToString("N"));
    }

    internal void Admitted(AgentStageSession session)
    {
        if (session.Role != AgentRole.Qa) return;
        Emit(ActivityKind.QaAdmitted, ActivitySource.Admission, ActivityPhase.Qa, session.BeadId,
            "qa-admission", role: ActivityRole.Qa, occurrence: Guid.NewGuid().ToString("N"));
    }

    internal void Published(AgentStageResult result)
    {
        if (result.PublicationReceipt is not { } receipt || Role(AgentRoles.ToConfigKey(receipt.Role)) is not { } role) return;
        var kind = receipt.PublishedState.Planned ? ActivityKind.PlanningPublished
            : receipt.Outcome == AgentOutcomeChoice.ChangesRequired ? ActivityKind.ChangesRequired
            : receipt.Role == AgentRole.PlanChallenger && receipt.Outcome == AgentOutcomeChoice.Approved ? ActivityKind.PlanningApproved
            : receipt.PublishedState.ProductDecisionRequired ? ActivityKind.PauseObserved
            : receipt.Role is AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa ? ActivityKind.DeliveryApproved
            : ActivityKind.StateObserved;
        var source = kind is ActivityKind.StateObserved or ActivityKind.PauseObserved ? ActivitySource.Inventory : ActivitySource.Publication;
        // DeliveryApproved uses the Delivery phase even when QA publishes its result.
        var phase = kind == ActivityKind.DeliveryApproved ? ActivityPhase.Delivery : role.Phase;
        Emit(kind, source, phase, result.BeadId, result.InvocationId, result.InvocationId, role.Role,
            receipt.PublishedState.ProductDecisionRequired ? ActivityReason.ProductDecision : ActivityReason.None,
            receipt.PublishedState.RepairCycles,
            occurrence: result.PlanningApproval?.PlanRevision);
        lock (gate)
        {
            Interlocked.Increment(ref inventoryVersion);
            if (inventory.Count < inventoryLimit || inventory.ContainsKey(result.BeadId))
                inventory[result.BeadId] = (receipt.PublishedState.ProductDecisionRequired ? ActivityReason.ProductDecision : ActivityReason.None,
                    State(receipt.PublishedState));
        }
    }

    internal void Retry(AgentStageSession session, AgentStageResult attempt, long number)
    {
        if (Role(AgentRoles.ToConfigKey(session.Role)) is not { } role) return;
        Emit(ActivityKind.OperationalRetryStarted, ActivitySource.Recovery, role.Phase, session.BeadId,
            attempt.InvocationId, attempt.InvocationId, role.Role, ActivityReason.OperationalRetry, number <= 1000000000 ? (int)number : null);
    }

    internal void MergeVerified(AgentStageSession session, AgentStageResult invocation, MergerVerifiedMergeEvidence merge)
    {
        // Called only after the independent live verifier accepts checks and reachability.
        var reference = "verified-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(invocation.InvocationId)));
        var facts = new MergerFacts(session.BeadId, merge.PullRequest, merge.CheckedHead, merge.MergeCommit,
            merge.SourceCommentId, reference, true);
        Emit(ActivityKind.MergeVerified, ActivitySource.MergeVerifier, ActivityPhase.Merger,
            session.BeadId, reference, invocation.InvocationId, ActivityRole.Merger, proof: new(facts, facts));
    }

    internal void Closed(AgentStageSession session, AgentStageResult invocation, BeadWorkflowState state)
    {
        Emit(ActivityKind.BeadClosed, ActivitySource.Publication, ActivityPhase.Merger,
            session.BeadId, invocation.InvocationId, invocation.InvocationId, ActivityRole.Merger);
        lock (gate)
        {
            Interlocked.Increment(ref inventoryVersion);
            if (inventory.Count < inventoryLimit || inventory.ContainsKey(session.BeadId))
                inventory[session.BeadId] = (ActivityReason.None, State(state));
        }
    }

    internal void CleanupFailed(AgentStageSession session, AgentStageResult invocation) =>
        Emit(ActivityKind.CleanupFailed, ActivitySource.Recovery, ActivityPhase.Merger,
            session.BeadId, invocation.InvocationId, invocation.InvocationId, ActivityRole.Merger, ActivityReason.CleanupFailure);

    private static string State(BeadSnapshot bead)
    {
        var observation = BeadWorkflowObservation.Read(bead);
        return observation.State is { } state ? State(state)
            : $"{Status(bead.Status)}:invalid:{bead.Labels.Contains("needs-human")}:{bead.Labels.Contains("product-decision-required")}";
    }
    private static string State(BeadWorkflowState state) =>
        $"{Status(state.Status)}:{state.Stage}:{state.Planned}:{state.NeedsHuman}:{state.ProductDecisionRequired}:{state.RepairCycles}";
    private static string Status(string status) => status is "open" or "in_progress" or "closed" ? status : "unknown";

    internal void Paused(NeedsHumanRequest request, BeadSnapshot bead)
    {
        var reason = request.Cause switch
        {
            NeedsHumanCause.OperationalRetriesExhausted => ActivityReason.OperationalRetriesExhausted,
            NeedsHumanCause.DuplicateFailure => ActivityReason.DuplicateFailure,
            NeedsHumanCause.RepairBudgetExhausted => ActivityReason.RepairBudgetExhausted,
            NeedsHumanCause.EnvironmentBlocked => ActivityReason.EnvironmentBlocked,
            NeedsHumanCause.DeliveryOutcomeFailure => ActivityReason.OutcomeFailed,
            NeedsHumanCause.PlanningContextTooLarge => ActivityReason.PlanningContextTooLarge,
            _ => ActivityReason.MissingEvidence
        };
        lock (gate)
        {
            Interlocked.Increment(ref inventoryVersion);
            Emit(ActivityKind.PauseObserved, ActivitySource.Inventory, ActivityPhase.Runtime, request.BeadId,
                request.InvocationId ?? "pause-observation", request.InvocationId, reason: reason,
                occurrence: request.InvocationId is null ? State(bead) : null);
            if (inventory.Count < inventoryLimit || inventory.ContainsKey(bead.Id))
                inventory[bead.Id] = (ActivityReason.NeedsHuman, State(bead));
        }
    }

    internal void CleanupFailed(string bead, string source, int? attempts = null) =>
        Emit(ActivityKind.CleanupFailed, ActivitySource.Recovery, ActivityPhase.Runtime, bead,
            source, reason: ActivityReason.CleanupFailure, count: attempts,
            occurrence: attempts?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal void UnknownInventory()
    {
        lock (gate)
        {
            if (!inventoryFailed)
                Emit(ActivityKind.EvidenceUnavailable, ActivitySource.Journal, ActivityPhase.Runtime, null,
                    "inventory", reason: ActivityReason.SourceFailed, occurrence: Guid.NewGuid().ToString("N"));
            inventory.Clear(); lastInventory = null; inventoryFailed = true;
        }
    }

    internal void Observe(IEnumerable<BeadSnapshot> beads, long? expectedVersion = null)
    {
        lock (gate)
        {
            if (expectedVersion is { } expected && expected != inventoryVersion)
            {
                Emit(ActivityKind.EvidenceUnavailable, ActivitySource.Journal, ActivityPhase.Runtime, null,
                    "inventory-overlap", reason: ActivityReason.StaleSource,
                    occurrence: expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (lastInventory is { } stamp && clock.GetElapsedTime(stamp) > ActivityLimits.FreshFor)
            {
                Emit(ActivityKind.EvidenceUnavailable, ActivitySource.Journal, ActivityPhase.Runtime, null,
                    "inventory", reason: ActivityReason.StaleSource, occurrence: Guid.NewGuid().ToString("N"));
                inventory.Clear();
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var capacityLost = false;
            foreach (var bead in beads)
            {
                if (bead.IssueType is not ("task" or "feature" or "bug") || !ActivityProjector.SafeIdentifier(bead.Id)) continue;
                var human = bead.Labels.Contains("needs-human");
                var product = bead.Labels.Contains("product-decision-required");
                var pause = human && product ? ActivityReason.CombinedPause : human ? ActivityReason.NeedsHuman
                    : product ? ActivityReason.ProductDecision : ActivityReason.None;
                var state = State(bead);
                var retained = inventory.TryGetValue(bead.Id, out var old);
                if (!retained && inventory.Count >= inventoryLimit) { capacityLost = true; continue; }
                seen.Add(bead.Id);
                if (retained && old.State == state) continue;
                // Closing with retained pause labels (Abort) is not pause removal.
                var kind = pause != ActivityReason.None && (!retained || old.Pause != pause) ? ActivityKind.PauseObserved
                    : retained && old.Pause != ActivityReason.None && pause == ActivityReason.None ? ActivityKind.PauseRemovalObserved
                    : ActivityKind.StateObserved;
                Emit(kind, ActivitySource.Inventory, ActivityPhase.Runtime, bead.Id, "inventory",
                    reason: kind == ActivityKind.PauseRemovalObserved ? ActivityReason.MissingEvidence : pause switch
                    {
                        ActivityReason.NeedsHuman => ActivityReason.NeedsHumanReasonUnavailable,
                        ActivityReason.ProductDecision => ActivityReason.ProductDecisionReasonUnavailable,
                        ActivityReason.CombinedPause => ActivityReason.CombinedPauseReasonUnavailable,
                        _ => pause
                    },
                    occurrence: Guid.NewGuid().ToString("N"));
                inventory[bead.Id] = (pause, state);
            }
            foreach (var id in inventory.Keys.Where(id => !seen.Contains(id)).ToArray()) inventory.Remove(id);
            if (capacityLost)
                Emit(ActivityKind.EvidenceUnavailable, ActivitySource.Journal, ActivityPhase.Runtime, null,
                    "inventory-capacity", reason: ActivityReason.Capacity, count: inventoryLimit);
            lastInventory = clock.GetTimestamp();
            inventoryFailed = false;
        }
    }

    public void Dispose() => active.Value = previous;
}
