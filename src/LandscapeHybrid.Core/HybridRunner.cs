using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using PatchForeman;
using RoadMaskMerger;
using SeamFinder.Core;

namespace LandscapeHybrid;

public enum ModManager { ModOrganizer2, DirectOrVortex }

public sealed class HybridSettings
{
    /// <summary>MO2 reads modlist.txt/plugins.txt of a profile. DirectOrVortex reads the game's own Data folder and
    /// plugins.txt (Vortex deploys its mods into that folder, so it is the same case as a plain install).</summary>
    public ModManager ModManager { get; set; } = ModManager.ModOrganizer2;
    public string Mo2InstancePath { get; set; } = "";
    public string Mo2Profile { get; set; } = "";
    /// <summary>MO2: game install or its Data folder, empty = read from ModOrganizer.ini.
    /// Direct/Vortex: the game's install or Data folder, empty = detect.</summary>
    public string GameDataPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string OutputPluginName { get; set; } = "Groundskeeper.esp";

    public bool Seams { get; set; } = true;
    public bool TextureLayers { get; set; } = true;
    public bool Floating { get; set; }
    public float FloatingThresholdUnits { get; set; } = 96f;
    public string FloatingWorldspace { get; set; } = "Tamriel";
    public string RoadSourcePlugin { get; set; } = "";
    public string RoadMaskFolder { get; set; } = "";
    public string RoadWorldspace { get; set; } = "Tamriel";
    public bool TrustNorthernRoads { get; set; }
    /// <summary>Trust Northern Vanaheimr Roads (Northern_Vanaheimr_Roads.esp and its patches) as a terrain source: its terrain is kept.</summary>
    public bool TrustVanaheimrRoads { get; set; }
    /// <summary>The preferred road mod: a road mod that EDITS THE LANDSCAPE. Its terrain wins wherever it conflicts, ahead of every other
    /// mod (its own patches rank above it, as patches always do). Road mods that only change textures do not count. Empty = none.</summary>
    public string PreferredRoadMod { get; set; } = "";
    public bool MatchNeighborsToTrustedChain { get; set; }
    /// <summary>Plugins whose terrain/water edits the user chose to REVERT to the trusted data below them.
    /// Every other plugin that overwrites trusted terrain or water is kept (treated as trusted). Default: none.</summary>
    public List<string> RevertPlugins { get; set; } = new();
    /// <summary>Terrain winners in the user's order (first = highest). Wherever these mods conflict, the highest one wins,
    /// and each beats every mod not in the list; a winner is also kept (never offered for revert). Empty = load order decides.</summary>
    public List<string> TerrainWinners { get; set; } = new();
    /// <summary>Water mods to protect, in priority order (first wins): wherever another plugin has overwritten the water of
    /// one of these, its water is restored. Empty = no named water protection.</summary>
    public List<string> WaterMods { get; set; } = new();
    /// <summary>True = run every pass and the merge but write nothing into the output folder.</summary>
    public bool DryRun { get; set; }

    /// <summary>After Generate, open the problem map on the result (the map is also always available from its own button).</summary>
    public bool OpenProblemMapAfter { get; set; }
}

public sealed record HybridResult(
    bool Ran, int SeamCells, int TextureCells, int RoadCells, int FloatingRefs, int MergedCells,
    IReadOnlyList<string> OutputPaths, IReadOnlyList<string> Notes);

/// <param name="Plugin">A plugin that currently overwrites terrain or water a trusted plugin authored.</param>
/// <param name="RestoresTo">The trusted plugin whose terrain it would be reverted to (last one seen).</param>
public sealed record RevertCandidate(string Plugin, int Cells, int MaxUnits, string RestoresTo, IReadOnlyList<string> SampleCells,
    int WaterCells = 0, string WaterRestoresTo = "");

