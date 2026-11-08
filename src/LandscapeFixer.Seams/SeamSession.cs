using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

/// <summary>
/// A staged copy of a load order plus a Mutagen environment over it, opened once and reused for several SeamFixer runs
/// (the revert plan runs SeamFixer up to eight times over the same plugins). Reading the plugins is the part that does not change
/// between runs; only the trust inputs do.
/// </summary>
public sealed class SeamSession : IDisposable
{
    readonly string _mergedFolder;
    readonly IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _env;
    readonly Dictionary<ModKey, int> _priorityIndex;
    readonly Dictionary<string, HashSet<string>> _mastersByPlugin;
    readonly Action<string> _openLog;
    List<Mutagen.Bethesda.Plugins.Cache.IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter>>? _cells;

    SeamSession(string mergedFolder, IGameEnvironment<ISkyrimMod, ISkyrimModGetter> env, Dictionary<ModKey, int> priorityIndex,
        Dictionary<string, HashSet<string>> mastersByPlugin, Action<string> openLog)
    { _mergedFolder = mergedFolder; _env = env; _priorityIndex = priorityIndex; _mastersByPlugin = mastersByPlugin; _openLog = openLog; }

    public static SeamSession Open(List<Mo2Resolver.ResolvedPlugin> loadOrder, Action<string> log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mergedFolder = Path.Combine(Path.GetTempPath(), "SeamFixerMerged-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {mergedFolder} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, mergedFolder);
        log($"[timing] staging {sw.Elapsed.TotalSeconds:F1} s");
        try
        {
            var modKeys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
                .Create(GameRelease.SkyrimSE)
                .WithLoadOrder(modKeys)
                .WithTargetDataFolder(mergedFolder)
                .Build();
            var priorityIndex = modKeys.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx);
            var masters = SeamFixer.BuildMastersByPlugin(env.LoadOrder.ListedOrder);
            log($"[timing] environment ready {sw.Elapsed.TotalSeconds:F1} s");
            return new SeamSession(mergedFolder, env, priorityIndex, masters, log);
        }
        catch
        {
            try { Directory.Delete(mergedFolder, recursive: true); } catch { /* scratch in %TEMP% */ }
            throw;
        }
    }

    /// <summary>One SeamFixer run over the opened load order. <paramref name="planOnly"/> skips building and writing the patch: the log is the result.</summary>
    public SeamFixResult Run(string outputPluginName, string outputDirectory, Action<string> log,
        bool trustNorthernRoads = false, WaterTrustOptions? waterTrust = null, IReadOnlyList<string>? customTrustedPlugins = null,
        IReadOnlyList<string>? priorityOverNorthernRoadsPlugins = null, bool genuineWaterRestore = true, bool planOnly = false)
        => SeamFixer.GenerateFixPluginCore(_env.LinkCache, _priorityIndex, _mergedFolder, outputPluginName, outputDirectory, log,
            trustNorthernRoads, waterTrust ?? WaterTrustOptions.None, customTrustedPlugins ?? [], priorityOverNorthernRoadsPlugins ?? [],
            _mastersByPlugin, genuineWaterRestore, planOnly,
            // the winning cell list does not depend on the trust inputs: enumerate it once per session
            _cells ??= _env.LinkCache.WinningContextOverrides<Cell, ICellGetter>(_env.LinkCache).ToList());

    public void Dispose()
    {
        _env.Dispose();
        try { Directory.Delete(_mergedFolder, recursive: true); }
        catch (Exception ex) { _openLog($"(could not clean up temp folder {_mergedFolder}: {ex.Message})"); }
    }
}
