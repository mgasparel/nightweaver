using System.Text.Json;

internal static class RelationsQaFixture
{
    internal static async Task SeedAsync(Func<string[], Task<string>> run)
    {
        async Task<string> Create(string title, string type)
        {
            using var result = JsonDocument.Parse(await run(["create", "--title", title, "--type", type, "--json"]));
            return result.RootElement.GetProperty("id").GetString()!;
        }
        var epic = await Create("QA relations epic", "epic");
        var parent = "";
        for (var index = 0; index < 17; index++)
        {
            var child = await Create($"QA relation child {index:00} <script>literal</script>", index % 3 == 0 ? "bug" : index % 3 == 1 ? "feature" : "task");
            await run(["dep", "add", child, epic, "--type", "parent-child"]);
            if (index == 0) parent = child;
        }
        var nested = await Create("QA relation nested child", "spike");
        await run(["dep", "add", nested, parent, "--type", "parent-child"]);
        var origin = await Create("QA relation origin", "feature");
        await run(["dep", "add", epic, origin, "--type", "discovered-from"]);
        var discovered = await Create("QA relation discovered work", "task");
        await run(["dep", "add", discovered, epic, "--type", "discovered-from"]);
        var blocker = await Create("QA relation blocker", "bug");
        await run(["dep", "add", epic, blocker]);
        var blocked = await Create("QA relation blocked", "chore");
        await run(["dep", "add", blocked, epic]);
        await Create("QA relation standalone", "decision");
    }
}
