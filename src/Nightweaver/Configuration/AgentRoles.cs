namespace Nightweaver.Configuration;

internal static class AgentRoles
{
    private static readonly (AgentRole Role, string Key)[] Mappings =
    [
        (AgentRole.Planner, "planner"),
        (AgentRole.PlanChallenger, "plan_challenger"),
        (AgentRole.Implementer, "implementer"),
        (AgentRole.Reviewer, "reviewer"),
        (AgentRole.Qa, "qa"),
        (AgentRole.Merger, "merger"),
        (AgentRole.Classifier, "classifier")
    ];

    internal static IReadOnlyList<AgentRole> All { get; } = Array.AsReadOnly(Mappings.Select(mapping => mapping.Role).ToArray());

    internal static string ToConfigKey(AgentRole role) =>
        Mappings.FirstOrDefault(mapping => mapping.Role == role).Key
        ?? throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Agent role");

    internal static bool TryParseConfigKey(string key, out AgentRole role)
    {
        foreach (var (mappedRole, mappedKey) in Mappings)
        {
            if (string.Equals(key, mappedKey, StringComparison.Ordinal))
            {
                role = mappedRole;
                return true;
            }
        }

        role = default;
        return false;
    }
}
