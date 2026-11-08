using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SeamFinder.Core;

namespace LandscapeHybrid;

/// <summary>
/// After Generate: every placed reference the output carries must still have the load order's state for "disabled", "deleted", its base and
/// its enable parent. A patch that disables a road piece (or deletes something) has to keep winning; only positions (the floating pass) may differ.
/// </summary>
public static class OutputVerifier
{
    public sealed record Mismatch(FormKey Ref, string Winner, string Expected, string Ours);

    public sealed record Report(int RefsChecked, IReadOnlyList<Mismatch> Mismatches);

    static string State(IPlacedGetter r)
    {
        var o = r as IPlacedObjectGetter;
        var flags = ((ISkyrimMajorRecordGetter)r).SkyrimMajorRecordFlags;
        var disabled = flags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled);
        var deleted = r.IsDeleted;
        // a deleted record's other fields are meaningless
        var bs = deleted ? "" : Norm(o?.Base.FormKey);
        var ep = deleted ? "" : $"{Norm(o?.EnableParent?.Reference.FormKey)}/{o?.EnableParent?.Flags}";
        return $"disabled={disabled} deleted={deleted} base={bs} enableParent={ep}";
    }

    static string Norm(FormKey? k) => k is null || k.Value.IsNull ? "" : k.Value.ToString().ToLowerInvariant();

    public static Report Verify(IReadOnlyList<Mo2Resolver.ResolvedPlugin> loadOrderWithoutOutput, string outputPluginPath)
    {
        // which cells (worldspace + grid) the output carries
        var cells = new HashSet<(FormKey, int, int)>();
        var ours = new Dictionary<FormKey, string>();
        using (var g = SkyrimMod.CreateFromBinaryOverlay(outputPluginPath, SkyrimRelease.SkyrimSE))
            foreach (var ws in g.Worldspaces)
                foreach (var blk in ws.SubCells) foreach (var sb in blk.Items) foreach (var c in sb.Items)
                {
                    if (c.Grid is null) continue;
                    cells.Add((ws.FormKey, c.Grid.Point.X, c.Grid.Point.Y));
                    foreach (var r in c.Temporary.Concat(c.Persistent)) ours[r.FormKey] = State(r);
                }

        var winner = new Dictionary<FormKey, (string State, string Plugin)>();
        foreach (var p in loadOrderWithoutOutput) // load order: later plugins overwrite earlier ones
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(p.FilePath, SkyrimRelease.SkyrimSE);
            foreach (var ws in mod.Worldspaces)
                foreach (var blk in ws.SubCells) foreach (var sb in blk.Items) foreach (var c in sb.Items)
                {
                    if (c.Grid is null || !cells.Contains((ws.FormKey, c.Grid.Point.X, c.Grid.Point.Y))) continue;
                    foreach (var r in c.Temporary.Concat(c.Persistent))
                        if (ours.ContainsKey(r.FormKey)) winner[r.FormKey] = (State(r), p.FileName);
                }
        }

        var bad = new List<Mismatch>();
        foreach (var (k, st) in ours)
            if (winner.TryGetValue(k, out var w) && w.State != st) bad.Add(new Mismatch(k, w.Plugin, w.State, st));
        return new Report(ours.Count, bad);
    }
}
