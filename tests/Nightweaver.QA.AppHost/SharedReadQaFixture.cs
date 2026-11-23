using System.Security.Cryptography;
using System.Text.Json;

namespace Nightweaver.QA;

internal static class SharedReadQaFixture
{
    private static readonly string[] PauseLabels = ["product-decision-required", "stage:implement"];
    private static readonly string[] Requested = ["summary", "history", "relations", "environment"];
    private static readonly string[] Missing = ["Matching verified merge reports", "Product lineage", "2,000-row workload"];
    internal static async Task SeedAsync(string root, Func<string[], Task<string>> run)
    {
        // Freeze bytes and IDs across fresh hosts. The fixed timestamp is within
        // the all-activity window; 24h trials must report their own window limits.
        var rows = Enumerable.Range(0, 50).Select(index => new
        {
            id = $"qa-read-{index:D3}",
            title = $"Shared read fixture {index:D3}", issue_type = "task",
            status = index >= 34 ? "closed" : "open", priority = 2,
            description = "Shared read specification. " + new string('x', 512),
            created_at = "2026-11-23T00:00:00Z", updated_at = "2026-11-23T00:00:00Z",
            closed_at = index >= 34 ? "2026-11-23T00:00:00Z" : (string?)null,
            labels = index < 8 ? PauseLabels : [],
            comments = index < 8 ? Enumerable.Range(0, 8).Select(comment => new
            {
                id = index * 8 + comment + 1, author = "qa",
                text = $"Shared read history {index:D3}/{comment}. Which direction must the fixture use?",
                created_at = "2026-11-23T00:00:00Z"
            }).ToArray() : []
        });
        var seed = Path.Combine(root, "shared-read.jsonl");
        await File.WriteAllLinesAsync(seed, rows.Select(row => JsonSerializer.Serialize(row)));
        await run(["import", seed]);
        await run(["dep", "add", "qa-read-009", "qa-read-010"]);
        await File.WriteAllTextAsync(Path.Combine(root, "shared-read-manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "nightweaver-shared-read-fixture-v1", inventory = 50, paused = 8, closed = 16,
            selected = "qa-read-000", seedSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(seed))),
            seedBytes = new FileInfo(seed).Length,
            requested = Requested, missing = Missing
        }));
    }
}
