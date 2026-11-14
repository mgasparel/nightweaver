using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal sealed record PlanningGrounding(string CheckoutRevision, string DefaultRevision, string ProductInputs, string? ObservedRemoteRevision = null);
internal sealed record PlanningTask(string Id, string Title, string Outcome, string Context,
    string Approach, string[] AcceptanceCriteria, string Verification, string[] DependsOn);
internal sealed record PlanningConstraint(string Requirement, string Source);
internal sealed record PlanningCoverage(string Requirement, string[] TaskIds, string? SatisfiedEvidence, string? AuthorizedExclusion);
internal sealed record PlanningQuestion(string Question, string RecommendedAnswer, string Tradeoffs);
internal sealed record PlanningFinding(string Violation, string Evidence, string Direction);
internal sealed record PlanningDocument(string Schema, PlanningGrounding Grounding,
    PlanningTask[]? Tasks = null, PlanningConstraint[]? Constraints = null, PlanningCoverage[]? Coverage = null,
    string? PlanCommentId = null, string? PlanRevision = null, string? Assessment = null,
    string? Revalidation = null, PlanningFinding[]? Findings = null, PlanningQuestion[]? Questions = null);
internal sealed record PlanningApproval(string PlanCommentId, string PlanRevision, string ReviewCommentId,
    string ReviewRevision, PlanningGrounding Grounding);
internal sealed record PlanningObservation(PlanningGrounding Grounding, BeadSnapshot Product,
    IReadOnlyList<BeadCommentSnapshot> Comments);

