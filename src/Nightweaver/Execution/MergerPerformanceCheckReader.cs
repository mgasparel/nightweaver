using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Nightweaver.Configuration;

namespace Nightweaver.Execution;

internal sealed record PerformanceCheckEvidence(string Command, string EvidenceId, int Total, int Passed,
    double ElapsedSeconds)
{
    internal const string Subject = "nightweaver-full-suite-local-v1";
    internal static string CommandFor(string id) => "dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 "
        + $"--report-xunit-xml --report-xunit-xml-filename {id}.xml --show-slowest-tests 20";

    internal static PerformanceCheckEvidence? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var fields = root.EnumerateObject().ToArray();
        if (fields.Length != 10 || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 10) return null;
        bool Text(string name, out string value)
        {
            value = "";
            if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
            value = field.GetString()!;
            return true;
        }
        bool Number(string name, out int value)
        {
            value = 0;
            return root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
        }
        if (!Text("reason", out var reason) || reason != "hard-limit"
            || !Text("evidence_id", out var id) || !Guid.TryParseExact(id, "N", out _)
            || !Text("command", out var command) || command != CommandFor(id)
            || !Number("total", out var total) || total < 995
            || !Number("passed", out var passed) || passed != total
            || !Number("failed", out var failed) || failed != 0
            || !Number("skipped", out var skipped) || skipped != 0
            || !Number("hard_limit_seconds", out var limit) || limit != 300
            || !Number("test_exit_code", out var exit) || exit != 0
            || !root.TryGetProperty("elapsed_seconds", out var elapsed) || elapsed.ValueKind != JsonValueKind.Number
            || !elapsed.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 300) return null;
        return new(command, id, total, passed, seconds);
    }
}

