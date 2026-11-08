using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SeamFinder.Core;

/// <summary>
/// "Do no harm" check for the finished plugin. Every cell Groundskeeper writes is compared with its neighbours, and with how the
/// same edge looked BEFORE this plugin (the load-order winner without it). If the plugin introduced a height gap along a shared edge
/// that was not there before, the cell it wrote is repaired: its edge vertices are pulled to the neighbour's edge, the change is
/// blended inward over a few vertices so no cliff appears, the heights are re-encoded and the vertex normals the move affects are
/// recomputed. A repair that cannot be done safely (a step too steep for the heightmap format) falls back to the cell's previous
/// terrain. Cells the plugin does not write are never touched, so another mod's own seams stay that mod's.
///
/// Why it exists: a pass can edit a cell's terrain (a road merged into one cell, a winner restored) while the neighbouring cell,
/// which belongs to another plugin, keeps its old edge. Seen in game as thin dark slits along cell borders.
/// </summary>
public static class EdgeGuard
{
    public sealed record Result(int CellsAdjusted, int EdgesRepaired, int CellsReverted, IReadOnlyList<string> Messages);

    /// <summary>A gap this small is the heightmap's own rounding, not a seam (one raw VHGT step is 8 units).</summary>
    const float Tolerance = 8f;
    /// <summary>The plugin must have made the gap this much worse than it was before to count as introduced.</summary>
    const float WorseBy = 4f;
    /// <summary>Vertices over which an edge correction fades out toward the cell's interior.</summary>
    const int BlendWidth = 4;
    /// <summary>VHGT stores each step as a signed byte of 8 units; keep clear of the 1016 limit.</summary>
    const float MaxStep = 1000f;

    sealed class Cell3
    {
        public required Cell Cell;
        public required float[,] Heights;      // current (being repaired)
        public required float[,] Original;     // what the plugin held when the guard started
        public required float[,] Previous;     // the winner before this plugin
        public bool Modified;
        public bool Reverted;
    }

