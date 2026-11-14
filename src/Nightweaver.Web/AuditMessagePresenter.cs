using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

/// <summary>Presentation only: recorded assessments, requests and approvals never establish current authority.</summary>
internal static class AuditMessagePresenter
{
    private const string Recorded = "Recorded evidence only; current workflow state and approval freshness are not established by this message.";

    internal static CommentPresentation? Json(JsonElement root, BeadsCommentSnapshot comment, BeadSnapshot bead)
    {
        var schema = Read(root, "schema");
        var sections = new List<MessageSection>();
        void Add(string label, string text, bool details = false, string? source = null) => sections.Add(new(label, text, details, source));
        CommentPresentation Result(string title, string summary, string? role = null, string? kind = null)
            => new("supported", title, summary, role, null, sections, comment.Text, comment.Id) { Kind = kind };
        switch (schema)
        {
            case "nightweaver-human-guidance-v1":
            case "nightweaver-human-action-v1":
                var action = Read(root, "action");
                var guidance = schema == "nightweaver-human-guidance-v1";
                if (guidance ? action is not ("note_only" or "resume") : action is not ("retry" or "abort"))
                    throw new JsonException("Unknown audit action.");
                Add("Actor", Read(root, "actor"));
                Add("Requested action", action switch { "note_only" => "Save note", "resume" => "Resume", "retry" => "Reset budget and retry", _ => "Abort and close" });
                Add("Request status", "This records intent only. The accompanying state update may have failed or be uncertain; inspect current state.");
                return Result(action switch { "note_only" => "Human note", "resume" => "Human resume request", "retry" => "Human retry request", _ => "Human abort request" },
                    guidance ? Read(root, "instruction") : "A human action was requested.", kind: guidance ? "human-guidance" : "human-action");
            case "nightweaver-delivery-outcome-v1":
            case "nightweaver-merger-outcome-v1":
            case "nightweaver-planning-outcome-v1":
                // The merger schema is also a classifier contract. Never promote classifier choices.
                if (root.TryGetProperty("choice", out _) || root.TryGetProperty("question", out _))
                    return Unsupported(comment, schema);
                if (Read(root, "bead_id") != bead.Id) throw new JsonException("Foreign publication.");
                var role = Read(root, "role");
                if (schema == "nightweaver-delivery-outcome-v1" ? role is not ("implementer" or "reviewer" or "qa")
                    : schema == "nightweaver-merger-outcome-v1" ? role != "merger" : role is not ("planner" or "plan_challenger"))
                    throw new JsonException("Invalid publication role.");
                var outcome = Read(root, "outcome");
                var description = outcome switch
                {
                    "ReadyForReview" when role is "implementer" or "planner" => "Ready for review",
                    "Approved" when role is "reviewer" => "Review approved",
                    "Approved" when role is "qa" => "QA checks approved",
                    "Approved" when role is "merger" => "Merge reported approved",
                    "Approved" when role is "plan_challenger" => "Plan assessment approved",
                    "ChangesRequired" when role is not ("implementer" or "planner") => "Changes required",
                    "ProductDecisionRequired" => "Human product decision required",
                    "TechnicalBlocker" => "Technical blocker reported",
                    "IncompleteOrUnclear" => "Outcome incomplete or unclear",
                    _ => throw new JsonException("Unknown role outcome.")
                };
                Add("Reported outcome", description);
                Add("Classification reason", Read(root, "classification_diagnostic"));
                Add("Classification provider", Read(root, "provider"), true);
                Add("Invocation", Read(root, "invocation_id"), true);
                Add("Invocation log", Read(root, "invocation_log"), true);
                Add("Evidence status", Recorded);
                if (root.TryGetProperty("planning_approval", out var approval) && approval.ValueKind != JsonValueKind.Null)
                {
                    if (schema != "nightweaver-planning-outcome-v1" || role != "plan_challenger" || outcome != "Approved")
                        throw new JsonException("Unexpected approval.");
                    Add("Host-recorded planning approval", "Exact candidate and review identity recorded by the host; viewing does not validate freshness.");
                    Add("Candidate comment", Read(approval, "plan_comment_id"), true, Read(approval, "plan_comment_id"));
                    Add("Candidate revision", Read(approval, "plan_revision"), true);
                    Add("Review comment", Read(approval, "review_comment_id"), true, Read(approval, "review_comment_id"));
                    Add("Review revision", Read(approval, "review_revision"), true);
                    Add("Approval grounding", Grounding(approval.GetProperty("grounding"), true), true);
                }
                return Result(schema == "nightweaver-planning-outcome-v1" ? "Host-recorded Planning outcome" : "Host-recorded delivery outcome", description, role);
            case "nightweaver-plan-v1":
                Add("Evidence status", Recorded);
                Add("Grounding", Grounding(root.GetProperty("grounding")), true);
                var tasks = Array(root, "tasks", true);
                foreach (var task in tasks)
                {
                    var id = Read(task, "id");
                    var title = Read(task, "title");
                    var outcomeText = Read(task, "outcome");
                    var context = Read(task, "context");
                    var approach = Read(task, "approach");
                    var acceptance = Strings(task, "acceptanceCriteria", true);
                    var verification = Read(task, "verification");
                    var dependencies = Strings(task, "dependsOn");
                    Add($"Task {id}: {title}", $"Outcome: {outcomeText}\n\nContext: {context}\n\nApproach: {approach}\n\nAcceptance criteria:\n{acceptance}\n\nVerification: {verification}\n\nDependencies: {dependencies}", true);
                }
                foreach (var constraint in Array(root, "constraints"))
                    Add("Constraint", Read(constraint, "requirement") + "\nSource: " + Read(constraint, "source"), true);
                foreach (var coverage in Array(root, "coverage", true))
                    Add("Requirement coverage", Read(coverage, "requirement") + "\nTasks: " + Strings(coverage, "taskIds")
                        + "\nSatisfied evidence: " + Nullable(coverage, "satisfiedEvidence")
                        + "\nAuthorized exclusion: " + Nullable(coverage, "authorizedExclusion"), true);
                AddQuestions(root, sections);
                return Result("Planning candidate", $"Candidate proposes {tasks.Length} tasks. Inspect task outcomes, constraints and requirement coverage.", "planner");
            case "nightweaver-plan-review-v1":
                var assessment = Read(root, "assessment");
                Add("Evidence status", "Challenger-recorded assessment. " + Recorded);
                Add("Candidate comment", Read(root, "planCommentId"), true, Read(root, "planCommentId"));
                Add("Candidate revision", Read(root, "planRevision"), true);
                Add("Grounding", Grounding(root.GetProperty("grounding")), true);
                Add("Revalidation", Nullable(root, "revalidation"), true);
                foreach (var finding in OptionalArray(root, "findings"))
                    Add("Review finding", "Violation: " + Read(finding, "violation") + "\nEvidence: " + Read(finding, "evidence") + "\nDirection: " + Read(finding, "direction"), true);
                AddQuestions(root, sections);
                return Result("Planning review assessment", assessment, "plan_challenger");
            default: return null;
        }
    }

