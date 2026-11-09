using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SeamFinder.Core;

namespace LandscapeHybrid.UI;

/// <summary>
/// The 3D terrain view: the real terrain (33 x 33 vertices per cell, read from the winning landscape of the scanned load order) of a block of cells
/// around a chosen cell, drawn as a lit mesh, with the problem marks on it. Each cell is its own mesh, so a height seam shows as a real crack.
/// </summary>
public partial class ReliefWindow : Window
{
    const double Cell = 4096, Quad = 128;
    readonly ReliefSession _session;
    readonly List<ProblemMark> _marks;
    readonly string _world;
    ProblemMark? _selected;
    int _cx, _cy;
    ReliefGrid? _grid;
    readonly bool _ready;
    int _loadId;

    // orbit camera around a target point (scene units are game units, centred on the middle of the centre cell)
    double _yaw = -0.6, _pitch = 0.75, _dist = 9000;
    Point3D _target = new(0, 0, 0);
    Point? _drag; bool _panning;

    public ReliefWindow(ReliefSession session, string worldspace, int cx, int cy, IEnumerable<ProblemMark> marks, ProblemMark? selected)
    {
        InitializeComponent();
        _session = session; _world = worldspace; _cx = cx; _cy = cy; _marks = marks.ToList(); _selected = selected;
        _ready = true;
        Catcher.MouseLeftButtonDown += (s, e) => { _drag = e.GetPosition(Catcher); _panning = false; Catcher.CaptureMouse(); };
        Catcher.MouseRightButtonDown += (s, e) => { _drag = e.GetPosition(Catcher); _panning = true; Catcher.CaptureMouse(); };
        Catcher.MouseUp += (s, e) => { _drag = null; Catcher.ReleaseMouseCapture(); };
        Catcher.MouseMove += OnMove;
        Catcher.MouseWheel += (s, e) => { _dist = Math.Clamp(_dist * Math.Pow(0.9, e.Delta / 120.0), 600, 120000); UpdateCamera(); };
        UpdateCamera();
        Loaded += async (_, _) => await LoadAsync();
    }

    int Radius => RadiusCombo.SelectedIndex + 1;
    double Exag => ExagSlider.Value;

