using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Cli;

internal sealed record CliOptions(ProjectSelector? Selector, bool DryRun, bool Help, DispatchCommandOptions? Dispatch = null);

internal sealed record DispatchCommandOptions(string? BeadId, AgentRole? ExplicitRole);
