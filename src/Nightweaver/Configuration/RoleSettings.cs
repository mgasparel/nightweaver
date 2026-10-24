using System.Collections.ObjectModel;

namespace Nightweaver.Configuration;

internal sealed class RoleSettings
{
    private readonly ReadOnlyDictionary<AgentRole, AgentSettings> settings;

    internal RoleSettings(IReadOnlyDictionary<AgentRole, AgentSettings> settings)
    {
        var copy = new Dictionary<AgentRole, AgentSettings>();
        foreach (var role in AgentRoles.All)
        {
            if (!settings.TryGetValue(role, out var setting))
            {
                throw new ArgumentException($"Missing settings for Agent role '{AgentRoles.ToConfigKey(role)}'", nameof(settings));
            }

            copy.Add(role, setting);
        }

        this.settings = new ReadOnlyDictionary<AgentRole, AgentSettings>(copy);
    }

    internal AgentSettings this[AgentRole role] => settings[role];

    internal IEnumerable<(AgentRole Role, AgentSettings Settings)> InCanonicalOrder() =>
        AgentRoles.All.Select(role => (role, settings[role]));
}