/// <param name="WaterModChoices">Plugins in the load order that look like water mods (known names, or "water" in the name).</param>
/// <param name="LoadOrder">The plugins of the load order, lowest (loaded first) to highest (loaded last).</param>
public sealed record TerrainPlan(IReadOnlyList<RevertCandidate> Candidates, int SkippedWater, int SkippedReference,
    IReadOnlyList<string> WaterModChoices, IReadOnlyList<string> RoadModChoices, IReadOnlyList<string> LoadOrder);

/// <summary>
/// One run: resolve the load order once (Mod Organizer 2, or the game's Data folder for Vortex and plain installs), run
/// the selected terrain passes over it (each against the base list, like the standalone tools), then fold their patches
/// into one plugin with the PatchForeman merge.
/// Works alongside Cl3mus33's Landscape Fixer: its plugin is never a terrain source (see
/// TrustResolver.ThirdPartyGeneratedOutputs) and is left out of the source list, so run order between the
/// two tools does not matter and neither reads the other's output.
///
/// Trust is explicit: <see cref="Plan"/> lists every plugin that overwrites trusted terrain or water; a run keeps
/// all of them unless the user listed them in <see cref="HybridSettings.RevertPlugins"/>.
/// </summary>
public static partial class HybridRunner
{
    public static readonly string[] PassPluginNames =
        { "LandscapeSeamFixes.esp", "LandscapeTextureFixes.esp", "RoadMaskMerge.esp", "FloatingObjectFixes.esp", "LandscapeSeamPlan.esp" };

    static readonly string[] KnownWaterMods = { "CS Water Mod.esp", "Water for ENB.esp", "RealisticWaterTwo.esp", "Simplicity of Sea.esp" };

    /// <summary>Names an earlier version of this tool wrote its merged plugin under; still recognised as our own output.</summary>
    static readonly string[] LegacyOutputNames = { "LandscapeTerrain.esp" };

