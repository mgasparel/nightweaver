using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

// Narrow read-only facade over the runtime parser, preserving malformed observations
// as data for operator views. Independent repair observations remain inspectable.
internal sealed record BeadWorkflowObservation(BeadWorkflowState? State, int? RepairCycles, string? Error)
{
    internal static BeadWorkflowObservation Read(BeadSnapshot bead)
    {
        int? repairs;
        try { repairs = BeadWorkflowState.ReadRepairCycles(bead); }
        catch (FormatException) { repairs = null; }
        try
        {
            var state = BeadWorkflowState.Parse(bead);
            var duplicateStage = bead.Labels.Count(label => label.StartsWith("stage:", StringComparison.Ordinal)) > 1;
            // Preserve the console's conservative observation of duplicate arbitrary metadata,
            // without changing the runtime admission parser or guessing its custom meanings.
            var duplicateMetadata = bead.Payload.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1);
            return new(state, repairs, duplicateStage ? "Multiple stage labels are present."
                : duplicateMetadata ? "Bead metadata contains duplicate fields." : null);
        }
        catch (FormatException error) { return new(null, repairs, error.Message); }
    }
}
