using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record MessageSection(string Label, string Text);
internal sealed record CommentPresentation(string State, string Title, string Summary, string? Role,
    string? Stage, IReadOnlyList<MessageSection> Sections, string Original, string SourceId);

/// <summary>Read-only display adapters. Successful presentation does not verify workflow evidence.</summary>
internal static class CommentPresenter
{
    internal static CommentPresentation Project(BeadsCommentSnapshot comment, BeadSnapshot bead)
    {
        var text = comment.Text;
        var claimedContract = false;
        CommentPresentation Fallback(string reason) => new("fallback", "Inspect message", reason,
            null, null, [], text, comment.Id);
        if (!text.TrimStart().StartsWith('{'))
            return text.Contains("nightweaver-", StringComparison.Ordinal) && text.Contains("\"schema\"", StringComparison.Ordinal)
                ? Fallback("Mixed or malformed Nightweaver payload. Current explanation is uncertain; inspect the original and history.")
                : new("ordinary", "Comment", text, null, null, [], text, comment.Id);
        try
        {
            if (text.Length > 262144) throw new JsonException("Message exceeds display parsing limit.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schemaValue))
                return new("ordinary", "Comment", text, null, null, [], text, comment.Id);
            var schema = schemaValue.ValueKind == JsonValueKind.String ? schemaValue.GetString()! : "";
            claimedContract = schema.StartsWith("nightweaver-", StringComparison.Ordinal);
            if (schemaValue.ValueKind != JsonValueKind.String && text.Contains("nightweaver-", StringComparison.Ordinal))
                throw new JsonException("Invalid schema type.");
            if (!schema.StartsWith("nightweaver-", StringComparison.Ordinal))
                return new("ordinary", "Comment", text, null, null, [], text, comment.Id);
            ValidateUnique(root);
            if (string.IsNullOrWhiteSpace(comment.Id) || comment.IssueId != bead.Id)
                throw new JsonException("Missing or mismatched source identity.");
            string Read(JsonElement value, string name)
            {
                if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(field.GetString())) throw new JsonException($"Missing or invalid {name}.");
                return field.GetString()!;
            }
            string Required(string name) => Read(root, name);
            string? Optional(string name)
            {
                if (!root.TryGetProperty(name, out var value)) return null;
                if (value.ValueKind != JsonValueKind.String) throw new JsonException($"Invalid {name}.");
                return value.GetString();
            }
            void Identity()
            {
                if (Required("bead_id") != bead.Id || comment.IssueId != bead.Id)
                    throw new JsonException("Message identity does not match this Bead.");
            }
            var sections = new List<MessageSection>();
            void Add(string label, string value) => sections.Add(new(label, value));
            string title, summary;
            string? role = null, stage = null;
            switch (schema)
            {
                case "nightweaver-needs-human-v1":
                    Identity();
                    if (Required("bead_type") != bead.IssueType) throw new JsonException("Bead type mismatch.");
                    var cause = Required("cause");
                    if (cause is not ("operational-retries-exhausted" or "duplicate-failure" or "repair-budget-exhausted"
                        or "environment-blocked" or "delivery-outcome-failure")) throw new JsonException("Unknown escalation cause.");
                    role = Required("failed_role");
                    if (role is not ("planner" or "plan_challenger" or "implementer" or "reviewer" or "qa" or "merger"))
                        throw new JsonException("Unknown role.");
                    stage = Required("stage");
                    if (stage is not ("(none)" or "stage:plan" or "stage:plan-challenge" or "stage:plan-approved"
                        or "stage:implement" or "stage:review" or "stage:qa" or "stage:merge")) throw new JsonException("Unknown stage.");
                    foreach (var name in new[] { "invocation_id", "log_directory" })
                        Add(name == "invocation_id" ? "Invocation" : "Log directory", Optional(name) ?? "Unavailable in this historical record");
                    title = "Human attention required";
                    summary = Required("reason");
                    Add("Cause", cause.Replace('-', ' '));
                    if (root.TryGetProperty("latest_findings", out var findings))
                    {
                        if (findings.ValueKind != JsonValueKind.String) throw new JsonException("Invalid latest findings.");
                        if (!string.IsNullOrWhiteSpace(findings.GetString())) Add("Latest findings", findings.GetString()!);
                    }
                    foreach (var (key, label) in new[] { ("environment_retention", "Environment retention"), ("cleanup_preflight", "Historical cleanup warning") })
                        if (Optional(key) is { Length: > 0 } warning) Add(label, warning);
                    if (root.TryGetProperty("log_files", out var logs))
                    {
                        if (logs.ValueKind != JsonValueKind.Array || logs.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                            throw new JsonException("Invalid log references.");
                        Add("Log references", string.Join("\n", logs.EnumerateArray().Select(x => x.GetString())));
                    }
                    break;
                case "nightweaver-merger-rejection-v1":
                    if (comment.IssueId != bead.Id) throw new JsonException("Comment identity mismatch.");
                    var kind = Required("kind");
                    if (kind is not ("conflict" or "design" or "checks")) throw new JsonException("Unknown rejection kind.");
                    title = $"Reported {kind} rejection"; role = "merger"; stage = "stage:merge";
                    summary = Required("summary");
                    Add("Subject", Required("subject")); Add("Reported findings", Required("details"));
                    Add("Pull request", Required("pull_request")); Add("Checked head", Required("checked_head"));
                    if (kind == "checks")
                    {
                        var origin = Required("check_source");
                        if (origin is not ("local" or "remote")) throw new JsonException("Unknown check origin.");
                        Add("Check origin", origin);
                        if (origin == "local")
                        {
                            if (!root.TryGetProperty("local_exit_code", out var code) || !code.TryGetInt32(out var exit) || exit <= 0)
                                throw new JsonException("Invalid local exit code.");
                            Add("Local exit code", exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                    Add("Evidence status", "Reported by the Merger; displaying this message does not verify the check result.");
                    break;
                case "nightweaver-needs-human-pause-failure-v1":
                case "nightweaver-product-decision-pause-failure-v1":
                case "nightweaver-needs-human-cleanup-failure-v1":
                    Identity(); title = schema.Contains("cleanup", StringComparison.Ordinal) ? "Historical cleanup warning" : "Supplemental pause failure";
                    summary = Required("failure");
                    break;
                case "nightweaver-human-guidance-v1":
                case "nightweaver-human-action-v1":
                    return new("ordinary", "Comment", text, null, null, [], text, comment.Id);
                case "nightweaver-plan-question-v1":
                    if (comment.IssueId != bead.Id) throw new JsonException("Comment identity mismatch.");
                    if (!root.TryGetProperty("grounding", out var grounding) || grounding.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Missing grounding.");
                    foreach (var name in new[] { "checkoutRevision", "defaultRevision", "productInputs" }) _ = Read(grounding, name);
                    if (!root.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() == 0)
                        throw new JsonException("Missing questions.");
                    foreach (var question in questions.EnumerateArray())
                    {
                        Add("Question", Read(question, "question")); Add("Recommended answer", Read(question, "recommendedAnswer"));
                        Add("Tradeoffs", Read(question, "tradeoffs"));
                    }
                    title = "Planning question"; summary = sections[0].Text;
                    break;
                default:
                    return Fallback($"Unsupported Nightweaver contract: {schema}. Inspect the original; current explanation is uncertain.");
            }
            return new("supported", title, summary, role, stage, sections, text, comment.Id);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return claimedContract || text.Contains("nightweaver-", StringComparison.Ordinal)
                ? Fallback("Malformed Nightweaver message. Current explanation is uncertain; inspect the original and history.")
                : new("ordinary", "Comment", text, null, null, [], text, comment.Id);
        }
    }

    internal static bool IsPauseCandidate(string text, bool question)
    {
        bool Family(string value) => question
            ? value.StartsWith("nightweaver-plan-question-", StringComparison.Ordinal)
            : value.StartsWith("nightweaver-needs-human-", StringComparison.Ordinal)
                || value.StartsWith("nightweaver-product-decision-pause-failure-", StringComparison.Ordinal);
        try
        {
            if (text.Length > 262144) throw new JsonException("Display parse limit.");
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("schema", out var schema)
                && schema.ValueKind == JsonValueKind.String && Family(schema.GetString()!);
        }
        catch (JsonException)
        {
            // Only schema-like records can displace pause evidence; ordinary notes cannot.
            return text.Contains("\"schema\"", StringComparison.Ordinal)
                && (question ? text.Contains("nightweaver-plan-question-", StringComparison.Ordinal)
                    : text.Contains("nightweaver-needs-human-", StringComparison.Ordinal)
                        || text.Contains("nightweaver-product-decision-pause-failure-", StringComparison.Ordinal));
        }
    }

    internal static bool IsSupplemental(CommentPresentation message) => message.State == "supported"
        && message.Title is "Supplemental pause failure" or "Historical cleanup warning";

    private static void ValidateUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate field.");
                ValidateUnique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) ValidateUnique(element);
    }
}
