using System.Globalization;
using SeamFinder.Core;

namespace LandscapeHybrid;

public enum ProblemKind { HeightSeam, TextureSeam, WaterSeam, FloatingObject }

/// <summary>One thing that may still be wrong. A seam sits on the shared edge of cell (X,Y) and cell (X2,Y2); a floating
/// object sits inside cell (X,Y) (X2,Y2 equal X,Y). Severity is in the unit of its kind (height units, exposed vertices, ...).
/// OfficialOnly: both sides come from Bethesda's own plugins, so it is how the base game ships, not something a mod broke.</summary>
public sealed record ProblemMark(string Worldspace, int X, int Y, int X2, int Y2, ProblemKind Kind, double Severity, string Detail, bool OfficialOnly = false)
{
    public string ConsoleCommand => $"cow {Worldspace} {X} {Y}";
}

public sealed record ProblemMapData(List<ProblemMark> Marks, Dictionary<string, List<OverviewCell>> Overview, int PluginCount, IReadOnlyList<string> Notes)
{
    /// <summary>The load order the scan used (after any "without" / "extra" changes): the 3D view reads terrain from the same one.</summary>
    public IReadOnlyList<Mo2Resolver.ResolvedPlugin> LoadOrder { get; init; } = Array.Empty<Mo2Resolver.ResolvedPlugin>();
}

