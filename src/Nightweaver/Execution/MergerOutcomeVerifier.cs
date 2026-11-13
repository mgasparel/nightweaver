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
    string SourceCommentId,
    string? CheckSource = null,
    int? LocalExitCode = null);

internal sealed record MergerRejectionVerification(bool Succeeded, string Diagnostic,
    MergerRejectionFinding? Finding = null);

internal sealed record MergerVerifiedMergeEvidence(string PullRequest, string CheckedHead, string MergeCommit,
    string MergeMethod, string SourceCommentId);

internal sealed record MergerApprovalVerification(bool Succeeded, string Diagnostic,
    MergerVerifiedMergeEvidence? Evidence = null);

internal interface IMergerOutcomeVerifier
{
    Task<MergerApprovalVerification> VerifyApprovalAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        AgentOutcomeClassificationResult classification,
        IReadOnlyList<BeadCommentSnapshot> comments,
        string expectedAssignee,
        CancellationToken cancellationToken);

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
    private const string LocalCheckEvidencePrefix = "NIGHTWEAVER_LOCAL_CHECK_V1 ";
    private const string LocalCheckEvidenceEnd = "END_NIGHTWEAVER_LOCAL_CHECK_V1";
    private readonly BeadsReader beadsReader = new(processRunner);

    public async Task<MergerApprovalVerification> VerifyApprovalAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        AgentOutcomeClassificationResult classification,
        IReadOnlyList<BeadCommentSnapshot> comments,
        string expectedAssignee,
        CancellationToken cancellationToken)
    {
        if (session.Role != AgentRole.Merger || invocation.Role != AgentRole.Merger
            || invocation.BeadId != session.BeadId || classification.Outcome != AgentOutcomeChoice.Approved)
            return ApprovalFailed("Only the current classified Merger approval can enter merge verification.");
        if (invocation.Failure is not null || invocation.Observations is not { } observations
            || observations.ProcessStatus != AgentExecutionStatus.Exited || observations.ExitCode != 0)
            return ApprovalFailed("A failed, timed out, cancelled, or unobserved Merger invocation cannot authorize completion.");
        if (observations.BeforeState.Stage != "stage:merge" || observations.ObservedState != observations.BeforeState
            || observations.BeforeState.Status != "in_progress"
            || !string.Equals(observations.BeforeAssignee, expectedAssignee, StringComparison.Ordinal)
            || !string.Equals(observations.ObservedAssignee, expectedAssignee, StringComparison.Ordinal)
            || observations.WorktreeClean != true)
            return ApprovalFailed("The Merger workflow, owner, or clean-worktree proof changed during invocation.");

        var expectedBranch = $"nightweaver/{session.BeadId}";
        var expectedHead = observations.HeadSha;
        if (!string.Equals(observations.BranchName, expectedBranch, StringComparison.Ordinal) || !IsSha(expectedHead))
            return ApprovalFailed("The invocation did not retain the expected Merger branch and valid current HEAD.");
        var verifiedHead = expectedHead!;
        if (invocation.PreInvocationCommentIds is null)
            return ApprovalFailed("The invocation has no comment baseline, so merge evidence cannot be attributed to this run.");

        var evidenceRows = new List<(BeadCommentSnapshot Comment, string PullRequest, string CheckedHead,
            string MergeMethod, string MergeCommit)>();
        foreach (var comment in comments.Where(item => !invocation.PreInvocationCommentIds.Contains(item.Id)))
        {
            var parsed = ParseMergeEvidence(comment.Text);
            if (parsed is { } row) evidenceRows.Add((comment, row.PullRequest, row.CheckedHead, row.MergeMethod, row.MergeCommit));
        }
        if (evidenceRows.Count != 1)
            return ApprovalFailed("The current invocation must provide exactly one structured verified-merge evidence comment.");
        var evidence = evidenceRows[0];
        if (!string.Equals(evidence.CheckedHead, verifiedHead, StringComparison.OrdinalIgnoreCase))
            return ApprovalFailed("The merge evidence does not identify the retained current HEAD.");
        var expectedRepository = session.Repository.OriginRepository;
        var expectedOriginUrl = session.Repository.OriginUrl;
        var pullRequestIdentity = GitHubRepositoryIdentity.ParsePullRequestUrl(evidence.PullRequest);
        if (expectedRepository is null || string.IsNullOrWhiteSpace(expectedOriginUrl))
            return ApprovalFailed("The Target Repository has no validated hosted origin identity for PR verification.");
        if (pullRequestIdentity is null || !expectedRepository.Matches(pullRequestIdentity.Repository))
            return ApprovalFailed("The current invocation PR URL does not identify the validated Target Repository.");

        try
        {
            var bead = await ReadBeadAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
            if (BeadWorkflowState.Parse(bead) != observations.BeforeState || bead.Status != "in_progress"
                || !string.Equals(bead.Assignee, expectedAssignee, StringComparison.Ordinal))
                return ApprovalFailed("The current Bead workflow state or claim owner changed before merge verification.");

            var branch = await RunGitAsync(["branch", "--show-current"], session.Worktree.Path, cancellationToken).ConfigureAwait(false);
            var head = await RunGitAsync(["rev-parse", "HEAD"], session.Worktree.Path, cancellationToken).ConfigureAwait(false);
            var status = await RunGitAsync(["status", "--porcelain"], session.Worktree.Path, cancellationToken).ConfigureAwait(false);
            var unmerged = await RunGitAsync(["ls-files", "-u"], session.Worktree.Path, cancellationToken).ConfigureAwait(false);
            if (branch.StandardOutput.Trim() != expectedBranch
                || !string.Equals(head.StandardOutput.Trim(), verifiedHead, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(status.StandardOutput) || !string.IsNullOrEmpty(unmerged.StandardOutput))
                return ApprovalFailed("The retained branch, HEAD, index, or worktree changed during independent merge verification.");
            foreach (var rebaseState in new[] { "rebase-merge", "rebase-apply" })
            {
                var gitPath = await RunGitAsync(["rev-parse", "--git-path", rebaseState], session.Worktree.Path,
                    cancellationToken).ConfigureAwait(false);
                var path = gitPath.StandardOutput.Trim();
                if (!Path.IsPathFullyQualified(path)) path = Path.GetFullPath(Path.Combine(session.Worktree.Path, path));
                if (Directory.Exists(path) || File.Exists(path))
                    return ApprovalFailed("The retained worktree still contains an unfinished rebase.");
            }

            if (!await OriginMatchesValidatedTargetAsync(session.Repository, expectedRepository, cancellationToken).ConfigureAwait(false))
                return ApprovalFailed("The Target Repository origin changed from its validated repository identity.");
            var pr = await ReadMergedPullRequestAsync(session.Worktree.Path, pullRequestIdentity, cancellationToken).ConfigureAwait(false);
            if (!MatchesMergedPullRequest(pr, pullRequestIdentity, expectedRepository, expectedBranch, verifiedHead,
                    session.Project.TargetRepository.DefaultBranch, evidence.MergeCommit))
                return ApprovalFailed("The pull request or base repository identity, configured base, current head, merged state, or merge commit does not match this invocation.");

            var checks = await RunJsonCommandAsync("gh",
                ["pr", "checks", pullRequestIdentity.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--repo", expectedRepository.CliRepository, "--required", "--json", "name,state,bucket"],
                session.Worktree.Path, cancellationToken).ConfigureAwait(false);
            using (var checksDocument = JsonDocument.Parse(checks.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 }))
            {
                var root = checksDocument.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0
                    || root.EnumerateArray().Any(check => check.ValueKind != JsonValueKind.Object
                        || !ReadString(check, "name", out var name) || string.IsNullOrWhiteSpace(name)
                        || !ReadString(check, "state", out var state) || state != "SUCCESS"
                        || !ReadString(check, "bucket", out var bucket) || bucket != "pass"))
                    return ApprovalFailed("Required pull request checks were missing, pending, or not passing for the current head.");
            }

            var afterChecks = await ReadMergedPullRequestAsync(session.Worktree.Path, pullRequestIdentity, cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesMergedPullRequest(afterChecks, pullRequestIdentity, expectedRepository, expectedBranch, verifiedHead,
                    session.Project.TargetRepository.DefaultBranch, evidence.MergeCommit))
                return ApprovalFailed("The pull request or base repository identity or merged head changed while required checks were being observed.");
            if (!await OriginMatchesValidatedTargetAsync(session.Repository, expectedRepository, cancellationToken).ConfigureAwait(false))
                return ApprovalFailed("The Target Repository origin changed while PR checks were being verified.");

            var defaultBranch = session.Project.TargetRepository.DefaultBranch;
            var checkedBranch = await RunGitAsync(["check-ref-format", $"refs/heads/{defaultBranch}"],
                session.Worktree.Path, cancellationToken).ConfigureAwait(false);
            if (checkedBranch.ExitCode != 0)
                return ApprovalFailed("The configured default branch is not a valid Git branch name.");
            var fetch = await RunGitAsyncAllowFailure(["fetch", expectedOriginUrl, defaultBranch], session.Worktree.Path,
                cancellationToken).ConfigureAwait(false);
            if (fetch.ExitCode != 0)
                return ApprovalFailed("The configured origin default branch could not be freshly fetched for merge confirmation.");
            var reachable = await RunGitAsyncAllowFailure(["merge-base", "--is-ancestor", evidence.MergeCommit, "FETCH_HEAD"],
                session.Worktree.Path, cancellationToken).ConfigureAwait(false);
            if (reachable.ExitCode != 0)
                return ApprovalFailed("The reported merge commit is not reachable from the freshly fetched configured default branch.");
            if (!await OriginMatchesValidatedTargetAsync(session.Repository, expectedRepository, cancellationToken).ConfigureAwait(false))
                return ApprovalFailed("The Target Repository origin changed during merge verification.");

            return new(true, "Verified current-invocation PR, target-repository required checks, merged commit, and configured default-branch reachability.",
                new(evidence.PullRequest, evidence.CheckedHead, evidence.MergeCommit, evidence.MergeMethod, evidence.Comment.Id));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return ApprovalFailed($"Could not independently verify Merger completion ({exception.GetType().Name}): {Limit(exception.Message)}");
        }
    }

    private async Task<MergedPullRequestObservation> ReadMergedPullRequestAsync(string worktreePath,
        GitHubPullRequestIdentity pullRequest, CancellationToken cancellationToken)
    {
        var number = pullRequest.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await RunJsonCommandAsync("gh", ["pr", "view", number, "--repo",
            pullRequest.Repository.CliRepository, "--json",
            "state,baseRefName,headRefName,headRefOid,url,mergedAt,mergeCommit"],
            worktreePath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !HasUniqueNames(root)
            || !ReadString(root, "state", out var state)
            || !ReadString(root, "baseRefName", out var baseBranch)
            || !ReadString(root, "headRefName", out var headBranch)
            || !ReadString(root, "headRefOid", out var head)
            || !ReadString(root, "url", out var url)
            || !ReadString(root, "mergedAt", out var mergedAt)
            || !root.TryGetProperty("mergeCommit", out var mergeCommit) || mergeCommit.ValueKind != JsonValueKind.Object
            || !HasUniqueNames(mergeCommit) || !ReadString(mergeCommit, "oid", out var mergeCommitOid))
            return new(null, null, null, null, null, null, null);
        return new(state, baseBranch, headBranch, head, url, mergedAt, mergeCommitOid);
    }

    private static bool MatchesMergedPullRequest(MergedPullRequestObservation pullRequest,
        GitHubPullRequestIdentity expectedPullRequest, GitHubRepositoryIdentity expectedRepository,
        string expectedBranch, string expectedHead, string expectedBase, string expectedMergeCommit)
    {
        var actualPullRequest = GitHubRepositoryIdentity.ParsePullRequestUrl(pullRequest.Url);
        return pullRequest.State == "MERGED"
            && string.Equals(pullRequest.BaseBranch, expectedBase, StringComparison.Ordinal)
            && string.Equals(pullRequest.HeadBranch, expectedBranch, StringComparison.Ordinal)
            && string.Equals(pullRequest.Head, expectedHead, StringComparison.OrdinalIgnoreCase)
            && actualPullRequest is not null && expectedRepository.Matches(actualPullRequest.Repository)
            && actualPullRequest.Number == expectedPullRequest.Number
            && !string.IsNullOrWhiteSpace(pullRequest.MergedAt)
            && string.Equals(pullRequest.MergeCommit, expectedMergeCommit, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> OriginMatchesValidatedTargetAsync(ValidatedTargetRepository repository,
        GitHubRepositoryIdentity expectedRepository, CancellationToken cancellationToken)
    {
        var currentOrigin = await RunGitAsyncAllowFailure(["config", "--get", "remote.origin.url"], repository.Path,
            cancellationToken).ConfigureAwait(false);
        if (currentOrigin.ExitCode != 0) return false;
        return expectedRepository.Matches(GitHubRepositoryIdentity.ParseRemoteUrl(currentOrigin.StandardOutput.Trim()));
    }

    internal static (string PullRequest, string CheckedHead, string MergeMethod, string MergeCommit)? ParseMergeEvidence(string text)
    {
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 6 || fields[0] != "nightweaver-merge-v1") return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields.Skip(1))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0 || separator == field.Length - 1
                || !values.TryAdd(field[..separator], field[(separator + 1)..])) return null;
        }
        if (values.Count != 5 || !values.TryGetValue("pull_request", out var pullRequest)
            || !Uri.TryCreate(pullRequest, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !values.TryGetValue("checked_head", out var checkedHead) || !IsSha(checkedHead)
            || !values.TryGetValue("required_checks", out var requiredChecks) || requiredChecks != "passed"
            || !values.TryGetValue("merge_method", out var mergeMethod) || mergeMethod is not ("squash" or "merge" or "rebase")
            || !values.TryGetValue("merge_commit", out var mergeCommit) || !IsSha(mergeCommit)) return null;
        return (pullRequest, checkedHead, mergeMethod, mergeCommit);
    }

    private static MergerApprovalVerification ApprovalFailed(string diagnostic) => new(false, diagnostic);

    private sealed record MergedPullRequestObservation(string? State, string? BaseBranch, string? HeadBranch,
        string? Head, string? Url, string? MergedAt, string? MergeCommit);

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

            var log = finding is { Kind: "checks", CheckSource: "local" }
                ? await MergerLocalCheckReader.ReadAsync(hostPaths, project.Name, invocation, cancellationToken).ConfigureAwait(false)
                : await FailureLogTailReader.ReadAsync(hostPaths, project.Name, invocation, cancellationToken).ConfigureAwait(false);
            if (log.HadInvalidUtf8)
                return Failed("The Merger invocation log contains invalid UTF-8 and cannot verify its rejection finding.");
            if (finding.Kind == "checks")
            {
                var checkFailure = await VerifyCheckFailureAsync(session.Worktree.Path, finding,
                    expectedBranch, expectedHead!, log, cancellationToken).ConfigureAwait(false);
                if (checkFailure is not null) return Failed(checkFailure);
            }
            else if (!log.Text.Contains(finding.Subject, StringComparison.OrdinalIgnoreCase)
                || !log.Text.Contains(finding.Details, StringComparison.OrdinalIgnoreCase))
                return Failed("The current Merger log does not retain the subject and diagnostics recorded in the rejection comment.");
            else if (finding.Kind == "conflict" && !log.Text.Contains("conflict", StringComparison.OrdinalIgnoreCase))
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

    private async Task<string?> VerifyCheckFailureAsync(
        string worktreePath,
        MergerRejectionFinding finding,
        string expectedBranch,
        string expectedHead,
        FailureLogTail log,
        CancellationToken cancellationToken)
    {
        var before = await ReadPullRequestAsync(worktreePath, finding.PullRequest, cancellationToken).ConfigureAwait(false);
        if (!MatchesCurrentPullRequest(before, finding.PullRequest, expectedBranch, expectedHead))
            return "The pull request does not identify the open retained branch and current checked HEAD.";

        if (finding.CheckSource == "local")
            return VerifyLocalCheckFailure(finding, expectedHead, log);

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
        if (!matchedFailure)
            return "No required check with the reported name is in a failed terminal state for the current HEAD.";

        var after = await ReadPullRequestAsync(worktreePath, finding.PullRequest, cancellationToken).ConfigureAwait(false);
        return MatchesCurrentPullRequest(after, finding.PullRequest, expectedBranch, expectedHead)
            ? null
            : "The pull request identity or head changed while required checks were being observed.";
    }

    private async Task<PullRequestObservation> ReadPullRequestAsync(string worktreePath, string pullRequest,
        CancellationToken cancellationToken)
    {
        var result = await RunJsonCommandAsync("gh",
            ["pr", "view", pullRequest, "--json", "state,headRefName,headRefOid,url"],
            worktreePath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
        var pr = document.RootElement;
        if (pr.ValueKind != JsonValueKind.Object || !HasUniqueNames(pr)
            || !ReadString(pr, "state", out var state)
            || !ReadString(pr, "headRefName", out var branch)
            || !ReadString(pr, "headRefOid", out var head)
            || !ReadString(pr, "url", out var url))
            return new(null, null, null, null);
        return new(state, branch, head, url);
    }

    private static bool MatchesCurrentPullRequest(PullRequestObservation pullRequest, string expectedUrl,
        string expectedBranch, string expectedHead) =>
        pullRequest.State == "OPEN"
        && pullRequest.HeadBranch == expectedBranch
        && string.Equals(pullRequest.Head, expectedHead, StringComparison.OrdinalIgnoreCase)
        && string.Equals(pullRequest.Url, expectedUrl, StringComparison.Ordinal);

    private static string? VerifyLocalCheckFailure(MergerRejectionFinding finding, string expectedHead, FailureLogTail log)
    {
        if (log.Truncated || log.HadInvalidUtf8)
            return "The local check transcript is truncated or invalid and cannot verify its result.";
        var lines = log.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var startLines = lines.Select((line, index) => (line, index))
            .Where(item => item.line.StartsWith(LocalCheckEvidencePrefix, StringComparison.Ordinal)).ToArray();
        var endLines = lines.Select((line, index) => (line, index))
            .Where(item => item.line == LocalCheckEvidenceEnd).ToArray();
        if (startLines.Length != 1 || endLines.Length != 1 || endLines[0].index <= startLines[0].index)
            return "The settled log does not contain one complete local check transcript for this invocation.";

        JsonDocument transcript;
        try
        {
            var metadata = startLines[0].line[LocalCheckEvidencePrefix.Length..];
            transcript = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException)
        {
            return "The local check transcript metadata is malformed.";
        }
        using (transcript)
        {
            var root = transcript.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasUniqueNames(root)
                || root.EnumerateObject().Count() != 3
                || !ReadString(root, "command", out var command)
                || command is not ("dotnet build" or "dotnet test")
                || !ReadString(root, "checked_head", out var checkedHead)
                || !string.Equals(checkedHead, expectedHead, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("exit_code", out var exitCodeElement)
                || exitCodeElement.ValueKind != JsonValueKind.Number
                || !exitCodeElement.TryGetInt32(out var exitCode) || exitCode <= 0
                || !string.Equals(command, finding.Subject, StringComparison.Ordinal)
                || finding.LocalExitCode != exitCode)
                return "The local check transcript does not identify an allowlisted failing command, nonzero exit, and retained HEAD.";

            var output = string.Join('\n', lines[(startLines[0].index + 1)..endLines[0].index]);
            if (!output.Contains(finding.Details, StringComparison.Ordinal)
                || !output.Contains(finding.Summary, StringComparison.Ordinal))
                return "The local check transcript does not retain the reported summary and diagnostics.";

            var hasTerminalFailure = command switch
            {
                "dotnet build" => HasBuildFailureSummary(output),
                "dotnet test" => HasTestFailureSummary(output),
                _ => false
            };
            return hasTerminalFailure
                ? null
                : "The local check transcript has no unambiguous nonzero build/test failure summary.";
        }
    }

    private static bool HasBuildFailureSummary(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var terminalResult = Array.FindLastIndex(lines, line =>
            line.Trim().Equals("Build FAILED.", StringComparison.OrdinalIgnoreCase)
            || line.Trim().Equals("Build succeeded.", StringComparison.OrdinalIgnoreCase));
        return terminalResult >= 0
            && lines[terminalResult].Trim().Equals("Build FAILED.", StringComparison.OrdinalIgnoreCase)
            && lines.Skip(terminalResult + 1).Any(line => System.Text.RegularExpressions.Regex.IsMatch(line.Trim(),
                @"^[1-9][0-9]* Error\(s\)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
    }

    private static bool HasTestFailureSummary(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var terminalResult = Array.FindLastIndex(lines, line =>
            line.Trim().Equals("Test run summary: Failed!", StringComparison.OrdinalIgnoreCase)
            || line.Trim().Equals("Test run summary: Passed!", StringComparison.OrdinalIgnoreCase));
        if (terminalResult < 0
            || !lines[terminalResult].Trim().Equals("Test run summary: Failed!", StringComparison.OrdinalIgnoreCase))
            return false;
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(terminalResult + 1))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line.Trim(),
                @"^(total|failed|succeeded|skipped):\s*([0-9]+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[2].Value, out var count) || !totals.TryAdd(match.Groups[1].Value, count))
                return false;
        }
        return totals.TryGetValue("total", out var total) && total > 0
            && totals.TryGetValue("failed", out var failed) && failed > 0 && failed <= total
            && (!totals.TryGetValue("succeeded", out var succeeded) || succeeded <= total - failed)
            && (!totals.TryGetValue("skipped", out var skipped) || skipped <= total - failed - succeeded)
            && (totals.Count != 4 || (long)failed + succeeded + skipped == total);
    }

    private sealed record PullRequestObservation(string? State, string? HeadBranch, string? Head, string? Url);

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

    private async Task<ProcessResult> RunGitAsyncAllowFailure(IReadOnlyList<string> arguments, string worktreePath,
        CancellationToken cancellationToken) =>
        await processRunner.RunAsync(GitCommand.Create(arguments, worktreePath), cancellationToken).ConfigureAwait(false);

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
                if (summary.Length < 12 || details.Length < 12
                    || !Uri.TryCreate(pullRequest, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    continue;

                string? checkSource = null;
                int? localExitCode = null;
                if (kind == "checks")
                {
                    if (!ReadString(root, "check_source", out checkSource)) continue;
                    if (checkSource == "remote")
                    {
                        if (root.EnumerateObject().Count() != 8) continue;
                    }
                    else if (checkSource == "local")
                    {
                        if (root.EnumerateObject().Count() != 9
                            || subject is not ("dotnet build" or "dotnet test")
                            || !root.TryGetProperty("local_exit_code", out var exitCodeElement)
                            || exitCodeElement.ValueKind != JsonValueKind.Number
                            || !exitCodeElement.TryGetInt32(out var exitCode) || exitCode <= 0)
                            continue;
                        localExitCode = exitCode;
                    }
                    else continue;
                }
                else if (root.EnumerateObject().Count() != 7 || root.TryGetProperty("check_source", out _)
                    || root.TryGetProperty("local_exit_code", out _))
                    continue;

                if (kind == "conflict" && !IsSafeRepositoryRelativePath(subject)) continue;
                return new(kind, uri.AbsoluteUri, checkedHead, subject, summary, details, comment.Id, checkSource, localExitCode);
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static bool IsSafeRepositoryRelativePath(string path)
    {
        if (path.Length is 0 or > 1024 || path.Any(char.IsControl)
            || path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path)
            || path.Contains(':') || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            return false;

        var segments = path.Replace('\\', '/').Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not ("." or "..")
            && !segment.EndsWith(' ') && !segment.EndsWith('.'));
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
