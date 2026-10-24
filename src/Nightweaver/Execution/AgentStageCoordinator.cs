using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal delegate Task<ProvisionedWorktree> ProvisionAgentWorktree(
    HostPaths hostPaths, ProjectSlug project, ValidatedTargetRepository repository, string beadId,
    WorktreePurpose purpose, CancellationToken cancellationToken);

internal delegate Task<PreparedAgentInvocation> PrepareAgentInvocation(
    HostPaths hostPaths, ProjectConfig project, AgentRole role, AgentDispatchContext context,
    string worktreePath, CancellationToken cancellationToken);

internal delegate Task<AgentExecutionResult> ExecuteAgentInvocation(
    HostPaths hostPaths, ProjectSlug project, PreparedAgentInvocation invocation, CancellationToken cancellationToken);

internal sealed record AgentStageSession(
    HostPaths HostPaths,
    ProjectConfig Project,
    ValidatedTargetRepository Repository,
    string BeadId,
    AgentRole Role,
    WorktreePurpose Purpose,
    ProvisionedWorktree Worktree,
    BeadWorkflowState InitialState,
    AgentDispatchContext DispatchContext);

/// <summary>Coordinates exactly one provision, preparation, execution, and post-exit verification for a Bead stage.</summary>
internal sealed class AgentStageCoordinator
{
    private readonly BeadsReader beadsReader;
    private readonly ProvisionAgentWorktree provision;
    private readonly PrepareAgentInvocation prepare;
    private readonly ExecuteAgentInvocation execute;
    private readonly AgentPostExitVerifier verifier;

    internal AgentStageCoordinator(
        IProcessRunner processRunner,
        ProvisionAgentWorktree? provision = null,
        PrepareAgentInvocation? prepare = null,
        ExecuteAgentInvocation? execute = null)
    {
        beadsReader = new BeadsReader(processRunner);
        this.provision = provision ?? new WorktreeProvisioner(processRunner).ProvisionAsync;
        this.prepare = prepare ?? new AgentInvocationPreparer().PrepareAsync;
        this.execute = execute ?? AgentInvocationExecutor.ExecuteAsync;
        verifier = new AgentPostExitVerifier(processRunner, beadsReader);
    }

    internal async Task<AgentStageResult> RunAsync(
        HostPaths hostPaths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        AgentRole role,
        CancellationToken cancellationToken)
    {
        var session = await OpenSessionAsync(hostPaths, project, repository, beadId, role, expectedAssignee: null, cancellationToken);
        return await RunAttemptAsync(session, session.InitialState, session.Worktree.HeadSha, cancellationToken);
    }

    internal async Task<AgentStageSession> OpenSessionAsync(
        HostPaths hostPaths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        AgentRole role,
        string? expectedAssignee,
        CancellationToken cancellationToken,
        BeadWorkflowState? expectedState = null)
    {
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(beadId)) throw new AgentStageInputException("Bead ID must be nonblank.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedRole(role))
            throw new AgentStageInputException(role == AgentRole.Classifier
                ? "Classifier is not a lifecycle role for single-stage execution."
                : $"Unknown Agent role '{role}'.");

        var initial = await ReadRequestedStateAsync(repository, beadId, cancellationToken);
        EnsureValidInput(role, initial.State);
        if (expectedAssignee is not null) EnsureExpectedClaim(initial, expectedAssignee, requireInProgress: true);
        if (expectedState is not null && initial.State != expectedState)
            throw new AgentStageInputException($"Bead '{beadId}' workflow state changed before stage opening; refusing to dispatch against stale verified state.");

        var purpose = role is AgentRole.Planner or AgentRole.PlanChallenger
            ? WorktreePurpose.Planning : WorktreePurpose.Delivery;
        var worktree = await provision(hostPaths, project.Name, repository, beadId, purpose, cancellationToken);

        var fresh = await ReadRequestedStateAsync(repository, beadId, cancellationToken);
        EnsureValidInput(role, fresh.State);
        if (expectedAssignee is not null) EnsureExpectedClaim(fresh, expectedAssignee, requireInProgress: true);
        if (fresh.State != initial.State || (expectedAssignee is not null
            && !string.Equals(fresh.Bead.Assignee, initial.Bead.Assignee, StringComparison.Ordinal)))
            throw new AgentStageInputException($"Bead '{beadId}' workflow state changed during provisioning; refusing to dispatch a stale stage.");

        _ = ParseProvisionedHead(worktree.HeadSha);
        return new AgentStageSession(hostPaths, project, repository, beadId, role, purpose, worktree,
            fresh.State, new AgentDispatchContext(fresh.State.Id, fresh.State.IssueType, fresh.State.Stage, fresh.State.RepairCycles));
    }

