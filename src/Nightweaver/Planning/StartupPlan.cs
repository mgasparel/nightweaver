using Nightweaver.Inspection;

namespace Nightweaver.Planning;

// A patch to status and assignee only; all other fields remain in Inventory.
internal sealed record ReleaseClaim(string BeadId, string ExpectedAssignee)
{
    internal string ExpectedStatus { get; } = "in_progress";
    internal string TargetStatus { get; } = "open";
    internal string? TargetAssignee { get; }
}

internal enum StartupDiagnosticCode { ForeignInProgressClaim, UnassignedInProgressClaim }
internal sealed record StartupDiagnostic(string BeadId, StartupDiagnosticCode Code, string? Assignee);
internal sealed record StartupPlan(BeadsInventory Inventory, IReadOnlyList<ReleaseClaim> Releases, IReadOnlyList<StartupDiagnostic> Diagnostics);
