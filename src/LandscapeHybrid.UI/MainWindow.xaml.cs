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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Groundskeeper", "settings.json");

    bool _loading = true;
    bool _busy;
    string? _savedProfile;
    HashSet<string> _revert = new(StringComparer.OrdinalIgnoreCase);
    List<string> _winners = new();
    // water mods in priority order; the ticked ones are the protected set
    List<string> _waterOrder = new();
    HashSet<string> _waterTicked = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        Load();
        _loading = false;
        ApplyMode();
        RefreshProfiles(null);
        RebuildWaterList();
        RebuildWinnersList();
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
        if (s.ModManager == ModManager.ModOrganizer2) ModeMo2.IsChecked = true; else ModeDirect.IsChecked = true;
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
        _winners = s.TerrainWinners.ToList();
        _waterOrder = s.WaterMods.ToList();
        _waterTicked = new HashSet<string>(s.WaterMods, StringComparer.OrdinalIgnoreCase);
        if (_revert.Count > 0) ScanSummary.Text = $"{_revert.Count} mod(s) ticked for revert from last time. Scan again to see current conflicts.";
    }

    HybridSettings Collect(bool dryRun) => new()
    {
        ModManager = ModeDirect.IsChecked == true ? ModManager.DirectOrVortex : ModManager.ModOrganizer2,
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
        TerrainWinners = _winners.ToList(),
        WaterMods = _waterOrder.Where(w => _waterTicked.Contains(w)).ToList(),
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

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyMode();
        // a scan belongs to one load order: drop the old list when the mod manager changes
        ConflictList.Children.Clear(); ScanSummary.Text = ""; _revert.Clear();
    }

    // MO2 needs an instance and a profile; Vortex / a plain install only needs the game folder.
    void ApplyMode()
    {
        var mo2 = ModeDirect.IsChecked != true;
        var v = mo2 ? Visibility.Visible : Visibility.Collapsed;
        InstanceLabel.Visibility = InstanceBox.Visibility = InstanceBrowse.Visibility = v;
        ProfileLabel.Visibility = ProfileCombo.Visibility = ProfileHint.Visibility = v;
        GameLabel.Text = mo2 ? "Game folder (optional):" : "Game folder (optional):";
    }

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
    void BrowseGame_Click(object s, RoutedEventArgs e) { if (PickFolder("Skyrim install folder (or its Data folder)") is { } p) GameBox.Text = p; }
    void BrowseOutput_Click(object s, RoutedEventArgs e) { if (PickFolder("Output folder") is { } p) OutputBox.Text = p; }

    void OpenOutput_Click(object s, RoutedEventArgs e)
    {
        var p = OutputBox.Text.Trim();
        if (Directory.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
    }

    // ---- terrain / water conflicts ----

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var s = Collect(true);
        if (Validate(s, false) is { } problem) { MessageBox.Show(this, problem, "Groundskeeper", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        SetBusy(true, "Scanning terrain and water conflicts...");
        LogBox.Clear();
        void Log(string m) => Dispatcher.BeginInvoke(() => { LogBox.AppendText(m + Environment.NewLine); LogBox.ScrollToEnd(); });
        try
        {
            var plan = await Task.Run(() => HybridRunner.Plan(s, Log));
            ConflictList.Children.Clear();
            foreach (var c in plan.Candidates)
            {
                var what = new List<string>();
                if (c.Cells > 0) what.Add($"terrain {c.Cells} cell(s), up to {c.MaxUnits} units, reverts to {c.RestoresTo}");
                if (c.WaterCells > 0) what.Add($"water {c.WaterCells} cell(s), reverts to {c.WaterRestoresTo}");
                var cb = new CheckBox
                {
                    Tag = c.Plugin,
                    IsChecked = _revert.Contains(c.Plugin),
                    Content = new TextBlock { Text = $"{c.Plugin}  -  {string.Join("; ", what)}", Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap }, // TextBlock: a plain string would eat "_" as an access key
                    ToolTip = (c.SampleCells.Count > 0 ? "Example cells: " + string.Join(", ", c.SampleCells) + ". " : "")
                              + "Tick to REVERT this mod's terrain and water in these cells; untick to keep it.",
                };
                cb.Checked += Revert_Changed; cb.Unchecked += Revert_Changed;
                var win = new Button { Content = "Make winner", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0), Tag = c.Plugin,
                    ToolTip = "Put this mod in the winners list below: it will win where it conflicts, ahead of mods loaded after it." };
                win.Click += (_, _) => AddWinner(c.Plugin);
                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(win, Dock.Right);
                row.Children.Add(win); row.Children.Add(cb);
                ConflictList.Children.Add(row);
            }
            // a saved tick for a mod that no longer conflicts is dropped
            _revert.RemoveWhere(r => plan.Candidates.All(c => !c.Plugin.Equals(r, StringComparison.OrdinalIgnoreCase)));
            ScanSummary.Text = plan.Candidates.Count == 0
                ? "No mod overwrites trusted terrain or water."
                : $"{plan.Candidates.Count} mod(s) overwrite trusted terrain ({plan.Candidates.Sum(c => c.Cells)} cells) or water ({plan.Candidates.Sum(c => c.WaterCells)} cells); {plan.SkippedWater} more terrain cells skipped for water, {plan.SkippedReference} for placed references. Tick only the ones you want reverted.";

            // water mods found in the load order join the priority list (ticked ones keep their place at the top)
            foreach (var w in plan.WaterModChoices)
                if (!_waterOrder.Contains(w, StringComparer.OrdinalIgnoreCase)) _waterOrder.Add(w);
            RebuildWaterList();
            SetBusy(false, "Scan finished. Nothing was written.");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            SetBusy(false, "Scan failed - see the log.");
        }
    }

    void AddWinner(string plugin)
    {
        if (!_winners.Contains(plugin, StringComparer.OrdinalIgnoreCase)) _winners.Add(plugin);
        _revert.Remove(plugin); // a winner is never reverted
        foreach (var row in ConflictList.Children.OfType<DockPanel>())
            if (row.Children.OfType<CheckBox>().FirstOrDefault() is { Tag: string t } cb && t.Equals(plugin, StringComparison.OrdinalIgnoreCase)) cb.IsChecked = false;
        RebuildWinnersList();
    }

    void RebuildWinnersList()
    {
        WinnersList.Children.Clear();
        if (_winners.Count == 0)
        {
            WinnersList.Children.Add(new TextBlock { Text = "No winners chosen: load order decides.", Style = (Style)FindResource("Hint") });
            return;
        }
        for (var i = 0; i < _winners.Count; i++)
        {
            var name = _winners[i];
            var row = new DockPanel { LastChildFill = true };
            var remove = new Button { Content = "✕", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove from the winners list" };
            var down = new Button { Content = "▼", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(2, 0, 0, 0), ToolTip = "Lower priority", IsEnabled = i < _winners.Count - 1 };
            var up = new Button { Content = "▲", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Higher priority", IsEnabled = i > 0 };
            up.Click += (_, _) => MoveWinner(name, -1);
            down.Click += (_, _) => MoveWinner(name, +1);
            remove.Click += (_, _) => { _winners.RemoveAll(w => w.Equals(name, StringComparison.OrdinalIgnoreCase)); RebuildWinnersList(); };
            foreach (var b in new[] { remove, down, up }) { DockPanel.SetDock(b, Dock.Right); row.Children.Add(b); }
            row.Children.Add(new TextBlock { Text = $"{i + 1}.  {name}", VerticalAlignment = VerticalAlignment.Center });
            WinnersList.Children.Add(row);
        }
    }

    void MoveWinner(string name, int delta)
    {
        var i = _winners.FindIndex(w => w.Equals(name, StringComparison.OrdinalIgnoreCase));
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _winners.Count) return;
        (_winners[i], _winners[j]) = (_winners[j], _winners[i]);
        RebuildWinnersList();
    }

    void Revert_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string plugin } cb) return;
        if (cb.IsChecked == true) _revert.Add(plugin); else _revert.Remove(plugin);
    }

    void RebuildWaterList()
    {
        WaterList.Children.Clear();
        if (_waterOrder.Count == 0)
        {
            WaterList.Children.Add(new TextBlock { Text = "Run the scan to list the water mods in your load order.", Style = (Style)FindResource("Hint") });
            return;
        }
        for (var i = 0; i < _waterOrder.Count; i++)
        {
            var name = _waterOrder[i];
            var row = new DockPanel { LastChildFill = true };
            var down = new Button { Content = "▼", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(2, 0, 0, 0), Tag = name, ToolTip = "Lower priority" };
            var up = new Button { Content = "▲", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0), Tag = name, ToolTip = "Higher priority" };
            up.IsEnabled = i > 0; down.IsEnabled = i < _waterOrder.Count - 1;
            up.Click += (_, _) => MoveWater(name, -1);
            down.Click += (_, _) => MoveWater(name, +1);
            DockPanel.SetDock(down, Dock.Right); DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(down); row.Children.Add(up);
            var cb = new CheckBox { Content = new TextBlock { Text = $"{i + 1}.  {name}", Margin = new Thickness(0) }, IsChecked = _waterTicked.Contains(name), Tag = name };
            cb.Checked += Water_Changed; cb.Unchecked += Water_Changed;
            row.Children.Add(cb);
            WaterList.Children.Add(row);
        }
    }

    void MoveWater(string name, int delta)
    {
        var i = _waterOrder.FindIndex(w => w.Equals(name, StringComparison.OrdinalIgnoreCase));
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _waterOrder.Count) return;
        (_waterOrder[i], _waterOrder[j]) = (_waterOrder[j], _waterOrder[i]);
        RebuildWaterList();
    }

    void Water_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string name } cb) return;
        if (cb.IsChecked == true) _waterTicked.Add(name); else _waterTicked.Remove(name);
    }

    // ---- run ----

    string? Validate(HybridSettings s, bool needOutput)
    {
        if (s.ModManager == ModManager.ModOrganizer2)
        {
            if (!File.Exists(Path.Combine(s.Mo2InstancePath, "ModOrganizer.ini"))) return "Pick your MO2 instance folder (the one holding ModOrganizer.ini).";
            if (s.Mo2Profile == "") return "Pick an MO2 profile.";
        }
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
        if (Validate(s, !dry) is { } problem) { MessageBox.Show(this, problem, "Groundskeeper", MessageBoxButton.OK, MessageBoxImage.Information); return; }

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
