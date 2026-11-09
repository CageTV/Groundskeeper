using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LandscapeHybrid;
using Microsoft.Win32;

namespace LandscapeHybrid.UI;

public partial class ProblemMapWindow : Window
{
    readonly HybridSettings _settings;
    readonly string[] _extraPlugins;
    ProblemMapData? _data;
    bool _ready;
    bool _throughUsed;
    ProblemMark? _current;
    (int X, int Y)? _lastCell;
    SeamFinder.Core.ReliefSession? _relief;

    /// <param name="extraPlugins">Plugins to append at the end of the load order for the scan, to preview a fresh output before it is enabled.</param>
    public ProblemMapWindow(HybridSettings settings, IEnumerable<string>? extraPlugins = null)
    {
        InitializeComponent();
        _settings = settings;
        _extraPlugins = (extraPlugins ?? Array.Empty<string>()).ToArray();
        Map.MarkClicked += m => { _current = m; ShowDetails(m); if (m is not null) SelectInList(m); };
        Map.CellClicked += (x, y) => { _lastCell = (x, y); ReliefButton.IsEnabled = _data is not null; };
        Closed += (_, _) => { try { _relief?.Dispose(); } catch { } };
        Map.CellHovered += (x, y) => HoverText.Text = $"Cell ({x}, {y})   console: cow {SelectedWorld} {x} {y}";
        Loaded += async (_, _) => await ScanAsync();
    }

    string SelectedWorld => WorldCombo.SelectedItem as string ?? "Tamriel";

