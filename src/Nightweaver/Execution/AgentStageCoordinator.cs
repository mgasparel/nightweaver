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
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(beadId)) throw new AgentStageInputException("Bead ID must be nonblank.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedRole(role))
            throw new AgentStageInputException(role == AgentRole.Classifier
                ? "Classifier is not a lifecycle role for single-stage execution."
                : $"Unknown Agent role '{role}'.");

        var initialInventory = await ReadSnapshotAsync(repository, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var initial = ReadRequestedState(initialInventory, beadId);
        EnsureValidInput(role, initial);

        var purpose = role is AgentRole.Planner or AgentRole.PlanChallenger
            ? WorktreePurpose.Planning : WorktreePurpose.Delivery;
        var worktree = await provision(hostPaths, project.Name, repository, beadId, purpose, cancellationToken);

        var freshInventory = await ReadSnapshotAsync(repository, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var before = ReadRequestedState(freshInventory, beadId);
        EnsureValidInput(role, before);
        if (before != initial)
            throw new AgentStageInputException($"Bead '{beadId}' workflow state changed during provisioning; refusing to dispatch a stale stage.");
        var beforeHead = ParseProvisionedHead(worktree.HeadSha);

        var dispatch = new AgentDispatchContext(before.Id, before.IssueType, before.Stage, before.RepairCycles);
        var invocation = await prepare(hostPaths, project, role, dispatch, worktree.Path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await execute(hostPaths, project.Name, invocation, cancellationToken);
        return await verifier.VerifyAsync(repository, worktree.Path, role, before, beforeHead, execution, cancellationToken);
    }

    private async Task<BeadsInventory> ReadSnapshotAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
    {
        try { return await beadsReader.ReadSnapshotAsync(repository, cancellationToken); }
        catch (ToolExecutionException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static BeadWorkflowState ReadRequestedState(BeadsInventory inventory, string beadId)
    {
        if (!inventory.ById.TryGetValue(beadId, out var bead))
            throw new AgentStageInputException($"Bead '{beadId}' was not found in the Target Repository tracker.");
        try { return BeadWorkflowState.Parse(bead); }
        catch (FormatException exception) { throw new AgentStageInputException(exception.Message); }
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
