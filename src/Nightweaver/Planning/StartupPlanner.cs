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

    internal static StartupPlan Plan(string recoveryActor, BeadsInventory inventory,
        IReadOnlyList<GitWorktreeSnapshot> registry, RuntimeDirectorySnapshot worktrees, RuntimeDirectorySnapshot tmp)
    {
        var phase1 = Plan(recoveryActor, inventory);
        var diagnostics = phase1.Diagnostics.ToList();
        var removals = new List<RemoveWorktree>();
        var survivors = new List<RetainedWorktree>();
        var orphans = new List<DeleteOrphanTmp>();
        var uncertainIds = new HashSet<string>(RuntimePaths.Comparer);
        void Diagnostic(string id, StartupDiagnosticCode code, string path, string? reason = null) =>
            diagnostics.Add(new(id, code, Path: path, Reason: reason));

        if (worktrees.State == DirectoryState.UnsafeLink) Diagnostic("", StartupDiagnosticCode.UnsafeRuntimePath, worktrees.Path);
        if (tmp.State == DirectoryState.UnsafeLink) Diagnostic("", StartupDiagnosticCode.UnsafeRuntimePath, tmp.Path);
        var owned = registry.Where(entry => !entry.Main && !entry.Bare && RuntimePaths.ChildId(worktrees.Path, entry.Path) is not null).ToArray();
        foreach (var entry in owned.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            var id = RuntimePaths.ChildId(worktrees.Path, entry.Path)!;
            var matches = worktrees.Entries.Where(child => RuntimePaths.Comparer.Equals(child.Path, entry.Path)).ToArray();
            var disk = matches.FirstOrDefault();
            var presence = worktrees.State == DirectoryState.UnsafeLink ? DirectoryState.UnsafeLink : disk?.State ?? DirectoryState.Missing;
            var ambiguous = matches.Length > 1 || disk is not null && (disk.AmbiguousIdentity || !string.Equals(disk.Id, id, StringComparison.Ordinal));
            var hasBead = inventory.ById.TryGetValue(id, out var bead);
            if (!hasBead) Diagnostic(id, StartupDiagnosticCode.MissingBead, entry.RawPath);
            if (entry.Locked) Diagnostic(id, StartupDiagnosticCode.LockedWorktree, entry.RawPath, entry.LockReason);
            if (entry.Prunable) Diagnostic(id, StartupDiagnosticCode.PrunableRegistration, entry.RawPath, entry.PruneReason);
            if (presence == DirectoryState.Missing) Diagnostic(id, StartupDiagnosticCode.MissingRegisteredPath, entry.RawPath);
            if (presence == DirectoryState.UnsafeLink) Diagnostic(id, StartupDiagnosticCode.UnsafeRuntimePath, entry.RawPath);
            if (ambiguous) Diagnostic(id, StartupDiagnosticCode.AmbiguousWorktreeIdentity, entry.RawPath);

            var reasons = new List<string>();
            if (bead?.Status == "closed") reasons.Add("closed");
            foreach (var label in new[] { "needs-human", "planned", "product-decision-required" })
                if (bead?.Labels.Contains(label, StringComparer.Ordinal) == true) reasons.Add(label);
            var supported = bead?.Status is "open" or "in_progress" or "closed";
            if (bead is not null && !supported && reasons.Count == 0)
                Diagnostic(id, StartupDiagnosticCode.UnsupportedBeadStatus, entry.RawPath, bead.Status);
            var certain = hasBead && !entry.Locked && !entry.Prunable && presence == DirectoryState.Present && !ambiguous;
            if (certain && reasons.Count > 0)
                removals.Add(new(id, entry, presence, reasons.AsReadOnly()));
            else
            {
                var reusable = certain && supported;
                var protectsTmp = presence != DirectoryState.Missing || entry.Locked || ambiguous;
                survivors.Add(new(id, entry, presence, reusable, protectsTmp));
                if (!reusable && protectsTmp) uncertainIds.Add(id);
            }
        }
        foreach (var disk in worktrees.Entries)
        {
            if (disk.State == DirectoryState.UnsafeLink) Diagnostic(disk.Id, StartupDiagnosticCode.UnsafeRuntimePath, disk.Path);
            if (disk.AmbiguousIdentity)
            {
                Diagnostic(disk.Id, StartupDiagnosticCode.AmbiguousWorktreeIdentity, disk.Path);
                uncertainIds.Add(disk.Id);
            }
            if (owned.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, disk.Path))) continue;
            Diagnostic(disk.Id, StartupDiagnosticCode.UnregisteredWorktreeDirectory, disk.Path);
            uncertainIds.Add(disk.Id);
        }
        foreach (var directory in tmp.Entries.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            if (directory.AmbiguousIdentity)
            {
                Diagnostic(directory.Id, StartupDiagnosticCode.AmbiguousWorktreeIdentity, directory.Path);
                Diagnostic(directory.Id, StartupDiagnosticCode.TmpCleanupDeferred, directory.Path, "ambiguous tmp identity");
            }
            else if (directory.State == DirectoryState.UnsafeLink)
            {
                Diagnostic(directory.Id, StartupDiagnosticCode.UnsafeRuntimePath, directory.Path);
                Diagnostic(directory.Id, StartupDiagnosticCode.TmpCleanupDeferred, directory.Path, "unsafe tmp path");
            }
            else if (worktrees.State == DirectoryState.UnsafeLink || uncertainIds.Contains(directory.Id))
                Diagnostic(directory.Id, StartupDiagnosticCode.TmpCleanupDeferred, directory.Path, "unresolved worktree backing");
            else if (!survivors.Any(entry => entry.ProtectsTmp && RuntimePaths.Comparer.Equals(entry.BeadId, directory.Id)))
                orphans.Add(new(directory.Id, directory.Path));
        }
        return phase1 with
        {
            Removals = removals.OrderBy(entry => entry.BeadId, StringComparer.Ordinal).ThenBy(entry => entry.Path, StringComparer.Ordinal).ToList().AsReadOnly(),
            SurvivingWorktrees = survivors.OrderBy(entry => entry.BeadId, StringComparer.Ordinal).ThenBy(entry => entry.Registration.Path, StringComparer.Ordinal).ToList().AsReadOnly(),
            OrphanTmp = orphans.AsReadOnly(),
            Diagnostics = diagnostics.Distinct().OrderBy(entry => entry.Code is StartupDiagnosticCode.ForeignInProgressClaim or StartupDiagnosticCode.UnassignedInProgressClaim ? 1 : entry.Code == StartupDiagnosticCode.TmpCleanupDeferred ? 3 : 2)
                .ThenBy(entry => entry.BeadId, StringComparer.Ordinal).ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ThenBy(entry => entry.Code).ThenBy(entry => entry.Reason, StringComparer.Ordinal).ToList().AsReadOnly()
        };
    }
}
