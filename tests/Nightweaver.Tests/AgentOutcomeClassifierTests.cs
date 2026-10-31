using System.Net;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class AgentOutcomeClassifierTests
{
    [Fact]
    public async Task PrimaryDecisionsRequestContainsOneBoundedNamedChoiceQuestionAndDoesNotFallbackOnValidUnclear()
    {
        using var fixture = new OutcomeFixture();
        var primaryCalls = 0;
        var fallbackCalls = 0;
        using var client = new HttpClient(new DelegateHttpMessageHandler(async request =>
        {
            primaryCalls++;
            Assert.Equal("https://api.openai.com/v1/decisions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
            var input = root.GetProperty("input").GetString();
            Assert.NotNull(input);
            Assert.Contains("invocation-log:", input, StringComparison.Ordinal);
            Assert.Contains("bead-comment:comment-1", input, StringComparison.Ordinal);
            var questions = root.GetProperty("questions");
            Assert.Equal(1, questions.GetArrayLength());
            var question = questions[0];
            Assert.Equal("choice", question.GetProperty("type").GetString());
            Assert.Equal(AgentOutcomeClassifier.QuestionName, question.GetProperty("name").GetString());
            Assert.Equal(4, question.GetProperty("choices").GetArrayLength());
            return JsonResponse("incomplete_or_unclear");
        }), disposeHandler: true);

        var classifier = new AgentOutcomeClassifier(client, executeFallback: (_, _, _, _, _) =>
        {
            fallbackCalls++;
            throw new InvalidOperationException("Valid primary outcomes must not launch fallback.");
        }, apiKeyOverride: "fixture-key");

        var result = await fixture.ClassifyAsync(classifier);

        Assert.Equal(AgentOutcomeChoice.IncompleteOrUnclear, result.Outcome);
        Assert.Equal("openai-decisions", result.Provider);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public async Task InvalidPrimaryUsesExactlyOneFallbackWithTheSameNamedQuestionChoicesAndEvidence()
    {
        using var fixture = new OutcomeFixture();
        var primaryCalls = 0;
        var fallbackCalls = 0;
        string? primaryEvidence = null;
        string? fallbackPrompt = null;
        using var client = new HttpClient(new DelegateHttpMessageHandler(async request =>
        {
            primaryCalls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            primaryEvidence = body.RootElement.GetProperty("input").GetString();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("offline")
            };
        }), disposeHandler: true);

        var classifier = new AgentOutcomeClassifier(client,
            executeFallback: (_, _, invocation, maximumBytes, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                fallbackCalls++;
                Assert.NotEmpty(invocation.Arguments);
                Assert.Equal(AgentOutcomeClassifier.MaximumFallbackOutputBytes, maximumBytes);
                Assert.EndsWith("-outcome-classifier", invocation.InvocationId, StringComparison.Ordinal);
                fallbackPrompt = invocation.StandardInput;
                var answer = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = AgentOutcomeClassifier.Schema,
                    question = AgentOutcomeClassifier.QuestionName,
                    choice = "ready_for_review"
                });
                var execution = new AgentExecutionResult(AgentExecutionStatus.Exited, 0,
                    invocation.InvocationId, invocation.LogFilePath);
                return Task.FromResult(new AgentStandardOutputCaptureResult(execution, answer, false));
            }, apiKeyOverride: "fixture-key");

        var result = await fixture.ClassifyAsync(classifier);

        Assert.Equal(AgentOutcomeChoice.ReadyForReview, result.Outcome);
        Assert.Equal("standard-agent-cli", result.Provider);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(1, fallbackCalls);
        Assert.NotNull(primaryEvidence);
        Assert.Contains(AgentOutcomeClassifier.QuestionName, fallbackPrompt, StringComparison.Ordinal);
        Assert.Contains("ready_for_review", fallbackPrompt, StringComparison.Ordinal);
        Assert.Contains("product_decision_required", fallbackPrompt, StringComparison.Ordinal);
        Assert.Contains("technical_blocker", fallbackPrompt, StringComparison.Ordinal);
        Assert.Contains("incomplete_or_unclear", fallbackPrompt, StringComparison.Ordinal);
        Assert.Contains(primaryEvidence, fallbackPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidFallbackIsReportedAfterOneAttemptAndNeverRepeatsTheLifecycleInvocation()
    {
        using var fixture = new OutcomeFixture();
        using var client = new HttpClient(new DelegateHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway))), disposeHandler: true);
        var fallbackCalls = 0;
        var classifier = new AgentOutcomeClassifier(client,
            executeFallback: (_, _, invocation, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                fallbackCalls++;
                return Task.FromResult(new AgentStandardOutputCaptureResult(
                    new AgentExecutionResult(AgentExecutionStatus.Exited, 0, invocation.InvocationId, invocation.LogFilePath),
                    Encoding.UTF8.GetBytes("not json"), false));
            }, apiKeyOverride: "fixture-key");

        var result = await fixture.ClassifyAsync(classifier);

        Assert.Null(result.Outcome);
        Assert.Equal(1, fallbackCalls);
    }

    private static HttpResponseMessage JsonResponse(string choice) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            answers = new[] { new { name = AgentOutcomeClassifier.QuestionName, type = "choice", choice, confidence = 0.9 } }
        }), Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class OutcomeFixture : IDisposable
    {
        private const string BeadId = "ns-outcome.1";
        private const string Owner = "nightweaver:demo";
        private readonly TempDirectory temp = new();

        internal OutcomeFixture()
        {
            Paths = new HostPaths(temp.CreateDirectory("host"));
            Worktree = Directory.CreateDirectory(Path.Combine(Paths.WorktreesRoot(ProjectSlug.Parse("demo")), BeadId)).FullName;
            var roles = AgentRoles.All.ToDictionary(role => role,
                _ => new AgentSettings(AgentCli.Claude, "fixture", "low", 1));
            Project = new(ProjectSlug.Parse("demo"), "fixture.json", new("target", "main"),
                new(1, 1, 1), new(3, 1), new(AgentCli.Claude, "fixture", "low", 1), new RoleSettings(roles));
            Context = new AgentDispatchContext(BeadId, "task", "stage:implement", 0);
            var invocationId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture)
                + "-implementer";
            var logDirectory = Directory.CreateDirectory(Path.Combine(Paths.LogsRoot(Project.Name), BeadId)).FullName;
            var logPath = Path.Combine(logDirectory, invocationId + ".log");
            File.WriteAllText(logPath, "Implementation completed. Checks passed. Branch and PR handoff recorded.");
            var state = new BeadWorkflowState(BeadId, "task", "in_progress", "stage:implement", false, false, false, 0);
            var observations = new AgentStageObservations(state, state, Owner, Owner, AgentExecutionStatus.Exited,
                0, $"nightweaver/{BeadId}", new string('a', 40), "", true);
            Invocation = new AgentStageResult(AgentRole.Implementer, BeadId, invocationId, logPath, null, [],
                Observations: observations);
        }

        internal HostPaths Paths { get; }
        internal ProjectConfig Project { get; }
        internal string Worktree { get; }
        internal AgentDispatchContext Context { get; }
        internal AgentStageResult Invocation { get; }
        internal Task<AgentOutcomeClassificationResult> ClassifyAsync(AgentOutcomeClassifier classifier) =>
            classifier.ClassifyAsync(Paths, Project, Context, Worktree, Invocation,
                [new BeadCommentSnapshot("comment-1", "Implementation checks passed.", DateTimeOffset.UtcNow)],
                TestContext.Current.CancellationToken);

        public void Dispose() => temp.Dispose();
    }
}
