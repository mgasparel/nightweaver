using Nightweaver.Inspection;
using Nightweaver.Execution;

namespace Nightweaver.Planning;

/// <summary>Applies the Product/Plan role rules for the conservative startup sweep.</summary>
internal static class StartupEnvironmentClassifier
{
    internal static bool IsPaused(BeadSnapshot bead) =>
        HasLabel(bead, "needs-human") || HasLabel(bead, "product-decision-required");

    internal static bool IsCompletedTmpOwner(BeadSnapshot bead, string recoveryActor)
    {
        if (!IsSafeBeadId(bead.Id)) return false;
        if (IsPaused(bead)) return false;
        var stages = StageLabels(bead);
        if (stages.Count > 0 && !(bead.IssueType == "task" && stages.Count == 1 && stages[0] == "stage:merge"))
            return false;
        if (bead.IssueType is "feature" or "bug")
            return HasLabel(bead, "planned") && stages.Count == 0 && IsUnassignedOpenOrRecoverable(bead, recoveryActor);
        return bead.IssueType == "task" && !HasLabel(bead, "planned") && bead.Status == "closed"
            && bead.Assignee is null;
    }

    internal static bool IsCompletedWorktreeOwner(BeadSnapshot bead, string recoveryActor, GitWorktreeSnapshot registration)
    {
        if (!IsSafeBeadId(bead.Id)) return false;
        if (registration.Main || registration.Bare || IsPaused(bead)) return false;
        var stages = StageLabels(bead);
        if (bead.IssueType is "feature" or "bug")
            return HasLabel(bead, "planned") && stages.Count == 0
                && IsUnassignedOpenOrRecoverable(bead, recoveryActor)
                && registration.Detached && registration.Branch is null;

        return bead.IssueType == "task" && !HasLabel(bead, "planned")
            && bead.Status == "closed" && bead.Assignee is null
            && (stages.Count == 0 || stages.Count == 1 && stages[0] == "stage:merge")
            && !registration.Detached
            && string.Equals(registration.Branch, $"refs/heads/nightweaver/{bead.Id}", StringComparison.Ordinal);
    }

    internal static bool HasLabel(BeadSnapshot bead, string label) => bead.Labels.Contains(label, StringComparer.Ordinal);

    internal static IReadOnlyList<string> StageLabels(BeadSnapshot bead) => bead.Labels
        .Where(label => label.StartsWith("stage:", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool IsUnassignedOpenOrRecoverable(BeadSnapshot bead, string recoveryActor) =>
        bead.Status == "open" && bead.Assignee is null
        || bead.Status == "in_progress" && string.Equals(bead.Assignee, recoveryActor, StringComparison.Ordinal);

    private static bool IsSafeBeadId(string id) => WorktreeProvisioner.IsSafeBeadId(id)
        && id.Length > 0 && char.IsAsciiLetterOrDigit(id[0]);
}
