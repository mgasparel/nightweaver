using System.Globalization;
using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Execution;

/// <summary>A normalized snapshot of the Bead fields that govern Agent authority.</summary>
internal sealed record BeadWorkflowState(
    string Id,
    string IssueType,
    string Status,
    string? Stage,
    bool Planned,
    bool ProductDecisionRequired,
    bool NeedsHuman,
    int RepairCycles)
{
    private static readonly HashSet<string> KnownStages = new(StringComparer.Ordinal)
    {
        "stage:plan", "stage:plan-challenge", "stage:plan-approved",
        "stage:implement", "stage:review", "stage:qa", "stage:merge"
    };

    internal static BeadWorkflowState Parse(BeadSnapshot bead)
    {
        var stages = StageLabelParser.Parse(bead.Labels);
        if (stages.Count > 1)
            throw new FormatException($"Bead '{bead.Id}' has multiple stage labels: {string.Join(", ", stages)}.");
        var stage = stages.SingleOrDefault();
        if (stage is not null && !KnownStages.Contains(stage))
            throw new FormatException($"Bead '{bead.Id}' has unsupported stage label '{stage}'.");

        var planned = bead.Labels.Contains("planned", StringComparer.Ordinal);
        var productDecision = bead.Labels.Contains("product-decision-required", StringComparer.Ordinal);
        var needsHuman = bead.Labels.Contains("needs-human", StringComparer.Ordinal);
        if ((planned && (stage is not null || productDecision || needsHuman)) || (productDecision && needsHuman))
            throw new FormatException($"Bead '{bead.Id}' has contradictory workflow labels.");

        var repairCycles = ReadRepairCycles(bead);
        return new(bead.Id, bead.IssueType, bead.Status, stage, planned, productDecision, needsHuman, repairCycles);
    }

    private static int ReadRepairCycles(BeadSnapshot bead)
    {
        JsonElement metadata = default;
        var metadataCount = 0;
        foreach (var property in bead.Payload.EnumerateObject())
        {
            if (property.Name != "metadata") continue;
            metadata = property.Value;
            metadataCount++;
        }
        if (metadataCount > 1) throw new FormatException($"Bead '{bead.Id}' has duplicate metadata fields.");
        if (metadataCount == 0 || metadata.ValueKind == JsonValueKind.Null) return 0;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Bead '{bead.Id}' metadata must be an object or null.");

        JsonElement value = default;
        var valueCount = 0;
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name != "repair_cycles") continue;
            value = property.Value;
            valueCount++;
        }
        if (valueCount > 1) throw new FormatException($"Bead '{bead.Id}' has duplicate repair_cycles metadata.");
        if (valueCount == 0) return 0;

        int result;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out var number) || decimal.Truncate(number) != number
                || number > int.MaxValue || number < int.MinValue)
                throw new FormatException($"Bead '{bead.Id}' repair_cycles must be a 32-bit integer.");
            result = decimal.ToInt32(number);
        }
        else if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
        }
        else
        {
            throw new FormatException($"Bead '{bead.Id}' repair_cycles must be an integer number or integer string.");
        }

        if (result < 0) throw new FormatException($"Bead '{bead.Id}' repair_cycles cannot be negative.");
        return result;
    }
}
