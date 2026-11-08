using LandscapeHybrid;

// LandscapeHybrid --mo2 <instance> <profile> --output=<folder> [--game=<path>] [--name=LandscapeTerrain.esp]
//   [--no-seams] [--no-textures] [--floating] [--floating-threshold=96] [--floating-worldspace=Tamriel]
//   [--road-plugin="Northern Roads.esp"] [--road-masks=<folder>] [--trust-northern-roads]
//   [--match-neighbors] [--write]
// Default is a DRY RUN: every pass and the merge run, nothing is written to the output folder.

static string? Arg(string[] a, string key) =>
    a.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) is { } v ? v[(key.Length + 1)..].Trim('"') : null;

if (args.Length < 3 || args[0] != "--mo2" || Arg(args, "--output") is null)
{
    Console.WriteLine("Usage: LandscapeHybrid --mo2 <instance> <profile> --output=<folder> [options] [--write]");
    Console.WriteLine("  --game=<path> --name=LandscapeTerrain.esp --no-seams --no-textures --floating");
    Console.WriteLine("  --floating-threshold=96 --floating-worldspace=Tamriel --road-plugin=<esp> --road-masks=<folder>");
    Console.WriteLine("  --trust-northern-roads --match-neighbors   (dry run unless --write)");
    return 2;
}

var s = new HybridSettings
{
    Mo2InstancePath = args[1], Mo2Profile = args[2],
    GameDataPath = Arg(args, "--game") ?? "",
    OutputDirectory = Arg(args, "--output")!,
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
    DryRun = !args.Contains("--write"),
};

Console.WriteLine(s.DryRun ? "DRY RUN (pass --write to produce the plugin)" : "WRITE");
try
{
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
