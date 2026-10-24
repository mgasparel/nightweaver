using System.Collections.ObjectModel;

namespace Nightweaver.Inspection;

internal sealed class BeadsInventory(IEnumerable<BeadSnapshot> beads)
{
    internal IReadOnlyDictionary<string, BeadSnapshot> ById { get; } =
        new ReadOnlyDictionary<string, BeadSnapshot>(beads.ToDictionary(bead => bead.Id, StringComparer.Ordinal));
}
