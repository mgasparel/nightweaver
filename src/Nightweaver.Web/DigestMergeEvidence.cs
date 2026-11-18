using Nightweaver.Execution;
using Nightweaver.Inspection;
using System.Text.Json.Serialization;

namespace Nightweaver.Web;

// A compact presentation of the existing host protocol, never new workflow authority.
internal sealed record DigestMergeEvidence(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("pullRequest")] string? PullRequest = null,
    [property: JsonPropertyName("mergeCommit")] string? MergeCommit = null)
{
    [JsonPropertyName("cleanupWarning")] public string? CleanupWarning { get; init; }
    [JsonPropertyName("commitUrl")]
    public string? CommitUrl => PullRequest is { } pr && MergeCommit is { } commit
        && GitHubRepositoryIdentity.ParsePullRequestUrl(pr) is { } identity
        ? $"https://{identity.Repository.CliRepository}/commit/{commit}" : null;
    internal static DigestMergeEvidence Read(BeadSnapshot bead, IReadOnlyList<BeadsCommentSnapshot> comments)
    {
        if (bead.Status != "closed" || bead.IssueType != "task") return new("not-applicable");
        var records = comments.Where(c => c.Text.StartsWith("nightweaver-verified-merge-v1", StringComparison.Ordinal)).ToArray();
        if (records.Length == 0) return new("missing");
        var verified = new List<(string Pr, string Head, string Commit)>();
        foreach (var comment in records)
        {
            var fields = Parse(comment, bead);
            if (fields is null) return new("invalid");
            var sources = comments.Where(c => c.Id == fields["source_comment"]).ToArray();
            if (sources.Length != 1 || sources[0].IssueId != bead.Id || sources[0].CreatedAt > comment.CreatedAt
                || MergerOutcomeVerifier.ParseMergeEvidence(sources[0].Text) is not { } source
                || source.PullRequest != fields["pull_request"] || source.CheckedHead != fields["checked_head"]
                || source.MergeCommit != fields["merge_commit"]) return new("invalid");
            verified.Add((source.PullRequest, source.CheckedHead, source.MergeCommit));
        }
        var identities = verified.Distinct().ToArray();
        if (identities.Length != 1) return new("conflicting");
        const string cleanupStart = "nightweaver-finalization-v1 Verified merge closure completed; cleanup=failed: ";
        var cleanup = comments.Where(c => c.IssueId == bead.Id && c.Text.StartsWith(cleanupStart, StringComparison.Ordinal)
                && c.Text.Contains("; Product completion=", StringComparison.Ordinal))
            .OrderByDescending(c => c.CreatedAt).FirstOrDefault();
        var warning = cleanup?.Text[cleanupStart.Length..].Split("; Product completion=", 2)[0];
        return new("verified", identities[0].Pr, identities[0].Commit)
        { CleanupWarning = warning?[..Math.Min(600, warning.Length)] };
    }

    internal static Dictionary<string, string>? Parse(BeadsCommentSnapshot comment, BeadSnapshot bead)
    {
        if (comment.IssueId != bead.Id || string.IsNullOrWhiteSpace(comment.Id)) return null;
        var fields = comment.Text.Split(' ');
        string[] keys = ["pull_request", "checked_head", "merge_commit", "required_checks", "source_comment"];
        if (fields.Length != 6 || fields[0] != "nightweaver-verified-merge-v1") return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Length; i++)
        {
            if (!fields[i + 1].StartsWith(keys[i] + "=", StringComparison.Ordinal)) return null;
            var value = fields[i + 1][(keys[i].Length + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return null;
            values.Add(keys[i], value);
        }
        if (values["required_checks"] != "passed" || !Hash(values["checked_head"]) || !Hash(values["merge_commit"])
            || !Uri.TryCreate(values["pull_request"], UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || values["pull_request"].Contains('\\') || values["source_comment"] == comment.Id) return null;
        return values;
    }

    private static bool Hash(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
}
