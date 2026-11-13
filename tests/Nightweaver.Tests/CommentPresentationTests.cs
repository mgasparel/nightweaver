using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class CommentPresentationTests
{
    private static BeadSnapshot Bead(bool question = false) => BeadsReader.Parse(JsonSerializer.Serialize(new[] { new
    {
        id = "demo-1", title = "Paused work", status = "open", issue_type = question ? "feature" : "task", priority = 2,
        created_at = "2026-11-13T00:00:00Z", updated_at = "2026-11-13T00:00:00Z",
        labels = new[] { question ? "product-decision-required" : "needs-human", question ? "stage:plan" : "stage:merge" }
    } })).ById["demo-1"];
    private static BeadsCommentSnapshot Comment(string text, string id = "source") => new(id, "demo-1", "host", text, DateTimeOffset.UnixEpoch);
    private static string Diagnostic(string retention = "environment_retention") => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["schema"] = "nightweaver-needs-human-v1", ["bead_id"] = "demo-1", ["bead_type"] = "task",
        ["stage"] = "stage:merge", ["failed_role"] = "merger", ["cause"] = "delivery-outcome-failure",
        ["reason"] = "The invocation log did not verify the reported check failure.",
        ["latest_findings"] = "Reported test rejection; subsequent corrective prose remains independent.",
        [retention] = "Retained for inspection", ["invocation_id"] = "invocation-1", ["log_directory"] = "H:/deep/logs",
        ["log_files"] = (string[])["H:/deep/logs/check.log"], ["extra"] = new { nested = (string[])["untouched"] }
    });

    [Theory]
    [InlineData("environment_retention", "Environment retention")]
    [InlineData("cleanup_preflight", "Historical cleanup warning")]
    public void CurrentAndHistoricalEscalationsKeepDistinctFieldsAndExactOriginal(string field, string label)
    {
        var text = Diagnostic(field);
        var result = CommentPresenter.Project(Comment(text), Bead());
        Assert.Equal("supported", result.State);
        Assert.Equal("merger", result.Role);
        Assert.Contains("did not verify", result.Summary);
        Assert.Contains(result.Sections, s => s.Label == label && s.Text == "Retained for inspection");
        Assert.Contains(result.Sections, s => s.Label == "Latest findings");
        Assert.Equal(text, result.Original);
    }

    [Theory]
    [InlineData("conflict", null)]
    [InlineData("design", null)]
    [InlineData("checks", "local")]
    [InlineData("checks", "remote")]
    public void RejectionsAreReportedRatherThanNewlyVerified(string kind, string? origin)
    {
        var text = JsonSerializer.Serialize(new { schema = "nightweaver-merger-rejection-v1", kind,
            subject = "dotnet test", summary = "The retained test run failed.", details = "One failing test; see original evidence.",
            pull_request = "https://example.invalid/pr/1", checked_head = new string('a', 40), check_source = origin, local_exit_code = 1 });
        var result = CommentPresenter.Project(Comment(text), Bead());
        Assert.Equal("supported", result.State);
        Assert.Contains("Reported", result.Title);
        Assert.Contains(result.Sections, s => s.Label == "Evidence status" && s.Text.Contains("does not verify", StringComparison.Ordinal));
        if (origin is not null) Assert.Contains(result.Sections, s => s.Label == "Check origin" && s.Text == origin);
    }

    [Theory]
    [InlineData("nightweaver-needs-human-pause-failure-v1")]
    [InlineData("nightweaver-product-decision-pause-failure-v1")]
    [InlineData("nightweaver-needs-human-cleanup-failure-v1")]
    public void SupplementalFailuresRemainTheirOwnEvents(string schema)
    {
        var result = CommentPresenter.Project(Comment(JsonSerializer.Serialize(new { schema, bead_id = "demo-1", failure = "Claim release failed" })), Bead());
        Assert.Equal("supported", result.State);
        Assert.Equal("Claim release failed", result.Summary);
        Assert.Null(result.Role);
    }

    [Theory]
    [InlineData("{\"schema\":\"nightweaver-needs-human-v1\",")]
    [InlineData("{\"schema\":\"nightweaver-needs-human-v2\"}")]
    [InlineData("{\"schema\":\"nightweaver-qa-outcome-v1\",\"choice\":\"approved\"}")]
    [InlineData("{\"schema\":\"nightweaver-merger-outcome-v1\",\"question\":\"x\",\"choice\":\"y\"}")]
    [InlineData("{\"schema\":\"nightweaver-needs-human-v1\",\"schema\":\"nightweaver-needs-human-v1\"}")]
    [InlineData("Example before {\"schema\":\"nightweaver-needs-human-v1\"} after")]
    public void UnsupportedAndMalformedContractsAreInspectable(string text)
    {
        var result = CommentPresenter.Project(Comment(text), Bead());
        Assert.Equal("fallback", result.State);
        Assert.Equal(text, result.Original);
        Assert.Contains("uncertain", result.Summary);
    }

    [Theory]
    [InlineData("demo-1", "other")]
    [InlineData("merger", "invented-role")]
    [InlineData("delivery-outcome-failure", "invented-cause")]
    [InlineData("\"log_files\":[", "\"log_files\":false,\"unused\":[")]
    public void WrongIdentityEnumsAndTypesFailConservatively(string from, string to)
        => Assert.Equal("fallback", CommentPresenter.Project(Comment(Diagnostic().Replace(from, to, StringComparison.Ordinal)), Bead()).State);

    [Fact]
    public void DeepExtraDataFailsWithoutAffectingOrdinaryContent()
    {
        var deep = Diagnostic().TrimEnd('}') + ",\"deep\":" + new string('[', 30) + "0" + new string(']', 30) + "}";
        Assert.Equal("fallback", CommentPresenter.Project(Comment(deep), Bead()).State);
        foreach (var text in new[] { "Corrective note: scenario name is unverified.", "{\"example\": [1,2]}", "```json\n{\"example\":true}\n```" })
            Assert.Equal("ordinary", CommentPresenter.Project(Comment(text), Bead()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerUnknownDiagnosticWinsOverOlderSupportedEvenWithOrdinaryNotes(bool note)
    {
        var comments = new List<BeadsCommentSnapshot> { Comment(Diagnostic(), "older") };
        if (note) comments.Add(Comment("Ordinary human note", "note"));
        comments.Add(Comment("{\"schema\":\"nightweaver-needs-human-v2\"}", "newer"));
        if (note) comments.Add(Comment("Another note", "later-note"));
        var selected = AttentionDeskProjector.ProjectWithSelection(Bead(), comments, 3);
        Assert.Equal("newer", selected.ActiveCommentId);
        Assert.Equal("fallback", selected.Item.Message!.State);
        Assert.True(selected.Item.CanRetry);
        var workspace = BeadWorkspaceProjector.Project(Bead(), comments, selected, BeadWorkspaceProjector.UnavailableEnvironment("unavailable"));
        Assert.Equal(selected.Item.Message, workspace.Comments.Single(c => c.Id == "newer").Message);
        Assert.Contains("older", workspace.History.OlderCommentIds);
    }

    [Fact]
    public void ProducerShapedPlanningQuestionSurvivesLaterHumanNotes()
    {
        var text = JsonSerializer.Serialize(new { schema = "nightweaver-plan-question-v1",
            grounding = new { checkoutRevision = new string('a',40), defaultRevision = new string('b',40), productInputs = new string('c',64) },
            questions = new[] { new { question = "Which policy should apply?", recommendedAnswer = "Keep existing policy", tradeoffs = "A wider policy needs new scope" } } });
        var comments = new[] { Comment(text), Comment("An ordinary later note", "note") };
        var selection = AttentionDeskProjector.ProjectWithSelection(Bead(true), comments, 3);
        Assert.Equal("source", selection.ActiveCommentId);
        Assert.Equal("structured", selection.SelectionSource);
        Assert.Equal((string[])["Question", "Recommended answer", "Tradeoffs"], selection.Item.Message!.Sections.Select(s => s.Label));
        Assert.True(selection.Item.CanDecide);
        var invalid = Comment(text.Replace("recommendedAnswer", "unknown", StringComparison.Ordinal), "invalid");
        Assert.Equal("fallback", CommentPresenter.Project(invalid, Bead(true)).State);
        Assert.Equal("invalid", AttentionDeskProjector.ProjectWithSelection(Bead(true), [.. comments, invalid], 3).ActiveCommentId);
    }

    [Theory]
    [InlineData("operational-retries-exhausted")]
    [InlineData("duplicate-failure")]
    [InlineData("repair-budget-exhausted")]
    [InlineData("environment-blocked")]
    [InlineData("delivery-outcome-failure")]
    public void EveryProducerCauseRemainsReadable(string cause)
    {
        var message = CommentPresenter.Project(Comment(Diagnostic().Replace("delivery-outcome-failure", cause, StringComparison.Ordinal)), Bead());
        Assert.Equal("supported", message.State);
        Assert.Contains(message.Sections, s => s.Label == "Cause" && s.Text == cause.Replace('-', ' '));
    }

    [Fact]
    public void NestedFindingsAndOrdinaryJsonCannotBecomePauseEvents()
    {
        var text = Diagnostic();
        var result = CommentPresenter.Project(Comment(text), Bead());
        Assert.Equal("source", result.SourceId);
        Assert.False(CommentPresenter.IsPauseCandidate("A note mentioning nightweaver-needs-human-v1", false));
        Assert.False(CommentPresenter.IsPauseCandidate("{\"example\":{\"schema\":\"nightweaver-needs-human-v1\"}}", false));
        Assert.Equal("fallback", CommentPresenter.Project(Comment(text) with { IssueId = "foreign" }, Bead()).State);
        Assert.Equal("fallback", CommentPresenter.Project(Comment(text) with { Id = "" }, Bead()).State);
        Assert.Equal("ordinary", CommentPresenter.Project(Comment("[1,2,3]"), Bead()).State);
    }

    [Fact]
    public void SupplementalWarningDoesNotReplacePrimaryDiagnosticOrInventAnAssociation()
    {
        var supplemental = Comment("{\"schema\":\"nightweaver-needs-human-pause-failure-v1\",\"bead_id\":\"demo-1\",\"failure\":\"Claim release failed\"}", "supplemental");
        var selected = AttentionDeskProjector.ProjectWithSelection(Bead(), [Comment(Diagnostic(), "primary"), supplemental], 3);
        Assert.Equal("primary", selected.ActiveCommentId);
        Assert.Contains("did not verify", selected.Item.Message!.Summary);
        Assert.Equal("supplemental", Assert.Single(selected.Item.OtherPauseReports).SourceId);
        Assert.True(selected.Item.CanRetry);
    }

    [Fact]
    public void SourceFromAnotherStageIsQualifiedWithoutChangingActionGuards()
    {
        var selection = AttentionDeskProjector.ProjectWithSelection(Bead(), [Comment(Diagnostic().Replace("stage:merge", "stage:review", StringComparison.Ordinal))], 3);
        Assert.Contains("may not explain the current pause", selection.Item.ContextNotice);
        Assert.True(selection.Item.CanRetry);
        Assert.True(selection.Item.CanAbort);
    }
}
