using System.Text.Json;

internal static class SchemaMessageQaFixture
{
    internal static async Task SeedAsync(Func<string[], Task<string>> run)
    {
        async Task<string> Create(string title, bool question = false)
        {
            var result = await run(["create", "--title", title, "--type", question ? "feature" : "task", "--priority", "0", "--labels",
                question ? "product-decision-required,stage:plan" : "needs-human,stage:merge", "--json"]);
            using var json = JsonDocument.Parse(result);
            return json.RootElement.GetProperty("id").GetString()!;
        }
        async Task Add(string id, object body) => await run(["comments", "add", id, JsonSerializer.Serialize(body)]);
        var primary = await Create("Schema messages: verification pause");
        foreach (var (kind, origin) in new[] { ("conflict", "remote"), ("design", "remote"), ("checks", "remote"), ("checks", "local") })
            await Add(primary, new { schema = "nightweaver-merger-rejection-v1", kind, check_source = origin, local_exit_code = 1,
                subject = "dotnet test", summary = $"Reported {origin} {kind} failure", details = "Reported failing check; scenario name is not verified as a root cause.",
                pull_request = origin == "local" ? "https://github.com/mgasparel/nightweaver/pull/28" : "javascript:window.__schemaInjected=true", checked_head = new string('a', 40) });
        await Add(primary, new { schema = "nightweaver-needs-human-v1", bead_id = primary, bead_type = "task", stage = "stage:merge",
            cause = "repair-budget-exhausted", failed_role = "merger", reason = "Historical budget exhaustion", latest_findings = "Historical findings",
            cleanup_preflight = "Historical locked directory warning" });
        foreach (var schema in new[] { "nightweaver-needs-human-pause-failure-v1", "nightweaver-product-decision-pause-failure-v1", "nightweaver-needs-human-cleanup-failure-v1" })
            await Add(primary, new { schema, bead_id = primary, failure = "Separate supplemental failure: " + schema });
        await run(["comments", "add", primary, "Corrective prose: the scenario name is unverified. <img src='https://example.invalid/hostile' onerror='window.__schemaInjected=true'>"]);
        await Add(primary, new { schema = "nightweaver-needs-human-v1", bead_id = primary, bead_type = "task", stage = "stage:merge",
            cause = "delivery-outcome-failure", failed_role = "merger", reason = "Verification could not establish the reported test result.",
            latest_findings = "Reported failure remains separate from verification.\n" + new string('x', 2500) + "\n{\"schema\":\"nightweaver-needs-human-v1\",\"reason\":\"nested text is not an event\"}",
            invocation_id = "schema-invocation", log_directory = "H:/" + string.Join('/', Enumerable.Repeat("long-evidence-directory", 20)),
            log_files = (string[])["H:/retained/check.log"], environment_retention = "Environment retained; cleanup is deferred.",
            additional_diagnostic = new { unknown = (string[])["Preserved extra diagnostic data"] } });
        var question = await Create("Schema messages: product question", true);
        await Add(question, new { schema = "nightweaver-plan-question-v1", grounding = new { checkoutRevision = new string('a', 40), defaultRevision = new string('b', 40), productInputs = new string('c', 64) },
            questions = new[] { new { question = "Which retention policy should apply?", recommendedAnswer = "Keep retained evidence", tradeoffs = "More disk space, but inspectable failures" } } });
        await run(["comments", "add", question, "A later ordinary note must not displace this question."]);
        var uncertainQuestion = await Create("Schema messages: uncertain product question", true);
        await Add(uncertainQuestion, new { schema = "nightweaver-plan-question-v2", questions = new[] { new { question = "Unsupported question" } } });
        await run(["comments", "add", uncertainQuestion, "{\"schema\":\"nightweaver-plan-question-v1\",\"questions\":false}"]);
        await run(["comments", "add", uncertainQuestion, "Later ordinary note preserves uncertain source"]);
        var fallback = await Create("Schema messages: uncertain current explanation");
        await Add(fallback, new { schema = "nightweaver-needs-human-v1", bead_id = fallback, bead_type = "task", stage = "stage:merge", cause = "duplicate-failure", failed_role = "merger",
            reason = "Older supported explanation", latest_findings = "Older evidence", environment_retention = "retained" });
        await run(["comments", "add", fallback, "Interposed human note"]);
        await Add(fallback, new { schema = "nightweaver-needs-human-v99", reason = "New unsupported diagnostic" });
        await run(["comments", "add", fallback, "Later human note"]);
        var malformed = await Create("Schema messages: malformed source");
        await run(["comments", "add", malformed, "{\"schema\":\"nightweaver-needs-human-v1\",\"reason\":"]);
        await Add(malformed, new { schema = "nightweaver-needs-human-v1", bead_id = "foreign", reason = "Foreign identity" });
        await run(["comments", "add", malformed, "Mixed prose {\"schema\":\"nightweaver-needs-human-v1\"} preserved exactly"]);
        foreach (var isQuestion in new[] { false, true })
        {
            var duplicate = await Create("Schema messages: duplicate " + (isQuestion ? "question" : "diagnostic"), isQuestion);
            var schema = isQuestion ? "nightweaver-plan-question-v1" : "nightweaver-needs-human-v1";
            if (isQuestion)
                await Add(duplicate, new { schema, grounding = new { checkoutRevision = "checkout", defaultRevision = "main", productInputs = "inputs" },
                    questions = new[] { new { question = "Older supported question", recommendedAnswer = "Keep policy", tradeoffs = "Disk space" } } });
            else
                await Add(duplicate, new { schema, bead_id = duplicate, bead_type = "task", stage = "stage:merge", cause = "duplicate-failure", failed_role = "merger",
                    reason = "Older supported diagnostic", environment_retention = "retained" });
            await run(["comments", "add", duplicate, "Interposed ordinary note"]);
            await run(["comments", "add", duplicate, $"{{\"schema\":\"{schema}\",\"schema\":false}}"]);
            await run(["comments", "add", duplicate, "Later ordinary note"]);
        }
    }
}
