using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Inspection;
using Nightweaver.Execution;

namespace Nightweaver.Web;

internal sealed record PlanningReadiness(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("canAuthorize")] bool CanAuthorize,
    [property: JsonPropertyName("expected")] string Expected)
{
    internal static PlanningReadiness? Project(BeadSnapshot bead)
    {
        if (bead.IssueType is not ("feature" or "bug")) return null;
        var ready = bead.Labels.Contains("ready-for-planning", StringComparer.Ordinal);
        var canAuthorize = !ready && bead.Status == "open" && bead.Assignee is null
            && !AttentionDeskProjector.IsPaused(bead)
            && !bead.Labels.Contains("planned", StringComparer.Ordinal)
            && AttentionDeskProjector.InvalidWorkflowReason(bead) is null
            && AttentionDeskProjector.Stage(bead) is null or "stage:plan" or "stage:plan-challenge" or "stage:plan-approved";
        var identity = JsonSerializer.Serialize(AttentionMutationIdentity.Capture(bead));
        return new(ready, canAuthorize, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    internal static string ParseRequest(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var fields = document.RootElement.EnumerateObject().ToArray();
                if (fields.Length == 1 && fields[0].NameEquals("expected")
                    && fields[0].Value.ValueKind == JsonValueKind.String
                    && fields[0].Value.GetString() is { Length: 64 } expected
                    && expected.All(Uri.IsHexDigit)) return expected;
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        throw new AttentionRequestValidationException("Supply only the expected Planning snapshot from the workspace.");
    }
}

internal sealed partial class TrackerBridge
{
    internal async Task<AttentionMutationResult> AuthorizePlanningAsync(string beadId, string expected,
        CancellationToken cancellationToken)
    {
        if (!BeadIdSafety.IsSafe(beadId))
            throw new AttentionActionException(400, "Invalid Bead ID", "Use a safe exact tracker ID.");
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var target = await targetRepositoryValidator.ValidateAsync(project.TargetRepository, project.ConfigFilePath, cancellationToken);
            var initial = await ReadExactBeadAsync(target, beadId, cancellationToken);
            if (initial is null) throw new AttentionActionException(404, "Bead not found", "The Product Bead no longer exists.");
            var planning = PlanningReadiness.Project(initial);
            if (planning is not { CanAuthorize: true } || planning.Expected != expected)
                throw new AttentionActionException(409, "Bead changed", "Refresh the workspace. Only an open, unclaimed, unpaused Product backlog item can be authorized.");

            await beadsWriter.AuthorizePlanningAsync(target, beadId, cancellationToken);
            var final = await ReadExactBeadAsync(target, beadId, cancellationToken);
            var authorized = initial with { Labels = initial.Labels.Append("ready-for-planning").ToArray() };
            if (final is null || !AttentionMutationIdentity.Capture(authorized)
                .SameObservedIdentity(AttentionMutationIdentity.Capture(final)))
                throw new AttentionActionException(409, "Bead changed", "Planning readiness could not be verified. Refresh the workspace.");
            return new(beadId, "ready-for-planning", true, false, true);
        }
        finally { operationGate.Release(); }
    }
}
