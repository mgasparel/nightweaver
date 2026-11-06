using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Cli;

internal sealed record CliOptions(ProjectSelector? Selector, bool DryRun, bool Help, DispatchCommandOptions? Dispatch = null,
    BackgroundCommandOptions? Background = null);

internal sealed record DispatchCommandOptions(string? BeadId, AgentRole? ExplicitRole);

internal sealed record BackgroundCommandOptions(
    BackgroundAction Action,
    ProjectSelector? Selector,
    string? Urls = null,
    bool Force = false,
    BackgroundOwnerOptions? Owner = null);

internal sealed record BackgroundOwnerOptions(
    string Root,
    string ConfigPath,
    string Project,
    string RunId,
    string Urls);

internal enum BackgroundAction { Start, Status, Stop, Restart, Owner }