    async Task ScanAsync()
    {
        BusyOverlay.Visibility = Visibility.Visible;
        RescanButton.IsEnabled = false;
        BusyText.Text = _extraPlugins.Length > 0
            ? $"Scanning the load order with {string.Join(", ", _extraPlugins.Select(System.IO.Path.GetFileName))} added at the end (about 20-40 seconds)..."
            : "Scanning the load order (about 20-40 seconds)...";
        try
        {
            try { _relief?.Dispose(); } catch { }
            _relief = null;
            var s = _settings;
            var through = ThroughCheck.IsChecked == true;
            _throughUsed = through;
            _data = await Task.Run(() => HybridRunner.ProblemMap(s, _ => { }, _extraPlugins, withoutPlugins: through ? new[] { "LandscapeFixer.esp" } : null));
            _ready = false;
            var worlds = _data.Overview.OrderByDescending(kv => kv.Value.Count).Select(kv => kv.Key)
                .Concat(_data.Marks.Select(m => m.Worldspace)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            WorldCombo.ItemsSource = worlds;
            WorldCombo.SelectedItem = worlds.FirstOrDefault(w => w.Equals("Tamriel", StringComparison.OrdinalIgnoreCase)) ?? worlds.FirstOrDefault();
            _ready = true;
            Refresh(refit: true);
            if (_data.Notes.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, _data.Notes), "Groundskeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            BusyText.Text = "The scan failed: " + ex.Message;
        }
        finally { RescanButton.IsEnabled = true; }
    }

    List<ProblemMark> Visible()
    {
        if (_data is null) return new();
        var w = SelectedWorld; var min = MinSlider.Value;
        return _data.Marks.Where(m => m.Worldspace.Equals(w, StringComparison.OrdinalIgnoreCase)
            && (OfficialCheck.IsChecked == true || !m.OfficialOnly)
            && m.Kind switch
            {
                ProblemKind.HeightSeam => HeightCheck.IsChecked == true && m.Severity >= min,
                ProblemKind.TextureSeam => TexCheck.IsChecked == true,
                ProblemKind.WaterSeam => WaterCheck.IsChecked == true,
                ProblemKind.FloatingObject => FloatCheck.IsChecked == true,
                _ => false,
            }).ToList();
    }

    void Refresh(bool refit)
    {
        if (_data is null || !_ready) return;
        MinText.Text = ((int)MinSlider.Value).ToString(CultureInfo.InvariantCulture);
        var w = SelectedWorld;
        var all = _data.Marks.Where(m => m.Worldspace.Equals(w, StringComparison.OrdinalIgnoreCase) && (OfficialCheck.IsChecked == true || !m.OfficialOnly)).ToList();
        int N(ProblemKind k) => all.Count(m => m.Kind == k);
        HeightCheck.Content = $"Height seams ({N(ProblemKind.HeightSeam)})";
        WaterCheck.Content = $"Possible water seams ({N(ProblemKind.WaterSeam)})";
        FloatCheck.Content = $"Floating objects left alone ({N(ProblemKind.FloatingObject)})";
        TexCheck.Content = $"Texture seams ({N(ProblemKind.TextureSeam)}, many are harmless)";
        var vis = Visible();
        Map.SetData(_data.Overview.TryGetValue(w, out var cells) ? cells : new(), vis, refit);
        Summary.Text = $"{vis.Count} shown in {w}. Scanned {_data.PluginCount} plugins" + (_throughUsed ? " as if Landscape Fixer's output were off, so seams are credited to the mods that cause them." : ", your enabled outputs included: this is what the game would still show.");
        var items = vis.OrderByDescending(m => Rank(m)).Take(600).ToList();
        ProblemList.ItemsSource = items.Select(m => new ListBoxItem { Content = Row(m), Tag = m, Foreground = new SolidColorBrush(MapView.KindColors[m.Kind]) }).ToList();
        _current = null; ShowDetails(null);
    }

    // seams and floating objects have different units: rank within a kind by size, and show floating objects and big height steps first
    static double Rank(ProblemMark m) => m.Kind switch
    {
        ProblemKind.HeightSeam => 1000 + m.Severity,
        ProblemKind.FloatingObject => 500 + Math.Min(m.Severity, 400),
        ProblemKind.WaterSeam => 200 + m.Severity,
        _ => m.Severity,
    };

    static string Row(ProblemMark m) => m.Kind switch
    {
        ProblemKind.HeightSeam => $"Height step {m.Severity:F0}u   ({m.X},{m.Y}) | ({m.X2},{m.Y2})",
        ProblemKind.WaterSeam => $"Water {m.Severity:F0}/33 exposed   ({m.X},{m.Y}) | ({m.X2},{m.Y2})",
        ProblemKind.TextureSeam => $"Texture   ({m.X},{m.Y}) | ({m.X2},{m.Y2})",
        _ => $"Floating {m.Severity:F0}u   cell ({m.X},{m.Y})",
    };

    void ShowDetails(ProblemMark? m)
    {
        CopyButton.IsEnabled = m is not null;
        if (m is not null) ReliefButton.IsEnabled = true;
        Details.Text = m is null ? "Nothing selected." : $"{m.Detail}\n{m.ConsoleCommand}";
    }

    void SelectInList(ProblemMark m)
    {
        foreach (ListBoxItem it in ProblemList.Items)
            if (ReferenceEquals(it.Tag, m)) { ProblemList.SelectedItem = it; ProblemList.ScrollIntoView(it); return; }
        ProblemList.SelectedItem = null;
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => Refresh(refit: sender == WorldCombo);

    void ProblemList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemList.SelectedItem is ListBoxItem { Tag: ProblemMark m }) { _current = m; Map.Select(m, center: true); ShowDetails(m); }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try { Clipboard.SetText(_current.ConsoleCommand); HoverText.Text = $"Copied: {_current.ConsoleCommand}"; } catch { HoverText.Text = "The clipboard is busy; try again."; }
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "Groundskeeper-problems.csv" };
        if (dlg.ShowDialog(this) == true) HybridRunner.WriteProblemCsv(_data, dlg.FileName);
    }

    async void Relief_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        var world = SelectedWorld;
        int x, y;
        if (_current is not null) { x = _current.X; y = _current.Y; }
        else if (_lastCell is { } c) { x = c.X; y = c.Y; }
        else return;
        ReliefButton.IsEnabled = false;
        try
        {
            if (_relief is null)
            {
                HoverText.Text = "Opening the load order for the 3D view (a few seconds the first time)...";
                var order = _data.LoadOrder;
                _relief = await Task.Run(() => SeamFinder.Core.ReliefSession.Open(order));
            }
            HoverText.Text = "Wheel zooms, drag pans, click a coloured edge or dot for details.";
            new ReliefWindow(_relief, world, x, y, Visible(), _current) { Owner = this }.Show();
        }
        catch (Exception ex) { HoverText.Text = "Could not open the 3D view: " + ex.Message; }
        finally { ReliefButton.IsEnabled = true; }
    }

    async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    async void Through_Changed(object sender, RoutedEventArgs e) { if (IsLoaded && RescanButton.IsEnabled) await ScanAsync(); }
}
