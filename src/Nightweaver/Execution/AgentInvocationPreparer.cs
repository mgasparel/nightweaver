using System.Globalization;
using System.Collections.ObjectModel;
using System.Security;
using System.Text;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>Prepares prompt and launch data without creating runtime files or starting a process.</summary>
internal sealed class AgentInvocationPreparer
{
    private static readonly object IdentifierLock = new();
    private static long lastIdentifierUtcTicks = DateTime.MinValue.Ticks;

    private readonly TimeProvider timeProvider;

    internal AgentInvocationPreparer(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<PreparedAgentInvocation> PrepareAsync(
        HostPaths hostPaths,
        ProjectConfig project,
        AgentRole role,
        AgentDispatchContext context,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(worktreePath);
        cancellationToken.ThrowIfCancellationRequested();

        var (promptFileName, roleSuffix) = GetPromptFileAndSuffix(role);
        ValidateContext(context, project);
        var workingDirectory = ValidateWorktreePath(hostPaths, project, context.BeadId, worktreePath);

        var promptPath = Path.GetFullPath(Path.Combine(hostPaths.Root, "prompts", promptFileName));
        var prompt = await ReadPromptAsync(promptPath, role, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var remainingRepairBudget = project.Limits.MaxRepairCycles - context.RepairCycles;
        var dispatchHeader = string.Join('\n',
            $"Bead ID: {context.BeadId}",
            $"Bead type: {context.BeadType}",
            $"Stage: {context.StageLabel ?? "(none)"}",
            $"default_branch: {project.TargetRepository.DefaultBranch}",
            $"Remaining repair budget: {remainingRepairBudget.ToString(CultureInfo.InvariantCulture)}");
        var standardInput = dispatchHeader + "\n\n" + prompt;
        return BuildInvocation(hostPaths, project, context, workingDirectory, project.Roles[role], standardInput, roleSuffix);
    }

    /// <summary>Prepares the classifier's code-owned comparison protocol without loading a lifecycle prompt template.</summary>
    internal Task<PreparedAgentInvocation> PrepareClassifierAsync(
        HostPaths hostPaths,
        ProjectConfig project,
        AgentDispatchContext context,
        string worktreePath,
        string comparisonPayload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(worktreePath);
        ArgumentNullException.ThrowIfNull(comparisonPayload);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContext(context, project);
        var workingDirectory = ValidateWorktreePath(hostPaths, project, context.BeadId, worktreePath);
        if (Encoding.UTF8.GetByteCount(comparisonPayload) > DuplicateFailureClassifierLimits.MaximumPayloadBytes)
            throw new AgentInvocationPreparationException("The classifier comparison payload exceeds the configured serialized size limit.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BuildInvocation(hostPaths, project, context, workingDirectory,
            project.Roles[AgentRole.Classifier], comparisonPayload, "classifier"));
    }

    private PreparedAgentInvocation BuildInvocation(
        HostPaths hostPaths,
        ProjectConfig project,
        AgentDispatchContext context,
        string workingDirectory,
        AgentSettings settings,
        string standardInput,
        string roleSuffix)
    {
        var arguments = BuildArguments(settings);
        var invocationId = AllocateInvocationId(roleSuffix);
        var beadTmpRoot = RuntimePaths.Normalize(Path.Combine(hostPaths.TmpRoot(project.Name), context.BeadId));
        var temporaryDirectory = RuntimePaths.Normalize(Path.Combine(beadTmpRoot, invocationId));
        var beadLogsRoot = RuntimePaths.Normalize(Path.Combine(hostPaths.LogsRoot(project.Name), context.BeadId));
        var logFilePath = RuntimePaths.Normalize(Path.Combine(beadLogsRoot, invocationId + ".log"));
        EnsureDirectChild(beadTmpRoot, temporaryDirectory, "temporary directory");
        EnsureDirectChild(beadLogsRoot, logFilePath, "log file");

        IReadOnlyDictionary<string, string> environmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NIGHTWEAVER_BEAD_ID"] = context.BeadId,
            ["TMP"] = temporaryDirectory,
            ["TEMP"] = temporaryDirectory,
            ["TMPDIR"] = temporaryDirectory
        };

        return new PreparedAgentInvocation(
            AgentCliNames.ToConfigValue(settings.Cli),
            arguments,
            workingDirectory,
            standardInput,
            environmentOverrides,
            settings.Timeout,
            invocationId,
            temporaryDirectory,
            logFilePath);
    }

    private static (string FileName, string Suffix) GetPromptFileAndSuffix(AgentRole role) => role switch
    {
        AgentRole.Planner => ("planner.md", "planner"),
        AgentRole.PlanChallenger => ("plan-challenger.md", "plan-challenger"),
        AgentRole.Implementer => ("implementer.md", "implementer"),
        AgentRole.Reviewer => ("reviewer.md", "reviewer"),
        AgentRole.Qa => ("qa.md", "qa"),
        AgentRole.Merger => ("merger.md", "merger"),
        AgentRole.Classifier => throw new AgentInvocationPreparationException(
            "The Classifier Agent role is not supported by invocation preparation; classifier prompts belong to a separate workflow."),
        _ => throw new AgentInvocationPreparationException($"Unknown Agent role value '{role}'.")
    };

