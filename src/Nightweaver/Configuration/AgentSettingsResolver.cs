namespace Nightweaver.Configuration;

internal static class AgentSettingsResolver
{
    internal static AgentSettings Apply(AgentSettings inherited, AgentSettingsOverride overlay) => new(
        overlay.Cli.Or(inherited.Cli),
        overlay.Model.Or(inherited.Model),
        overlay.Effort.Or(inherited.Effort),
        overlay.TimeoutMinutes.Or(inherited.TimeoutMinutes));

    internal static (AgentSettings AgentDefaults, RoleSettings Roles) Resolve(
        AgentSettingsOverride agentDefaults,
        IReadOnlyDictionary<AgentRole, AgentSettingsOverride> roleOverrides)
    {
        var resolvedDefaults = Apply(ProjectConfigDefaults.AgentDefaults, agentDefaults);
        var resolvedRoles = new Dictionary<AgentRole, AgentSettings>();
        foreach (var role in AgentRoles.All)
        {
            var overlay = roleOverrides.TryGetValue(role, out var roleOverride)
                ? roleOverride
                : AgentSettingsOverride.None;
            resolvedRoles.Add(role, Apply(resolvedDefaults, overlay));
        }

        return (resolvedDefaults, new RoleSettings(resolvedRoles));
    }
}
