using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LandscapeHybrid.UI;

public partial class MainWindow : Window
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LandscapeHybrid", "settings.json");

    bool _loading = true;
    bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Load();
        _loading = false;
        RefreshProfiles(null);
        Closing += (_, _) => Save();
    }

    // ---- settings ----

    void Load()
    {
        var s = new HybridSettings();
        try
        {
            if (File.Exists(SettingsPath))
                s = JsonSerializer.Deserialize<HybridSettings>(File.ReadAllText(SettingsPath)) ?? s;
        }
        catch { /* a broken settings file just means defaults */ }

        if (string.IsNullOrWhiteSpace(s.Mo2InstancePath)) s.Mo2InstancePath = DetectInstanceFromExePath() ?? "";
        InstanceBox.Text = s.Mo2InstancePath;
        GameBox.Text = s.GameDataPath;
        OutputBox.Text = s.OutputDirectory;
        PluginNameBox.Text = string.IsNullOrWhiteSpace(s.OutputPluginName) ? "LandscapeTerrain.esp" : s.OutputPluginName;
        SeamsCheck.IsChecked = s.Seams;
        TexturesCheck.IsChecked = s.TextureLayers;
        FloatingCheck.IsChecked = s.Floating;
        NeighborsCheck.IsChecked = s.MatchNeighborsToTrustedChain;
        NorthernCheck.IsChecked = s.TrustNorthernRoads;
        RoadPluginBox.Text = s.RoadSourcePlugin;
        RoadMasksBox.Text = s.RoadMaskFolder;
        RoadWorldspaceBox.Text = string.IsNullOrWhiteSpace(s.RoadWorldspace) ? "Tamriel" : s.RoadWorldspace;
        _savedProfile = s.Mo2Profile;
        _revert = new HashSet<string>(s.RevertPlugins, StringComparer.OrdinalIgnoreCase);
        if (_revert.Count > 0) ScanSummary.Text = $"{_revert.Count} mod(s) ticked for revert from last time. Scan again to see current conflicts.";
    }

    string? _savedProfile;
    HashSet<string> _revert = new(StringComparer.OrdinalIgnoreCase);

    HybridSettings Collect(bool dryRun) => new()
    {
        Mo2InstancePath = InstanceBox.Text.Trim(),
        Mo2Profile = ProfileCombo.SelectedItem as string ?? "",
        GameDataPath = GameBox.Text.Trim(),
        OutputDirectory = OutputBox.Text.Trim(),
        OutputPluginName = PluginNameBox.Text.Trim(),
        Seams = SeamsCheck.IsChecked == true,
        TextureLayers = TexturesCheck.IsChecked == true,
        Floating = FloatingCheck.IsChecked == true,
        MatchNeighborsToTrustedChain = NeighborsCheck.IsChecked == true,
        TrustNorthernRoads = NorthernCheck.IsChecked == true,
        RoadSourcePlugin = RoadPluginBox.Text.Trim(),
        RoadMaskFolder = RoadMasksBox.Text.Trim(),
        RoadWorldspace = RoadWorldspaceBox.Text.Trim(),
        RevertPlugins = _revert.OrderBy(x => x).ToList(),
        DryRun = dryRun,
    };

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Collect(false), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not worth interrupting the user */ }
    }

    // Started from MO2's executables list the exe lives inside (or next to) the instance: walk up to ModOrganizer.ini.
    static string? DetectInstanceFromExePath()
    {
        try
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "ModOrganizer.ini"))) return d.FullName;
        }
        catch { }
        return null;
    }

    // ---- setup ----

    void InstanceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshProfiles(ProfileCombo.SelectedItem as string);
    }

    void RefreshProfiles(string? keep)
    {
        var want = keep ?? _savedProfile;
        ProfileCombo.Items.Clear();
        var dir = Path.Combine(InstanceBox.Text.Trim(), "profiles");
        if (!Directory.Exists(dir)) return;
        foreach (var p in Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n))
            ProfileCombo.Items.Add(p);
        if (want != null && ProfileCombo.Items.Contains(want)) ProfileCombo.SelectedItem = want;
        else if (ProfileCombo.Items.Count > 0) ProfileCombo.SelectedIndex = 0;
    }

    static string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    void BrowseInstance_Click(object s, RoutedEventArgs e) { if (PickFolder("MO2 instance folder (contains ModOrganizer.ini)") is { } p) InstanceBox.Text = p; }
    void BrowseGame_Click(object s, RoutedEventArgs e) { if (PickFolder("Skyrim install folder") is { } p) GameBox.Text = p; }
    void BrowseOutput_Click(object s, RoutedEventArgs e) { if (PickFolder("Output folder") is { } p) OutputBox.Text = p; }

    void OpenOutput_Click(object s, RoutedEventArgs e)
    {
        var p = OutputBox.Text.Trim();
        if (Directory.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
    }

    // ---- terrain conflicts ----

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var s = Collect(true);
        if (Validate(s, false) is { } problem) { MessageBox.Show(this, problem, "Landscape Hybrid", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        SetBusy(true, "Scanning terrain conflicts...");
        LogBox.Clear();
        void Log(string m) => Dispatcher.BeginInvoke(() => { LogBox.AppendText(m + Environment.NewLine); LogBox.ScrollToEnd(); });
        try
        {
            var plan = await Task.Run(() => HybridRunner.Plan(s, Log));
            ConflictList.Children.Clear();
            foreach (var c in plan.Candidates)
            {
                var cb = new CheckBox
                {
                    Tag = c.Plugin,
                    IsChecked = _revert.Contains(c.Plugin),
                    Content = $"{c.Plugin}  -  {c.Cells} cell(s), up to {c.MaxUnits} units, reverts to {c.RestoresTo}",
                    ToolTip = "Example cells: " + string.Join(", ", c.SampleCells) + ". Tick to REVERT this mod's terrain in these cells; untick to keep it.",
                };
                cb.Checked += Revert_Changed; cb.Unchecked += Revert_Changed;
                ConflictList.Children.Add(cb);
            }
            // a saved tick for a mod that no longer conflicts is dropped
            _revert.RemoveWhere(r => plan.Candidates.All(c => !c.Plugin.Equals(r, StringComparison.OrdinalIgnoreCase)));
            ScanSummary.Text = plan.Candidates.Count == 0
                ? "No mod overwrites trusted terrain."
                : $"{plan.Candidates.Count} mod(s) overwrite trusted terrain in {plan.Candidates.Sum(c => c.Cells)} cell(s) ({plan.SkippedWater} more skipped for water, {plan.SkippedReference} for placed references). Tick only the ones you want reverted.";
            SetBusy(false, "Scan finished. Nothing was written.");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            SetBusy(false, "Scan failed - see the log.");
        }
    }

    void Revert_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string plugin } cb) return;
        if (cb.IsChecked == true) _revert.Add(plugin); else _revert.Remove(plugin);
    }

    // ---- run ----

    string? Validate(HybridSettings s, bool needOutput)
    {
        if (!File.Exists(Path.Combine(s.Mo2InstancePath, "ModOrganizer.ini"))) return "Pick your MO2 instance folder (the one holding ModOrganizer.ini).";
        if (s.Mo2Profile == "") return "Pick an MO2 profile.";
        if (needOutput && s.OutputDirectory == "") return "Pick an output folder (a dedicated mod folder, not your Data folder).";
        if (needOutput && !s.OutputPluginName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) return "The plugin name must end in .esp.";
        if (!s.Seams && !s.TextureLayers && !s.Floating && s.RoadSourcePlugin == "") return "Switch on at least one pass.";
        return null;
    }

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dry = (string)((Button)sender).Tag == "dry";
        var s = Collect(dry);
        if (Validate(s, !dry) is { } problem) { MessageBox.Show(this, problem, "Landscape Hybrid", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        SetBusy(true, dry ? "Previewing..." : "Generating...");
        LogBox.Clear();
        Save();
        var sw = Stopwatch.StartNew();
        void Log(string m) => Dispatcher.BeginInvoke(() => { LogBox.AppendText(m + Environment.NewLine); LogBox.ScrollToEnd(); });
        try
        {
            var r = await Task.Run(() => HybridRunner.Run(s, Log));
            Log("");
            foreach (var n in r.Notes) Log("note: " + n);
            Log($"Seam cells {r.SeamCells}, texture cells {r.TextureCells}, road cells {r.RoadCells}, floating refs {r.FloatingRefs}, merged cells {r.MergedCells}.");
            SetBusy(false, dry ? $"Preview finished in {sw.Elapsed:mm\\:ss}. Nothing was written."
                : r.OutputPaths.Count > 0 ? $"Wrote {string.Join(", ", r.OutputPaths.Select(Path.GetFileName))} in {sw.Elapsed:mm\\:ss}. Enable it after your other landscape mods."
                : $"Finished in {sw.Elapsed:mm\\:ss}: no pass produced anything to write.");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            SetBusy(false, "Failed - see the log.");
        }
    }

    void SetBusy(bool busy, string status)
    {
        _busy = busy;
        DryRunButton.IsEnabled = WriteButton.IsEnabled = ScanButton.IsEnabled = !busy;
        Bar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        Bar.IsIndeterminate = busy;
        StatusText.Text = status;
    }
}