/// <summary>Recoverable Beads-native Planning evidence and conservative grounding validation.</summary>
internal sealed class PlanningEvidence(IProcessRunner runner)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string CandidateSchema = "nightweaver-plan-v1";
    internal const string ReviewSchema = "nightweaver-plan-review-v1";
    internal const string QuestionSchema = "nightweaver-plan-question-v1";
    private readonly BeadsReader reader = new(runner);
    private static readonly string[] ProductFields = ["title", "description", "design", "acceptance_criteria", "notes"];

    internal async Task<PlanningGrounding> CaptureAsync(AgentStageSession session, CancellationToken token)
        => (await ObserveAsync(session, token)).Grounding;

    internal async Task<PlanningObservation> ObserveAsync(AgentStageSession session, CancellationToken token)
    {
        // Independent read-only Git processes can overlap. Settle every read before
        // observing tracker authority; no observation is reused across mutations.
        var statusRead = runner.RunAsync(GitCommand.Create(["status", "--porcelain"], session.Worktree.Path), token);
        var remoteRead = runner.RunAsync(GitCommand.Create(
            ["rev-parse", "--verify", $"refs/remotes/origin/{session.Project.TargetRepository.DefaultBranch}"], session.Repository.Path), token);
        var checkoutRead = GitRevisionAsync(session.Worktree.Path, "HEAD", token);
        var defaultRead = GitRevisionAsync(session.Repository.Path,
            $"refs/heads/{session.Project.TargetRepository.DefaultBranch}", token);
        await Task.WhenAll(statusRead, remoteRead, checkoutRead, defaultRead);
        var status = await statusRead;
        if (status.ExitCode != 0 || status.StandardOutput.Length != 0)
            throw new AgentStageInputException("Planning grounding requires an observable clean isolated Planning checkout; preserve changes for inspection.");
        var remote = await remoteRead;
        var checkoutRevision = await checkoutRead;
        var defaultRevision = await defaultRead;
        // Keep tracker observations last and use this same fresh Product/discussion
        // for both grounding and publication authority. Never reuse across writes.
        var inventory = await reader.ReadSnapshotAsync(session.Repository, token);
        var bead = inventory.ById[session.BeadId];
        var comments = await reader.ReadCommentsAsync(session.Repository, session.BeadId, token);
        // Workflow, timestamps and evidence comments change during normal turns. Product text
        // and all ordinary discussion remain inputs, including human guidance after Resume.
        var fields = ProductFields
            .Select(name => bead.Payload.TryGetProperty(name, out var value) ? value.ToString() : "");
        var discussion = comments.Where(c => !IsProtocol(c.Text))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id + ":" + c.Text);
        var input = JsonSerializer.Serialize(fields.Concat(discussion), JsonOptions);
        return new(new(checkoutRevision, defaultRevision, Hash(input),
            remote.ExitCode == 0 ? remote.StandardOutput.Trim() : null), bead, comments);
    }

    internal async Task<string> ReadContextAsync(AgentStageSession session, CancellationToken token)
    {
        var inventory = await reader.ReadSnapshotAsync(session.Repository, token);
        var comments = await reader.ReadCommentsAsync(session.Repository, session.BeadId, token);
        var candidate = LatestCandidate(comments);
        var context = JsonSerializer.Serialize(new
        {
            product = inventory.ById[session.BeadId].Payload,
            discussion = comments.Where(c => !IsProtocol(c.Text)).Select(c => new { c.Id, c.Text }),
            candidateCommentId = candidate?.Comment.Id,
            candidateText = candidate?.Comment.Text
        }, JsonOptions);
        if (Encoding.UTF8.GetByteCount(context) > 128 * 1024)
            throw new AgentStageInputException("Planning Product and candidate context exceeds the bounded classification input.");
        return context;
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task<string> GitRevisionAsync(string path, string revision, CancellationToken token)
    {
        var result = await runner.RunAsync(GitCommand.Create(["rev-parse", "--verify", revision], path), token);
        var sha = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || sha.Length is < 40 or > 64 || sha.Any(c => !Uri.IsHexDigit(c)))
            throw new ToolExecutionException($"Cannot verify Planning grounding revision '{revision}' in '{path}'.");
        return sha;
    }

    internal static bool IsProtocol(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.TryGetProperty("schema", out var schema)
                && schema.GetString() is CandidateSchema or ReviewSchema or QuestionSchema
                    or "nightweaver-planning-outcome-v1" or PlanningPublisher.ReleaseSchema;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    internal static PlanningDocument? Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 64 * 1024) return null;
        try { return JsonSerializer.Deserialize<PlanningDocument>(text, JsonOptions); }
        catch (JsonException) { return null; }
    }

    internal static (BeadCommentSnapshot Comment, PlanningDocument Document)? LatestCandidate(
        IReadOnlyList<BeadCommentSnapshot> comments)
    {
        var candidate = comments.Select(c => (Comment: c, Document: Parse(c.Text)))
            .Where(c => c.Document?.Schema == CandidateSchema)
            .OrderBy(c => c.Comment.CreatedAt).LastOrDefault();
        return candidate.Document is null ? null : (candidate.Comment, candidate.Document);
    }

    internal static string? ValidateCandidate(PlanningDocument plan)
    {
        if (plan.Tasks is not { Length: > 0 } || plan.Coverage is not { Length: > 0 } || plan.Constraints is null)
            return "Plan requires executable tasks, Product coverage and separately sourced constraints (possibly empty).";
        if (plan.Tasks.Any(t => t is null || !Text(t.Id, t.Title, t.Outcome, t.Context, t.Approach, t.Verification)
            || t.AcceptanceCriteria is not { Length: > 0 } || t.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace)
            || t.DependsOn is null)) return "Each task requires outcome, context, approach, observable acceptance criteria, verification and dependencies.";
        if (plan.Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != plan.Tasks.Length)
            return "Task identities must be unique.";
        var tasks = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        if (tasks.Count != plan.Tasks.Length) return "Task identities must be unique.";
        if (plan.Constraints.Any(c => c is null || !Text(c.Requirement, c.Source))) return "Hard constraints require authoritative sources.";
        if (plan.Coverage.Any(c => c is null || !Text(c.Requirement) || c.TaskIds is null
            || c.TaskIds.Any(id => !tasks.ContainsKey(id))
            || c.TaskIds.Length == 0 && !Text(c.SatisfiedEvidence) && !Text(c.AuthorizedExclusion)))
            return "Every coverage entry requires planned tasks, existing satisfaction evidence or Product-authorized exclusion.";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (!tasks.TryGetValue(id, out var task) || active.Contains(id)) return false;
            if (visited.Contains(id)) return true;
            active.Add(id);
            foreach (var dep in task.DependsOn) if (!Visit(dep)) return false;
            active.Remove(id); visited.Add(id); return true;
        }
        return tasks.Keys.All(Visit) ? null : "Task dependencies must reference known tasks and form an acyclic graph.";
    }

    internal static (string? Error, PlanningApproval? Approval) Verify(
        AgentStageResult invocation, AgentOutcomeChoice outcome, PlanningGrounding current,
        IReadOnlyList<BeadCommentSnapshot> comments)
    {
        if (invocation.PlanningGrounding != current) return ("Grounding changed during the turn; targeted revalidation is required.", null);
        var fresh = comments.Where(c => invocation.PreInvocationCommentIds is not null
            && !invocation.PreInvocationCommentIds.Contains(c.Id)).ToArray();
        if (fresh.Length == 0) return ("A current-invocation Planning evidence comment is required.", null);
        var documents = fresh.Select(c => (Comment: c, Document: Parse(c.Text)))
            .Where(c => c.Document is not null).ToArray();
        if (outcome == AgentOutcomeChoice.ProductDecisionRequired)
        {
            var question = documents.LastOrDefault(c => c.Document!.Schema == QuestionSchema).Document;
            return question?.Grounding == current && question.Questions is { Length: > 0 }
                && question.Questions.All(q => q is not null && Text(q.Question, q.RecommendedAnswer, q.Tradeoffs))
                ? (null, null) : ("A product pause requires concrete questions, recommendations and tradeoffs at current grounding.", null);
        }
        var candidate = LatestCandidate(comments);
        if (candidate is null) return ("No recoverable candidate plan was found.", null);
        var (planComment, plan) = candidate.Value;
        var invalid = ValidateCandidate(plan);
        if (invalid is not null) return (invalid, null);
        if (invocation.Role == AgentRole.Planner)
            return outcome == AgentOutcomeChoice.ReadyForReview && fresh.Any(c => c.Id == planComment.Id)
                && plan.Grounding == current ? (null, null) : ("Planner must submit a fresh grounded candidate for challenge.", null);
        if (invocation.PreInvocationCommentIds?.Contains(planComment.Id) != true)
            return ("Challenger must review an existing candidate rather than authoring its own replacement.", null);
        var review = documents.LastOrDefault(c => c.Document!.Schema == ReviewSchema);
        if (review.Document is not { } assessment || assessment.Grounding != current
            || assessment.PlanCommentId != planComment.Id || assessment.PlanRevision != Hash(planComment.Text)
            || !Text(assessment.Assessment)) return ("Challenger evidence must identify and assess the exact latest plan revision at current grounding.", null);
        if (plan.Grounding != current && !Text(assessment.Revalidation))
            return ("Changed grounding requires targeted revalidation of affected portions and preservation of unaffected work.", null);
        if (outcome == AgentOutcomeChoice.ChangesRequired)
            return assessment.Findings is { Length: > 0 } && assessment.Findings.All(f => f is not null && Text(f.Violation, f.Evidence, f.Direction))
                ? (null, null) : ("Repair requires evidenced outcome/contract violations and direction, including disproven assumptions.", null);
        if (outcome != AgentOutcomeChoice.Approved || assessment.Findings is { Length: > 0 }
            || assessment.Questions is { Length: > 0 }) return ("Approval must have no unresolved findings or product questions.", null);
        return (null, new(planComment.Id, Hash(planComment.Text), review.Comment.Id, Hash(review.Comment.Text), current));
    }

    internal static bool ApprovalStillCurrent(PlanningApproval approval, PlanningGrounding current,
        IReadOnlyList<BeadCommentSnapshot> comments)
    {
        var latest = LatestCandidate(comments);
        return approval.Grounding == current && latest?.Comment.Id == approval.PlanCommentId
            && Hash(latest.Value.Comment.Text) == approval.PlanRevision
            && comments.Any(c => c.Id == approval.ReviewCommentId && Hash(c.Text) == approval.ReviewRevision);
    }

    private static bool Text(params string?[] values) => values.All(v => !string.IsNullOrWhiteSpace(v));
}
