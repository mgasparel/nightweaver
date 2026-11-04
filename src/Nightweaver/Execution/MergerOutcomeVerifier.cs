using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal sealed record MergerRejectionFinding(
    string Kind,
    string PullRequest,
    string CheckedHead,
    string Subject,
    string Summary,
    string Details,
    string SourceCommentId);

internal sealed record MergerRejectionVerification(bool Succeeded, string Diagnostic,
    MergerRejectionFinding? Finding = null);

internal interface IMergerOutcomeVerifier
{
    Task<MergerRejectionVerification> VerifyRejectionAsync(
        HostPaths hostPaths,
        ProjectConfig project,
        AgentStageSession session,
        AgentStageResult invocation,
        AgentOutcomeClassificationResult classification,
        IReadOnlyList<BeadCommentSnapshot> comments,
        string expectedAssignee,
        CancellationToken cancellationToken);
}

/// <summary>Independently verifies that classified Merger rejection evidence belongs to the current invocation.</summary>
internal sealed class MergerOutcomeVerifier(IProcessRunner processRunner) : IMergerOutcomeVerifier
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    private readonly BeadsReader beadsReader = new(processRunner);

    public async Task<MergerRejectionVerification> VerifyRejectionAsync(
        HostPaths hostPaths,
        ProjectConfig project,
        AgentStageSession session,
        AgentStageResult invocation,
        AgentOutcomeClassificationResult classification,
        IReadOnlyList<BeadCommentSnapshot> comments,
        string expectedAssignee,
        CancellationToken cancellationToken)
    {
        if (session.Role != AgentRole.Merger || invocation.Role != AgentRole.Merger
            || classification.Outcome != AgentOutcomeChoice.ChangesRequired)
            return Failed("Only a classified Merger rejection can enter rejection verification.");
        if (invocation.Failure is not null || invocation.Observations is not { } observations
            || observations.ProcessStatus != AgentExecutionStatus.Exited || observations.ExitCode != 0)
            return Failed("A failed, timed out, cancelled, or unobserved Merger invocation cannot be a semantic rejection.");
        if (observations.BeforeState.Stage != "stage:merge"
            || observations.ObservedState != observations.BeforeState
            || !string.Equals(observations.BeforeAssignee, expectedAssignee, StringComparison.Ordinal)
            || !string.Equals(observations.ObservedAssignee, expectedAssignee, StringComparison.Ordinal))
            return Failed("The Merger workflow state or claim owner changed during the invocation.");

        var expectedBranch = $"nightweaver/{session.BeadId}";
        if (!string.Equals(observations.BranchName, expectedBranch, StringComparison.Ordinal))
            return Failed($"The Merger worktree branch is not the retained branch '{expectedBranch}'.");
        var expectedHead = observations.HeadSha;
        if (!IsSha(expectedHead))
            return Failed("The Merger invocation did not leave a valid observable HEAD.");

        try
        {
            var bead = await ReadBeadAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
            var state = BeadWorkflowState.Parse(bead);
            if (state != observations.BeforeState || bead.Status != "in_progress"
                || !string.Equals(bead.Assignee, expectedAssignee, StringComparison.Ordinal))
                return Failed("The current Bead state or owner no longer matches the claimed stage:merge input.");

            var branch = await RunGitAsync(["branch", "--show-current"], session.Worktree.Path, cancellationToken)
                .ConfigureAwait(false);
            var head = await RunGitAsync(["rev-parse", "HEAD"], session.Worktree.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(branch.StandardOutput.Trim(), expectedBranch, StringComparison.Ordinal)
                || !string.Equals(head.StandardOutput.Trim(), expectedHead, StringComparison.OrdinalIgnoreCase))
                return Failed("The retained Merger branch or HEAD changed during outcome classification.");

            var unmerged = await RunGitAsync(["ls-files", "-u"], session.Worktree.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(unmerged.StandardOutput))
                return Failed("The Merger left unresolved index entries; an actionable rejection cannot be handed to Delivery.");
            foreach (var rebaseState in new[] { "rebase-merge", "rebase-apply" })
            {
                var gitPath = await RunGitAsync(["rev-parse", "--git-path", rebaseState], session.Worktree.Path,
                    cancellationToken).ConfigureAwait(false);
                var path = gitPath.StandardOutput.Trim();
                if (!Path.IsPathFullyQualified(path)) path = Path.GetFullPath(Path.Combine(session.Worktree.Path, path));
                if (Directory.Exists(path) || File.Exists(path))
                    return Failed("The Merger still has an active rebase; the rejected operation must be aborted before handoff.");
            }

            var finding = FindCurrentFinding(invocation, comments);
            if (finding is null)
                return Failed("No structured actionable Merger rejection comment from this invocation was found.");
            if (!string.Equals(finding.CheckedHead, expectedHead, StringComparison.OrdinalIgnoreCase))
                return Failed("The rejection finding does not identify the retained current HEAD.");

            var log = await FailureLogTailReader.ReadAsync(hostPaths, project.Name, invocation, cancellationToken)
                .ConfigureAwait(false);
            if (log.HadInvalidUtf8)
                return Failed("The Merger invocation log contains invalid UTF-8 and cannot verify its rejection finding.");
            if (!log.Text.Contains(finding.Subject, StringComparison.OrdinalIgnoreCase)
                || !log.Text.Contains(finding.Details, StringComparison.OrdinalIgnoreCase))
                return Failed("The current Merger log does not retain the subject and diagnostics recorded in the rejection comment.");

            if (finding.Kind == "checks")
            {
                var checkFailure = await VerifyFailedCurrentHeadCheckAsync(session.Worktree.Path, finding,
                    expectedBranch, expectedHead!, cancellationToken).ConfigureAwait(false);
                if (checkFailure is not null) return Failed(checkFailure);
            }
            else if (finding.Kind == "conflict"
                && !log.Text.Contains("conflict", StringComparison.OrdinalIgnoreCase))
            {
                return Failed("The current Merger log contains no conflict diagnostic for the reported path.");
            }

            return new(true, $"Verified current-invocation Merger {finding.Kind} evidence against unchanged workflow ownership and retained HEAD.", finding);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Failed($"Could not independently verify Merger rejection evidence ({exception.GetType().Name}): {Limit(exception.Message)}");
        }
    }

    private async Task<string?> VerifyFailedCurrentHeadCheckAsync(
        string worktreePath,
        MergerRejectionFinding finding,
        string expectedBranch,
        string expectedHead,
        CancellationToken cancellationToken)
    {
        var pullRequest = await RunJsonCommandAsync("gh",
            ["pr", "view", finding.PullRequest, "--json", "state,headRefName,headRefOid,url"],
            worktreePath, cancellationToken).ConfigureAwait(false);
        using var prDocument = JsonDocument.Parse(pullRequest.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
        var pr = prDocument.RootElement;
        if (pr.ValueKind != JsonValueKind.Object
            || !ReadString(pr, "state", out var state) || state != "OPEN"
            || !ReadString(pr, "headRefName", out var branch) || branch != expectedBranch
            || !ReadString(pr, "headRefOid", out var head) || !string.Equals(head, expectedHead, StringComparison.OrdinalIgnoreCase)
            || !ReadString(pr, "url", out var url) || !string.Equals(url, finding.PullRequest, StringComparison.Ordinal))
            return "The pull request does not identify the open retained branch and current checked HEAD.";

        var checks = await RunJsonCommandAsync("gh",
            ["pr", "checks", finding.PullRequest, "--required", "--json", "name,state,bucket"],
            worktreePath, cancellationToken).ConfigureAwait(false);
        using var checksDocument = JsonDocument.Parse(checks.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
        if (checksDocument.RootElement.ValueKind != JsonValueKind.Array)
            return "gh pr checks did not return a JSON array of required checks.";
        var matchedFailure = checksDocument.RootElement.EnumerateArray().Any(check =>
            check.ValueKind == JsonValueKind.Object
            && ReadString(check, "name", out var name) && name == finding.Subject
            && ReadString(check, "bucket", out var bucket) && bucket == "fail"
            && ReadString(check, "state", out var checkState)
            && checkState is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED");
        return matchedFailure ? null : "No required check with the reported name is in a failed terminal state for the current HEAD.";
    }

    private async Task<ProcessResult> RunJsonCommandAsync(string fileName, IReadOnlyList<string> arguments,
        string worktreePath, CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(fileName, arguments, worktreePath,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["GH_PROMPT_DISABLED"] = "1" },
            ProcessTimeout);
        var result = await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ToolExecutionException($"{fileName} {string.Join(' ', arguments)} exited {result.ExitCode}", result.StandardError);
        return result;
    }

    private async Task<ProcessResult> RunGitAsync(IReadOnlyList<string> arguments, string worktreePath,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(GitCommand.Create(arguments, worktreePath), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ToolExecutionException($"git {string.Join(' ', arguments)} exited {result.ExitCode}", result.StandardError);
        return result;
    }

    private async Task<BeadSnapshot> ReadBeadAsync(ValidatedTargetRepository repository, string beadId,
        CancellationToken cancellationToken)
    {
        var inventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        return inventory.ById.TryGetValue(beadId, out var bead)
            ? bead
            : throw new AgentStageInputException($"Bead '{beadId}' disappeared during Merger outcome verification.");
    }

    private static MergerRejectionFinding? FindCurrentFinding(AgentStageResult invocation,
        IReadOnlyList<BeadCommentSnapshot> comments)
    {
        if (invocation.PreInvocationCommentIds is null) return null;
        foreach (var comment in comments.Where(item => !invocation.PreInvocationCommentIds.Contains(item.Id)).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(comment.Text, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !HasUniqueNames(root)
                    || !ReadString(root, "schema", out var schema) || schema != "nightweaver-merger-rejection-v1"
                    || !ReadString(root, "kind", out var kind) || kind is not ("conflict" or "design" or "checks")
                    || !ReadString(root, "pull_request", out var pullRequest)
                    || !ReadString(root, "checked_head", out var checkedHead) || !IsSha(checkedHead)
                    || !ReadString(root, "subject", out var subject)
                    || !ReadString(root, "summary", out var summary)
                    || !ReadString(root, "details", out var details))
                    continue;
                if (root.EnumerateObject().Count() != 7 || summary.Length < 12 || details.Length < 12
                    || !Uri.TryCreate(pullRequest, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    continue;
                if (kind == "conflict" && !subject.Contains('/') && !subject.Contains('\\')) continue;
                return new(kind, uri.AbsoluteUri, checkedHead, subject, summary, details, comment.Id);
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static bool ReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasUniqueNames(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool IsSha(string? value) => value is { Length: >= 40 and <= 64 } && value.All(Uri.IsHexDigit);
    private static MergerRejectionVerification Failed(string diagnostic) => new(false, diagnostic);
    private static string Limit(string value) => value.Length <= 384 ? value : value[..384];
}
