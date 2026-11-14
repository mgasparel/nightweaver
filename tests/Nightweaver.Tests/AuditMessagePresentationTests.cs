using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class AuditMessagePresentationTests
{
    private static readonly BeadSnapshot Bead = BeadsReader.Parse("""[{"id":"demo-1","title":"Audit","status":"open","issue_type":"task","priority":2,"labels":["needs-human","stage:implement"],"created_at":"2026-11-13T00:00:00Z","updated_at":"2026-11-13T00:00:00Z"}]""").ById["demo-1"];
    private static CommentPresentation Project(string text) => CommentPresenter.Project(new("source", "demo-1", "host", text, DateTimeOffset.UnixEpoch), Bead);
    private static readonly PlanningGrounding Grounding = new(new('a', 40), new('b', 40), new('c', 64));
    private static readonly JsonSerializerOptions PublicationOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static string Serialize(object value) => JsonSerializer.Serialize(value, PlanningEvidence.JsonOptions);

    public static TheoryData<string, string> Outcomes => new()
    {
        { "implementer", "ReadyForReview" }, { "implementer", "ProductDecisionRequired" },
        { "reviewer", "Approved" }, { "reviewer", "ChangesRequired" }, { "reviewer", "ProductDecisionRequired" },
        { "qa", "Approved" }, { "qa", "ChangesRequired" }, { "qa", "ProductDecisionRequired" },
        { "merger", "Approved" }, { "merger", "ChangesRequired" }, { "merger", "ProductDecisionRequired" },
        { "planner", "ReadyForReview" }, { "planner", "ProductDecisionRequired" },
        { "plan_challenger", "Approved" }, { "plan_challenger", "ChangesRequired" }, { "plan_challenger", "ProductDecisionRequired" }
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void PublicationVariantsRemainAttributedAndNeverAssertCurrentState(string role, string outcome)
    {
        var schema = role is "planner" or "plan_challenger" ? "nightweaver-planning-outcome-v1"
            : role == "merger" ? "nightweaver-merger-outcome-v1" : "nightweaver-delivery-outcome-v1";
        var text = Serialize(new { schema, bead_id = "demo-1", role, outcome, invocation_id = "invocation-1", provider = "openai-decisions",
            classification_diagnostic = "Classifier reason", invocation_log = "H:/logs/invocation.log", extra = new { untouched = true } });
        var message = Project(text);
        Assert.Equal("supported", message.State);
        Assert.Equal(role, message.Role);
        Assert.DoesNotContain(outcome, message.Summary);
        Assert.Contains(message.Sections, s => s.Label == "Classification reason" && s.Text == "Classifier reason");
        Assert.Contains(message.Sections, s => s.Label == "Evidence status" && s.Text.Contains("not established", StringComparison.Ordinal));
        Assert.Equal(text, message.Original);
        foreach (var invalid in new[]
        {
            text.Replace("\"provider\":\"openai-decisions\"", "\"provider\":false", StringComparison.Ordinal),
            text.Replace($"\"outcome\":\"{outcome}\"", "\"outcome\":\"FutureOutcome\"", StringComparison.Ordinal),
            text.Replace($"\"role\":\"{role}\"", "\"role\":\"invented\"", StringComparison.Ordinal),
            text.Replace("\"bead_id\":\"demo-1\"", "\"bead_id\":\"foreign\"", StringComparison.Ordinal)
        }) Assert.Equal("fallback", Project(invalid).State);
    }

    [Theory]
    [InlineData("note_only", "Human note")]
    [InlineData("resume", "Human resume request")]
    [InlineData("retry", "Human retry request")]
    [InlineData("abort", "Human abort request")]
    public void RealAuditProducersDescribeIntentEvenWhenUpdateFails(string action, string title)
    {
        var text = action is "retry" or "abort" ? BeadsWriter.CreateHumanActionComment(action)
            : BeadsWriter.CreateHumanGuidanceComment(action, "Keep this instruction visible.");
        var message = Project(text);
        Assert.Equal(title, message.Title);
        Assert.Contains(message.Sections, s => s.Label == "Actor" && s.Text == "local-console");
        Assert.Contains(message.Sections, s => s.Label == "Request status" && s.Text.Contains("may have failed", StringComparison.Ordinal));
        var comment = new BeadsCommentSnapshot("audit", "demo-1", "operator", text, DateTimeOffset.UnixEpoch);
        Assert.Equal(action is "retry" or "abort" ? "human-action" : "human-guidance",
            AttentionDeskProjector.ClassifyComment(comment, Bead).Kind);
        var selected = AttentionDeskProjector.ProjectWithSelection(Bead, [comment], 3);
        Assert.True(selected.Item.CanRetry); // Audit text never removes the persisted pause.
        Assert.Equal(text, message.Original);
    }

    [Fact]
    public void ProducerCandidateKeepsAllNestedDataInBoundedDetails()
    {
        var tasks = Enumerable.Range(1, 80).Select(i => new PlanningTask("task-" + i, "Task " + i, "Observable outcome", "Context", "Advisory approach", ["Observable AC"], "Run verification", i == 1 ? [] : ["task-1"])).ToArray();
        var text = Serialize(new PlanningDocument("nightweaver-plan-v1", Grounding, tasks,
            [new("Hard requirement", "Product requirement")], [new("Requirement", ["task-1"], null, null)],
            Questions: [new("Which policy?", "Keep policy", "More storage")]));
        var message = Project(text);
        Assert.Equal("supported", message.State);
        Assert.Contains("80 tasks", message.Summary);
        Assert.All(message.Sections.Where(s => s.Label != "Evidence status"), s => Assert.True(s.Details));
        Assert.Contains(message.Sections, s => s.Text.Contains("Observable AC", StringComparison.Ordinal) && s.Text.Contains("Advisory approach", StringComparison.Ordinal));
        Assert.Contains(message.Sections, s => s.Label == "Constraint" && s.Text.Contains("Product requirement", StringComparison.Ordinal));
        Assert.Equal(text, message.Original);
        Assert.Contains(message.Sections, s => s.Label == "Tradeoffs" && s.Text == "More storage");
        Assert.Equal("fallback", Project(text.Replace("\"dependsOn\":[]", "\"dependsOn\":false", StringComparison.Ordinal)).State);
    }

    [Fact]
    public void ReviewAssessmentAndHostApprovalRemainSeparateWithExactSourceIdentity()
    {
        var review = Serialize(new PlanningDocument("nightweaver-plan-review-v1", Grounding, PlanCommentId: "candidate", PlanRevision: new('d',64),
            Assessment: "Changes required", Revalidation: "Preserve unaffected tasks", Findings: [new("Coverage missing", "Requirement 4", "Add coverage")],
            Questions: [new("Which policy?", "Keep policy", "More storage")]));
        var message = Project(review);
        Assert.Equal("Planning review assessment", message.Title);
        Assert.Contains(message.Sections, s => s.Label == "Review finding" && s.Text.Contains("Add coverage", StringComparison.Ordinal));
        Assert.Contains(message.Sections, s => s.Label == "Tradeoffs" && s.Text == "More storage");
        var approval = JsonSerializer.Serialize(new { schema = "nightweaver-planning-outcome-v1", bead_id = "demo-1", role = "plan_challenger",
            outcome = "Approved", provider = "standard-agent-cli", invocation_id = "turn", invocation_log = "H:/logs/turn.log", classification_diagnostic = "Exact candidate assessed",
            planning_approval = new PlanningApproval("candidate", new('d',64), "review", new('e',64), Grounding) }, PublicationOptions);
        var host = Project(approval);
        Assert.Equal("supported", host.State);
        Assert.Contains(host.Sections, s => s.Label == "Host-recorded planning approval");
        Assert.Contains(host.Sections, s => s.CommentId == "candidate");
        Assert.Contains(host.Sections, s => s.CommentId == "review");
        Assert.Contains(host.Sections, s => s.Label == "Approval grounding" && s.Text.Contains(new string('a',40), StringComparison.Ordinal));
        Assert.Equal(approval, host.Original);
        Assert.Equal("fallback", Project(approval.Replace("\"outcome\":\"Approved\"", "\"outcome\":\"ChangesRequired\"", StringComparison.Ordinal)).State);
        // The real evidence record permits absent/null findings and questions for approval.
        var approvedReview = Serialize(new PlanningDocument("nightweaver-plan-review-v1", Grounding,
            PlanCommentId: "candidate", PlanRevision: new('d',64), Assessment: "Approved"));
        Assert.Equal("supported", Project(approvedReview).State);
        Assert.Equal("fallback", Project(approvedReview.Replace("\"findings\":null", "\"findings\":false", StringComparison.Ordinal)).State);
    }

    [Theory]
    [InlineData("nightweaver-merge-v1", "Agent-reported merge")]
    [InlineData("nightweaver-verified-merge-v1", "Host-recorded verified merge")]
    public void ExactMergeTextHasAttributedChecksAndInspectableReferences(string schema, string title)
    {
        var head = new string('a',40);
        var text = schema == "nightweaver-merge-v1"
            ? $"{schema} pull_request=https://example.invalid/pr/1 checked_head={head} required_checks=passed merge_method=squash merge_commit={head}"
            : $"{schema} pull_request=https://example.invalid/pr/1 checked_head={head} merge_commit={head} required_checks=passed source_comment=report";
        var message = Project(text);
        Assert.Equal(title, message.Title);
        Assert.Contains(message.Sections, s => s.Label == "Reported checks" && s.Text.Contains("according to", StringComparison.Ordinal));
        Assert.Equal(text, message.Original);
        foreach (var invalid in new[] { text + " extra=unknown", text.Replace(head, "invalid", StringComparison.Ordinal), text.Replace("=passed", "=failed", StringComparison.Ordinal) })
            Assert.Equal("fallback", Project(invalid).State);
    }

    [Fact]
    public void FinalizationDiagnosticRemainsLiteralAndSeparate()
    {
        const string text = "nightweaver-finalization-v1 Verified merge closure completed; cleanup=failed: locked file; Product completion=safely ineligible.";
        var message = Project(text);
        Assert.Equal("Post-merge finalization diagnostic", message.Title);
        Assert.Contains("cleanup=failed: locked file", message.Summary);
        Assert.Equal(text, message.Original);
    }

    [Theory]
    [InlineData("{\"schema\":\"nightweaver-plan-v1\"}")]
    [InlineData("{\"schema\":\"nightweaver-plan-v2\"}")]
    [InlineData("{\"schema\":\"nightweaver-plan-review-v1\",\"assessment\":false}")]
    [InlineData("{\"schema\":\"nightweaver-human-guidance-v1\",\"action\":\"resume\",\"instruction\":\"Go\",\"actor\":false}")]
    [InlineData("{\"schema\":\"nightweaver-human-action-v1\",\"action\":\"resume\",\"actor\":\"operator\"}")]
    [InlineData("{\"schema\":\"nightweaver-merger-outcome-v1\",\"question\":\"merger_outcome\",\"choice\":\"approved\"}")]
    [InlineData("{\"schema\":\"nightweaver-planner-outcome-v1\",\"choice\":\"approved\"}")]
    [InlineData("{\"schema\":\"nightweaver-plan-challenger-outcome-v1\",\"choice\":\"approved\"}")]
    [InlineData("{\"schema\":\"nightweaver-reviewer-evidence-v1\"}")]
    [InlineData("nightweaver-verified-merge-v1 malformed")]
    [InlineData("nightweaver-finalization-v1 future shape")]
    public void InvalidAndClassifierOnlyRecordsStayExplicitFallbacks(string text)
    {
        var result = Project(text);
        Assert.Equal("fallback", result.State);
        Assert.Equal(text, result.Original);
        Assert.Contains("uncertain", result.Summary);
    }
}
