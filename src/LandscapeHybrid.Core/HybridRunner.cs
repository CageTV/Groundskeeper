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
    public string OutputPluginName { get; set; } = "LandscapeTerrain.esp";

    public bool Seams { get; set; } = true;
    public bool TextureLayers { get; set; } = true;
    public bool Floating { get; set; }
    public float FloatingThresholdUnits { get; set; } = 96f;
    public string FloatingWorldspace { get; set; } = "Tamriel";
    public string RoadSourcePlugin { get; set; } = "";
    public string RoadMaskFolder { get; set; } = "";
    public string RoadWorldspace { get; set; } = "Tamriel";
    public bool TrustNorthernRoads { get; set; }
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
}

public sealed record HybridResult(
    bool Ran, int SeamCells, int TextureCells, int RoadCells, int FloatingRefs, int MergedCells,
    IReadOnlyList<string> OutputPaths, IReadOnlyList<string> Notes);

/// <param name="Plugin">A plugin that currently overwrites terrain or water a trusted plugin authored.</param>
/// <param name="RestoresTo">The trusted plugin whose terrain it would be reverted to (last one seen).</param>
public sealed record RevertCandidate(string Plugin, int Cells, int MaxUnits, string RestoresTo, IReadOnlyList<string> SampleCells,
    int WaterCells = 0, string WaterRestoresTo = "");

/// <param name="WaterModChoices">Plugins in the load order that look like water mods (known names, or "water" in the name).</param>
public sealed record TerrainPlan(IReadOnlyList<RevertCandidate> Candidates, int SkippedWater, int SkippedReference,
    IReadOnlyList<string> WaterModChoices);

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
public static class HybridRunner
{
    public static readonly string[] PassPluginNames =
        { "LandscapeSeamFixes.esp", "LandscapeTextureFixes.esp", "RoadMaskMerge.esp", "FloatingObjectFixes.esp", "LandscapeSeamPlan.esp" };

    static readonly string[] KnownWaterMods = { "CS Water Mod.esp", "Water for ENB.esp", "RealisticWaterTwo.esp", "Simplicity of Sea.esp" };

    public static bool IsOwnOutput(string fileName, string mergedName)
    {
        if (PassPluginNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return true;
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

    sealed record Env(List<Mo2Resolver.ResolvedPlugin> All, List<Mo2Resolver.ResolvedPlugin> BaseOrder, Func<string, string?> Asset);

    static Env Resolve(HybridSettings s, Action<string> log)
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
            string data;
            if (!string.IsNullOrWhiteSpace(s.GameDataPath)) data = Mo2Resolver.NormalizeGameDataPath(s.GameDataPath);
            else if (DetectGameData() is { } found) data = found;
            else throw new InvalidOperationException("Pick the Skyrim install folder (or its Data folder): it could not be found automatically.");
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

    /// <summary>Finds every plugin that currently overwrites terrain or water a trusted plugin authored, and what it would
    /// be reverted to. Writes nothing. The user picks which of these to revert; the rest are kept.</summary>
    public static TerrainPlan Plan(HybridSettings s, Action<string> log)
    {
        var env = Resolve(s, log);
        return PlanFrom(env.BaseOrder, s, log);
    }

    static TerrainPlan PlanFrom(List<Mo2Resolver.ResolvedPlugin> baseOrder, HybridSettings s, Action<string> log)
    {
        // Keeping a plugin makes it trusted, which can expose NEW overwriters of it (A overwrites trusted T; once A is kept,
        // B overwriting A shows up). Iterate until no new overwriter appears, so "nothing ticked" really means "nothing changes".
        var revert = new HashSet<string>(s.RevertPlugins, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, (int Cells, int Max, string To, List<string> Samples)>(StringComparer.OrdinalIgnoreCase);
        var water = new Dictionary<string, (int Cells, string To)>(StringComparer.OrdinalIgnoreCase);
        int skippedWater = 0, skippedRef = 0;
        for (var pass = 1; pass <= 8; pass++)
        {
            var trusted = found.Keys.Where(k => !revert.Contains(k)).Concat(s.TerrainWinners).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var staging = Path.Combine(Path.GetTempPath(), "GroundskeeperPlan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var lines = new List<string>();
            try
            {
                SeamFixer.GenerateFixPluginForResolvedPlugins(baseOrder, "LandscapeSeamPlan.esp", staging,
                    m => lines.Add(m), s.TrustNorthernRoads, waterTrust: WaterOptions(s), customTrustedPlugins: trusted,
                    priorityOverNorthernRoadsPlugins: s.TerrainWinners);
            }
            finally { try { Directory.Delete(staging, recursive: true); } catch { /* scratch in %TEMP% */ } }

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
        var winners = new HashSet<string>(s.TerrainWinners, StringComparer.OrdinalIgnoreCase);
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
        return new TerrainPlan(list, skippedWater, skippedRef, waterChoices);
    }

    static WaterTrustOptions WaterOptions(HybridSettings s) =>
        s.WaterMods.Count == 0 ? WaterTrustOptions.None : new WaterTrustOptions(CustomWaterMods: s.WaterMods.ToList());

    public static HybridResult Run(HybridSettings s, Action<string> log)
    {
        var notes = new List<string>();
        var env = Resolve(s, log);
        var baseOrder = env.BaseOrder;

        if (env.All.Any(p => p.FileName.Equals("LandscapeFixer.esp", StringComparison.OrdinalIgnoreCase)))
            notes.Add("Landscape Fixer's LandscapeFixer.esp is active: it is left out of the terrain sources (never read as a source).");
        else
            notes.Add("LandscapeFixer.esp is not active in your load order. Run Landscape Fixer and enable its output; this tool does not need it to work.");
        log($"{baseOrder.Count} source plugins (own outputs and LandscapeFixer.esp excluded).");

        // Trust: a plugin that overwrites trusted terrain or water is KEPT (trusted) unless the user ticked it for revert.
        var customTrusted = new List<string>();
        if (s.Seams || s.TextureLayers)
        {
            var plan = PlanFrom(baseOrder, s, log);
            var revert = new HashSet<string>(s.RevertPlugins, StringComparer.OrdinalIgnoreCase);
            customTrusted = plan.Candidates.Select(c => c.Plugin).Where(p => !revert.Contains(p)).Concat(s.TerrainWinners).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (s.TerrainWinners.Count > 0) log($"Terrain winners, highest first: {string.Join(" > ", s.TerrainWinners)}.");
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
            try
            {
                var written = run();
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
                    priorityOverNorthernRoadsPlugins: s.TerrainWinners);
                seam = r.CellsPatched; return r.OutputPath;
            });
            Pass("Texture layers", s.TextureLayers, () =>
            {
                var r = TextureLayerFixer.GenerateFixPluginForResolvedPlugins(baseOrder, "LandscapeTextureFixes.esp", staging, log,
                    s.TrustNorthernRoads, customTrusted);
                tex = r.CellsPatched; return r.OutputPath;
            });
            Pass("Roads", !string.IsNullOrWhiteSpace(s.RoadSourcePlugin), () =>
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
            var cells = merged.Stats.CellsFromOneSource + merged.Stats.CellsHeightAndTextureMerged;
            var outs = s.DryRun ? Array.Empty<string>() : merged.OutputPaths.ToArray();
            log(s.DryRun ? "Dry run: nothing written to the output folder." : $"Wrote {string.Join(", ", outs.Select(Path.GetFileName))}.");
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
