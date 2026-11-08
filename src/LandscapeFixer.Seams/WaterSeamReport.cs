using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

/// <summary>One shared cell edge where the water of the two cells does not agree and some of the edge sits below the higher water plane.</summary>
public sealed record WaterSeamRow(
    string Worldspace, int X1, int Y1, int X2, int Y2, string Kind,
    float? Height1, float? Height2, string Type1, string Type2, int ExposedVertices, string Plugin1, string Plugin2);

/// <summary>
/// Read-only report of possible water seams (the "random squares of water" users describe). For every pair of neighbouring exterior
/// cells it compares the effective water (the cell's own height and type, else the worldspace default) and lists the pairs that
/// disagree where at least one vertex of the shared edge lies below the higher water plane.
///
/// This is a REPORT, not a fix: rivers and waterfalls legitimately step between cells, so a mismatch is only a candidate. Cells with
/// most of the edge exposed (ExposedVertices high) are the likeliest to be visible.
/// </summary>
public static class WaterSeamReport
{
    const float NoWater = 1_000_000f;

    public static List<WaterSeamRow> Run(List<Mo2Resolver.ResolvedPlugin> loadOrder, Action<string> log)
    {
        var merged = Path.Combine(Path.GetTempPath(), "WaterSeamReport-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {merged} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, merged);
        try
        {
            var keys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>.Create(GameRelease.SkyrimSE).WithLoadOrder(keys).WithTargetDataFolder(merged).Build();
            return Analyze(env.LinkCache, keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i), log);
        }
        finally { try { Directory.Delete(merged, true); } catch { /* scratch in %TEMP% */ } }
    }

    sealed record CellWater(float? H, FormKey? Type, float[,]? Z, string Plugin);
    sealed record WsInfo(string Name, float? H, FormKey? Type);

    static List<WaterSeamRow> Analyze(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, Dictionary<ModKey, int> priority, Action<string> log)
    {
        var byWs = new Dictionary<FormKey, Dictionary<(int, int), CellWater>>();
        var wsInfo = new Dictionary<FormKey, WsInfo>();
        foreach (var ctx in cache.WinningContextOverrides<Cell, ICellGetter>(cache))
        {
            var c = ctx.Record;
            if (c.Grid is null) continue;
            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsCtx)) continue;
            var ws = wsCtx.Record;
            wsInfo[ws.FormKey] = new WsInfo(ws.EditorID ?? ws.FormKey.ToString(), ws.LandDefaults?.DefaultWaterHeight, ws.Water.FormKeyNullable);
            var (land, _) = HeightmapDecoder.ResolveWinningLandscape(c.FormKey, cache, priority);
            var z = land?.VertexHeightMap is { } v ? HeightmapDecoder.DecodeHeights(v) : null;
            if (!byWs.TryGetValue(ws.FormKey, out var d)) byWs[ws.FormKey] = d = new();
            d[(c.Grid.Point.X, c.Grid.Point.Y)] = new CellWater(c.WaterHeight, c.Water.FormKeyNullable, z, ctx.ModKey.FileName);
        }

        float? Eff(FormKey ws, float? h) { var e = h ?? wsInfo[ws].H; return e is { } x && x < NoWater ? x : null; }
        string TypeName(FormKey ws, FormKey? t) => (t ?? wsInfo[ws].Type)?.ToString() ?? "-";

        var rows = new List<WaterSeamRow>();
        long pairs = 0;
        foreach (var (ws, cells) in byWs)
            foreach (var ((x, y), a) in cells)
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    if (!cells.TryGetValue((x + dx, y + dy), out var b) || a.Z is null || b.Z is null) continue;
                    pairs++;
                    var ha = Eff(ws, a.H); var hb = Eff(ws, b.H);
                    if (ha is null && hb is null) continue;
                    var ta = TypeName(ws, a.Type); var tb = TypeName(ws, b.Type);
                    var sameH = ha.HasValue && hb.HasValue && Math.Abs(ha.Value - hb.Value) < 1f;
                    if (sameH && ta == tb) continue;
                    var top = Math.Max(ha ?? float.MinValue, hb ?? float.MinValue);
                    var exposed = 0;
                    for (var i = 0; i <= 32; i++)
                    {
                        var za = dx == 1 ? a.Z[32, i] : a.Z[i, 32];
                        var zb = dx == 1 ? b.Z[0, i] : b.Z[i, 0];
                        if (Math.Min(za, zb) < top) exposed++;
                    }
                    if (exposed == 0) continue;
                    var kind = (ha is null || hb is null) ? "water on one side only" : !sameH ? "different water heights" : "different water types";
                    rows.Add(new WaterSeamRow(wsInfo[ws].Name, x, y, x + dx, y + dy, kind, ha, hb, ta, tb, exposed, a.Plugin, b.Plugin));
                }
        log($"Water seam report: {pairs} neighbouring cell pairs checked, {rows.Count} with a possible water mismatch.");
        return rows.OrderByDescending(r => r.ExposedVertices).ThenBy(r => r.Worldspace).ToList();
    }

    public static void WriteCsv(IEnumerable<WaterSeamRow> rows, string path)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        w.WriteLine("worldspace,x1,y1,x2,y2,kind,height1,height2,type1,type2,exposed_vertices_of_33,winning_plugin_cell1,winning_plugin_cell2");
        foreach (var r in rows)
            w.WriteLine(string.Join(",", Q(r.Worldspace), r.X1, r.Y1, r.X2, r.Y2, Q(r.Kind),
                r.Height1?.ToString("F0") ?? "", r.Height2?.ToString("F0") ?? "", Q(r.Type1), Q(r.Type2), r.ExposedVertices, Q(r.Plugin1), Q(r.Plugin2)));
    }
}
