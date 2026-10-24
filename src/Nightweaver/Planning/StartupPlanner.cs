using Nightweaver.Inspection;

namespace Nightweaver.Planning;

internal static class StartupPlanner
{
    internal static StartupPlan Plan(string recoveryActor, BeadsInventory inventory)
    {
        var releases = new List<ReleaseClaim>();
        var diagnostics = new List<StartupDiagnostic>();
        foreach (var bead in inventory.ById.Values.OrderBy(bead => bead.Id, StringComparer.Ordinal))
        {
            if (bead.Status != "in_progress") continue;
            if (string.Equals(bead.Assignee, recoveryActor, StringComparison.Ordinal))
                releases.Add(new ReleaseClaim(bead.Id, recoveryActor));
            else
                diagnostics.Add(new StartupDiagnostic(bead.Id, bead.Assignee is null
                    ? StartupDiagnosticCode.UnassignedInProgressClaim : StartupDiagnosticCode.ForeignInProgressClaim, bead.Assignee));
        }
        return new StartupPlan(inventory, releases.AsReadOnly(), diagnostics.AsReadOnly());
    }
}
