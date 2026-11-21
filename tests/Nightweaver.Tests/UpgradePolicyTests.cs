using Nightweaver.Upgrade;

namespace Nightweaver.Tests;

public sealed class UpgradePolicyTests
{
    private static UpgradeFacts Proven => new()
    {
        IdentityAndRootInventoryMatch = true, CompatibilityProven = true,
        JournalAndLocksAvailable = true, EvidenceReconciled = true, SuppressionConfirmed = true,
        AllOriginalOwnersAndJobsSettled = true, OldPayloadRetained = true,
        AllCandidateOwnersAndJobsSettled = true,
        NewPayloadMatches = true, OldPayloadMatches = true, RollbackStateReadable = true,
        CurrentIntentMatches = true, ExplicitStartAuthorized = true,
        Enabled = true, DesiredRunning = true, FreshRunningAcceptance = true, FreshStoppedAcceptance = true
    };

    private static readonly (UpgradePhase Phase, UpgradeStep Step, UpgradePhase Next)[] Transitions =
    [
        (UpgradePhase.Inspected, UpgradeStep.Preflight, UpgradePhase.Prepared),
        (UpgradePhase.Prepared, UpgradeStep.BeginDrain, UpgradePhase.Draining),
        (UpgradePhase.Draining, UpgradeStep.ConfirmSettlement, UpgradePhase.Settled),
        (UpgradePhase.Settled, UpgradeStep.BeginReplacement, UpgradePhase.Replacing),
        (UpgradePhase.Replacing, UpgradeStep.CommitPayload, UpgradePhase.Installed),
        (UpgradePhase.Installed, UpgradeStep.BeginVerification, UpgradePhase.Verifying),
        (UpgradePhase.Verifying, UpgradeStep.Verify, UpgradePhase.Verifying),
        (UpgradePhase.Verifying, UpgradeStep.Accept, UpgradePhase.Accepted),
        (UpgradePhase.Replacing, UpgradeStep.BeginRollback, UpgradePhase.RollingBack),
        (UpgradePhase.Installed, UpgradeStep.BeginRollback, UpgradePhase.RollingBack),
        (UpgradePhase.Verifying, UpgradeStep.BeginRollback, UpgradePhase.RollingBack),
        (UpgradePhase.RollingBack, UpgradeStep.CommitRollback, UpgradePhase.RolledBack)
    ];

    [Fact]
    public void ProvenBoundariesAdvance()
    {
        foreach (var (phase, step, expected) in Transitions)
        {
            var decision = UpgradePolicy.Evaluate(phase, step, Proven);
            Assert.True(decision.Allowed);
            Assert.Equal(expected, decision.Phase);
        }
    }

    [Fact]
    public void EveryOtherPhaseStepPairRefusesWithoutAdvancing()
    {
        var allowed = Transitions.Select(row => (row.Phase, row.Step)).ToHashSet();
        foreach (var phase in Enum.GetValues<UpgradePhase>())
        foreach (var step in Enum.GetValues<UpgradeStep>())
        {
            if (allowed.Contains((phase, step))) continue;
            Refused(phase, step, Proven, UpgradeRefusal.InvalidTransition);
        }
        Refused((UpgradePhase)999, UpgradeStep.Accept, Proven, UpgradeRefusal.InvalidTransition);
    }

    [Fact]
    public void UnknownOrInterruptedEvidenceBlocksEveryBoundary()
    {
        foreach (var row in Transitions)
        {
            var (phase, step, _) = row;
            Refused(phase, step, Proven with { IdentityAndRootInventoryMatch = false }, UpgradeRefusal.IdentityUnknown);
            Refused(phase, step, Proven with { EvidenceReconciled = false }, UpgradeRefusal.EvidenceUncertain);
            Refused(phase, step, Proven with { CompatibilityProven = false }, UpgradeRefusal.CompatibilityUnsupported);
            Refused(phase, step, Proven with { JournalAndLocksAvailable = false }, UpgradeRefusal.InterlockUnavailable);
        }
    }

    [Fact]
    public void LostDrainReplyNeverProvesNativeSettlementOrAllowsRollback()
    {
        Refused(UpgradePhase.Draining, UpgradeStep.ConfirmSettlement,
            Proven with { AllOriginalOwnersAndJobsSettled = false }, UpgradeRefusal.NativeSettlementPending);
        Refused(UpgradePhase.Verifying, UpgradeStep.BeginRollback,
            Proven with { AllOriginalOwnersAndJobsSettled = false }, UpgradeRefusal.NativeSettlementPending);
        Refused(UpgradePhase.Prepared, UpgradeStep.BeginDrain,
            Proven with { SuppressionConfirmed = false }, UpgradeRefusal.InterlockUnavailable);
    }

