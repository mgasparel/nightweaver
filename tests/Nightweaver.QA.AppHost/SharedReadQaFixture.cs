using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;

namespace Nightweaver.QA;

internal static class SharedReadQaFixture
{
    private static readonly string[] PauseLabels = ["product-decision-required", "stage:implement"];
    private static readonly string[] Requested = ["summary", "history", "relations", "environment"];
    private static readonly string[] Missing = ["Matching verified merge reports", "Product lineage", "2,000-row workload"];
    internal static async Task SeedAsync(string root, Func<string[], Task<string>> run)
    {
        // The runner freezes timestamps and bytes across fresh hosts. A later
        // candidate can reuse the same timestamp while the window remains valid.
        var timestamp = Environment.GetEnvironmentVariable("NIGHTWEAVER_QA_FIXTURE_TIMESTAMP")
            ?? throw new InvalidOperationException("Supply the fixture timestamp from the baseline runner.");
        _ = DateTimeOffset.ParseExact(timestamp, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        var rows = Enumerable.Range(0, 50).Select(index => new
        {
            id = $"qa-read-{index:D3}",
            title = $"Shared read fixture {index:D3}", issue_type = "task",
            status = index >= 34 ? "closed" : "open", priority = 2,
            description = "Shared read specification. " + new string('x', 512),
            created_at = timestamp, updated_at = timestamp,
            closed_at = index >= 34 ? timestamp : (string?)null,
            labels = index < 8 ? PauseLabels : [],
            comments = index < 8 ? Enumerable.Range(0, 8).Select(comment => new
            {
                id = index * 8 + comment + 1, author = "qa",
                text = $"Shared read history {index:D3}/{comment}. Which direction must the fixture use?",
                created_at = timestamp
            }).ToArray() : []
        });
        var seed = Path.Combine(root, "shared-read.jsonl");
        await File.WriteAllLinesAsync(seed, rows.Select(row => JsonSerializer.Serialize(row)));
        await run(["import", seed]);
        await run(["dep", "add", "qa-read-009", "qa-read-010"]);
        await File.WriteAllTextAsync(Path.Combine(root, "shared-read-manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "nightweaver-shared-read-fixture-v1", inventory = 50, paused = 8, closed = 16,
            selected = "qa-read-000", timestamp, seedSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(seed))),
            seedBytes = new FileInfo(seed).Length,
            requested = Requested, missing = Missing
        }));
    }
}