/// <summary>Passing correctness output is valid here; it is never test-failure evidence.</summary>
internal static class MergerPerformanceCheckReader
{
    internal static async Task VerifyAsync(HostPaths paths, ProjectSlug project, AgentStageResult invocation,
        MergerRejectionFinding finding, CancellationToken cancellationToken)
    {
        using var pinned = InvocationRuntimeFiles.OpenExistingSettledLogForRead(paths, project,
            invocation.BeadId, invocation.InvocationId, invocation.LogFilePath);
        var stream = pinned.Stream;
        var length = stream.Length;
        if (length > MergerLocalCheckReader.MaximumLogBytes) throw new InvalidDataException("Performance log exceeds 8 MiB.");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != length) throw new InvalidDataException("Performance log changed during reading.");
        var marker = "NIGHTWEAVER_PERFORMANCE_CHECK_V1 " + invocation.InvocationId;
        // Latin1 preserves byte offsets; only the bounded evidence is decoded as strict UTF-8.
        var scan = Encoding.Latin1.GetString(bytes);
        var first = scan.IndexOf(marker, StringComparison.Ordinal);
        var last = first < 0 ? -1 : scan.IndexOf(marker, first + marker.Length, StringComparison.Ordinal);
        if (first < 0 || last < 0 || scan.IndexOf(marker, last + marker.Length, StringComparison.Ordinal) >= 0
            || first > 0 && scan[first - 1] != '\n'
            || last < 4 || scan[(last - 4)..last] != "END_"
            || last + marker.Length < scan.Length && scan[last + marker.Length] != '\n'
                && !(scan[last + marker.Length] == '\r' && last + marker.Length + 1 < scan.Length
                    && scan[last + marker.Length + 1] == '\n'))
            throw new InvalidDataException("Performance framing is missing, quoted or duplicated.");
        var end = last - 4;
        if (end - first > MergerLocalCheckReader.MaximumEvidenceBytes)
            throw new InvalidDataException("Performance evidence exceeds 32 KiB.");
        var frame = new UTF8Encoding(false, true).GetString(bytes, first, end - first).TrimEnd('\r', '\n');
        var opening = marker + " ";
        if (!frame.StartsWith(opening, StringComparison.Ordinal) || frame.Contains('\n') || frame.Contains('\r')
            || end > 0 && scan[end - 1] != '\n') throw new InvalidDataException("Performance frame must contain one complete JSON line.");
        using var document = JsonDocument.Parse(frame[opening.Length..], new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        bool Matches(string name, string expected) => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
        if (names.Length != 10 || names.Distinct(StringComparer.Ordinal).Count() != 10
            || !Matches("schema", "nightweaver-merger-rejection-v1") || !Matches("kind", "performance")
            || !Matches("pull_request", finding.PullRequest) || !Matches("checked_head", finding.CheckedHead)
            || !Matches("subject", finding.Subject) || !Matches("summary", finding.Summary) || !Matches("details", finding.Details)
            || !Matches("check_source", "local") || !root.TryGetProperty("local_exit_code", out var exit)
            || !exit.TryGetInt32(out var code) || code != 0 || !root.TryGetProperty("performance", out var payload)
            || PerformanceCheckEvidence.Parse(payload) is not { } evidence || evidence != finding.Performance)
            throw new InvalidDataException("Performance frame and comment disagree or contain invalid policy evidence.");
        var suffix = scan[(last + marker.Length)..];
        if (suffix.Contains("Test run summary:", StringComparison.OrdinalIgnoreCase)
            || suffix.Contains("Build succeeded.", StringComparison.OrdinalIgnoreCase)
            || suffix.Contains("Build FAILED.", StringComparison.OrdinalIgnoreCase)
            || suffix.Contains("NIGHTWEAVER_LOCAL_CHECK", StringComparison.Ordinal)
            || suffix.Contains("\"elapsed_seconds\"", StringComparison.Ordinal))
            throw new InvalidDataException("A later result contradicts settled performance evidence.");
        VerifyTerminalSummary(scan[..first], evidence);
        await VerifyMeasurementAsync(paths, project, invocation, finding, evidence, cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyTerminalSummary(string text, PerformanceCheckEvidence evidence)
    {
        // Require a complete terminal observation, not merely the Agent's matching JSON claims.
        var summaries = Regex.Matches(text, @"(?im)^Test run summary:.*$", RegexOptions.CultureInvariant);
        var complete = Regex.Matches(text,
            @"(?m)^Test run summary: Passed!\r?\n\s*total: (?<total>[0-9]+)\r?\n\s*failed: 0\r?\n\s*succeeded: (?<passed>[0-9]+)\r?\n\s*skipped: 0\r?$",
            RegexOptions.CultureInvariant);
        if (summaries.Count != 1 || complete.Count != 1
            || !int.TryParse(complete[0].Groups["total"].Value, out var total) || total != evidence.Total
            || !int.TryParse(complete[0].Groups["passed"].Value, out var passed) || passed != evidence.Passed
            || text.Contains("Build FAILED.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Missing, incomplete or contradictory actual passing terminal evidence.");
    }

    private static async Task VerifyMeasurementAsync(HostPaths paths, ProjectSlug project, AgentStageResult invocation,
        MergerRejectionFinding finding, PerformanceCheckEvidence evidence, CancellationToken cancellationToken)
    {
        async Task<string> Read(string name, int limit)
        {
            using var file = InvocationRuntimeFiles.OpenExistingMeasurementForRead(paths, project, invocation, evidence.EvidenceId, name);
            var length = file.Stream.Length;
            if (length > limit) throw new InvalidDataException("Retained measurement exceeds bounded read limit.");
            var bytes = new byte[(int)length];
            await file.Stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (file.Stream.Length != length) throw new InvalidDataException("Retained measurement changed during reading.");
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        using var document = JsonDocument.Parse(await Read("result.json", MergerLocalCheckReader.MaximumEvidenceBytes).ConfigureAwait(false),
            new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        RequireUniqueKeys(root);
        bool Text(string key, string value) => root.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.String && field.GetString() == value;
        bool Number(string key, int value) => root.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var actual) && actual == value;
        if (!Text("schema", "nightweaver-suite-measurement-v1") || !Text("invocationId", invocation.InvocationId)
            || !Text("evidenceId", evidence.EvidenceId) || !Text("candidateRevision", finding.CheckedHead)
            || !Text("commandPolicy", PerformanceCheckEvidence.Subject) || !Text("command", evidence.Command)
            || !root.TryGetProperty("candidateTree", out var tree) || tree.ValueKind != JsonValueKind.String
            || !Regex.IsMatch(tree.GetString()!, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)
            || !root.TryGetProperty("dirty", out var dirty) || dirty.ValueKind != JsonValueKind.False
            || !Number("threads", 6) || !Number("testExitCode", 0) || !Number("total", evidence.Total)
            || !Number("passed", evidence.Passed) || !Number("failed", 0) || !Number("skipped", 0)
            || !root.TryGetProperty("measurementErrors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0
            || !root.TryGetProperty("elapsedSeconds", out var elapsed) || elapsed.ValueKind != JsonValueKind.Number
            || !elapsed.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds != evidence.ElapsedSeconds)
            throw new InvalidDataException("Retained measurement does not substantiate the performance frame.");
        VerifyTerminalSummary(await Read("suite.log", MergerLocalCheckReader.MaximumLogBytes).ConfigureAwait(false), evidence);

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var xmlReader = XmlReader.Create(new StringReader(await Read("report-1.xml", MergerLocalCheckReader.MaximumLogBytes).ConfigureAwait(false)), settings);
        var xml = XDocument.Load(xmlReader);
        var assemblies = xml.Root?.Name == "assemblies" ? xml.Root.Elements("assembly").ToArray() : [];
        long total = 0;
        foreach (var assembly in assemblies)
        {
            bool Count(string key, int value) => int.TryParse((string?)assembly.Attribute(key), out var actual) && actual == value;
            if (!int.TryParse((string?)assembly.Attribute("total"), out var count) || count <= 0
                || !Count("passed", count) || !Count("failed", 0) || !Count("skipped", 0)
                || !Count("errors", 0) || !Count("not-run", 0)
                || !((string?)assembly.Attribute("environment") ?? "").Contains("6 threads", StringComparison.Ordinal))
                throw new InvalidDataException("Retained xUnit report is failed or incomplete.");
            total += count;
        }
        if (total != evidence.Total) throw new InvalidDataException("Retained xUnit counts disagree with measurement.");
    }

    private static void RequireUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate measurement JSON key.");
                RequireUniqueKeys(field.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RequireUniqueKeys(item);
    }
}
