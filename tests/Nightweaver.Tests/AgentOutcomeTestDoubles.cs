using System.Net;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

internal static class FixtureOutcomeClassifier
{
    internal static AgentOutcomeClassifier Create()
    {
        var client = new HttpClient(new EvidenceDrivenDecisionsHandler(), disposeHandler: true);
        return new AgentOutcomeClassifier(client, apiKeyOverride: "fixture-key");
    }

    private sealed class EvidenceDrivenDecisionsHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var evidence = body.RootElement.GetProperty("input").GetString() ?? string.Empty;
            using var document = JsonDocument.Parse(evidence);
            var role = document.RootElement.GetProperty("role").GetString();
            var sources = document.RootElement.GetProperty("sources").ToString();
            var choice = sources.Contains("product decision:", StringComparison.OrdinalIgnoreCase)
                ? "product_decision_required"
                : role switch
                {
                    "implementer" => "ready_for_review",
                    "reviewer" when sources.Contains("blocking finding:", StringComparison.OrdinalIgnoreCase) => "changes_required",
                    "reviewer" => "approved",
                    "qa" when sources.Contains("qa failure:", StringComparison.OrdinalIgnoreCase) => "changes_required",
                    "qa" => "approved",
                "merger" when sources.Contains("nightweaver-merger-rejection-v1", StringComparison.Ordinal) => "changes_required",
                "merger" when sources.Contains("nightweaver-merge-v1", StringComparison.Ordinal) => "approved",
                "merger" => "incomplete_or_unclear",
                    _ => throw new InvalidOperationException($"Unexpected outcome role '{role}'.")
                };
            var question = role switch
            {
                "implementer" => AgentOutcomeClassifier.QuestionName,
                "reviewer" => "reviewer_outcome",
                "qa" => "qa_outcome",
                "merger" => "merger_outcome",
                _ => throw new InvalidOperationException($"Unexpected outcome role '{role}'.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    answers = new[] { new { name = question, type = "choice", choice } }
                }), Encoding.UTF8, "application/json")
            };
        }
    }
}

internal sealed class FixedAgentOutcomeClassifier(AgentOutcomeChoice? outcome, string diagnostic = "fixture outcome")
    : IAgentOutcomeClassifier
{
    public Task<AgentOutcomeClassificationResult> ClassifyAsync(
        Nightweaver.Configuration.HostPaths hostPaths,
        Nightweaver.Configuration.ProjectConfig project,
        AgentDispatchContext context,
        string worktreePath,
        AgentStageResult invocation,
        IReadOnlyList<Nightweaver.Inspection.BeadCommentSnapshot> comments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentOutcomeClassificationResult(outcome, "fixture", diagnostic));
    }
}

internal sealed class DelegateDeliveryOutcomePublisher(
    Func<AgentStageSession, AgentStageResult, AgentOutcomeClassificationResult, bool, CancellationToken,
        Task<AgentStageResult>> publish) : IDeliveryOutcomePublisher
{
    public Task<AgentStageResult> PublishAsync(AgentStageSession session, AgentStageResult invocation,
        AgentOutcomeClassificationResult classification, bool releaseClaim, CancellationToken cancellationToken) =>
        publish(session, invocation, classification, releaseClaim, cancellationToken);
}

internal sealed class FixedDeliveryOutcomeVerifier(
    string? mutationFailure = null, string? readyFailure = null) : IDeliveryOutcomeVerifier
{
    public Task<string?> VerifyNoClassifierMutationAsync(Nightweaver.Inspection.ValidatedTargetRepository repository,
        string worktreePath, AgentStageResult invocation, string expectedAssignee, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(mutationFailure);
    }

    public Task<string?> VerifyApprovalAsync(Nightweaver.Inspection.ValidatedTargetRepository repository,
        string worktreePath, AgentStageResult invocation, string expectedAssignee, string defaultBranch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(readyFailure);
    }
}
