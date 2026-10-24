using Nightweaver.Configuration;
using Nightweaver.Planning;

namespace Nightweaver.Reporting;

internal sealed record ConfigurationSection(ConcurrencyConfig Concurrency, LimitsConfig Limits);

internal sealed record AgentRolesSection(AgentSettings AgentDefaults, RoleSettings Roles);

internal sealed record DryRunReport(ProjectHeader Project, ConfigurationSection Configuration, AgentRolesSection AgentRoles,
    StartupPlan StartupPlan, PlanningPoolPlan PlanningPoolPlan);
