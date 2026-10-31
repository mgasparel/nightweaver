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
            var choice = evidence.Contains("Implementer product decision:", StringComparison.OrdinalIgnoreCase)
                ? "product_decision_required" : "ready_for_review";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    answers = new[] { new { name = AgentOutcomeClassifier.QuestionName, type = "choice", choice } }
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

internal sealed class DelegateImplementerOutcomePublisher(
    Func<AgentStageSession, AgentStageResult, AgentOutcomeClassificationResult, bool, CancellationToken,
        Task<AgentStageResult>> publish) : IImplementerOutcomePublisher
{
    public Task<AgentStageResult> PublishAsync(AgentStageSession session, AgentStageResult invocation,
        AgentOutcomeClassificationResult classification, bool releaseClaim, CancellationToken cancellationToken) =>
        publish(session, invocation, classification, releaseClaim, cancellationToken);
}

internal sealed class FixedImplementerOutcomeVerifier(
    string? mutationFailure = null, string? readyFailure = null) : IImplementerOutcomeVerifier
{
    public Task<string?> VerifyNoClassifierMutationAsync(Nightweaver.Inspection.ValidatedTargetRepository repository,
        string worktreePath, AgentStageResult invocation, string expectedAssignee, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(mutationFailure);
    }

    public Task<string?> VerifyReadyForReviewAsync(Nightweaver.Inspection.ValidatedTargetRepository repository,
        string worktreePath, AgentStageResult invocation, string expectedAssignee, string defaultBranch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(readyFailure);
    }
}
