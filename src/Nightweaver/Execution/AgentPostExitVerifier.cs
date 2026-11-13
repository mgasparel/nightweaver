using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum AgentOperationalFailureKind
{
    NonZeroExit,
    TimedOut,
    DirtyWorktree,
    ForbiddenHeadMutation,
    IllegalBeadTransition,
    OutcomeVerificationFailed,
    EnvironmentBlocked
}

internal sealed record AgentOperationalFailure(AgentOperationalFailureKind Kind, string Reason);

internal sealed record AgentStageObservations(
    BeadWorkflowState BeforeState,
    BeadWorkflowState ObservedState,
    string? BeforeAssignee,
    string? ObservedAssignee,
    AgentExecutionStatus ProcessStatus,
    int? ExitCode,
    string? BranchName,
    string? HeadSha,
    string? WorktreeStatus,
    bool? WorktreeClean);

internal sealed record DeliveryOutcomePublicationReceipt(
    AgentRole Role,
    AgentOutcomeChoice Outcome,
    BeadWorkflowState PublishedState,
    string? PublishedAssignee,
    bool ClaimReleased,
    bool HandoffToMerger);


internal sealed record AgentStageResult(
    AgentRole Role,
    string BeadId,
    string InvocationId,
    string LogFilePath,
    AgentOperationalFailure? Failure,
    IReadOnlyList<string> Diagnostics,
    BeadWorkflowState? VerifiedState = null,
    AgentStageObservations? Observations = null,
    AgentOutcomeClassificationResult? OutcomeClassification = null,
    IReadOnlySet<string>? PreInvocationCommentIds = null,
    DeliveryOutcomePublicationReceipt? PublicationReceipt = null,
    MergerCompletionReceipt? MergerCompletion = null,
    PlanningGrounding? PlanningGrounding = null,
    PlanningApproval? PlanningApproval = null,
    string? PlanningContext = null)
{
    internal bool Succeeded => Failure is null;
}

/// <summary>Collects post-exit observations and applies the stable operational-failure precedence.</summary>
internal sealed class AgentPostExitVerifier(IProcessRunner processRunner, BeadsReader beadsReader)
{
    internal async Task<AgentStageResult> VerifyAsync(
        ValidatedTargetRepository repository,
        string worktreePath,
        AgentRole role,
        BeadWorkflowState before,
        string? beforeAssignee,
        string beforeHead,
        AgentExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        string? porcelain = null;
        string? afterHead = null;
        string? branchName = null;
        string? afterAssignee = null;
        bool? worktreeClean = null;
        BeadsInventory? postInventory = null;
        BeadWorkflowState? after = null;
        AuthorityMatrixResult? transition = null;

        try
        {
            var status = await RunGitAsync(["status", "--porcelain"], worktreePath, "git status --porcelain", cancellationToken);
            porcelain = status.StandardOutput;
            worktreeClean = string.IsNullOrEmpty(porcelain);
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Could not inspect worktree status: {exception.Message}");
        }

        if (role != AgentRole.Classifier)
        {
            try
            {
                var branch = await RunGitAsync(["branch", "--show-current"], worktreePath, "git branch --show-current", cancellationToken);
                branchName = branch.StandardOutput.TrimEnd('\r', '\n');
                if (branchName.Length == 0 && role is not (AgentRole.Planner or AgentRole.PlanChallenger)) throw new ToolExecutionException("git branch --show-current returned an empty branch name");
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolExecutionException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception exception)
            {
                diagnostics.Add($"Could not inspect worktree branch: {exception.Message}");
            }
        }

        try
        {
            var head = await RunGitAsync(["rev-parse", "HEAD"], worktreePath, "git rev-parse HEAD", cancellationToken);
            afterHead = ParseHead(head.StandardOutput, "post-exit");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            diagnostics.Add($"Could not inspect worktree HEAD: {exception.Message}");
        }

        try
        {
            postInventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken);
            if (!postInventory.ById.TryGetValue(before.Id, out var postBead))
                throw new ToolExecutionException($"Bead '{before.Id}' is missing from the Target Repository tracker after execution");
            afterAssignee = postBead.Assignee;
            try { after = BeadWorkflowState.Parse(postBead); }
            catch (FormatException exception) { transition = AuthorityMatrixResult.Illegal(exception.Message); }
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Could not read the post-exit Bead state: {exception.Message}");
        }

        if (after is not null && postInventory is not null)
            transition = AuthorityMatrix.ValidateWorkerObservation(before, after, beforeAssignee, afterAssignee);