    public static Result Run(
        ISkyrimMod patchMod,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        string outputPluginName,
        Action<string>? log = null)
    {
        var messages = new List<string>();

        // winning cell for every exterior grid square (to find neighbours)
        var gridToCell = new Dictionary<(FormKey Ws, int X, int Y), FormKey>();
        foreach (var ctx in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            if (ctx.Record.Grid is null) continue;
            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var ws)) continue;
            gridToCell[(ws.Record.FormKey, ctx.Record.Grid.Point.X, ctx.Record.Grid.Point.Y)] = ctx.Record.FormKey;
        }

        var previousCache = new Dictionary<FormKey, float[,]?>();
        float[,]? Previous(FormKey cell)
        {
            if (previousCache.TryGetValue(cell, out var cached)) return cached;
            float[,]? best = null; var bestIdx = -1;
            foreach (var c in linkCache.ResolveAllContexts<Cell, ICellGetter>(cell, ResolveTarget.Winner))
            {
                var name = c.ModKey.FileName.String;
                if (name.Equals(outputPluginName, StringComparison.OrdinalIgnoreCase) || PluginSplitWriter.IsSplitSiblingOf(name, outputPluginName)
                    || TrustResolver.IsSiblingToolOutput(name)) continue;
                if (c.Record.Landscape?.VertexHeightMap is not { } vh) continue;
                var idx = priorityIndex.GetValueOrDefault(c.ModKey, -1);
                if (idx > bestIdx) { bestIdx = idx; best = HeightmapDecoder.DecodeHeights(vh); }
            }
            return previousCache[cell] = best;
        }

        // the cells this plugin writes, with their heights
        var mine = new Dictionary<(FormKey Ws, int X, int Y), Cell3>();
        foreach (var ws in patchMod.Worldspaces)
            foreach (var block in ws.SubCells)
                foreach (var sub in block.Items)
                    foreach (var cell in sub.Items)
                    {
                        if (cell.Grid is null || cell.Landscape?.VertexHeightMap is not { } vh) continue;
                        var prev = Previous(cell.FormKey);
                        if (prev is null) continue; // brand new terrain: nothing to compare with
                        var h = HeightmapDecoder.DecodeHeights(vh);
                        mine[(ws.FormKey, cell.Grid.Point.X, cell.Grid.Point.Y)] = new Cell3
                        { Cell = cell, Heights = h, Original = (float[,])h.Clone(), Previous = prev };
                    }

        // neighbour's heights as they will be in the game: ours if we write it, else the winner before us
        float[,]? Final((FormKey Ws, int X, int Y) key, out float[,]? previous, out Cell3? ours)
        {
            if (mine.TryGetValue(key, out ours)) { previous = ours.Previous; return ours.Heights; }
            previous = null;
            if (!gridToCell.TryGetValue(key, out var fk)) return null;
            previous = Previous(fk);
            return previous;
        }

        int edgesRepaired = 0;
        var directions = new (int Dx, int Dy)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var round = 0; round < 6; round++)
        {
            var changed = false;
            foreach (var (key, c) in mine.ToList())
            {
                if (c.Reverted) continue;
                foreach (var (dx, dy) in directions)
                {
                    var nKey = (key.Ws, key.X + dx, key.Y + dy);
                    var nFinal = Final(nKey, out var nPrev, out var nOurs);
                    if (nFinal is null || nPrev is null) continue;
                    var gapNow = EdgeGap(c.Heights, dx, dy, nFinal);
                    var gapBefore = EdgeGap(c.Previous, dx, dy, nPrev);
                    if (gapNow <= Tolerance || gapNow <= gapBefore + WorseBy) continue; // not introduced by us

                    // adjust the side that moved further from how it was; if the neighbour is not ours that is always this cell
                    var adjustNeighbour = nOurs is not null && !nOurs.Reverted
                        && EdgeShift(nOurs.Heights, nOurs.Previous, -dx, -dy) > EdgeShift(c.Heights, c.Previous, dx, dy);
                    if (adjustNeighbour)
                    {
                        PullEdge(nOurs!.Heights, -dx, -dy, c.Heights);
                        nOurs.Modified = true;
                    }
                    else
                    {
                        PullEdge(c.Heights, dx, dy, nFinal);
                        c.Modified = true;
                    }
                    edgesRepaired++; changed = true;
                    messages.Add($"  edge repaired at ({key.X},{key.Y}) toward ({nKey.Item2},{nKey.Item3}): gap {gapNow:F0} -> {EdgeGap(c.Heights, dx, dy, Final(nKey, out _, out _)!):F0} units (was {gapBefore:F0} before this plugin)");
                }
            }
            if (!changed) break;
        }

        // too steep for the heightmap format -> do not risk it: put the cell back as it was before this plugin
        int reverted = 0;
        foreach (var (key, c) in mine)
        {
            if (!c.Modified) continue;
            if (MaxAdjacentStep(c.Heights) > MaxStep)
            {
                c.Heights = (float[,])c.Previous.Clone(); c.Reverted = true; reverted++;
                messages.Add($"  cell ({key.X},{key.Y}): the repair would be too steep for the terrain format; the cell keeps its terrain from before this plugin.");
            }
        }

        // write the repaired heights back, with matching normals
        int adjusted = 0;
        foreach (var (key, c) in mine)
        {
            if (!c.Modified && !c.Reverted) continue;
            var (offset, deltas, decoded) = Encode(c.Heights);
            var land = c.Cell.Landscape!;
            land.VertexHeightMap = new LandscapeVertexHeightMap { Offset = offset, HeightMap = new Array2d<sbyte>(deltas) };
            RecomputeNormals(land, c.Original, decoded);
            adjusted++;
        }

        if (edgesRepaired > 0 || reverted > 0)
            log?.Invoke($"Seam guard: repaired {edgesRepaired} cell edge(s) in {adjusted} cell(s) that would otherwise have shown a new seam; {reverted} cell(s) kept their previous terrain.");
        return new Result(adjusted, edgesRepaired, reverted, messages);
    }

    // ---- geometry ----

    /// <summary>Largest height difference along the edge shared with a neighbour in direction (dx,dy).</summary>
    static float EdgeGap(float[,] cell, int dx, int dy, float[,] neighbour)
    {
        float max = 0;
        for (var i = 0; i <= 32; i++)
        {
            var (cx, cy, nx, ny) = EdgePair(dx, dy, i);
            max = Math.Max(max, Math.Abs(cell[cx, cy] - neighbour[nx, ny]));
        }
        return max;
    }

    /// <summary>How far an edge has moved from the previous winner's edge.</summary>
    static float EdgeShift(float[,] now, float[,] before, int dx, int dy)
    {
        float max = 0;
        for (var i = 0; i <= 32; i++)
        {
            var (cx, cy, _, _) = EdgePair(dx, dy, i);
            max = Math.Max(max, Math.Abs(now[cx, cy] - before[cx, cy]));
        }
        return max;
    }

    // vertex i on this cell's edge toward (dx,dy), and the matching vertex on the neighbour
    static (int Cx, int Cy, int Nx, int Ny) EdgePair(int dx, int dy, int i) => (dx, dy) switch
    {
        (1, 0) => (32, i, 0, i),
        (-1, 0) => (0, i, 32, i),
        (0, 1) => (i, 32, i, 0),
        _ => (i, 0, i, 32),
    };

    /// <summary>Move this cell's edge onto the neighbour's edge and fade the change out over the next vertices inward.</summary>
    static void PullEdge(float[,] cell, int dx, int dy, float[,] neighbour)
    {
        for (var i = 0; i <= 32; i++)
        {
            var (cx, cy, nx, ny) = EdgePair(dx, dy, i);
            var delta = neighbour[nx, ny] - cell[cx, cy];
            if (Math.Abs(delta) < 0.001f) continue;
            for (var k = 0; k < BlendWidth; k++)
            {
                var x = cx - dx * k; var y = cy - dy * k;
                if (x is < 0 or > 32 || y is < 0 or > 32) break;
                cell[x, y] += delta * (1f - (float)k / BlendWidth);
            }
        }
    }

    static float MaxAdjacentStep(float[,] h)
    {
        float max = 0;
        for (var x = 0; x <= 32; x++)
            for (var y = 0; y <= 32; y++)
            {
                if (x > 0) max = Math.Max(max, Math.Abs(h[x, y] - h[x - 1, y]));
                if (y > 0) max = Math.Max(max, Math.Abs(h[x, y] - h[x, y - 1]));
            }
        return max;
    }

    /// <summary>Heights back into VHGT (offset + signed 8-unit steps), each step measured from the already-quantised previous vertex so
    /// rounding never accumulates along a row. Returns the quantised heights the game will actually see.</summary>
    static (float Offset, sbyte[,] Deltas, float[,] Decoded) Encode(float[,] target)
    {
        var deltas = new sbyte[33, 33];
        var dec = new float[33, 33];
        var offset = target[0, 0] / 8f;
        dec[0, 0] = offset * 8f;
        for (var y = 0; y <= 32; y++)
        {
            if (y > 0)
            {
                var d0 = Step((target[0, y] - dec[0, y - 1]) / 8f);
                deltas[0, y] = d0; dec[0, y] = dec[0, y - 1] + d0 * 8f;
            }
            for (var x = 1; x <= 32; x++)
            {
                var d = Step((target[x, y] - dec[x - 1, y]) / 8f);
                deltas[x, y] = d; dec[x, y] = dec[x - 1, y] + d * 8f;
            }
        }
        return (offset, deltas, dec);
    }

    static sbyte Step(float step) => (sbyte)Math.Clamp((int)Math.Round(step, MidpointRounding.AwayFromZero), sbyte.MinValue, sbyte.MaxValue);

    // ---- normals ----

    /// <summary>Recompute the stored normal of every vertex whose surroundings changed. Skyrim stores them as signed bytes (value/127);
    /// central differences over the 128-unit vertex spacing agree with the game's own to about 1 degree on vanilla terrain.</summary>
    static void RecomputeNormals(Landscape land, float[,] before, float[,] after)
    {
        if (land.VertexNormals is null) return;
        bool Moved(int x, int y) => x is >= 0 and <= 32 && y is >= 0 and <= 32 && Math.Abs(after[x, y] - before[x, y]) > 0.5f;
        for (var x = 0; x <= 32; x++)
            for (var y = 0; y <= 32; y++)
            {
                var touched = false;
                for (var ox = -1; ox <= 1 && !touched; ox++) for (var oy = -1; oy <= 1 && !touched; oy++) touched = Moved(x + ox, y + oy);
                if (!touched) continue;
                float L(int a, int b) => after[Math.Clamp(a, 0, 32), Math.Clamp(b, 0, 32)];
                var spanX = (x is 0 or 32 ? 1 : 2) * 128f; var spanY = (y is 0 or 32 ? 1 : 2) * 128f;
                double nx = -(L(x + 1, y) - L(x - 1, y)) / spanX, ny = -(L(x, y + 1) - L(x, y - 1)) / spanY, nz = 1.0;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                land.VertexNormals[x, y] = new P3UInt8(ToByte(nx / len), ToByte(ny / len), ToByte(nz / len));
            }
    }

    static byte ToByte(double v) => unchecked((byte)(sbyte)Math.Clamp((int)Math.Round(v * 127.0), -127, 127));
}