    internal static CommentPresentation? Text(BeadsCommentSnapshot comment, BeadSnapshot bead)
    {
        var text = comment.Text;
        var prefix = text.Split(' ', 2)[0];
        if (prefix is not ("nightweaver-merge-v1" or "nightweaver-verified-merge-v1" or "nightweaver-finalization-v1")) return null;
        if (string.IsNullOrWhiteSpace(comment.Id) || comment.IssueId != bead.Id) return Unsupported(comment, prefix);
        var sections = new List<MessageSection>();
        var verified = prefix == "nightweaver-verified-merge-v1";
        if (prefix == "nightweaver-finalization-v1")
        {
            const string start = "nightweaver-finalization-v1 Verified merge closure completed; cleanup=";
            if (!text.StartsWith(start, StringComparison.Ordinal) || !text.Contains("; Product completion=", StringComparison.Ordinal)) return Unsupported(comment, prefix);
            sections.Add(new("Evidence status", "Host-recorded finalization diagnostic; inspect current state and housekeeping separately."));
            return new("supported", "Post-merge finalization diagnostic", text[(prefix.Length + 1)..], "merger", null, sections, text, comment.Id);
        }
        var fields = text.Split(' ');
        var keys = verified ? new[] { "pull_request", "checked_head", "merge_commit", "required_checks", "source_comment" }
            : new[] { "pull_request", "checked_head", "required_checks", "merge_method", "merge_commit" };
        if (fields.Length != 6) return Unsupported(comment, prefix);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Length; i++)
        {
            var field = fields[i + 1];
            if (!field.StartsWith(keys[i] + "=", StringComparison.Ordinal) || field.Length == keys[i].Length + 1) return Unsupported(comment, prefix);
            values.Add(keys[i], field[(keys[i].Length + 1)..]);
        }
        if (values["required_checks"] != "passed" || !Hash(values["checked_head"]) || !Hash(values["merge_commit"])
            || !Uri.TryCreate(values["pull_request"], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !verified && values["merge_method"] is not ("squash" or "merge" or "rebase")) return Unsupported(comment, prefix);
        sections.Add(new("Pull request", values["pull_request"], true));
        sections.Add(new("Checked head", values["checked_head"], true));
        sections.Add(new("Merge commit", values["merge_commit"], true));
        sections.Add(new("Reported checks", "Required checks passed, according to this source record.", true));
        if (verified) sections.Add(new("Source comment", values["source_comment"], true, values["source_comment"]));
        else sections.Add(new("Merge method", values["merge_method"], true));
        sections.Add(new("Evidence status", verified ? "Host-recorded verification; viewing does not perform a new verification or establish current state." : "Agent-reported merge evidence; viewing does not independently verify this report."));
        return new("supported", verified ? "Host-recorded verified merge" : "Agent-reported merge", "Pull request reported merged with passing required checks.", "merger", null, sections, text, comment.Id);
    }

