using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class DigestMergeEvidenceTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Pr = "https://github.com/owner/repo/pull/42";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-18T15:00:00Z");

    internal static BeadsCommentSnapshot[] Comments(string id) =>
    [
        new("source", id, "agent", $"nightweaver-merge-v1 pull_request={Pr} checked_head={Head} required_checks=passed merge_method=squash merge_commit={Commit}", Now),
        new("host", id, "host", MergerCompletionFinalizer.CreateEvidenceComment(new(Pr, Head, Commit, "squash", "source")), Now.AddSeconds(1))
    ];

    [Fact]
    public void ProducerRecordWithEmptyCloseReasonProjectsMergedAndExactReferencesDespiteCleanupFailure()
    {
        var bead = Bead();
        var comments = Comments(bead.Id).Append(new BeadsCommentSnapshot("cleanup", bead.Id, "host",
            "nightweaver-finalization-v1 Verified merge closure completed; cleanup=failed: locked report; Product completion=safely ineligible.", Now.AddSeconds(2))).ToArray();
        var evidence = DigestMergeEvidence.Read(bead, comments);
        var item = Project(bead, evidence);
        Assert.Equal("merged", item.Outcome);
        Assert.Equal("Merged", item.OutcomeLabel);
        Assert.Equal(Commit, Assert.Single(item.Commits));
        Assert.Equal(Pr, Assert.Single(item.PullRequests));
        Assert.Equal($"https://github.com/owner/repo/commit/{Commit}", evidence.CommitUrl);
        Assert.Equal("locked report", evidence.CleanupWarning);
        Assert.DoesNotContain("locked", item.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agent-only")]
    [InlineData("missing-source")]
    [InlineData("foreign-host")]
    [InlineData("foreign-source")]
    [InlineData("malformed")]
    [InlineData("mismatched")]
    [InlineData("conflicting")]
    [InlineData("duplicate-source")]
    [InlineData("credentials")]
    [InlineData("missing")]
    public void UnverifiedOrAmbiguousHistoryCannotPromoteOrdinaryShaNotes(string scenario)
    {
        var bead = Bead();
        var comments = Comments(bead.Id).ToList();
        switch (scenario)
        {
            case "agent-only": comments.RemoveAt(1); break;
            case "missing-source": comments.RemoveAt(0); break;
            case "foreign-host": comments[1] = comments[1] with { IssueId = "other" }; break;
            case "foreign-source": comments[0] = comments[0] with { IssueId = "other" }; break;
            case "malformed": comments[1] = comments[1] with { Text = comments[1].Text + " extra=unknown" }; break;
            case "mismatched": comments[1] = comments[1] with { Text = comments[1].Text.Replace(Commit, Head, StringComparison.Ordinal) }; break;
            case "conflicting":
                comments.Add(comments[0] with { Id = "other-source", Text = comments[0].Text.Replace(Commit, Head, StringComparison.Ordinal) });
                comments.Add(comments[1] with { Id = "other-host", Text = comments[1].Text.Replace(Commit, Head, StringComparison.Ordinal).Replace("=source", "=other-source", StringComparison.Ordinal) });
                break;
            case "duplicate-source": comments.Add(comments[0]); break;
            case "credentials": comments[1] = comments[1] with { Text = comments[1].Text.Replace("https://github.com", "https://user:secret@github.com", StringComparison.Ordinal) }; break;
            case "missing": comments.Clear(); break;
        }
        var evidence = DigestMergeEvidence.Read(bead, comments);
        Assert.NotEqual("verified", evidence.State);
        Assert.Null(evidence.PullRequest);
        Assert.Equal("Resolved", Project(bead, evidence).OutcomeLabel);
    }

    [Theory]
    [InlineData("duplicate; no fix claimed", "no_op", false)]
    [InlineData("aborted", "aborted", true)]
    [InlineData("Documentation resolved", "resolved", false)]
    public void NonCodeClosuresRetainTheirMeaning(string reason, string outcome, bool paused)
    {
        var bead = Bead(reason, "", paused);
        Assert.Equal(outcome, Project(bead, DigestMergeEvidence.Read(bead, [])).Outcome);
    }

    private static CatchUpDigestItem Project(BeadSnapshot bead, DigestMergeEvidence evidence) =>
        Assert.Single(CatchUpDigestProjector.Project(new BeadsInventory([bead]), "all", null, Now.AddMinutes(1), 3,
            new Dictionary<string, DigestMergeEvidence> { [bead.Id] = evidence }).StandaloneCompleted);

    private static BeadSnapshot Bead(string reason = "", string notes = "Ordinary note mentions commit abc1234 and PR #7", bool paused = false) =>
        new("task-1", "closed", "task", 1, Now, null, paused ? ["needs-human"] : [],
            JsonSerializer.SerializeToElement(new { title = "Delivered task", close_reason = reason, notes, closed_at = Now, updated_at = Now }));
}