public static partial class HybridRunner
{
    /// <summary>
    /// Read-only scan of the load order as the game will see it (your outputs included, so it shows what is STILL wrong):
    /// height seams, texture-layer seams, possible water seams and floating objects, plus a terrain overview to draw them on.
    /// <paramref name="withoutPlugins"/> are left out ("what if this were off"). <paramref name="extraPlugins"/> are appended at the end of the load order, to preview an output before enabling it.
    /// </summary>
    public static ProblemMapData ProblemMap(HybridSettings s, Action<string> log, IEnumerable<string>? extraPlugins = null, string floatingWorldspace = "Tamriel", IEnumerable<string>? withoutPlugins = null)
    {
        var env = Resolve(s, log);
        var order = env.All.ToList();
        foreach (var off in withoutPlugins ?? Array.Empty<string>())
            if (order.RemoveAll(p => p.FileName.Equals(off, StringComparison.OrdinalIgnoreCase)) > 0) log($"Scanning as if {off} were disabled.");
        foreach (var path in extraPlugins ?? Array.Empty<string>())
        {
            var name = Path.GetFileName(path);
            order.RemoveAll(p => p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            order.Add(new Mo2Resolver.ResolvedPlugin(name, path));
            log($"Previewing {name} at the end of the load order.");
        }

        var notes = new List<string>();
        T Guard<T>(string label, Func<T> run, T fallback)
        {
            try { return run(); }
            catch (Exception ex)
            {
                var m = $"{label} could not be scanned and is missing from the map: {ex.GetType().Name}: {ex.Message}";
                lock (notes) notes.Add(m);
                log(m);
                return fallback;
            }
        }

        // Each detector stages its own copy of every plugin: three at a time keeps disk and memory use reasonable on a big list.
        var gate = new SemaphoreSlim(3);
        Task<T> Gated<T>(Func<T> run) => Task.Run(() => { gate.Wait(); try { return run(); } finally { gate.Release(); } });
        var seamT = Gated(() => Guard("Height seams", () => SeamDetector.RunForResolvedPlugins(order, log).ReportCsvLines, new List<string>()));
        var texT = Gated(() => Guard("Texture seams", () => TextureLayerDetector.RunForResolvedPlugins(order, log).ReportCsvLines, new List<string>()));
        var waterT = Gated(() => Guard("Water seams", () => WaterSeamReport.Run(order, log), new List<WaterSeamRow>()));
        var floatT = Gated(() => Guard("Floating objects", () => FloatingObjectFixer.RunDetectionForResolvedPlugins(
            order, log, FloatingObjectFixer.DefaultThresholdUnits, floatingWorldspace, env.Asset).ReportCsvLines, new List<string>()));
        var overT = Gated(() => Guard("Terrain overview", () => TerrainOverview.Run(order, log), new OverviewResult(new(), new())));
        Task.WaitAll(seamT, texT, waterT, floatT, overT);

        var marks = new List<ProblemMark>();
        foreach (var f in Rows(seamT.Result))
        {
            // Worldspace,Edge,CellA_X,CellA_Y,PluginA,CellB_X,CellB_Y,PluginB,SamePlugin,ModVsBaseEdge,LocalRoughness,MaxDeltaUnits,WorstVertex
            if (f.Length < 12 || !Int(f[2], out var ax) || !Int(f[3], out var ay) || !Int(f[5], out var bx) || !Int(f[6], out var by) || !Dbl(f[11], out var d)) continue;
            marks.Add(new(f[0], ax, ay, bx, by, ProblemKind.HeightSeam, d, $"Ground height steps by {d:F0} units across the edge: {f[4]} | {f[7]}.", Official(f[4]) && Official(f[7])));
        }
        foreach (var f in Rows(texT.Result))
        {
            // Worldspace,Edge,CellA_X,CellA_Y,PluginA,CellB_X,CellB_Y,PluginB,SamePlugin,QuadrantA,QuadrantB,MismatchKind,DominantA,DominantB,...
            if (f.Length < 12 || !Int(f[2], out var ax) || !Int(f[3], out var ay) || !Int(f[5], out var bx) || !Int(f[6], out var by)) continue;
            var foreign = f[11].Equals("Foreign", StringComparison.OrdinalIgnoreCase);
            var kindText = foreign ? "no texture in common" : "a bridging texture exists";
            marks.Add(new(f[0], ax, ay, bx, by, ProblemKind.TextureSeam, foreign ? 2 : 1,
                $"Ground texture changes abruptly across the edge ({kindText}): {f[4]} | {f[7]}.", Official(f[4]) && Official(f[7])));
        }
        foreach (var r in waterT.Result)
            marks.Add(new(r.Worldspace, r.X1, r.Y1, r.X2, r.Y2, ProblemKind.WaterSeam, r.ExposedVertices,
                $"Possible water seam, {r.Kind}: {r.ExposedVertices} of 33 edge vertices below the higher water plane ({r.Plugin1} | {r.Plugin2}).", Official(r.Plugin1) && Official(r.Plugin2)));
        foreach (var f in Rows(floatT.Result))
        {
            // Worldspace,CellX,CellY,OwnerPlugin,RefFormID,BaseFormID,BaseEditorID,WorldX,WorldY,RefZ,TerrainZ,Delta
            if (f.Length < 12 || !Int(f[1], out var x) || !Int(f[2], out var y) || !Dbl(f[11], out var delta)) continue;
            if (delta <= 0) continue; // sunk objects are usually deliberate embedding; only floating ones are listed
            marks.Add(new(f[0], x, y, x, y, ProblemKind.FloatingObject, delta,
                $"{f[6]} ({f[4]}) floats {delta:F0} units above the ground and was not auto-corrected (over the safety cap or not confirmed by collision)."));
        }
        log($"Problem map: {marks.Count(m => m.Kind == ProblemKind.HeightSeam)} height seam(s), {marks.Count(m => m.Kind == ProblemKind.TextureSeam)} texture seam(s), " +
            $"{marks.Count(m => m.Kind == ProblemKind.WaterSeam)} possible water seam(s), {marks.Count(m => m.Kind == ProblemKind.FloatingObject)} floating object(s).");
        // The seam detectors label a worldspace by FormKey, the water and floating ones by EditorID: use the EditorID everywhere.
        var names = overT.Result.Names;
        for (var i = 0; i < marks.Count; i++)
            if (names.TryGetValue(marks[i].Worldspace, out var nice)) marks[i] = marks[i] with { Worldspace = nice };
        return new ProblemMapData(marks, overT.Result.Cells, order.Count, notes) { LoadOrder = order };
    }

    public static void WriteProblemCsv(ProblemMapData d, string path)
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        w.WriteLine("kind,worldspace,x,y,x2,y2,severity,official_only,console_command,detail");
        foreach (var m in d.Marks.OrderBy(m => m.Kind).ThenByDescending(m => m.Severity))
            w.WriteLine(string.Join(",", m.Kind, Q(m.Worldspace), m.X, m.Y, m.X2, m.Y2, m.Severity.ToString("F1", CultureInfo.InvariantCulture), m.OfficialOnly, Q(m.ConsoleCommand), Q(m.Detail)));
    }

    static readonly HashSet<string> OfficialPlugins = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    static bool Official(string plugin) =>
        OfficialPlugins.Contains(plugin) || (plugin.StartsWith("cc", StringComparison.OrdinalIgnoreCase) && (plugin.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || plugin.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)));

    static bool Int(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    static bool Dbl(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>The rows of a detector CSV (header skipped), each split on commas outside quotes.</summary>
    static IEnumerable<string[]> Rows(IEnumerable<string> lines) => lines.Skip(1).Where(l => l.Length > 0).Select(SplitCsv);

    static string[] SplitCsv(string line)
    {
        var parts = new List<string>(); var sb = new System.Text.StringBuilder(); var q = false;
        foreach (var ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }
}
