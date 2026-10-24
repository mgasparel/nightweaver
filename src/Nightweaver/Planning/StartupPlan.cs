using Nightweaver.Inspection;

namespace Nightweaver.Planning;

// A patch to status and assignee only; all other fields remain in Inventory.
internal sealed record ReleaseClaim(string BeadId, string ExpectedAssignee)
{
    internal string ExpectedStatus { get; } = "in_progress";
    internal string TargetStatus { get; } = "open";
    internal string? TargetAssignee { get; }
}

internal enum StartupDiagnosticCode
{
    ForeignInProgressClaim, UnassignedInProgressClaim, UnsafeRuntimePath, MissingBead, LockedWorktree,
    MissingRegisteredPath, PrunableRegistration, UnsupportedBeadStatus, UnregisteredWorktreeDirectory,
    AmbiguousWorktreeIdentity, TmpCleanupDeferred
}
internal sealed record StartupDiagnostic(string BeadId, StartupDiagnosticCode Code, string? Assignee = null, string? Path = null, string? Reason = null);
internal sealed record RemoveWorktree(string BeadId, GitWorktreeSnapshot Registration, DirectoryState Presence, IReadOnlyList<string> Reasons)
{
    internal string Path => Registration.Path;
    internal bool ExpectedLocked { get; }
}
internal sealed record RetainedWorktree(string BeadId, GitWorktreeSnapshot Registration, DirectoryState Presence, bool Reusable, bool ProtectsTmp);
internal sealed record DeleteOrphanTmp(string BeadId, string Path);
internal sealed record StartupPlan(BeadsInventory Inventory, IReadOnlyList<ReleaseClaim> Releases, IReadOnlyList<StartupDiagnostic> Diagnostics)
{
    internal IReadOnlyList<RemoveWorktree> Removals { get; init; } = Array.Empty<RemoveWorktree>();
    internal IReadOnlyList<RetainedWorktree> SurvivingWorktrees { get; init; } = Array.Empty<RetainedWorktree>();
    internal IReadOnlyList<DeleteOrphanTmp> OrphanTmp { get; init; } = Array.Empty<DeleteOrphanTmp>();
}
