namespace Nightweaver.Inspection;

/// <summary>Scoped local Beads writes for dispatch claims and needs-human escalation.</summary>
internal sealed class BeadsWriter(IProcessRunner processRunner)
{
    internal Task AcquireDispatchClaimAsync(ValidatedTargetRepository repository, string beadId, string actor,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["update", beadId, "--if-status", "open", "--if-assignee", "", "--status", "in_progress", "--assignee", actor, "--json"],
        $"acquire dispatch claim for Bead '{beadId}' as '{actor}'", cancellationToken, actor);

    internal Task ReleaseDispatchClaimAsync(ValidatedTargetRepository repository, string beadId, string actor,
        string expectedStatus, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "update", beadId };
        if (expectedStatus is "open" or "in_progress")
        {
            arguments.Add("--status");
            arguments.Add("open");
        }
        arguments.Add("--assignee");
        arguments.Add(string.Empty);
        arguments.Add("--if-assignee");
        arguments.Add(actor);
        arguments.Add("--if-status");
        arguments.Add(expectedStatus);
        arguments.Add("--json");
        return RunAsync(repository, arguments,
            $"release dispatch claim for Bead '{beadId}' as '{actor}' guarded by its observed {expectedStatus} state",
            cancellationToken, actor);
    }

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
        string operation, CancellationToken cancellationToken, string? actor = null)
    {
        try
        {
            BeadsReader.EnsureMutationFreeBackend(repository);
            var result = await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path, arguments, actor), cancellationToken);
            if (result.ExitCode != 0)
                throw new ToolExecutionException($"{operation}: bd exited {result.ExitCode}"
                    + (result.ExitCode == 13 ? " (the guarded state no longer matches; no retry was attempted)" : string.Empty)
                    + (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : $": {result.StandardError.TrimEnd()}"), result.StandardError);
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException($"{operation} in Target Repository '{repository.Path}' failed: {exception.Message}", exception.StandardError);
        }
    }
}