        var findings = new List<AgentOperationalFailure>();
        if (execution.Status == AgentExecutionStatus.TimedOut)
            findings.Add(new(AgentOperationalFailureKind.TimedOut, "Agent CLI exceeded its configured wall-clock timeout."));
        else if (execution.Status != AgentExecutionStatus.Exited)
            findings.Add(new(AgentOperationalFailureKind.NonZeroExit, $"Agent CLI returned unsupported execution status '{execution.Status}'."));
        else if (execution.ExitCode is null or not 0)
            findings.Add(new(AgentOperationalFailureKind.NonZeroExit,
                execution.ExitCode is null ? "Agent CLI exited without an observable exit code." : $"Agent CLI exited with code {execution.ExitCode}."));

        if (porcelain is { Length: > 0 } && role is not (AgentRole.Implementer or AgentRole.Merger))
        {
            var rendered = JsonSerializer.Serialize(porcelain);
            findings.Add(new(AgentOperationalFailureKind.DirtyWorktree, $"git status --porcelain returned non-empty output {rendered}."));
        }
        if (IsReadOnly(role) && afterHead is not null && !string.Equals(beforeHead, afterHead, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new(AgentOperationalFailureKind.ForbiddenHeadMutation,
                $"Read-only role changed HEAD from '{beforeHead}' to '{afterHead}'."));
        }
        if (transition is { IsLegal: false })
            findings.Add(new(AgentOperationalFailureKind.IllegalBeadTransition, transition.Reason!));

        var observations = (role != AgentRole.Classifier) && after is not null
            ? new AgentStageObservations(before, after, beforeAssignee, afterAssignee,
                execution.Status, execution.ExitCode, branchName, afterHead, porcelain, worktreeClean)
            : null;

        if (findings.Count > 0)
        {
            var primary = findings.OrderBy(failure => FailureOrder(failure.Kind)).First();
            foreach (var secondary in findings.Where(failure => !ReferenceEquals(failure, primary)))
                diagnostics.Add($"{secondary.Kind}: {secondary.Reason}");
            return new(role, before.Id, execution.InvocationId, execution.LogFilePath, primary,
                diagnostics.AsReadOnly(), role != AgentRole.Classifier ? null : after, observations);
        }

        if ((role != AgentRole.Classifier) && observations is null)
            throw new AgentPostExitVerificationException(execution.InvocationId, execution.LogFilePath,
                $"the completed {AgentRoles.ToConfigKey(role)} invocation lacks readable unchanged Bead state");

        if (diagnostics.Count > 0 && !AgentRoles.IsDeliveryRole(role) && role != AgentRole.Merger)
        {
            throw new AgentPostExitVerificationException(execution.InvocationId, execution.LogFilePath,
                string.Join("; ", diagnostics));
        }
        if (transition is null && !AgentRoles.IsDeliveryRole(role) && role != AgentRole.Merger)
        {
            throw new AgentPostExitVerificationException(execution.InvocationId, execution.LogFilePath,
                "the post-exit Bead state could not be validated");
        }

        return new(role, before.Id, execution.InvocationId, execution.LogFilePath, null,
            diagnostics.AsReadOnly(), role != AgentRole.Classifier ? null : after, observations);
    }

    private async Task<ProcessResult> RunGitAsync(IReadOnlyList<string> arguments, string workingDirectory,
        string operation, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(GitCommand.Create(arguments, workingDirectory), cancellationToken);
        if (result.ExitCode != 0)
            throw new ToolExecutionException($"{operation} in '{workingDirectory}' exited {result.ExitCode}", result.StandardError);
        return result;
    }

    private static string ParseHead(string output, string phase)
    {
        var value = output.TrimEnd('\r', '\n');
        if (value.Length is < 40 or > 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ToolExecutionException($"git rev-parse HEAD returned invalid {phase} output");
        return value;
    }

    private static bool IsReadOnly(AgentRole role) => role is
        AgentRole.Planner or AgentRole.PlanChallenger or AgentRole.Reviewer or AgentRole.Qa;

    private static int FailureOrder(AgentOperationalFailureKind kind) => kind switch
    {
        AgentOperationalFailureKind.TimedOut => 0,
        AgentOperationalFailureKind.NonZeroExit => 1,
        AgentOperationalFailureKind.DirtyWorktree => 2,
        AgentOperationalFailureKind.ForbiddenHeadMutation => 3,
        AgentOperationalFailureKind.IllegalBeadTransition => 4,
        AgentOperationalFailureKind.OutcomeVerificationFailed => 5,
        AgentOperationalFailureKind.EnvironmentBlocked => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown operational failure")
    };
}