    private static bool Hash(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static CommentPresentation Unsupported(BeadsCommentSnapshot comment, string schema)
        => new("fallback", "Inspect message", $"Unsupported or malformed Nightweaver contract: {schema}. Current explanation is uncertain; inspect the original.", null, null, [], comment.Text, comment.Id);
    private static string Read(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()! : throw new JsonException($"Missing or invalid {name}.");
    private static string Nullable(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return "Unavailable in this record";
        return field.ValueKind == JsonValueKind.String ? field.GetString()! : throw new JsonException($"Invalid {name}.");
    }
    private static JsonElement[] Array(JsonElement value, string name, bool nonempty = false)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array || nonempty && field.GetArrayLength() == 0)
            throw new JsonException($"Missing or invalid {name}.");
        return field.EnumerateArray().ToArray();
    }
    private static JsonElement[] OptionalArray(JsonElement value, string name)
        => !value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null ? [] : Array(value, name);
    private static void AddQuestions(JsonElement root, List<MessageSection> sections)
    {
        foreach (var question in OptionalArray(root, "questions"))
        {
            sections.Add(new("Question", Read(question, "question"), true));
            sections.Add(new("Recommended answer", Read(question, "recommendedAnswer"), true));
            sections.Add(new("Tradeoffs", Read(question, "tradeoffs"), true));
        }
    }
    private static string Strings(JsonElement value, string name, bool nonempty = false)
    {
        var items = Array(value, name, nonempty);
        if (items.Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()))) throw new JsonException($"Invalid {name}.");
        return items.Length == 0 ? "None recorded" : string.Join("\n", items.Select(x => x.GetString()));
    }
    private static string Grounding(JsonElement value, bool snake = false)
        => "Checkout revision: " + Read(value, snake ? "checkout_revision" : "checkoutRevision")
            + "\nDefault revision: " + Read(value, snake ? "default_revision" : "defaultRevision")
            + "\nProduct inputs: " + Read(value, snake ? "product_inputs" : "productInputs")
            + "\nObserved remote revision: " + Nullable(value, snake ? "observed_remote_revision" : "observedRemoteRevision");
}
