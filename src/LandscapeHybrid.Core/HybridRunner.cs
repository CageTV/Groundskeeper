using System.Text.RegularExpressions;
using PatchForeman;
using RoadMaskMerger;
using SeamFinder.Core;

namespace LandscapeHybrid;

public sealed class HybridSettings
{
    public string Mo2InstancePath { get; set; } = "";
    public string Mo2Profile { get; set; } = "";
    /// <summary>Game install or its Data folder; empty = read from ModOrganizer.ini.</summary>
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
    /// <summary>True = run every pass and the merge but write nothing into the output folder.</summary>
    public bool DryRun { get; set; }
}

public sealed record HybridResult(
    bool Ran, int SeamCells, int TextureCells, int RoadCells, int FloatingRefs, int MergedCells,
    IReadOnlyList<string> OutputPaths, IReadOnlyList<string> Notes);

/// <summary>
/// One run: resolve the MO2 load order once, run the selected terrain passes over it (each against the base
/// list, like the standalone tools), then fold their patches into one plugin with the PatchForeman merge.
/// Works alongside Cl3mus33's Landscape Fixer: its plugin is never a terrain source (see
/// TrustResolver.ThirdPartyGeneratedOutputs) and is left out of the source list, so run order between the
/// two tools does not matter and neither reads the other's output.
/// </summary>
public static class HybridRunner
{
    public static readonly string[] PassPluginNames =
        { "LandscapeSeamFixes.esp", "LandscapeTextureFixes.esp", "RoadMaskMerge.esp", "FloatingObjectFixes.esp" };

    public static bool IsOwnOutput(string fileName, string mergedName)
    {
        if (PassPluginNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return true;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var mergedStem = Path.GetFileNameWithoutExtension(mergedName);
        return stem.Equals(mergedStem, StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(stem, "^" + Regex.Escape(mergedStem) + @"_\d+$", RegexOptions.IgnoreCase);
    }

    public static HybridResult Run(HybridSettings s, Action<string> log)
    {
        var notes = new List<string>();

        var game = string.IsNullOrWhiteSpace(s.GameDataPath) ? ReadGamePathFromIni(s.Mo2InstancePath) : s.GameDataPath;
        var resolved = Mo2Resolver.Resolve(s.Mo2InstancePath, s.Mo2Profile, game);
        log($"Resolved {resolved.LoadOrder.Count} active plugins ({resolved.MissingPlugins.Count} missing).");

        if (resolved.LoadOrder.Any(p => p.FileName.Equals("LandscapeFixer.esp", StringComparison.OrdinalIgnoreCase)))
            notes.Add("Landscape Fixer's LandscapeFixer.esp is active: it is left out of the terrain sources (never read as a source).");
        else
            notes.Add("LandscapeFixer.esp is not active in this profile. Run Landscape Fixer and enable its output; this tool does not need it to work.");

        var baseOrder = resolved.LoadOrder
            .Where(p => !p.FileName.Equals("LandscapeFixer.esp", StringComparison.OrdinalIgnoreCase)
                     && !IsOwnOutput(p.FileName, s.OutputPluginName))
            .ToList();
        log($"{baseOrder.Count} source plugins (own outputs and LandscapeFixer.esp excluded).");

        var staging = Path.Combine(Path.GetTempPath(), "LandscapeHybrid-" + Guid.NewGuid().ToString("N"));
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
                var r = SeamFixer.GenerateFixPluginForResolvedPlugins(baseOrder, "LandscapeSeamFixes.esp", staging, log, s.TrustNorthernRoads);
                seam = r.CellsPatched; return r.OutputPath;
            });
            Pass("Texture layers", s.TextureLayers, () =>
            {
                var r = TextureLayerFixer.GenerateFixPluginForResolvedPlugins(baseOrder, "LandscapeTextureFixes.esp", staging, log, s.TrustNorthernRoads);
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
                    s.FloatingThresholdUnits, s.FloatingWorldspace, resolveAssetPath: rel => resolved.ResolveDataFile(rel));
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
