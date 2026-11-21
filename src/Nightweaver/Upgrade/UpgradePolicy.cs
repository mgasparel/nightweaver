namespace Nightweaver.Upgrade;

// A contract worksheet, not a persisted journal or permission to perform IO.
// Consumers must establish each fact under the locks described in upgrade-v1.md.
internal enum UpgradePhase { Inspected, Prepared, Draining, Settled, Replacing, Installed, Verifying, Accepted, RollingBack, RolledBack }
internal enum UpgradeStep { Preflight, BeginDrain, ConfirmSettlement, BeginReplacement, CommitPayload, BeginVerification, Verify, Accept, BeginRollback, CommitRollback }
internal enum UpgradeRefusal
{
    None, InvalidTransition, IdentityUnknown, CompatibilityUnsupported, InterlockUnavailable,
    EvidenceUncertain, NativeSettlementPending, PayloadMismatch, IntentChanged,
    AcceptanceUnproven, RollbackIncompatible
}

internal sealed record UpgradeFacts
{
    internal bool IdentityAndRootInventoryMatch { get; init; }
    internal bool CompatibilityProven { get; init; }
    internal bool JournalAndLocksAvailable { get; init; }
    internal bool EvidenceReconciled { get; init; }
    internal bool SuppressionConfirmed { get; init; }
    internal bool AllOriginalOwnersAndJobsSettled { get; init; }
    // True only after every candidate owner and its native descendants have
    // settled, or reconciled evidence proves no candidate was launched.
    internal bool AllCandidateOwnersAndJobsSettled { get; init; }
    internal bool OldPayloadRetained { get; init; }
    internal bool NewPayloadMatches { get; init; }
    internal bool OldPayloadMatches { get; init; }
    internal bool RollbackStateReadable { get; init; }
    internal bool CurrentIntentMatches { get; init; }
    internal bool ExplicitStartAuthorized { get; init; }
    internal bool DesiredRunning { get; init; }
    internal bool Enabled { get; init; }
    internal bool MaintenanceActive { get; init; }
    internal bool FreshRunningAcceptance { get; init; }
    internal bool FreshStoppedAcceptance { get; init; }
}

internal sealed record UpgradeDecision(UpgradePhase Phase, UpgradeRefusal Refusal)
{
    internal bool Allowed => Refusal == UpgradeRefusal.None;
}

internal static class UpgradePolicy
{
    internal static UpgradeDecision Evaluate(UpgradePhase phase, UpgradeStep step, UpgradeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var next = (phase, step) switch
        {
            (UpgradePhase.Inspected, UpgradeStep.Preflight) => UpgradePhase.Prepared,
            (UpgradePhase.Prepared, UpgradeStep.BeginDrain) => UpgradePhase.Draining,
            (UpgradePhase.Draining, UpgradeStep.ConfirmSettlement) => UpgradePhase.Settled,
            (UpgradePhase.Settled, UpgradeStep.BeginReplacement) => UpgradePhase.Replacing,
            (UpgradePhase.Replacing, UpgradeStep.CommitPayload) => UpgradePhase.Installed,
            (UpgradePhase.Installed, UpgradeStep.BeginVerification) => UpgradePhase.Verifying,
            (UpgradePhase.Verifying, UpgradeStep.Verify) => UpgradePhase.Verifying,
            (UpgradePhase.Verifying, UpgradeStep.Accept) => UpgradePhase.Accepted,
            (UpgradePhase.Replacing or UpgradePhase.Installed or UpgradePhase.Verifying, UpgradeStep.BeginRollback) => UpgradePhase.RollingBack,
            (UpgradePhase.RollingBack, UpgradeStep.CommitRollback) => UpgradePhase.RolledBack,
            _ => (UpgradePhase?)null
        };
        UpgradeDecision Refuse(UpgradeRefusal reason) => new(phase, reason);
        if (next is null) return Refuse(UpgradeRefusal.InvalidTransition);
        if (!facts.IdentityAndRootInventoryMatch) return Refuse(UpgradeRefusal.IdentityUnknown);
        if (!facts.EvidenceReconciled) return Refuse(UpgradeRefusal.EvidenceUncertain);
        if (!facts.CompatibilityProven) return Refuse(UpgradeRefusal.CompatibilityUnsupported);
        if (!facts.JournalAndLocksAvailable) return Refuse(UpgradeRefusal.InterlockUnavailable);
        if (step != UpgradeStep.Preflight && !facts.SuppressionConfirmed)
            return Refuse(UpgradeRefusal.InterlockUnavailable);
        if (step is not (UpgradeStep.Preflight or UpgradeStep.BeginDrain)
            && !facts.AllOriginalOwnersAndJobsSettled)
            return Refuse(UpgradeRefusal.NativeSettlementPending);
        if (step is UpgradeStep.BeginReplacement or UpgradeStep.CommitPayload
            or UpgradeStep.BeginVerification or UpgradeStep.Verify or UpgradeStep.Accept
            or UpgradeStep.BeginRollback or UpgradeStep.CommitRollback)
        {
            if (!facts.OldPayloadRetained) return Refuse(UpgradeRefusal.PayloadMismatch);
        }
        if (step is UpgradeStep.CommitPayload or UpgradeStep.BeginVerification or UpgradeStep.Verify or UpgradeStep.Accept
            && !facts.NewPayloadMatches) return Refuse(UpgradeRefusal.PayloadMismatch);
        if (step is UpgradeStep.BeginRollback or UpgradeStep.CommitRollback)
        {
            if (!facts.AllCandidateOwnersAndJobsSettled)
                return Refuse(UpgradeRefusal.NativeSettlementPending);
            if (!facts.RollbackStateReadable) return Refuse(UpgradeRefusal.RollbackIncompatible);
            if (step == UpgradeStep.CommitRollback && !facts.OldPayloadMatches)
                return Refuse(UpgradeRefusal.PayloadMismatch);
        }
        if (step is UpgradeStep.BeginVerification or UpgradeStep.Verify or UpgradeStep.Accept)
        {
            if (!facts.CurrentIntentMatches) return Refuse(UpgradeRefusal.IntentChanged);
            // Disabled/stopped/maintenance intent is a successful stopped installation,
            // never an implicit request to launch or clear suppression.
            var running = facts.Enabled && facts.DesiredRunning && !facts.MaintenanceActive;
            if (running && !facts.ExplicitStartAuthorized) return Refuse(UpgradeRefusal.IntentChanged);
            if (step is UpgradeStep.Verify or UpgradeStep.Accept
                && !(running ? facts.FreshRunningAcceptance : facts.FreshStoppedAcceptance))
                return Refuse(UpgradeRefusal.AcceptanceUnproven);
        }
        return new(next.Value, UpgradeRefusal.None);
    }
}
