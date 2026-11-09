using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

/// <summary>One exterior cell's full-resolution terrain: 33 x 33 vertex heights, Heights[i, j] with i west to east and j south to north.</summary>
public sealed record ReliefCell(int X, int Y, float[,] Heights, float? WaterHeight, string Owner);

/// <summary>A block of cells around a centre cell. Cells without terrain are simply absent.</summary>
public sealed record ReliefGrid(string Worldspace, int CenterX, int CenterY, int Radius, List<ReliefCell> Cells);

/// <summary>
/// Read-only: stages a load order once and reads the winning full-resolution terrain of a block of cells on demand, for the 3D view.
/// Opening takes a few seconds; each <see cref="Load"/> after the first is quick.
/// </summary>
public sealed class ReliefSession : IDisposable
{
    const float NoWater = 1_000_000f;
    readonly string _merged;
    readonly IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _env;
    readonly Dictionary<ModKey, int> _priority;
    Dictionary<(string, int, int), (FormKey Cell, float? Water)>? _index;

    ReliefSession(string merged, IGameEnvironment<ISkyrimMod, ISkyrimModGetter> env, Dictionary<ModKey, int> priority)
    { _merged = merged; _env = env; _priority = priority; }

    public static ReliefSession Open(IReadOnlyList<Mo2Resolver.ResolvedPlugin> loadOrder)
    {
        var merged = Path.Combine(Path.GetTempPath(), "Relief-" + Guid.NewGuid().ToString("N"));
        Mo2Resolver.MaterializeMergedFolder(loadOrder.ToList(), merged);
        try
        {
            var keys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>.Create(GameRelease.SkyrimSE)
                .WithLoadOrder(keys).WithTargetDataFolder(merged).Build();
            return new ReliefSession(merged, env, keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i));
        }
        catch
        {
            try { Directory.Delete(merged, true); } catch { /* scratch in %TEMP% */ }
            throw;
        }
    }

    void BuildIndex()
    {
        var idx = new Dictionary<(string, int, int), (FormKey, float?)>();
        foreach (var ctx in _env.LinkCache.WinningContextOverrides<Cell, ICellGetter>(_env.LinkCache))
        {
            var cell = ctx.Record;
            if (cell.Grid is null || !ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var ws)) continue;
            var wh = cell.WaterHeight ?? ws.Record.LandDefaults?.DefaultWaterHeight;
            idx[((ws.Record.EditorID ?? ws.Record.FormKey.ToString()).ToLowerInvariant(), cell.Grid.Point.X, cell.Grid.Point.Y)] =
                (cell.FormKey, wh is { } w && w < NoWater ? w : null);
        }
        _index = idx;
    }

    public ReliefGrid Load(string worldspace, int cx, int cy, int radius)
    {
        if (_index is null) BuildIndex();
        var cells = new List<ReliefCell>();
        for (var y = cy - radius; y <= cy + radius; y++)
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if (!_index!.TryGetValue((worldspace.ToLowerInvariant(), x, y), out var c)) continue;
                var (land, owner) = HeightmapDecoder.ResolveWinningLandscape(c.Cell, _env.LinkCache, _priority);
                if (land?.VertexHeightMap is null) continue;
                cells.Add(new ReliefCell(x, y, HeightmapDecoder.DecodeHeights(land.VertexHeightMap), c.Water, owner.FileName));
            }
        return new ReliefGrid(worldspace, cx, cy, radius, cells);
    }

    public void Dispose()
    {
        _env.Dispose();
        try { Directory.Delete(_merged, true); } catch { /* scratch in %TEMP% */ }
    }
}