    async Task LoadAsync()
    {
        var id = ++_loadId;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyText.Text = $"Reading terrain around cell ({_cx}, {_cy})...";
        Heading.Text = $"{_world} ({_cx}, {_cy})";
        try
        {
            var world = _world; var cx = _cx; var cy = _cy; var r = Radius;
            var grid = await Task.Run(() => _session.Load(world, cx, cy, r));
            if (id != _loadId) return;
            _grid = grid;
            _dist = (2 * r + 1) * Cell * 0.6; _pitch = 0.9; // frames the block, looking at the chosen cell
            Rebuild();
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { BusyText.Text = "Could not read the terrain: " + ex.Message; }
    }

    void Rebuild()
    {
        if (_grid is null) return;
        var g = _grid;
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x5C, 0x5C, 0x66)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0xE8, 0xE2, 0xD4), new Vector3D(-0.5, -0.8, -0.35)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x2A, 0x30, 0x40), new Vector3D(0.6, -0.3, 0.7)));

        // origin: middle of the centre cell, so scene numbers stay small
        double ox = (g.CenterX + 0.5) * Cell, oy = (g.CenterY + 0.5) * Cell;
        double Sx(double wx) => wx - ox;
        double Sz(double wy) => -(wy - oy);
        var lo = g.Cells.Count == 0 ? 0f : g.Cells.Min(c => Min(c.Heights));
        var hi = g.Cells.Count == 0 ? 1f : g.Cells.Max(c => Max(c.Heights));
        if (hi - lo < 1) hi = lo + 1;
        var byCell = g.Cells.ToDictionary(c => (c.X, c.Y));
        var exag = Exag;
        double Y(float h) => (h - lo) * exag;

        foreach (var c in g.Cells)
        {
            var pos = new Point3DCollection(33 * 33); var uv = new PointCollection(33 * 33);
            for (var j = 0; j < 33; j++)
                for (var i = 0; i < 33; i++)
                {
                    pos.Add(new Point3D(Sx(c.X * Cell + i * Quad), Y(c.Heights[i, j]), Sz(c.Y * Cell + j * Quad)));
                    uv.Add(new Point(i / 32.0, 1 - j / 32.0));
                }
            var idx = new Int32Collection(32 * 32 * 6);
            for (var j = 0; j < 32; j++)
                for (var i = 0; i < 32; i++)
                {
                    int p00 = j * 33 + i, p10 = p00 + 1, p01 = p00 + 33, p11 = p01 + 1;
                    idx.Add(p00); idx.Add(p10); idx.Add(p01);
                    idx.Add(p10); idx.Add(p11); idx.Add(p01);
                }
            var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx, TextureCoordinates = uv };
            var mat = new DiffuseMaterial(new ImageBrush(TextureFor(c, lo, hi)));
            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });

            if (WaterCheck.IsChecked == true && c.WaterHeight is { } wh && Min(c.Heights) < wh)
            {
                var wy = Y(wh);
                var wm = new MeshGeometry3D();
                AddQuad(wm, new Point3D(Sx(c.X * Cell), wy, Sz(c.Y * Cell)), new Point3D(Sx((c.X + 1) * Cell), wy, Sz(c.Y * Cell)),
                    new Point3D(Sx((c.X + 1) * Cell), wy, Sz((c.Y + 1) * Cell)), new Point3D(Sx(c.X * Cell), wy, Sz((c.Y + 1) * Cell)));
                var wmat = new MaterialGroup();
                wmat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x99, 0x2E, 0x6F, 0xB8))));
                wmat.Children.Add(new SpecularMaterial(Brushes.White, 60));
                group.Children.Add(new GeometryModel3D(wm, wmat) { BackMaterial = wmat });
            }
        }

        if (WireCheck.IsChecked == true)
        {
            var lines = new MeshGeometry3D();
            foreach (var c in g.Cells)
            {
                Ribbon(lines, Enumerable.Range(0, 33).Select(i => new Point3D(Sx(c.X * Cell + i * Quad), Y(c.Heights[i, 0]) + 6, Sz(c.Y * Cell))), 14);
                Ribbon(lines, Enumerable.Range(0, 33).Select(j => new Point3D(Sx(c.X * Cell), Y(c.Heights[0, j]) + 6, Sz(c.Y * Cell + j * Quad))), 14);
            }
            group.Children.Add(Glow(lines, Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)));
        }

        if (MarksCheck.IsChecked == true)
        {
            foreach (var kind in Enum.GetValues<ProblemKind>())
            {
                var mesh = new MeshGeometry3D(); var curtain = new MeshGeometry3D(); var sel = new MeshGeometry3D();
                foreach (var m in _marks.Where(m => m.Kind == kind && m.Worldspace.Equals(_world, StringComparison.OrdinalIgnoreCase)))
                {
                    var isSel = _selected is not null && ReferenceEquals(m, _selected);
                    var target = isSel ? sel : mesh;
                    if (kind == ProblemKind.FloatingObject)
                    {
                        if (!byCell.TryGetValue((m.X, m.Y), out var fc)) continue;
                        if (m.WorldX is { } fx && m.WorldY is { } fy && m.RefZ is { } fz && m.TerrainZ is { } ft)
                        {
                            // the real object: a thin post from the ground up to where the object floats, and a block at the object itself
                            double px = Sx(fx), pz = Sz(fy), gy = Y((float)ft), ry = Y((float)fz);
                            AddBox(target, px, gy, pz, isSel ? 22 : 12, Math.Max(2, ry - gy));
                            AddBox(target, px, ry - 30 * exag, pz, isSel ? 130 : 90, 60 * exag);
                            continue;
                        }
                        double mx = Sx((m.X + 0.5) * Cell), mz = Sz((m.Y + 0.5) * Cell), baseY = Y(fc.Heights[16, 16]);
                        AddBox(target, mx, baseY, mz, isSel ? 90 : 60, 1800);
                        continue;
                    }
                    if (!byCell.TryGetValue((m.X, m.Y), out var a) || !byCell.TryGetValue((m.X2, m.Y2), out var b)) continue;
                    var pts = new List<Point3D>(); var lows = new List<double>(); var highs = new List<double>();
                    for (var k = 0; k < 33; k++)
                    {
                        float ha, hb; double wx, wy;
                        if (m.X2 == m.X + 1) { ha = a.Heights[32, k]; hb = b.Heights[0, k]; wx = (m.X + 1) * Cell; wy = m.Y * Cell + k * Quad; }
                        else if (m.X2 == m.X - 1) { ha = a.Heights[0, k]; hb = b.Heights[32, k]; wx = m.X * Cell; wy = m.Y * Cell + k * Quad; }
                        else if (m.Y2 == m.Y + 1) { ha = a.Heights[k, 32]; hb = b.Heights[k, 0]; wx = m.X * Cell + k * Quad; wy = (m.Y + 1) * Cell; }
                        else { ha = a.Heights[k, 0]; hb = b.Heights[k, 32]; wx = m.X * Cell + k * Quad; wy = m.Y * Cell; }
                        pts.Add(new Point3D(Sx(wx), Math.Max(Y(ha), Y(hb)) + 20, Sz(wy)));
                        lows.Add(Y(Math.Min(ha, hb))); highs.Add(Y(Math.Max(ha, hb)));
                    }
                    Ribbon(target, pts, isSel ? 90 : 55);
                    if (kind == ProblemKind.HeightSeam)
                        for (var k = 0; k < 32; k++)
                            AddQuad(isSel ? sel : curtain, new Point3D(pts[k].X, lows[k], pts[k].Z), new Point3D(pts[k + 1].X, lows[k + 1], pts[k + 1].Z),
                                new Point3D(pts[k + 1].X, highs[k + 1], pts[k + 1].Z), new Point3D(pts[k].X, highs[k], pts[k].Z));
                }
                var col = MapView.KindColors[kind];
                if (mesh.Positions.Count > 0) group.Children.Add(Glow(mesh, Color.FromRgb(col.R, col.G, col.B)));
                if (curtain.Positions.Count > 0) group.Children.Add(Glow(curtain, Color.FromArgb(0xAA, col.R, col.G, col.B)));
                if (sel.Positions.Count > 0) group.Children.Add(Glow(sel, Colors.White));
            }
        }

        Scene.Content = group;
        _target = byCell.TryGetValue((g.CenterX, g.CenterY), out var mid) ? new Point3D(0, Y((float)mid.Heights.Cast<float>().Average()), 0) : new Point3D(0, 0, 0);
        UpdateCamera();
        var owners = g.Cells.Select(c => c.Owner).Distinct().ToList();
        Info.Text = $"{g.Cells.Count} cell(s) read. Height range {lo:F0} to {hi:F0} units. Terrain comes from the winning plugin of each cell ({string.Join(", ", owners.Take(4))}{(owners.Count > 4 ? ", ..." : "")}).";
    }

    static float Min(float[,] h) { var m = float.MaxValue; foreach (var v in h) if (v < m) m = v; return m; }
    static float Max(float[,] h) { var m = float.MinValue; foreach (var v in h) if (v > m) m = v; return m; }

    static GeometryModel3D Glow(MeshGeometry3D mesh, Color c)
    {
        var mg = new MaterialGroup();
        mg.Children.Add(new DiffuseMaterial(new SolidColorBrush(Colors.Black)));
        mg.Children.Add(new EmissiveMaterial(new SolidColorBrush(c)));
        return new GeometryModel3D(mesh, mg) { BackMaterial = mg };
    }

    static void AddQuad(MeshGeometry3D m, Point3D a, Point3D b, Point3D c, Point3D d)
    {
        var i = m.Positions.Count;
        m.Positions.Add(a); m.Positions.Add(b); m.Positions.Add(c); m.Positions.Add(d);
        m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 2);
        m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 2); m.TriangleIndices.Add(i + 3);
    }

    /// <summary>A flat strip of the given width following a polyline (width is across the line, in the ground plane).</summary>
    static void Ribbon(MeshGeometry3D m, IEnumerable<Point3D> line, double width)
    {
        var p = line.ToList();
        for (var k = 0; k + 1 < p.Count; k++)
        {
            var d = p[k + 1] - p[k]; d.Y = 0;
            if (d.Length < 1e-6) continue;
            d.Normalize();
            var n = new Vector3D(-d.Z, 0, d.X) * (width / 2);
            AddQuad(m, p[k] - n, p[k + 1] - n, p[k + 1] + n, p[k] + n);
        }
    }

    static void AddBox(MeshGeometry3D m, double x, double y, double z, double w, double h)
    {
        double a = w / 2;
        Point3D[] v = { new(x - a, y, z - a), new(x + a, y, z - a), new(x + a, y, z + a), new(x - a, y, z + a),
                        new(x - a, y + h, z - a), new(x + a, y + h, z - a), new(x + a, y + h, z + a), new(x - a, y + h, z + a) };
        AddQuad(m, v[0], v[1], v[5], v[4]); AddQuad(m, v[1], v[2], v[6], v[5]); AddQuad(m, v[2], v[3], v[7], v[6]);
        AddQuad(m, v[3], v[0], v[4], v[7]); AddQuad(m, v[4], v[5], v[6], v[7]);
    }

    /// <summary>The ground colour of one cell: green lowland to rock to snow by height within the block, steep slopes greyer, ground under water blue.</summary>
    static BitmapSource TextureFor(ReliefCell c, float lo, float hi)
    {
        const int N = 96;
        var px = new byte[N * N * 4];
        for (var py = 0; py < N; py++)
            for (var qx = 0; qx < N; qx++)
            {
                double fx = qx / (double)(N - 1) * 32, fy = (N - 1 - py) / (double)(N - 1) * 32;
                var h = Sample(c.Heights, fx, fy);
                var slope = (Math.Abs(Sample(c.Heights, Math.Min(32, fx + 0.5), fy) - Sample(c.Heights, Math.Max(0, fx - 0.5), fy))
                           + Math.Abs(Sample(c.Heights, fx, Math.Min(32, fy + 0.5)) - Sample(c.Heights, fx, Math.Max(0, fy - 0.5)))) / Quad;
                var t = Math.Clamp((h - lo) / (hi - lo), 0, 1);
                var col = Ramp(t);
                var rock = Math.Clamp((slope - 0.35) / 0.7, 0, 1);
                col = Lerp(col, Color.FromRgb(0x8C, 0x88, 0x82), rock * 0.8);
                if (c.WaterHeight is { } wh && h < wh) col = Lerp(col, Color.FromRgb(0x1C, 0x3E, 0x6A), Math.Clamp((wh - h) / 300.0 + 0.35, 0.35, 0.9));
                var o = (py * N + qx) * 4;
                px[o] = col.B; px[o + 1] = col.G; px[o + 2] = col.R; px[o + 3] = 255;
            }
        var bmp = BitmapSource.Create(N, N, 96, 96, PixelFormats.Bgra32, null, px, N * 4);
        bmp.Freeze();
        return bmp;
    }

    static float Sample(float[,] h, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(32, x0 + 1), y1 = Math.Min(32, y0 + 1); x0 = Math.Min(32, x0); y0 = Math.Min(32, y0);
        double tx = x - Math.Floor(x), ty = y - Math.Floor(y);
        return (float)((h[x0, y0] * (1 - tx) + h[x1, y0] * tx) * (1 - ty) + (h[x0, y1] * (1 - tx) + h[x1, y1] * tx) * ty);
    }

    static readonly (double, Color)[] Stops = {
        (0, Color.FromRgb(0x3E, 0x5F, 0x3A)), (.3, Color.FromRgb(0x77, 0x82, 0x45)), (.55, Color.FromRgb(0x8A, 0x6C, 0x45)), (.8, Color.FromRgb(0x9A, 0x96, 0x90)), (1, Color.FromRgb(0xF2, 0xF2, 0xF2)) };
    static Color Ramp(double t)
    {
        var k = 0; while (k < Stops.Length - 2 && t > Stops[k + 1].Item1) k++;
        return Lerp(Stops[k].Item2, Stops[k + 1].Item2, (t - Stops[k].Item1) / (Stops[k + 1].Item1 - Stops[k].Item1));
    }
    static Color Lerp(Color a, Color b, double u) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * u), (byte)(a.G + (b.G - a.G) * u), (byte)(a.B + (b.B - a.B) * u));

    void UpdateCamera()
    {
        var cp = Math.Cos(_pitch); var sp = Math.Sin(_pitch);
        var off = new Vector3D(_dist * cp * Math.Sin(_yaw), _dist * sp, _dist * cp * Math.Cos(_yaw));
        Cam.Position = _target + off;
        Cam.LookDirection = -off;
        Cam.UpDirection = new Vector3D(0, 1, 0);
    }

    void OnMove(object s, MouseEventArgs e)
    {
        if (_drag is not { } f) return;
        var p = e.GetPosition(Catcher);
        double dx = p.X - f.X, dy = p.Y - f.Y;
        if (_panning)
        {
            // pan across the ground plane, relative to where the camera is looking
            var k = _dist / 700.0;
            var right = new Vector3D(Math.Cos(_yaw), 0, -Math.Sin(_yaw));
            var fwd = new Vector3D(-Math.Sin(_yaw), 0, -Math.Cos(_yaw));
            _target = _target - right * dx * k + fwd * dy * k;
        }
        else
        {
            _yaw -= dx * 0.008;
            _pitch = Math.Clamp(_pitch + dy * 0.006, 0.08, 1.52);
        }
        _drag = p;
        UpdateCamera();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG picture|*.png", FileName = $"Groundskeeper-{_world}-{_cx}_{_cy}.png" };
        if (dlg.ShowDialog(this) != true) return;
        var w = (int)ViewHost.ActualWidth; var h = (int)ViewHost.ActualHeight;
        if (w < 2 || h < 2) return;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        Hint.Visibility = Visibility.Hidden; ViewHost.UpdateLayout();
        try { rtb.Render(ViewHost); } finally { Hint.Visibility = Visibility.Visible; }
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(dlg.FileName); enc.Save(fs);
    }

    void Exag_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ExagText.Text = ((int)ExagSlider.Value).ToString(CultureInfo.InvariantCulture) + "x";
        if (_grid is not null) Rebuild();
    }
    void Layer_Changed(object sender, RoutedEventArgs e) { if (_ready && _grid is not null) Rebuild(); }
    async void Radius_Changed(object sender, RoutedEventArgs e) { if (_ready && IsLoaded) await LoadAsync(); }

    async Task Move(int dx, int dy) { _cx += dx; _cy += dy; _selected = null; await LoadAsync(); }
    async void North_Click(object s, RoutedEventArgs e) => await Move(0, 1);
    async void South_Click(object s, RoutedEventArgs e) => await Move(0, -1);
    async void East_Click(object s, RoutedEventArgs e) => await Move(1, 0);
    async void West_Click(object s, RoutedEventArgs e) => await Move(-1, 0);
}