    private static void ValidateContext(AgentDispatchContext context, ProjectConfig project)
    {
        if (!IsSafeBeadId(context.BeadId))
            throw new AgentInvocationPreparationException($"Bead ID '{context.BeadId}' is not a safe literal path segment.");
        ValidateSingleLine(context.BeadType, "Bead type");
        ValidateSingleLine(project.TargetRepository.DefaultBranch, "default_branch");
        if (context.RepairCycles < 0)
            throw new AgentInvocationPreparationException("repair_cycles cannot be negative.");
        if (project.Limits.MaxRepairCycles < 0)
            throw new AgentInvocationPreparationException("max_repair_cycles cannot be negative.");
        if (context.StageLabel is not null
            && (!context.StageLabel.StartsWith("stage:", StringComparison.Ordinal)
                || context.StageLabel.Length == "stage:".Length
                || context.StageLabel.Any(char.IsWhiteSpace)
                || context.StageLabel.Any(char.IsControl)))
            throw new AgentInvocationPreparationException(
                $"Stage label '{context.StageLabel}' must be one nonempty stage:* label without whitespace or control characters.");
    }

    private static void ValidateSingleLine(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character is '\r' or '\n' || char.IsControl(character)))
            throw new AgentInvocationPreparationException($"{fieldName} must be a nonempty single-line value.");
    }

    private static bool IsSafeBeadId(string beadId) => BeadIdSafety.IsSafe(beadId);

    private static string ValidateWorktreePath(HostPaths hostPaths, ProjectConfig project, string beadId, string worktreePath)
    {
        if (!Path.IsPathFullyQualified(worktreePath))
            throw new AgentInvocationPreparationException("The provisioned worktree path must be absolute.");

        string actual;
        try
        {
            actual = RuntimePaths.Normalize(worktreePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new AgentInvocationPreparationException($"The provisioned worktree path '{worktreePath}' is invalid: {exception.Message}");
        }

        var expected = RuntimePaths.Normalize(Path.Combine(hostPaths.WorktreesRoot(project.Name), beadId));
        if (!RuntimePaths.Comparer.Equals(actual, expected))
            throw new AgentInvocationPreparationException(
                $"The provisioned worktree path '{actual}' does not match the canonical Bead worktree path '{expected}'.");
        return actual;
    }

    private static async Task<string> ReadPromptAsync(string promptPath, AgentRole role, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(promptPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or SecurityException)
        {
            throw new AgentInvocationPreparationException(
                $"Could not read the {role} prompt template at '{promptPath}': {exception.Message}");
        }
    }

    private static ReadOnlyCollection<string> BuildArguments(AgentSettings settings)
    {
        var arguments = new List<string>();
        switch (settings.Cli)
        {
            case AgentCli.Claude:
                arguments.AddRange(["-p", "--dangerously-skip-permissions", "--no-session-persistence"]);
                if (settings.Model is not null)
                    arguments.AddRange(["--model", settings.Model]);
                arguments.AddRange(["--effort", settings.Effort]);
                break;
            case AgentCli.Codex:
                arguments.AddRange(["exec", "--dangerously-bypass-approvals-and-sandbox", "--ephemeral"]);
                if (settings.Model is not null)
                    arguments.AddRange(["--model", settings.Model]);
                arguments.AddRange(["-c", $"model_reasoning_effort=\"{settings.Effort}\"", "-"]);
                break;
            default:
                throw new AgentInvocationPreparationException($"Unknown Agent CLI value '{settings.Cli}'.");
        }

        return Array.AsReadOnly(arguments.ToArray());
    }

    private string AllocateInvocationId(string roleSuffix)
    {
        var clockTicks = timeProvider.GetUtcNow().UtcTicks;
        long allocatedTicks;
        lock (IdentifierLock)
        {
            if (lastIdentifierUtcTicks == DateTime.MaxValue.Ticks)
                throw new AgentInvocationPreparationException("The process-wide invocation timestamp range is exhausted.");

            allocatedTicks = Math.Max(clockTicks, lastIdentifierUtcTicks + 1);
            lastIdentifierUtcTicks = allocatedTicks;
        }

        var timestamp = new DateTime(allocatedTicks, DateTimeKind.Utc)
            .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);
        return $"{timestamp}-{roleSuffix}";
    }

    private static void EnsureDirectChild(string parent, string child, string description)
    {
        if (!RuntimePaths.Comparer.Equals(Path.GetDirectoryName(child), parent))
            throw new AgentInvocationPreparationException($"Computed {description} '{child}' is not a direct child of '{parent}'.");
    }
}
