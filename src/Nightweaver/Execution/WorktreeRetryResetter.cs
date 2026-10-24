using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>Resets an existing, validated per-Bead worktree before one operational retry.</summary>
internal sealed class WorktreeRetryResetter
{
    private readonly IProcessRunner processRunner;
    private readonly WorktreeProvisioner provisioner;

    internal WorktreeRetryResetter(IProcessRunner processRunner)
    {
        this.processRunner = processRunner;
        provisioner = new WorktreeProvisioner(processRunner);
    }

    internal async Task<ProvisionedWorktree> ResetAsync(
        HostPaths paths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        WorktreePurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(repository);
        if (!WorktreeProvisioner.IsSafeBeadId(beadId) || !char.IsAsciiLetterOrDigit(beadId[0]))
            throw new AgentStageInputException($"Bead ID '{beadId}' is not a safe retry worktree identity.");
        cancellationToken.ThrowIfCancellationRequested();

        var destination = RuntimePaths.Normalize(Path.Combine(paths.WorktreesRoot(project), beadId));
        await RunDestructiveCommandAsync(paths, project, repository, beadId, purpose, destination,
            ["reset", "--hard", "HEAD"], "git reset --hard HEAD", cancellationToken).ConfigureAwait(false);
        await RunDestructiveCommandAsync(paths, project, repository, beadId, purpose, destination,
            ["clean", "-fd"], "git clean -fd", cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return await provisioner.VerifyRetryWorktreeAsync(paths, project, repository, beadId, purpose, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunDestructiveCommandAsync(
        HostPaths paths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        WorktreePurpose purpose,
        string destination,
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await provisioner.ValidateRetryWorktreeAsync(paths, project, repository, beadId, purpose, cancellationToken)
            .ConfigureAwait(false);
        var canonical = RuntimePaths.Normalize(Path.Combine(paths.WorktreesRoot(project), beadId));
        if (!string.Equals(destination, canonical, StringComparison.Ordinal))
            throw new ToolExecutionException($"{operation} at '{destination}' was refused because the canonical Bead worktree identity changed.");

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(GitCommand.Create(arguments, canonical), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException($"{operation} at '{canonical}' failed: {exception.Message}", exception.StandardError);
        }
        if (result.ExitCode != 0)
            throw new ToolExecutionException($"{operation} at '{canonical}' exited {result.ExitCode}"
                + (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : $": {result.StandardError.TrimEnd()}"),
                result.StandardError);
    }
}
