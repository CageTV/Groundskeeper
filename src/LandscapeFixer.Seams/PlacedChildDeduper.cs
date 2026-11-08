using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

/// <summary>
/// Last-line guard for the finalizer: Mutagen refuses to write a plugin in which one placed reference
/// (REFR/ACHR/...) FormKey appears twice ("Two records with the same FormKey were encountered").
/// That is what PatchForeman users hit on Nexus (08841E:Skyrim.esm, reported after adding Snow Fixer):
/// the verbatim cell copies carry placed children, and another pass (or another cell copy) can add the
/// same reference again - notably when a mod such as Mountain LOD overrides a reference into a different
/// cell than its master. This removes the extra copies, keeping the one that sits in the cell the winning
/// override of that reference lives in (so the engine's own placement wins), else the first one found.
/// </summary>
public static class PlacedChildDeduper
{
    public sealed record Result(int DuplicateGroups, int CopiesRemoved, IReadOnlyList<string> Messages);

    /// <param name="patchMod">The mod about to be written.</param>
    /// <param name="winningParentCell">FormKey of the cell holding the load-order winner of the given placed
    /// reference, or null when unknown (keeps the first copy).</param>
    public static Result Run(ISkyrimMod patchMod, Func<FormKey, FormKey?> winningParentCell)
    {
        var holders = new Dictionary<FormKey, List<(Cell Cell, bool Persistent)>>();
        foreach (var cell in patchMod.EnumerateMajorRecords<Cell>())
        {
            foreach (var p in cell.Persistent) Note(holders, p.FormKey, cell, true);
            foreach (var t in cell.Temporary) Note(holders, t.FormKey, cell, false);
        }

        var messages = new List<string>();
        int groups = 0, removed = 0;
        foreach (var (key, where) in holders.Where(h => h.Value.Count > 1).OrderBy(h => h.Key.ToString()))
        {
            groups++;
            var parent = winningParentCell(key);
            var keep = parent is { } pk ? where.FindIndex(w => w.Cell.FormKey == pk) : -1;
            if (keep < 0) keep = 0;
            for (var i = 0; i < where.Count; i++)
            {
                if (i == keep) continue;
                var (cell, persistent) = where[i];
                var list = persistent ? cell.Persistent : cell.Temporary;
                // remove exactly one copy per extra holder (a single list may itself hold it twice)
                var idx = list.FindIndex(r => r.FormKey == key);
                if (idx >= 0) { list.RemoveAt(idx); removed++; }
            }
            messages.Add($"  duplicate placed reference {key}: kept the copy in cell {where[keep].Cell.FormKey}, removed {where.Count - 1} other(s).");
        }
        return new Result(groups, removed, messages);
    }

    private static void Note(Dictionary<FormKey, List<(Cell, bool)>> holders, FormKey key, Cell cell, bool persistent)
    {
        if (!holders.TryGetValue(key, out var list)) holders[key] = list = new();
        list.Add((cell, persistent));
    }
}
