using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

/// <summary>One exterior cell reduced to a 4x4 block of mean heights plus its water plane, for drawing an orientation map.</summary>
public sealed record OverviewCell(int X, int Y, float[] Blocks, float? WaterHeight)
{
    public float Mean => Blocks.Average();
}

/// <summary>
/// Read-only: the terrain of every exterior cell, downsampled. Used as the backdrop of the problem map so the
/// marks sit on a recognisable land / ocean shape. Reads the winning landscape exactly as the detectors do.
/// </summary>
/// <param name="Cells">Cells per worldspace EditorID.</param>
/// <param name="Names">Worldspace FormKey text (as the seam detectors label it) to its EditorID.</param>
public sealed record OverviewResult(Dictionary<string, List<OverviewCell>> Cells, Dictionary<string, string> Names);

public static class TerrainOverview
{
    const float NoWater = 1_000_000f;

    public static OverviewResult Run(List<Mo2Resolver.ResolvedPlugin> loadOrder, Action<string> log)
    {
        var merged = Path.Combine(Path.GetTempPath(), "TerrainOverview-" + Guid.NewGuid().ToString("N"));
        Mo2Resolver.MaterializeMergedFolder(loadOrder, merged);
        try
        {
            var keys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>.Create(GameRelease.SkyrimSE)
                .WithLoadOrder(keys).WithTargetDataFolder(merged).Build();
            var priority = keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
            var result = new Dictionary<string, List<OverviewCell>>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ctx in env.LinkCache.WinningContextOverrides<Cell, ICellGetter>(env.LinkCache))
            {
                var cell = ctx.Record;
                if (cell.Grid is null) continue;
                if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsCtx)) continue;
                var ws = wsCtx.Record;
                var (land, _) = HeightmapDecoder.ResolveWinningLandscape(cell.FormKey, env.LinkCache, priority);
                if (land?.VertexHeightMap is null) continue;
                var z = HeightmapDecoder.DecodeHeights(land.VertexHeightMap);
                var blocks = new float[16];
                for (var by = 0; by < 4; by++)
                    for (var bx = 0; bx < 4; bx++)
                    {
                        double sum = 0; var n = 0;
                        for (var i = bx * 8; i < Math.Min(33, bx * 8 + 8); i++)
                            for (var j = by * 8; j < Math.Min(33, by * 8 + 8); j++) { sum += z[i, j]; n++; }
                        blocks[by * 4 + bx] = (float)(sum / n);
                    }
                var wh = cell.WaterHeight ?? ws.LandDefaults?.DefaultWaterHeight;
                var name = ws.EditorID ?? ws.FormKey.ToString();
                names[ws.FormKey.ToString()] = name;
                if (!result.TryGetValue(name, out var list)) result[name] = list = new();
                list.Add(new OverviewCell(cell.Grid.Point.X, cell.Grid.Point.Y, blocks, wh is { } w && w < NoWater ? w : null));
            }
            log($"Terrain overview: {result.Sum(kv => kv.Value.Count)} exterior cell(s) in {result.Count} worldspace(s).");
            return new OverviewResult(result, names);
        }
        finally { try { Directory.Delete(merged, true); } catch { /* scratch in %TEMP% */ } }
    }
}
