using System.Text.Json;

internal static class SchemaMessageQaFixture
{
    private static readonly string[] TaskAcceptance = ["Readable task details", "Exact original retained"];
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
        foreach (var role in new[] { "implementer", "reviewer", "qa", "merger" })
            await Add(primary, new { schema = role == "merger" ? "nightweaver-merger-outcome-v1" : "nightweaver-delivery-outcome-v1",
                bead_id = primary, role, outcome = role == "implementer" ? "ReadyForReview" : "ChangesRequired", provider = "openai-decisions",
                invocation_id = "mixed-" + role, classification_diagnostic = "Prior reported outcome; subsequent rejection and pause are separate.", invocation_log = "H:/logs/mixed.log", extra = new { preserved = true } });
        foreach (var action in new[] { "note_only", "resume" })
            await Add(primary, new { schema = "nightweaver-human-guidance-v1", action, actor = "local-console", instruction = "Operator correction: retain evidence and inspect the actual state." });
        foreach (var action in new[] { "retry", "abort" })
            await Add(primary, new { schema = "nightweaver-human-action-v1", action, actor = "local-console" });
        await run(["comments", "add", primary, "Corrective prose: the recorded Resume request did not clear this pause."]);
        var hash = new string('a',40);
        await run(["comments", "add", primary, $"nightweaver-merge-v1 pull_request=https://example.invalid/pr/42 checked_head={hash} required_checks=passed merge_method=squash merge_commit={hash}"]);
        using var mergeComments = JsonDocument.Parse(await run(["comments", primary, "--json"]));
        var mergeSource = mergeComments.RootElement.EnumerateArray().Last().GetProperty("id").ToString();
        await run(["comments", "add", primary, $"nightweaver-verified-merge-v1 pull_request=https://example.invalid/pr/42 checked_head={hash} merge_commit={hash} required_checks=passed source_comment={mergeSource}"]);
        await run(["comments", "add", primary, "nightweaver-finalization-v1 Verified merge closure completed; cleanup=failed: retained locked report; Product completion=safely ineligible."]);
        await Add(primary, new { schema = "nightweaver-merger-outcome-v1", question = "merger_outcome", choice = "approved" });
        await run(["comments", "add", primary, "nightweaver-merge-v1 malformed prefix record"]);
        var grounding = new { checkoutRevision = hash, defaultRevision = hash, productInputs = new string('c',64) };
        await Add(question, new { schema = "nightweaver-plan-v1", grounding,
            tasks = Enumerable.Range(1,40).Select(i => new { id = "task-" + i, title = "Readable task " + i, outcome = "Observable task outcome", context = "Existing console context", approach = "Advisory implementation approach",
                acceptanceCriteria = TaskAcceptance, verification = "Run focused verification", dependsOn = i == 1 ? Array.Empty<string>() : ["task-1"] }),
            constraints = new[] { new { requirement = "Read-only presentation", source = "Product requirement" } },
            coverage = Enumerable.Range(1,40).Select(i => new { requirement = "Requirement " + i, taskIds = new[] { "task-" + i }, satisfiedEvidence = (string?)null, authorizedExclusion = (string?)null }) });
        using var planComments = JsonDocument.Parse(await run(["comments", question, "--json"]));
        var planSource = planComments.RootElement.EnumerateArray().Last().GetProperty("id").ToString();
        await Add(question, new { schema = "nightweaver-plan-review-v1", grounding, planCommentId = planSource, planRevision = new string('d',64), assessment = "Changes required: requirement coverage needs evidence.",
            revalidation = "Preserve unaffected tasks", findings = new[] { new { violation = "Coverage gap", evidence = "Requirement 4", direction = "Add evidence" } },
            questions = new[] { new { question = "Which policy?", recommendedAnswer = "Keep policy", tradeoffs = "Storage cost" } } });
        await Add(question, new { schema = "nightweaver-planning-outcome-v1", bead_id = question, role = "plan_challenger", outcome = "Approved", provider = "standard-agent-cli", invocation_id = "planning-turn",
            classification_diagnostic = "Host-recorded exact candidate assessment", invocation_log = "https://example.invalid/planning-log",
            planning_approval = new { plan_comment_id = planSource, plan_revision = new string('d',64), review_comment_id = "unresolved-review", review_revision = new string('e',64),
                grounding = new { checkout_revision = hash, default_revision = hash, product_inputs = new string('c',64), observed_remote_revision = (string?)null } } });
        foreach (var schema in new[] { "nightweaver-plan-v2", "nightweaver-planner-outcome-v1", "nightweaver-plan-challenger-outcome-v1" })
            await Add(question, new { schema, choice = "approved" });
        await Add(question, new { schema = "nightweaver-plan-v1", tasks = false });
    }
}