    internal async Task<AgentStageResult> RunAttemptAsync(
        AgentStageSession session,
        BeadWorkflowState before,
        string beforeHead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(before);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureValidInput(session.Role, before);
        if (before.IssueType != session.InitialState.IssueType || before.Stage != session.InitialState.Stage
            || before.RepairCycles != session.InitialState.RepairCycles)
            throw new AgentStageInputException("Attempt workflow state no longer matches the original stage session.");
        var validHead = ParseProvisionedHead(beforeHead);
        var invocation = await prepare(session.HostPaths, session.Project, session.Role, session.DispatchContext,
            session.Worktree.Path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await execute(session.HostPaths, session.Project.Name, invocation, cancellationToken);
        return await verifier.VerifyAsync(session.Repository, session.Worktree.Path, session.Role, before,
            validHead, execution, cancellationToken);
    }

    internal async Task<(BeadSnapshot Bead, BeadWorkflowState State)> ReadRequestedStateAsync(
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken,
        bool allowConflictingPauseLabels = false)
    {
        var inventory = await ReadSnapshotAsync(repository, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!inventory.ById.TryGetValue(beadId, out var bead))
            throw new AgentStageInputException($"Bead '{beadId}' was not found in the Target Repository tracker.");
        try { return (bead, BeadWorkflowState.Parse(bead)); }
        catch (FormatException) when (allowConflictingPauseLabels
            && bead.Labels.Contains("product-decision-required", StringComparer.Ordinal)
            && bead.Labels.Contains("needs-human", StringComparer.Ordinal))
        {
            var normalized = bead with { Labels = bead.Labels.Where(label => label != "product-decision-required").ToArray() };
            try { return (bead, BeadWorkflowState.Parse(normalized) with { ProductDecisionRequired = true }); }
            catch (FormatException exception) { throw new AgentStageInputException(exception.Message); }
        }
        catch (FormatException exception) { throw new AgentStageInputException(exception.Message); }
    }

    internal static void EnsureExpectedClaim(
        (BeadSnapshot Bead, BeadWorkflowState State) snapshot,
        string expectedAssignee,
        bool requireInProgress)
    {
        if (string.IsNullOrWhiteSpace(expectedAssignee))
            throw new AgentStageInputException("Expected claim assignee must be nonblank.");
        if (!string.Equals(snapshot.Bead.Assignee, expectedAssignee, StringComparison.Ordinal))
            throw new AgentStageInputException($"Bead '{snapshot.State.Id}' is not owned by expected assignee '{expectedAssignee}'.");
        if (requireInProgress && snapshot.State.Status != "in_progress")
            throw new AgentStageInputException($"Bead '{snapshot.State.Id}' must have an in_progress claim before dispatch.");
        if (snapshot.State.Status is not ("open" or "in_progress"))
            throw new AgentStageInputException($"Bead '{snapshot.State.Id}' is terminal and cannot be dispatched.");
    }

    private async Task<BeadsInventory> ReadSnapshotAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
    {
        try { return await beadsReader.ReadSnapshotAsync(repository, cancellationToken); }
        catch (ToolExecutionException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void EnsureValidInput(AgentRole role, BeadWorkflowState state)
    {
        var reason = AuthorityMatrix.ValidateInput(role, state);
        if (reason is not null) throw new AgentStageInputException(reason);
    }

    private static bool IsSupportedRole(AgentRole role) => role is
        AgentRole.Planner or AgentRole.PlanChallenger or AgentRole.Implementer
        or AgentRole.Reviewer or AgentRole.Qa or AgentRole.Merger;

    private static string ParseProvisionedHead(string output)
    {
        var value = output.TrimEnd('\r', '\n');
        if (value.Length is < 40 or > 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ToolExecutionException("Worktree provisioner returned an invalid pre-dispatch HEAD SHA");
        return value;
    }
}