    [Fact]
    public void RollbackRequiresCandidateSettlementIndependentlyOfOriginalOwners()
    {
        // Original owners remain settled while a candidate is live, draining,
        // or unknown. None of those states establishes candidate settlement.
        var pending = Proven with { AllCandidateOwnersAndJobsSettled = false };
        foreach (var (phase, step, next) in Transitions.Where(row =>
            row.Step is UpgradeStep.BeginRollback or UpgradeStep.CommitRollback))
        {
            Refused(phase, step, pending, UpgradeRefusal.NativeSettlementPending);
            var settled = UpgradePolicy.Evaluate(phase, step,
                pending with { AllCandidateOwnersAndJobsSettled = true });
            Assert.True(settled.Allowed);
            Assert.Equal(next, settled.Phase);
        }
    }

    [Fact]
    public void MatchingLiveCandidateCanVerifyAndAcceptWithoutCandidateSettlement()
    {
        var live = Proven with { AllCandidateOwnersAndJobsSettled = false };
        Assert.True(UpgradePolicy.Evaluate(UpgradePhase.Installed, UpgradeStep.BeginVerification, live).Allowed);
        foreach (var step in new[] { UpgradeStep.Verify, UpgradeStep.Accept })
        {
            var decision = UpgradePolicy.Evaluate(UpgradePhase.Verifying, step, live);
            Assert.True(decision.Allowed);
            Assert.Equal(step == UpgradeStep.Accept ? UpgradePhase.Accepted : UpgradePhase.Verifying, decision.Phase);
            Refused(UpgradePhase.Verifying, step,
                live with { FreshRunningAcceptance = false }, UpgradeRefusal.AcceptanceUnproven);
        }
    }

    [Fact]
    public void PayloadCommitAndRollbackRequireExactBytesAndForwardStateReadability()
    {
        Refused(UpgradePhase.Settled, UpgradeStep.BeginReplacement,
            Proven with { OldPayloadRetained = false }, UpgradeRefusal.PayloadMismatch);
        Refused(UpgradePhase.Replacing, UpgradeStep.CommitPayload,
            Proven with { NewPayloadMatches = false }, UpgradeRefusal.PayloadMismatch);
        Refused(UpgradePhase.Installed, UpgradeStep.BeginRollback,
            Proven with { RollbackStateReadable = false }, UpgradeRefusal.RollbackIncompatible);
        Refused(UpgradePhase.RollingBack, UpgradeStep.CommitRollback,
            Proven with { OldPayloadMatches = false }, UpgradeRefusal.PayloadMismatch);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void StoppedDisabledAndMaintenanceIntentRequireStoppedAcceptance(bool enabled, bool desired, bool maintenance)
    {
        var facts = Proven with { Enabled = enabled, DesiredRunning = desired, MaintenanceActive = maintenance,
            ExplicitStartAuthorized = false, FreshRunningAcceptance = false };
        Assert.True(UpgradePolicy.Evaluate(UpgradePhase.Verifying, UpgradeStep.Accept, facts).Allowed);
        Refused(UpgradePhase.Verifying, UpgradeStep.Accept,
            facts with { FreshStoppedAcceptance = false }, UpgradeRefusal.AcceptanceUnproven);
        Assert.Equal(enabled, facts.Enabled);
        Assert.Equal(desired, facts.DesiredRunning);
        Assert.Equal(maintenance, facts.MaintenanceActive);
    }

    [Fact]
    public void FreshHealthCannotOverrideChangedIntentOrAuthorizeAnImplicitStart()
    {
        Refused(UpgradePhase.Installed, UpgradeStep.BeginVerification,
            Proven with { CurrentIntentMatches = false }, UpgradeRefusal.IntentChanged);
        Refused(UpgradePhase.Installed, UpgradeStep.BeginVerification,
            Proven with { ExplicitStartAuthorized = false }, UpgradeRefusal.IntentChanged);
        Refused(UpgradePhase.Verifying, UpgradeStep.Accept,
            Proven with { FreshRunningAcceptance = false }, UpgradeRefusal.AcceptanceUnproven);
    }

    private static void Refused(UpgradePhase phase, UpgradeStep step, UpgradeFacts facts, UpgradeRefusal expected)
    {
        var decision = UpgradePolicy.Evaluate(phase, step, facts);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Refusal);
        Assert.Equal(phase, decision.Phase);
    }
}
