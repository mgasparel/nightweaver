using System.Text.Json;

internal static class BeadPresentationQaFixture
{
    internal static async Task SeedAsync(Func<string[], Task<string>> run)
    {
        async Task<string> Create(string name, string type = "task", string labels = "", string metadata = "{}")
        {
            var result = await run(["create", "--title", "Workflow: " + name, "--description", "Description", "--acceptance", "Acceptance",
                "--type", type, "--labels", labels, "--metadata", metadata, "--json"]);
            using var json = JsonDocument.Parse(result);
            return json.RootElement.GetProperty("id").GetString()!;
        }
        await Create("manual Plan");
        var product = await Create("backlog Product", "feature");
        var linked = await Create("manual linked Plan");
        await run(["dep", "add", linked, product, "--type", "discovered-from"]);
        await Create("authorized Product", "bug", "ready-for-planning,stage:plan");
        await Create("historical technical attention", "feature", "planned,needs-human");
        await Create("conflicting pauses", "task", "needs-human,product-decision-required");
        await Create("unknown stage", "task", "stage:unknown");
        await Create("incompatible stage", "feature", "stage:merge");
        await Create("tracker epic", "epic");
        await Create("invalid repair budget", "task", "needs-human,stage:qa", "{\"repair_cycles\":\"unavailable\"}");
        var claimed = await Create("claimed stored work", "task", "stage:review", "{\"repair_cycles\":2}");
        await run(["update", claimed, "--status", "in_progress", "--assignee", "fixture-owner"]);
        await Create("long literal content", "task", "stage:qa,<img-onerror=hostile>," + new string('x', 250));
        foreach (var scenario in new[] { "published", "held", "conflict", "partial", "partial revision conflict" })
        {
            var partial = scenario.StartsWith("partial", StringComparison.Ordinal);
            var parent = await Create(scenario + " parent", "feature", partial ? "stage:plan-approved" : "planned");
            var metadata = JsonSerializer.Serialize(new { publication_product = parent, publication_revision = "fixture-revision" });
            var task = await Create(scenario + " graph", labels: scenario == "held" || partial ? "plan-unpublished" : "", metadata: metadata);
            await run(["dep", "add", task, parent, "--type", "discovered-from"]);
            await run(["update", parent, "--set-metadata", "publication_revision=fixture-revision"]);
            if (!partial)
            {
                var graph = JsonSerializer.Serialize(new[] { new { id = task, title = "Workflow: " + scenario + " graph",
                    description = "Description", acceptanceCriteria = "Acceptance", dependencies = Array.Empty<string>() } });
                await run(["update", parent, "--set-metadata", "publication_graph=" + graph]);
            }
            if (scenario == "conflict") await run(["update", task, "--set-metadata", "publication_product=missing"]);
            if (scenario == "partial revision conflict") await run(["update", task, "--set-metadata", "publication_revision=old"]);
        }
    }
}
