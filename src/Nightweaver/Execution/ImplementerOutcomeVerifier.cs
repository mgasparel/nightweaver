using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal interface IImplementerOutcomeVerifier
{
    Task<string?> VerifyNoClassifierMutationAsync(
        ValidatedTargetRepository repository,
        string worktreePath,
        AgentStageResult invocation,
        string expectedAssignee,
        CancellationToken cancellationToken);

    Task<string?> VerifyReadyForReviewAsync(
        ValidatedTargetRepository repository,
        string worktreePath,
        AgentStageResult invocation,
        string expectedAssignee,
        string defaultBranch,
        CancellationToken cancellationToken);
}

/// <summary>Checks completed Implementer work independently of the classifier's answer.</summary>
internal sealed class ImplementerOutcomeVerifier(IProcessRunner processRunner) : IImplementerOutcomeVerifier
{
    private readonly BeadsReader beadsReader = new(processRunner);

    public async Task<string?> VerifyNoClassifierMutationAsync(
        ValidatedTargetRepository repository,
        string worktreePath,
        AgentStageResult invocation,
        string expectedAssignee,
        CancellationToken cancellationToken)
    {
        if (invocation.Observations is not { } original)
            return "The Implementer invocation has no deterministic observations.";
        try
        {
            var status = await RunGitAsync(["status", "--porcelain"], worktreePath, cancellationToken).ConfigureAwait(false);
            var branch = await RunGitAsync(["branch", "--show-current"], worktreePath, cancellationToken).ConfigureAwait(false);
            var head = await RunGitAsync(["rev-parse", "HEAD"], worktreePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(status.StandardOutput, original.WorktreeStatus, StringComparison.Ordinal))
                return "Outcome fallback changed the Implementer worktree status.";
            if (!string.Equals(branch.StandardOutput.Trim(), original.BranchName, StringComparison.Ordinal))
                return "Outcome fallback changed the Implementer worktree branch.";
            if (!string.Equals(head.StandardOutput.Trim(), original.HeadSha, StringComparison.OrdinalIgnoreCase))
                return "Outcome fallback changed the Implementer worktree HEAD.";
            var inventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!inventory.ById.TryGetValue(invocation.BeadId, out var bead))
                return "Outcome fallback removed the Implementer Bead from the Target Repository tracker.";
            var state = BeadWorkflowState.Parse(bead);
            if (state != original.BeforeState
                || !string.Equals(bead.Assignee, expectedAssignee, StringComparison.Ordinal))
                return "Outcome fallback changed the Implementer Bead workflow state or claim owner.";
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return $"Could not independently verify classifier side effects: {exception.GetType().Name}.";
        }
    }

    public async Task<string?> VerifyReadyForReviewAsync(
        ValidatedTargetRepository repository,
        string worktreePath,
        AgentStageResult invocation,
        string expectedAssignee,
        string defaultBranch,
        CancellationToken cancellationToken)
    {
        if (invocation.Observations is not { } observations)
            return "The Implementer invocation has no deterministic process/work observations.";
        if (observations.ProcessStatus != AgentExecutionStatus.Exited || observations.ExitCode != 0)
            return "The Implementer process did not exit successfully.";
        if (observations.WorktreeClean != true)
            return "The Implementer worktree is not clean; approval was withheld and retained work was preserved.";

        var expectedBranch = $"nightweaver/{invocation.BeadId}";
        if (!string.Equals(observations.BranchName, expectedBranch, StringComparison.Ordinal))
            return $"The Implementer worktree branch must be '{expectedBranch}', observed '{observations.BranchName ?? "(unavailable)"}'.";
        if (!IsSha(observations.HeadSha))
            return "The Implementer worktree HEAD is unavailable or invalid.";

        var current = await VerifyNoClassifierMutationAsync(repository, worktreePath, invocation,
            expectedAssignee, cancellationToken).ConfigureAwait(false);
        if (current is not null) return current;

        ProcessResult remote;
        try
        {
            remote = await RunGitAsync(["ls-remote", "origin", $"refs/heads/{expectedBranch}"], worktreePath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return $"The expected remote branch could not be verified ({exception.GetType().Name}).";
        }
        var remoteRows = remote.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (remoteRows.Length != 1)
            return $"The expected remote branch '{expectedBranch}' was not observed exactly once.";
        var remoteParts = remoteRows[0].Split('\t', 2);
        if (remoteParts.Length != 2 || !IsSha(remoteParts[0])
            || remoteParts[1] != $"refs/heads/{expectedBranch}"
            || !string.Equals(remoteParts[0], observations.HeadSha, StringComparison.OrdinalIgnoreCase))
            return $"The remote branch '{expectedBranch}' does not identify verified HEAD '{observations.HeadSha}'.";

        var pullRequest = await ReadPullRequestAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        if (pullRequest.Error is not null) return pullRequest.Error;
        if (pullRequest.State != "OPEN"
            || pullRequest.BaseRefName != defaultBranch
            || pullRequest.HeadRefName != expectedBranch
            || !string.Equals(pullRequest.HeadRefOid, observations.HeadSha, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(pullRequest.Url))
            return "The open pull request does not match the configured base branch, expected head branch, and verified HEAD.";
        return null;
    }

    private async Task<ProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(GitCommand.Create(arguments, worktreePath), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ToolExecutionException($"git {string.Join(' ', arguments)} exited {result.ExitCode}", result.StandardError);
        return result;
    }

    private async Task<PullRequestObservation> ReadPullRequestAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var request = new ProcessRequest("gh",
            ["pr", "view", "--json", "state,baseRefName,headRefName,headRefOid,url"],
            worktreePath,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["GH_PROMPT_DISABLED"] = "1" },
            TimeSpan.FromSeconds(20));
        try
        {
            var process = await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new(null, null, null, null, null, $"gh pr view exited {process.ExitCode}; pull request verification is required before approval.");
            using var document = JsonDocument.Parse(process.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasUniquePropertyNames(root))
                return new(null, null, null, null, null, "gh pr view returned invalid JSON.");
            if (!TryGetString(root, "state", out var state)
                || !TryGetString(root, "baseRefName", out var baseRefName)
                || !TryGetString(root, "headRefName", out var headRefName)
                || !TryGetString(root, "headRefOid", out var headRefOid)
                || !TryGetString(root, "url", out var url))
                return new(null, null, null, null, null, "gh pr view omitted a required pull request field.");
            return new(state, baseRefName, headRefName, headRefOid, url, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new(null, null, null, null, null,
                $"Pull request verification was unavailable ({exception.GetType().Name}); approval was withheld.");
        }
    }

    private static bool HasUniquePropertyNames(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) return false;
        return true;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsSha(string? value) => value is { Length: >= 40 and <= 64 }
        && value.All(Uri.IsHexDigit);

    private sealed record PullRequestObservation(
        string? State,
        string? BaseRefName,
        string? HeadRefName,
        string? HeadRefOid,
        string? Url,
        string? Error);
}
