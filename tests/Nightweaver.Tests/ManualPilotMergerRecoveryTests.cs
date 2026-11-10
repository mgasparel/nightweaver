using System.Text.Json;
using System.Text.Json.Nodes;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

public sealed class ManualPilotMergerRecoveryTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Branch = "nightweaver/qa-owned";

    [Fact]
    public void FreshMatchingMergedEvidenceIsAccepted()
    {
        Assert.Equal(Head, ManualPilotMergerRecovery.VerifyMerged(PullRequest(), Checks(), Head, Branch, "main"));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("url")]
    [InlineData("headRefOid")]
    [InlineData("headRefName")]
    [InlineData("baseRefName")]
    [InlineData("repository")]
    [InlineData("mergeCommit")]
    public void MismatchedEvidenceIsRejected(string field)
    {
        var data = JsonNode.Parse(PullRequest().GetRawText())!.AsObject();
        if (field == "repository") data["baseRepository"]!["nameWithOwner"] = "other/repository";
        else if (field == "mergeCommit") data["mergeCommit"]!["oid"] = new string('2', 40);
        else data[field] = "mismatched";
        Assert.Throws<InvalidOperationException>(() => ManualPilotMergerRecovery.VerifyMerged(
            JsonSerializer.SerializeToElement(data), Checks(), Head, Branch, "main"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"state\":\"FAILURE\",\"bucket\":\"fail\"}]")]
    [InlineData("[{\"state\":\"PENDING\",\"bucket\":\"pending\"}]")]
    [InlineData("{}")]
    public void MissingOrNonpassingChecksAreRejected(string checks)
    {
        using var document = JsonDocument.Parse(checks);
        Assert.Throws<InvalidOperationException>(() => ManualPilotMergerRecovery.VerifyMerged(
            PullRequest(), document.RootElement, Head, Branch, "main"));
    }

    private static JsonElement Checks() => JsonSerializer.SerializeToElement(new[] { new { state = "SUCCESS", bucket = "pass" } });

    private static JsonElement PullRequest() => JsonSerializer.SerializeToElement(new
    {
        state = "MERGED", url = "https://github.com/nightweaver-fixture/target/pull/123",
        headRefOid = Head, headRefName = Branch, baseRefName = "main",
        baseRepository = new { nameWithOwner = "nightweaver-fixture/target", url = "https://github.com/nightweaver-fixture/target" },
        mergeCommit = new { oid = Head }
    });
}
