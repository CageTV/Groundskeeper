using LandscapeHybrid;

// Mod Organizer 2:   GroundskeeperCli --mo2 <instance> <profile> --output=<folder> [options]
// Vortex / plain:    GroundskeeperCli --direct [--game=<install or Data folder>] --output=<folder> [options]
//   options: [--name=LandscapeTerrain.esp] [--no-seams] [--no-textures] [--floating] [--floating-threshold=96]
//            [--floating-worldspace=Tamriel] [--road-plugin="Northern Roads.esp"] [--road-masks=<folder>]
//            [--trust-northern-roads] [--match-neighbors]
//            [--revert=<plugin>]...   revert that plugin's terrain/water (repeatable; default: nothing is reverted)
//            [--water-mod=<plugin>]... protect that water mod, first = highest priority (repeatable)
//            [--winner=<plugin>]...    terrain winners in your order, first = highest (repeatable)
//            [--plan]                  only list the plugins that overwrite trusted terrain/water, then exit
//            [--write]                 write the plugin (default is a dry run that writes nothing)

static string? Arg(string[] a, string key) =>
    a.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) is { } v ? v[(key.Length + 1)..].Trim('"') : null;
static List<string> Args(string[] a, string key) =>
    a.Where(x => x.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)).Select(x => x[(key.Length + 1)..].Trim('"')).ToList();

var mo2 = args.Length >= 3 && args[0] == "--mo2";
var direct = args.Length >= 1 && args[0] == "--direct";
var planOnly = args.Contains("--plan");
if ((!mo2 && !direct) || (Arg(args, "--output") is null && !planOnly))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  GroundskeeperCli --mo2 <instance> <profile> --output=<folder> [options]");
    Console.WriteLine("  GroundskeeperCli --direct [--game=<install or Data folder>] --output=<folder> [options]   (Vortex or a plain install)");
    Console.WriteLine("  options: --name= --no-seams --no-textures --floating --floating-threshold= --floating-worldspace=");
    Console.WriteLine("           --road-plugin= --road-masks= --trust-northern-roads --match-neighbors");
    Console.WriteLine("           --revert=<plugin> --water-mod=<plugin> --plan --write   (dry run unless --write)");
    return 2;
}

var s = new HybridSettings
{
    ModManager = mo2 ? ModManager.ModOrganizer2 : ModManager.DirectOrVortex,
    Mo2InstancePath = mo2 ? args[1] : "", Mo2Profile = mo2 ? args[2] : "",
    GameDataPath = Arg(args, "--game") ?? "",
    OutputDirectory = Arg(args, "--output") ?? "",
    OutputPluginName = Arg(args, "--name") ?? "LandscapeTerrain.esp",
    Seams = !args.Contains("--no-seams"),
    TextureLayers = !args.Contains("--no-textures"),
    Floating = args.Contains("--floating"),
    FloatingThresholdUnits = float.TryParse(Arg(args, "--floating-threshold"), out var ft) ? ft : 96f,
    FloatingWorldspace = Arg(args, "--floating-worldspace") ?? "Tamriel",
    RoadSourcePlugin = Arg(args, "--road-plugin") ?? "",
    RoadMaskFolder = Arg(args, "--road-masks") ?? "",
    TrustNorthernRoads = args.Contains("--trust-northern-roads"),
    MatchNeighborsToTrustedChain = args.Contains("--match-neighbors"),
    RevertPlugins = Args(args, "--revert"),
    WaterMods = Args(args, "--water-mod"),
    TerrainWinners = Args(args, "--winner"),
    DryRun = !args.Contains("--write"),
};

try
{
    if (planOnly)
    {
        var plan = HybridRunner.Plan(s, Console.WriteLine);
        Console.WriteLine();
        foreach (var c in plan.Candidates)
            Console.WriteLine($"{c.Plugin}: terrain {c.Cells} cell(s) up to {c.MaxUnits} units (-> {c.RestoresTo}); water {c.WaterCells} cell(s) (-> {c.WaterRestoresTo})");
        Console.WriteLine("water mods found: " + string.Join(", ", plan.WaterModChoices));
        return 0;
    }
    Console.WriteLine(s.DryRun ? "DRY RUN (pass --write to produce the plugin)" : "WRITE");
    var r = HybridRunner.Run(s, Console.WriteLine);
    Console.WriteLine();
    foreach (var n in r.Notes) Console.WriteLine("note: " + n);
    Console.WriteLine($"seam cells {r.SeamCells}, texture cells {r.TextureCells}, road cells {r.RoadCells}, floating refs {r.FloatingRefs}, merged cells {r.MergedCells}");
    foreach (var p in r.OutputPaths) Console.WriteLine("wrote " + p);
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAILED: " + ex.Message);
    return 1;
}
