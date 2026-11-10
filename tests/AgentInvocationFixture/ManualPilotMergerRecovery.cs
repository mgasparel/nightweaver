using System.Text.Json;

namespace Nightweaver.Tests.AgentInvocationFixture;

public static class ManualPilotMergerRecovery
{
    public static string VerifyMerged(JsonElement pullRequest, JsonElement requiredChecks,
        string expectedHead, string expectedBranch, string defaultBranch)
    {
        pullRequest.TryGetProperty("baseRepository", out var repository);
        pullRequest.TryGetProperty("mergeCommit", out var merge);
        if (Field(pullRequest, "state") != "MERGED"
            || Field(pullRequest, "url") != "https://github.com/nightweaver-fixture/target/pull/123"
            || Field(pullRequest, "headRefOid") != expectedHead
            || Field(pullRequest, "headRefName") != expectedBranch
            || Field(pullRequest, "baseRefName") != defaultBranch
            || Field(repository, "nameWithOwner") != "nightweaver-fixture/target"
            || Field(repository, "url") != "https://github.com/nightweaver-fixture/target"
            || Field(merge, "oid") != expectedHead)
            throw new InvalidOperationException("Already-merged fixture PR does not match the exact owned expected commit and repository.");

        if (requiredChecks.ValueKind != JsonValueKind.Array || requiredChecks.GetArrayLength() == 0
            || requiredChecks.EnumerateArray().Any(check => Field(check, "state") != "SUCCESS" || Field(check, "bucket") != "pass"))
            throw new InvalidOperationException("Already-merged fixture PR does not have fresh passing required checks.");
        return expectedHead;
    }

    private static string? Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
