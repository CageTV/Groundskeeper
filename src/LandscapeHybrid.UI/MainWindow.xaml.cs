using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ellipse = System.Windows.Shapes.Ellipse;
using Microsoft.Win32;

namespace LandscapeHybrid.UI;

public partial class MainWindow : Window
{
    // The MO2 instance this copy was started from, if any: it picks the settings file (one per instance, like a modlist of its own).
    readonly Mo2InstanceInfo? _detected = Mo2InstanceFinder.Detect();
    string? _lastDefaultOutput;

    string SettingsPath
    {
        get
        {
            // GROUNDSKEEPER_CONFIG names a settings file to use instead of the usual one (tests, or several setups side by side)
            if (Environment.GetEnvironmentVariable("GROUNDSKEEPER_CONFIG") is { Length: > 0 } custom) return custom;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Groundskeeper");
            if (_detected is null) return Path.Combine(root, "settings.json");
            var name = string.Concat(_detected.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(root, "instances", name + ".json");
        }
    }

    bool _loading = true;
    bool _busy;
    string? _savedProfile;
    string _preferredRoad = "";
    HashSet<string> _revert = new(StringComparer.OrdinalIgnoreCase);
    List<string> _winners = new();
    // the load order from the last scan, lowest first; used to place new winners where the load order has them
    List<string> _loadOrder = new();
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
        UpdateDefaultOutput();
        RebuildPreferredRoad(Array.Empty<string>());
        RebuildWaterList();
        RebuildWinnersList();
        Closing += (_, _) => Save();
        BuildStageStrip();
        StartHeaderAnimation();
    }

    // ---- header animation and the pass strip ----

    // the stage names are the "== name ==" headings the runner writes to the log
    static readonly (string Log, string Label)[] Stages =
    {
        ("Seams", "Seams"), ("Texture layers", "Textures"), ("Roads", "Roads"), ("Floating objects", "Floating"), ("Merge", "Merge"),
    };
    readonly Dictionary<string, (Border Chip, Ellipse Dot, TextBlock Text)> _stageUi = new();
    string? _activeStage;

    void StartHeaderAnimation()
    {
        // the logo's glow slowly breathes
        var glow = new DoubleAnimation(8, 26, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, glow);
    }