    public static bool IsOwnOutput(string fileName, string mergedName)
    {
        if (PassPluginNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return true;
        foreach (var legacy in LegacyOutputNames)
        {
            var ls = Path.GetFileNameWithoutExtension(legacy);
            var fs = Path.GetFileNameWithoutExtension(fileName);
            if (fs.Equals(ls, StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(fs, "^" + Regex.Escape(ls) + @"_\d+$", RegexOptions.IgnoreCase)) return true;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var mergedStem = Path.GetFileNameWithoutExtension(mergedName);
        return stem.Equals(mergedStem, StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(stem, "^" + Regex.Escape(mergedStem) + @"_\d+$", RegexOptions.IgnoreCase);
    }

    static readonly Regex RestoreLine = new(
        @"Restored \[(?<ws>[^\]]+)\] \((?<x>-?\d+),(?<y>-?\d+)\): (?<by>.+?) had overwritten (?<to>.+?)'s terrain by up to (?<u>\d+) units",
        RegexOptions.Compiled);
    static readonly Regex WaterLine = new(
        @"Restored genuine water \[(?<ws>[^\]]+)\] \((?<x>-?\d+),(?<y>-?\d+)\): (?<by>.+?) had lost (?<to>.+?)'s water data",
        RegexOptions.Compiled);
    static readonly Regex SkipSummary = new(
        @"skipped (?<w>\d+) with their own water plane; skipped (?<r>\d+) where a placed reference", RegexOptions.Compiled);

    internal sealed record Env(List<Mo2Resolver.ResolvedPlugin> All, List<Mo2Resolver.ResolvedPlugin> BaseOrder, Func<string, string?> Asset);

    internal static Env Resolve(HybridSettings s, Action<string> log)
    {
        List<Mo2Resolver.ResolvedPlugin> all;
        Func<string, string?> asset;
        if (s.ModManager == ModManager.ModOrganizer2)
        {
            var game = string.IsNullOrWhiteSpace(s.GameDataPath) ? ReadGamePathFromIni(s.Mo2InstancePath) : s.GameDataPath;
            var resolved = Mo2Resolver.Resolve(s.Mo2InstancePath, s.Mo2Profile, game);
            log($"Mod Organizer 2: resolved {resolved.LoadOrder.Count} active plugins ({resolved.MissingPlugins.Count} missing).");
            all = resolved.LoadOrder;
            asset = rel => resolved.ResolveDataFile(rel);
        }
        else
        {
            var data = DirectDataFolder(s);
            if (!File.Exists(Path.Combine(data, "Skyrim.esm")))
                throw new InvalidOperationException($"Skyrim.esm was not found in '{data}'. Pick the game's Data folder (Vortex deploys its mods into it).");
            all = new();
            foreach (var l in Mutagen.Bethesda.Plugins.Order.LoadOrder.GetLoadOrderListings(GameRelease.SkyrimSE, data, throwOnMissingMods: false))
            {
                if (!l.Enabled) continue;
                var name = l.ModKey.FileName.String;
                var path = Path.Combine(data, name);
                if (File.Exists(path)) all.Add(new Mo2Resolver.ResolvedPlugin(name, path));
            }
            log($"Direct / Vortex: {all.Count} active plugins in {data}.");
            asset = rel => { var p = Path.Combine(data, rel); return File.Exists(p) ? p : null; };
        }
        var baseOrder = all
            .Where(p => !p.FileName.Equals("LandscapeFixer.esp", StringComparison.OrdinalIgnoreCase)
                     && !IsOwnOutput(p.FileName, s.OutputPluginName))
            .ToList();
        return new Env(all, baseOrder, asset);
    }

    /// <summary>Direct / Vortex: the game's Data folder, from the chosen game folder or found automatically.</summary>
    public static string DirectDataFolder(HybridSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.GameDataPath)) return Mo2Resolver.NormalizeGameDataPath(s.GameDataPath);
        if (DetectGameData() is { } found) return found;
        throw new InvalidOperationException("Pick the Skyrim install folder (or its Data folder): it could not be found automatically.");
    }

    /// <summary>Finds every plugin that currently overwrites terrain or water a trusted plugin authored, and what it would
    /// be reverted to. Writes nothing. The user picks which of these to revert; the rest are kept.</summary>
    public static TerrainPlan Plan(HybridSettings s, Action<string> log)
    {
        var env = Resolve(s, log);
        return PlanFrom(env.BaseOrder, s, log);
    }

    static readonly Dictionary<string, List<string>> PlanCache = new();

    /// <summary>Identifies one SeamFixer plan run: every plugin file (path, size, write time) and every setting that changes its output.</summary>
    static string PlanKey(List<Mo2Resolver.ResolvedPlugin> order, HybridSettings s, List<string> trusted, List<string> winners)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in order)
        {
            var fi = new FileInfo(p.FilePath);
            sb.Append(p.FileName).Append('|').Append(p.FilePath).Append('|').Append(fi.Exists ? fi.Length : -1).Append('|').Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1).Append(';');
        }
        sb.Append("#T:").Append(string.Join(",", trusted)).Append("#W:").Append(string.Join(",", winners))
          .Append("#N:").Append(s.TrustNorthernRoads).Append("#WM:").Append(string.Join(",", s.WaterMods));
        return sb.ToString();
    }

    static TerrainPlan PlanFrom(List<Mo2Resolver.ResolvedPlugin> baseOrder, HybridSettings s, Action<string> log)
    {
        // Keeping a plugin makes it trusted, which can expose NEW overwriters of it (A overwrites trusted T; once A is kept,
        // B overwriting A shows up). Iterate until no new overwriter appears, so "nothing ticked" really means "nothing changes".
        var revert = new HashSet<string>(s.RevertPlugins, StringComparer.OrdinalIgnoreCase);
        var winnersList = EffectiveWinners(s, baseOrder.Select(p => p.FileName));
        var found = new Dictionary<string, (int Cells, int Max, string To, List<string> Samples)>(StringComparer.OrdinalIgnoreCase);
        var water = new Dictionary<string, (int Cells, string To)>(StringComparer.OrdinalIgnoreCase);
        int skippedWater = 0, skippedRef = 0;
        SeamSession? session = null;
        try
        {
        for (var pass = 1; pass <= 8; pass++)
        {
            var trusted = found.Keys.Where(k => !revert.Contains(k)).Concat(winnersList).Concat(TrustedFamilies(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Same plugin files + same trust/winner/water inputs give the same SeamFixer log, so a repeat (Scan, then Generate;
            // or a re-tick that revisits an earlier trust set) reuses it instead of re-reading ~750 MB of plugins.
            var key = PlanKey(baseOrder, s, trusted, winnersList);
            List<string> lines;
            lock (PlanCache) PlanCache.TryGetValue(key, out lines!);
            if (lines is null)
            {
                lines = new List<string>();
                // One staged copy + environment serves every pass of this plan; only the trust inputs change between passes,
                // and nothing is built or written (the log is all the plan reads).
                session ??= SeamSession.Open(baseOrder, m => lines.Add(m));
                session.Run("LandscapeSeamPlan.esp", Path.GetTempPath(), m => lines.Add(m), s.TrustNorthernRoads, waterTrust: WaterOptions(s),
                    customTrustedPlugins: trusted, priorityOverNorthernRoadsPlugins: winnersList, planOnly: true);
                lock (PlanCache) { if (PlanCache.Count >= 32) PlanCache.Clear(); PlanCache[key] = lines; }
            }

            foreach (var tl in lines.Where(l => l.StartsWith("[timing]"))) log($"  plan pass {pass} {tl}");
            var added = 0;
            var thisPass = new Dictionary<string, (int Cells, int Max, string To, List<string> Samples)>(StringComparer.OrdinalIgnoreCase);
            var thisWater = new Dictionary<string, (int Cells, string To)>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines)
            {
                if (SkipSummary.Match(l) is { Success: true } sm)
                {
                    skippedWater = int.Parse(sm.Groups["w"].Value);
                    skippedRef = int.Parse(sm.Groups["r"].Value);
                }
                if (WaterLine.Match(l) is { Success: true } wm)
                {
                    var wp = wm.Groups["by"].Value;
                    thisWater.TryGetValue(wp, out var wc);
                    thisWater[wp] = (wc.Cells + 1, wm.Groups["to"].Value);
                    continue;
                }
                if (RestoreLine.Match(l) is not { Success: true } m) continue;
                var plugin = m.Groups["by"].Value;
                var units = int.Parse(m.Groups["u"].Value);
                if (!thisPass.TryGetValue(plugin, out var cur)) cur = (0, 0, "", new List<string>());
                cur.Cells++;
                cur.Max = Math.Max(cur.Max, units);
                cur.To = m.Groups["to"].Value;
                if (cur.Samples.Count < 4) cur.Samples.Add($"{m.Groups["ws"].Value} ({m.Groups["x"].Value},{m.Groups["y"].Value})");
                thisPass[plugin] = cur;
            }
            foreach (var (plugin, st) in thisPass)
            {
                if (!found.TryGetValue(plugin, out var old)) { found[plugin] = st; added++; }
                else if (st.Cells > old.Cells) found[plugin] = st; // keep the fuller picture
            }
            foreach (var (plugin, w) in thisWater)
            {
                if (!found.ContainsKey(plugin)) { found[plugin] = (0, 0, "", new List<string>()); added++; }
                if (!water.TryGetValue(plugin, out var ow) || w.Cells > ow.Cells) water[plugin] = w;
            }
            if (added == 0) break;
        }
        }
        finally { session?.Dispose(); }
        var winners =new HashSet<string>(winnersList, StringComparer.OrdinalIgnoreCase);
        var list = found.Where(kv => !winners.Contains(kv.Key)).Select(kv =>
            {
                water.TryGetValue(kv.Key, out var w);
                return new RevertCandidate(kv.Key, kv.Value.Cells, kv.Value.Max, kv.Value.To, kv.Value.Samples, w.Cells, w.To ?? "");
            })
            .OrderByDescending(c => c.Cells + c.WaterCells).ToList();
        log($"Terrain plan: {list.Count} plugin(s) overwrite trusted terrain or water ({list.Sum(c => c.Cells)} terrain, {list.Sum(c => c.WaterCells)} water cell(s)).");
        var present = baseOrder.Select(p => p.FileName).ToList();
        // the original tools' four named water mods first, in their priority order, then anything else with "water" in its name
        var waterChoices = KnownWaterMods.Where(k => present.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Concat(present.Where(n => n.Contains("water", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var knownRoad = new[] { "Northern Roads.esp", "Northern_Vanaheimr_Roads.esp" };
        var roadChoices = knownRoad.Where(k => present.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Concat(present.Where(n => n.Contains("road", StringComparison.OrdinalIgnoreCase) && !n.Contains(" - ") && !HybridRunner.IsOwnOutput(n, s.OutputPluginName)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new TerrainPlan(list, skippedWater, skippedRef, waterChoices, roadChoices, baseOrder.Select(p => p.FileName).ToList());
    }

    /// <summary>Read-only: lists neighbouring cells whose water disagrees where the shared edge is below the water plane (candidate
    /// "random squares of water"). Writes nothing but the optional CSV.</summary>
    public static IReadOnlyList<WaterSeamRow> WaterSeams(HybridSettings s, Action<string> log, string? csvPath = null)
    {
        var env = Resolve(s, log);
        var rows = WaterSeamReport.Run(env.BaseOrder, log);
        if (!string.IsNullOrWhiteSpace(csvPath)) { WaterSeamReport.WriteCsv(rows, csvPath); log($"Wrote {csvPath}"); }
        return rows;
    }

    /// <summary>Explains the road inputs in plain words. Null when nothing is entered; a message starting with "Problem" when the road
    /// pass would fail; otherwise a short confirmation. The masks are ACMOS Road Generator's road images, laid out as
    /// Roads\&lt;worldspace&gt;\&lt;worldspace&gt;.32.&lt;x&gt;.&lt;y&gt;.png (or Paths Only).</summary>
    public static string? CheckRoadInputs(string roadPlugin, string maskFolder, string worldspace)
    {
        if (roadPlugin.Length == 0 && maskFolder.Length == 0) return null;
        if (roadPlugin.Length == 0) return "The road pass is skipped: no road source plugin is named.";
        if (maskFolder.Length == 0) return "Problem: the road pass needs the road mask folder (ACMOS Road Generator's road images, Nexus mod 79205).";
        if (!Directory.Exists(maskFolder)) return "Problem: the road mask folder does not exist.";
        var ws = (worldspace.Length == 0 ? "Tamriel" : worldspace).ToLowerInvariant();
        var roads = Path.Combine(maskFolder, "Roads", ws);
        var paths = Path.Combine(maskFolder, "Paths Only", ws);
        if (Directory.Exists(roads) && Directory.EnumerateFiles(roads, "*.png").Any())
            return $"Road masks found for '{ws}'.";
        if (Directory.Exists(paths) && Directory.EnumerateFiles(paths, "*.png").Any())
            return $"Problem: only 'Paths Only' masks were found for '{ws}'; the road pass looks in Roads\\{ws}.";
        return $"Problem: no road images in '{maskFolder}' for worldspace '{ws}'. Pick the folder that contains 'Roads' and 'Paths Only' (each with a '{ws}' folder of .png tiles).";
    }

    /// <summary>The terrain winners actually used: the preferred road mod's family first (its patches above it, later-loaded patch
    /// first), then the user's own winners. Names only; a plugin not in the load order is simply ignored by the passes.</summary>
    static List<string> EffectiveWinners(HybridSettings s, IEnumerable<string> loadOrderNames)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.PreferredRoadMod))
        {
            var pref = s.PreferredRoadMod.Trim();
            var stem = Path.GetFileNameWithoutExtension(pref);
            var order = loadOrderNames.ToList();
            var patches = order.Where(n => n.StartsWith(stem + " -", StringComparison.OrdinalIgnoreCase) && !n.Equals(pref, StringComparison.OrdinalIgnoreCase))
                               .Reverse(); // later in the load order = higher
            result.AddRange(patches);
            result.Add(pref);
        }
        foreach (var w in s.TerrainWinners)
            if (!result.Contains(w, StringComparer.OrdinalIgnoreCase)) result.Add(w);
        return result;
    }

    /// <summary>Plugin-name patterns trusted as terrain sources by the Passes checkboxes (a trailing * trusts a whole family).</summary>
    static IEnumerable<string> TrustedFamilies(HybridSettings s)
    {
        if (s.TrustVanaheimrRoads) yield return "Northern_Vanaheimr_Roads*";
    }

    static WaterTrustOptions WaterOptions(HybridSettings s) =>
        s.WaterMods.Count == 0 ? WaterTrustOptions.None : new WaterTrustOptions(CustomWaterMods: s.WaterMods.ToList());

    public static HybridResult Run(HybridSettings s, Action<string> log)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(s.OutputDirectory) && s.ModManager == ModManager.ModOrganizer2 && s.Mo2InstancePath.Length > 0)
        {
            s.OutputDirectory = Mo2InstanceFinder.DefaultOutputFolder(s.Mo2InstancePath);
            log($"No output folder chosen: using the mod folder {s.OutputDirectory} (created if it does not exist; refresh MO2 to see it).");
        }
        var direct = s.ModManager != ModManager.ModOrganizer2;
        // Without a mod manager the plugin has to sit in the game's Data folder to load, so that is the default and it is allowed.
        if (direct && string.IsNullOrWhiteSpace(s.OutputDirectory))
        {
            s.OutputDirectory = DirectDataFolder(s);
            log($"No output folder chosen: writing straight into the game's Data folder, {s.OutputDirectory}.");
        }
        if (!s.DryRun && !direct)
        {
            // MO2 keeps the Data folder clean (its mods are virtual): refuse the game's own Data folder or anything that looks like it.
            var full = Path.GetFullPath(s.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (File.Exists(Path.Combine(full, "Skyrim.esm")) || (Directory.Exists(Path.Combine(full, "Data")) && File.Exists(Path.Combine(full, "SkyrimSE.exe")))
                || string.Equals(Path.GetFileName(full), "Data", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{s.OutputDirectory}' looks like the game's Data folder or install. Pick a dedicated output folder (for example a 'Groundskeeper Output' mod folder).");
        }
        var env = Resolve(s, log);
        var baseOrder = env.BaseOrder;

        if (env.All.Any(p => p.FileName.Equals("LandscapeFixer.esp", StringComparison.OrdinalIgnoreCase)))
            notes.Add("Landscape Fixer's LandscapeFixer.esp is active: it is left out of the terrain sources (never read as a source).");
        else
            notes.Add("LandscapeFixer.esp is not active in your load order. Run Landscape Fixer and enable its output; this tool does not need it to work.");
        log($"{baseOrder.Count} source plugins (own outputs and LandscapeFixer.esp excluded).");

        // Trust: a plugin that overwrites trusted terrain or water is KEPT (trusted) unless the user ticked it for revert.
        var customTrusted = new List<string>();
        var winnersList = EffectiveWinners(s, baseOrder.Select(p => p.FileName));
        if (s.Seams || s.TextureLayers)
        {
            var swPlan = System.Diagnostics.Stopwatch.StartNew();
            var plan = PlanFrom(baseOrder, s, log);
            log($"Plan: {swPlan.Elapsed.TotalSeconds:F1} s");
            var revert = new HashSet<string>(s.RevertPlugins, StringComparer.OrdinalIgnoreCase);
            customTrusted = plan.Candidates.Select(c => c.Plugin).Where(p => !revert.Contains(p)).Concat(winnersList).Concat(TrustedFamilies(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (winnersList.Count > 0) log($"Terrain winners, highest first: {string.Join(" > ", winnersList)}.");
            var reverting = plan.Candidates.Where(c => revert.Contains(c.Plugin)).ToList();
            log($"Keeping {customTrusted.Count} plugin(s); reverting {reverting.Count} ({reverting.Sum(c => c.Cells)} terrain, {reverting.Sum(c => c.WaterCells)} water cell(s)).");
            if (reverting.Count == 0)
                notes.Add("No plugin was chosen for revert, so no terrain or water was reverted (texture hints and ticked water-mod protection still run).");
            foreach (var r in s.RevertPlugins.Where(r => plan.Candidates.All(c => !c.Plugin.Equals(r, StringComparison.OrdinalIgnoreCase))))
                notes.Add($"'{r}' is ticked for revert but no longer overwrites trusted terrain or water - nothing to do for it.");
        }

        var staging = Path.Combine(Path.GetTempPath(), "Groundskeeper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var patches = new List<Mo2Resolver.ResolvedPlugin>();
        int seam = 0, tex = 0, road = 0, flo = 0;

        void Pass(string label, bool on, Func<string?> run)
        {
            if (!on) return;
            log($"== {label} ==");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var written = run();
                log($"{label}: {sw.Elapsed.TotalSeconds:F1} s");
                if (written is not null && File.Exists(written))
                    patches.Add(new Mo2Resolver.ResolvedPlugin(Path.GetFileName(written), written));
                else log($"{label}: nothing to write.");
            }
            catch (Exception ex)
            {
                var msg = $"{label} FAILED and was skipped: {ex.GetType().Name}: {ex.Message}";
                log(msg); notes.Add(msg);
            }
        }

        try
        {
            Pass("Seams", s.Seams, () =>
            {
                var r = SeamFixer.GenerateFixPluginForResolvedPlugins(baseOrder, "LandscapeSeamFixes.esp", staging, log,
                    s.TrustNorthernRoads, waterTrust: WaterOptions(s), customTrustedPlugins: customTrusted,
                    priorityOverNorthernRoadsPlugins: winnersList);
                seam = r.CellsPatched; return r.OutputPath;
            });
            Pass("Texture layers", s.TextureLayers, () =>
            {
                var r = TextureLayerFixer.GenerateFixPluginForResolvedPlugins(baseOrder, "LandscapeTextureFixes.esp", staging, log,
                    s.TrustNorthernRoads, customTrusted);
                tex = r.CellsPatched; return r.OutputPath;
            });
            var roadCheck = string.IsNullOrWhiteSpace(s.RoadSourcePlugin) ? null : CheckRoadInputs(s.RoadSourcePlugin, s.RoadMaskFolder, s.RoadWorldspace);
            var roadBad = roadCheck?.StartsWith("Problem") ?? false;
            if (roadBad) { log("Roads skipped. " + roadCheck); notes.Add("Roads skipped. " + roadCheck); }
            Pass("Roads", !string.IsNullOrWhiteSpace(s.RoadSourcePlugin) && !roadBad, () =>
            {
                var r = RoadTerrainMerger.RunForResolvedPlugins(baseOrder, "RoadMaskMerge.esp", staging, log,
                    s.RoadSourcePlugin, s.RoadMaskFolder, s.RoadWorldspace);
                road = r.CellsMerged + r.CellsFellBackToFullRoad; return r.OutputPath;
            });
            Pass("Floating objects", s.Floating, () =>
            {
                var r = FloatingObjectFixer.RunFixForResolvedPlugins(baseOrder, "FloatingObjectFixes.esp", staging, log,
                    s.FloatingThresholdUnits, s.FloatingWorldspace, resolveAssetPath: env.Asset);
                flo = r.RefsCorrected; return r.OutputPath;
            });

            if (patches.Count == 0)
            {
                log("No pass produced a patch - nothing to merge.");
                return new(true, seam, tex, road, flo, 0, Array.Empty<string>(), notes);
            }

            log("== Merge ==");
            var swMerge = System.Diagnostics.Stopwatch.StartNew();
            var mergeOrder = baseOrder.Concat(patches).ToList();
            // SnowFixer = "": the old Snow Fixer is replaced by Landscape Fixer, whose plugin is never merged.
            var sources = new SourceToolPlugins(SnowFixer: "");
            var mergeOut = s.DryRun ? staging : s.OutputDirectory;
            if (!s.DryRun)
            {
                Directory.CreateDirectory(s.OutputDirectory);
                foreach (var f in Directory.EnumerateFiles(s.OutputDirectory, "*.esp"))
                    if (IsOwnOutput(Path.GetFileName(f), s.OutputPluginName)) File.Delete(f); // only this tool's own names
            }
            var merged = MergeEngine.RunForResolvedPlugins(mergeOrder, sources, s.OutputPluginName, mergeOut, log,
                dryRun: false, matchNeighborsToTrustedChain: s.MatchNeighborsToTrustedChain);
            log($"Merge: {swMerge.Elapsed.TotalSeconds:F1} s");
            var cells = merged.Stats.CellsFromOneSource + merged.Stats.CellsHeightAndTextureMerged;
            if (merged.Stats.PlacedRefsForwarded > 0) log($"Merge also forwarded {merged.Stats.PlacedRefsForwarded} placed reference(s).");
            var outs = s.DryRun ? Array.Empty<string>() : merged.OutputPaths.ToArray();
            log(s.DryRun ? "Dry run: nothing written to the output folder." : $"Wrote {string.Join(", ", outs.Select(Path.GetFileName))}.");
            if (!s.DryRun)
            {
                foreach (var o in outs)
                {
                    try
                    {
                        var rep = OutputVerifier.Verify(baseOrder, o);
                        if (rep.Mismatches.Count == 0) log($"Check: {rep.RefsChecked} placed references in {Path.GetFileName(o)} keep the load order's disabled/deleted/base/enable-parent state.");
                        else
                        {
                            var msg = $"CHECK FAILED: {rep.Mismatches.Count} placed reference(s) in {Path.GetFileName(o)} differ from the load order (disabled/deleted/base/enable parent).";
                            log(msg); notes.Add(msg);
                            foreach (var m in rep.Mismatches.Take(25)) log($"  {m.Ref} (winner {m.Winner}): expected [{m.Expected}] got [{m.Ours}]");
                        }
                    }
                    catch (Exception ex) { log($"Check skipped: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            if (!s.DryRun && direct && outs.Length > 0)
                notes.Add($"Written into {s.OutputDirectory}. Make sure {string.Join(", ", outs.Select(Path.GetFileName))} is ticked in your plugin list (Vortex, or the game's launcher) and loads after the plugins it fixes.");
            return new(true, seam, tex, road, flo, cells, outs, notes);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* scratch in %TEMP% */ }
        }
    }

    /// <summary>The Steam/GOG install the game registered itself under, or null.</summary>
    static string? DetectGameData()
    {
        try
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition" })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("installed path") is string root && Directory.Exists(Path.Combine(root, "Data")))
                    return Path.Combine(root, "Data");
            }
        }
        catch { /* not Windows, or no access: the user picks the folder */ }
        return null;
    }

    static string ReadGamePathFromIni(string instancePath)
    {
        var ini = Path.Combine(instancePath, "ModOrganizer.ini");
        if (File.Exists(ini))
            foreach (var line in File.ReadLines(ini))
                if (line.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line[9..].Trim().Trim('"');
                    if (v.StartsWith("@ByteArray(")) v = v[11..^1];
                    return v.Replace(@"\\", @"\");
                }
        return "";
    }
}
