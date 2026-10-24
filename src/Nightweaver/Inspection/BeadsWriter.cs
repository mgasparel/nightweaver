namespace Nightweaver.Inspection;

/// <summary>Scoped local Beads writes used for needs-human escalation only.</summary>
internal sealed class BeadsWriter(IProcessRunner processRunner)
{
    internal Task ApplyNeedsHumanAsync(ValidatedTargetRepository repository, string beadId, string expectedAssignee, string expectedStatus,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["update", beadId, "--assignee", expectedAssignee, "--add-label", "needs-human", "--remove-label", "product-decision-required",
            "--if-assignee", expectedAssignee, "--if-status", expectedStatus],
        $"apply needs-human to Bead '{beadId}' guarded by its observed {expectedStatus} claim state", cancellationToken);

    internal Task AddCommentAsync(ValidatedTargetRepository repository, string beadId, string comment,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["comments", "add", beadId, comment], $"add needs-human diagnostic comment to Bead '{beadId}'", cancellationToken);

    internal Task ReleaseClaimAsync(ValidatedTargetRepository repository, string beadId, string assignee,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["update", beadId, "--status", "open", "--assignee", "", "--if-assignee", assignee, "--if-status", "in_progress"],
        $"release needs-human claim for Bead '{beadId}' guarded by its live claim", cancellationToken);

    private async Task RunAsync(ValidatedTargetRepository repository, IReadOnlyList<string> arguments,
        string operation, CancellationToken cancellationToken)
    {
        try
        {
            BeadsReader.EnsureMutationFreeBackend(repository);
            var result = await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path, arguments), cancellationToken);
            if (result.ExitCode != 0)
                throw new ToolExecutionException($"{operation}: bd exited {result.ExitCode}"
                    + (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : $": {result.StandardError.TrimEnd()}"), result.StandardError);
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException($"{operation} in Target Repository '{repository.Path}' failed: {exception.Message}", exception.StandardError);
        }
    }
}