    void BuildStageStrip()
    {
        StageStrip.Children.Clear();
        foreach (var (log, label) in Stages)
        {
            var dot = new Ellipse { Width = 10, Height = 10, Fill = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = label, Foreground = (Brush)FindResource("Muted"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot); row.Children.Add(text);
            var chip = new Border
            {
                Child = row, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4, 2, 0, 2),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4C)),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            };
            StageStrip.Children.Add(chip);
            _stageUi[log] = (chip, dot, text);
        }
    }

    enum StageState { Idle, Active, Done }

    void SetStage(string logName, StageState state)
    {
        if (!_stageUi.TryGetValue(logName, out var ui)) return;
        ui.Dot.BeginAnimation(OpacityProperty, null); // stop any pulse
        ui.Dot.Opacity = 1;
        switch (state)
        {
            case StageState.Idle:
                ui.Dot.Fill = (Brush)FindResource("Muted"); ui.Text.Foreground = (Brush)FindResource("Muted");
                ui.Chip.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4C));
                ui.Chip.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                break;
            case StageState.Active:
                var orange = Color.FromRgb(0xFF, 0x9A, 0x3D);
                ui.Dot.Fill = new SolidColorBrush(orange); ui.Text.Foreground = Brushes.White;
                ui.Chip.BorderBrush = new SolidColorBrush(orange);
                ui.Chip.Background = new SolidColorBrush(Color.FromArgb(70, orange.R, orange.G, orange.B));
                ui.Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                break;
            case StageState.Done:
                var green = Color.FromRgb(0x5F, 0xD0, 0x8C);
                ui.Dot.Fill = new SolidColorBrush(green); ui.Text.Foreground = new SolidColorBrush(green);
                ui.Chip.BorderBrush = new SolidColorBrush(Color.FromArgb(160, green.R, green.G, green.B));
                ui.Chip.Background = new SolidColorBrush(Color.FromArgb(34, green.R, green.G, green.B));
                break;
        }
    }

    void ResetStages() { _activeStage = null; foreach (var (log, _) in Stages) SetStage(log, StageState.Idle); }

    // fed from every log line: a heading such as "== Seams ==" moves the highlight along
    void OnLogLine(string line)
    {
        if (!(line.StartsWith("== ") && line.EndsWith(" =="))) return;
        var name = line[3..^3];
        if (!_stageUi.ContainsKey(name)) return;
        if (_activeStage is not null) SetStage(_activeStage, StageState.Done);
        _activeStage = name;
        SetStage(name, StageState.Active);
    }

    void FinishStages(bool ok)
    {
        if (_activeStage is not null) SetStage(_activeStage, ok ? StageState.Done : StageState.Idle);
        _activeStage = null;
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

        if (string.IsNullOrWhiteSpace(s.Mo2InstancePath) && _detected is not null) s.Mo2InstancePath = _detected.Path;
        if (string.IsNullOrWhiteSpace(s.Mo2Profile) && _detected is not null) s.Mo2Profile = _detected.SelectedProfile;
        if (s.ModManager == ModManager.ModOrganizer2) ModeMo2.IsChecked = true; else ModeDirect.IsChecked = true;
        InstanceBox.Text = s.Mo2InstancePath;
        GameBox.Text = s.GameDataPath;
        OutputBox.Text = s.OutputDirectory;
        PluginNameBox.Text = string.IsNullOrWhiteSpace(s.OutputPluginName) || s.OutputPluginName.Equals("LandscapeTerrain.esp", StringComparison.OrdinalIgnoreCase) ? "Groundskeeper.esp" : s.OutputPluginName;
        SeamsCheck.IsChecked = s.Seams;
        TexturesCheck.IsChecked = s.TextureLayers;
        FloatingCheck.IsChecked = s.Floating;
        NeighborsCheck.IsChecked = s.MatchNeighborsToTrustedChain;
        NorthernCheck.IsChecked = s.TrustNorthernRoads;
        VanaheimrCheck.IsChecked = s.TrustVanaheimrRoads;
        _preferredRoad = s.PreferredRoadMod;
        RoadPluginBox.Text = s.RoadSourcePlugin;
        RoadMasksBox.Text = s.RoadMaskFolder;
        RoadWorldspaceBox.Text = string.IsNullOrWhiteSpace(s.RoadWorldspace) ? "Tamriel" : s.RoadWorldspace;
        UpdateRoadStatus();
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
        TrustVanaheimrRoads = VanaheimrCheck.IsChecked == true,
        PreferredRoadMod = PreferredRoadCombo.SelectedItem is string pr && pr != NoPreferredRoad ? pr : "",
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

    // ---- setup ----

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyMode();
        UpdateDefaultOutput();
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
        if (_loading) return;
        RefreshProfiles(ProfileCombo.SelectedItem as string);
        UpdateDefaultOutput();
    }

    // MO2 mode: an empty output box (or one still holding the previous default) gets <instance mods folder>\Groundskeeper Output,
    // the same idea as Landscape Fixer's "LandscapeFixer Output" mod folder. The folder itself is created when Generate runs.
    void UpdateDefaultOutput()
    {
        if (ModeDirect.IsChecked == true) return;
        var inst = InstanceBox.Text.Trim();
        if (inst.Length == 0 || !File.Exists(Path.Combine(inst, "ModOrganizer.ini"))) return;
        var def = Mo2InstanceFinder.DefaultOutputFolder(inst);
        var current = OutputBox.Text.Trim();
        if (current.Length == 0 || current.Equals(_lastDefaultOutput, StringComparison.OrdinalIgnoreCase)) OutputBox.Text = def;
        _lastDefaultOutput = def;
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
    void BrowseRoadMasks_Click(object s, RoutedEventArgs e) { if (PickFolder("ACMOS Road Generator roads folder (contains 'Roads' and 'Paths Only')") is { } p) RoadMasksBox.Text = p; }

    // Plain-language state of the road inputs, shown under the box so a wrong folder is obvious before a run.
    void RoadMasks_Changed(object sender, TextChangedEventArgs e) => UpdateRoadStatus();

    void UpdateRoadStatus()
    {
        if (RoadMasksStatus is null || RoadPluginBox is null || RoadMasksBox is null || RoadWorldspaceBox is null) return;
        RoadMasksStatus.Text = HybridRunner.CheckRoadInputs(RoadPluginBox.Text.Trim(), RoadMasksBox.Text.Trim(), RoadWorldspaceBox.Text.Trim()) ?? "";
    }

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
            _conflictBoxes.Clear();
            var rowIndex = 0;
            foreach (var c in plan.Candidates)
            {
                var what = new List<string>();
                if (c.Cells > 0) what.Add($"terrain {c.Cells} cell(s), up to {c.MaxUnits} units, reverts to {c.RestoresTo}");
                if (c.WaterCells > 0) what.Add($"water {c.WaterCells} cell(s), reverts to {c.WaterRestoresTo}");

                // two lines in one TextBlock: the name in bold, the numbers underneath, so a long list scans easily.
                // (TextBlock content, not a string: WPF would eat "_" in a plain string as an access key.)
                var label = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0) };
                label.Inlines.Add(new System.Windows.Documents.Run(c.Plugin) { FontWeight = FontWeights.SemiBold });
                label.Inlines.Add(new System.Windows.Documents.LineBreak());
                label.Inlines.Add(new System.Windows.Documents.Run(string.Join("; ", what)) { Foreground = (System.Windows.Media.Brush)FindResource("Muted"), FontSize = 12 });
                var cb = new CheckBox
                {
                    Tag = c.Plugin,
                    IsChecked = _revert.Contains(c.Plugin),
                    Content = label,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = (c.SampleCells.Count > 0 ? "Example cells: " + string.Join(", ", c.SampleCells) + ". " : "")
                              + "Tick to REVERT this mod's terrain and water in these cells; untick to keep it.",
                };
                cb.Checked += Revert_Changed; cb.Unchecked += Revert_Changed;
                _conflictBoxes[c.Plugin] = cb;
                // the action button comes FIRST (left), then the tick: the two are far apart so they are not mixed up
                var win = new Button
                {
                    Content = "Make winner", Width = 112, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Center, Tag = c.Plugin,
                    ToolTip = "Put this mod in the winners list below: it will win where it conflicts, ahead of mods loaded after it.",
                };
                win.Click += (_, _) => AddWinner(c.Plugin);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(win, 0); Grid.SetColumn(cb, 1);
                grid.Children.Add(win); grid.Children.Add(cb);
                ConflictList.Children.Add(new Border
                {
                    Child = grid, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(4),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(rowIndex++ % 2 == 0 ? 26 : 8), 255, 255, 255)),
                });
            }
            // a saved tick for a mod that no longer conflicts is dropped
            _revert.RemoveWhere(r => plan.Candidates.All(c => !c.Plugin.Equals(r, StringComparison.OrdinalIgnoreCase)));
            ScanSummary.Text = plan.Candidates.Count == 0
                ? "No mod overwrites trusted terrain or water."
                : $"{plan.Candidates.Count} mod(s) overwrite trusted terrain ({plan.Candidates.Sum(c => c.Cells)} cells) or water ({plan.Candidates.Sum(c => c.WaterCells)} cells); {plan.SkippedWater} more terrain cells skipped for water, {plan.SkippedReference} for placed references. Tick only the ones you want reverted.";

            // water mods found in the load order join the priority list (ticked ones keep their place at the top)
            _loadOrder = plan.LoadOrder.ToList();
            RebuildPreferredRoad(plan.RoadModChoices);
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

    const string NoPreferredRoad = "(none)";
    readonly Dictionary<string, CheckBox> _conflictBoxes = new(StringComparer.OrdinalIgnoreCase);

    void RebuildPreferredRoad(IEnumerable<string> found)
    {
        var keep = PreferredRoadCombo.SelectedItem as string ?? (_preferredRoad.Length > 0 ? _preferredRoad : NoPreferredRoad);
        var items = new List<string> { NoPreferredRoad };
        foreach (var n in found.Concat(keep != NoPreferredRoad ? new[] { keep } : Array.Empty<string>()))
            if (!items.Contains(n, StringComparer.OrdinalIgnoreCase)) items.Add(n);
        PreferredRoadCombo.Items.Clear();
        foreach (var i in items) PreferredRoadCombo.Items.Add(i);
        PreferredRoadCombo.SelectedItem = items.FirstOrDefault(i => i.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? NoPreferredRoad;
    }

    void AddWinner(string plugin)
    {
        if (!_winners.Contains(plugin, StringComparer.OrdinalIgnoreCase)) _winners.Insert(WinnerInsertIndex(plugin), plugin);
        _revert.Remove(plugin); // a winner is never reverted
        if (_conflictBoxes.TryGetValue(plugin, out var ticked)) ticked.IsChecked = false;
        RebuildWinnersList();
    }

    int LoadIndex(string plugin) => _loadOrder.FindIndex(p => p.Equals(plugin, StringComparison.OrdinalIgnoreCase));

    // A new winner goes where the load order puts it: above the first listed mod that loads EARLIER than it (the one loaded last is on top).
    // Mods already moved by hand keep their places; the new one is simply slotted in by its own load position.
    int WinnerInsertIndex(string plugin)
    {
        var mine = LoadIndex(plugin);
        if (mine < 0) return _winners.Count;
        for (var i = 0; i < _winners.Count; i++)
        {
            var other = LoadIndex(_winners[i]);
            if (other >= 0 && other < mine) return i;
        }
        return _winners.Count;
    }

    void SortWinners_Click(object sender, RoutedEventArgs e)
    {
        _winners = _winners.OrderByDescending(LoadIndex).ToList(); // unknown mods (index -1) sink to the bottom
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
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var remove = new Button { Content = "✕", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 10, 0), ToolTip = "Remove from the winners list" };
            var down = new Button { Content = "▼", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 2, 0), ToolTip = "Lower priority", IsEnabled = i < _winners.Count - 1 };
            var up = new Button { Content = "▲", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 2, 0), ToolTip = "Higher priority", IsEnabled = i > 0 };
            up.Click += (_, _) => MoveWinner(name, -1);
            down.Click += (_, _) => MoveWinner(name, +1);
            remove.Click += (_, _) => { _winners.RemoveAll(w => w.Equals(name, StringComparison.OrdinalIgnoreCase)); RebuildWinnersList(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(up); buttons.Children.Add(down); buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 0);
            var text = new TextBlock { Text = $"{i + 1}.  {name}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 1);
            row.Children.Add(buttons); row.Children.Add(text);
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
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var down = new Button { Content = "▼", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 10, 0), Tag = name, ToolTip = "Lower priority" };
            var up = new Button { Content = "▲", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 2, 0), Tag = name, ToolTip = "Higher priority" };
            up.IsEnabled = i > 0; down.IsEnabled = i < _waterOrder.Count - 1;
            up.Click += (_, _) => MoveWater(name, -1);
            down.Click += (_, _) => MoveWater(name, +1);
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            arrows.Children.Add(up); arrows.Children.Add(down);
            Grid.SetColumn(arrows, 0);
            var cb = new CheckBox { Content = new TextBlock { Text = $"{i + 1}.  {name}", Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap }, IsChecked = _waterTicked.Contains(name), Tag = name, VerticalContentAlignment = VerticalAlignment.Center };
            cb.Checked += Water_Changed; cb.Unchecked += Water_Changed;
            Grid.SetColumn(cb, 1);
            row.Children.Add(arrows); row.Children.Add(cb);
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
        if (needOutput && s.OutputDirectory == "" && s.ModManager != ModManager.ModOrganizer2) return "Pick an output folder (a dedicated folder, not your Data folder).";
        if (needOutput && !s.OutputPluginName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) return "The plugin name must end in .esp.";
        if (!s.Seams && !s.TextureLayers && !s.Floating && s.RoadSourcePlugin == "") return "Switch on at least one pass.";
        if (s.RoadSourcePlugin != "" && HybridRunner.CheckRoadInputs(s.RoadSourcePlugin, s.RoadMaskFolder, s.RoadWorldspace) is { } roadProblem && roadProblem.StartsWith("Problem")) return roadProblem;
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
        ResetStages();
        Save();
        var sw = Stopwatch.StartNew();
        void Log(string m) => Dispatcher.BeginInvoke(() => { OnLogLine(m); LogBox.AppendText(m + Environment.NewLine); LogBox.ScrollToEnd(); });
        try
        {
            var r = await Task.Run(() => HybridRunner.Run(s, Log));
            Dispatcher.Invoke(() => FinishStages(true));
            Log("");
            foreach (var n in r.Notes) Log("note: " + n);
            Log($"Seam cells {r.SeamCells}, texture cells {r.TextureCells}, road cells {r.RoadCells}, floating refs {r.FloatingRefs}, merged cells {r.MergedCells}.");
            SetBusy(false, dry ? $"Preview finished in {sw.Elapsed:mm\\:ss}. Nothing was written."
                : r.OutputPaths.Count > 0 ? $"Wrote {string.Join(", ", r.OutputPaths.Select(Path.GetFileName))} in {sw.Elapsed:mm\\:ss}. Enable it after your other landscape mods."
                : $"Finished in {sw.Elapsed:mm\\:ss}: no pass produced anything to write.");
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => FinishStages(false));
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
