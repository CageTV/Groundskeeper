using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SeamFinder.Core;

namespace LandscapeHybrid.UI;

/// <summary>
/// The problem map: terrain backdrop (land by height, water in blue) with the marks on top. Cell (x,y) covers
/// [x,x+1] x [y,y+1] in cell units, north up. Wheel zooms around the pointer, left-drag pans, click selects.
/// </summary>
public sealed class MapView : FrameworkElement
{
    List<OverviewCell> _cells = new();
    float _lo, _hi = 1;          // colour-ramp range of the current cells
    List<ProblemMark> _marks = new();
    double _scale = 8;           // pixels per cell
    double _ox, _oy;             // screen position of cell-space (0,0)
    Point? _dragFrom; bool _dragged;
    ProblemMark? _selected;
    (int X, int Y)? _hover;

    public event Action<ProblemMark?>? MarkClicked;
    public event Action<int, int>? CellHovered;
    public event Action<int, int>? CellClicked;

    public static readonly Dictionary<ProblemKind, Color> KindColors = new()
    {
        [ProblemKind.HeightSeam] = Color.FromRgb(0xFF, 0x5A, 0x4F),
        [ProblemKind.TextureSeam] = Color.FromRgb(0xC7, 0x68, 0xD8),
        [ProblemKind.WaterSeam] = Color.FromRgb(0x3D, 0xD6, 0xF5),
        [ProblemKind.FloatingObject] = Color.FromRgb(0xFF, 0xB3, 0x2B),
    };

    static readonly Brush[] LandRamp = BuildRamp();
    static Brush[] BuildRamp()
    {
        // low green -> olive -> brown -> grey -> white, 48 steps
        var stops = new (double, Color)[] { (0, Color.FromRgb(0x3E, 0x5F, 0x3A)), (.25, Color.FromRgb(0x77, 0x82, 0x45)), (.5, Color.FromRgb(0x8A, 0x6C, 0x45)), (.75, Color.FromRgb(0x9A, 0x96, 0x90)), (1, Color.FromRgb(0xEE, 0xEE, 0xEE)) };
        var r = new Brush[48];
        for (var i = 0; i < r.Length; i++)
        {
            var t = i / (double)(r.Length - 1);
            var k = 0; while (k < stops.Length - 2 && t > stops[k + 1].Item1) k++;
            var u = (t - stops[k].Item1) / (stops[k + 1].Item1 - stops[k].Item1);
            Color a = stops[k].Item2, b = stops[k + 1].Item2;
            var br = new SolidColorBrush(Color.FromRgb((byte)(a.R + (b.R - a.R) * u), (byte)(a.G + (b.G - a.G) * u), (byte)(a.B + (b.B - a.B) * u)));
            br.Freeze(); r[i] = br;
        }
        return r;
    }
    static readonly Brush[] WaterRamp = Enumerable.Range(0, 12).Select(i =>
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(0x1E + i * 2), (byte)(0x3C + i * 4), (byte)(0x6A + i * 5))); b.Freeze(); return (Brush)b;
    }).ToArray();

    public MapView() { ClipToBounds = true; Focusable = true; }

    public void SetData(List<OverviewCell> cells, List<ProblemMark> marks, bool refit)
    {
        _cells = cells; _marks = marks;
        if (cells.Count > 0)
        {
            var means = cells.Select(c => c.Mean).OrderBy(v => v).ToArray();
            _lo = means[(int)(means.Length * 0.02)]; _hi = means[(int)(means.Length * 0.98)];
            if (_hi - _lo < 1) _hi = _lo + 1;
        }
        if (refit) Fit();
        InvalidateVisual();
    }

    public void SetMarks(List<ProblemMark> marks) { _marks = marks; InvalidateVisual(); }

    public void Select(ProblemMark? m, bool center)
    {
        _selected = m;
        if (m is not null && center)
        {
            if (_scale < 16) _scale = 16;
            _ox = ActualWidth / 2 - (m.X + 0.5) * _scale;
            _oy = ActualHeight / 2 + (m.Y + 0.5) * _scale;
        }
        InvalidateVisual();
    }

    public void CenterOnCell(int x, int y)
    {
        if (_scale < 16) _scale = 16;
        _ox = ActualWidth / 2 - (x + 0.5) * _scale;
        _oy = ActualHeight / 2 + (y + 0.5) * _scale;
        InvalidateVisual();
    }

    public void Fit()
    {
        var pts = _cells.Count > 0 ? _cells.Select(c => (c.X, c.Y)) : _marks.Select(m => (m.X, m.Y));
        if (!pts.Any() || ActualWidth < 10) { _scale = 8; _ox = ActualWidth / 2; _oy = ActualHeight / 2; return; }
        int minX = pts.Min(p => p.Item1), maxX = pts.Max(p => p.Item1) + 1, minY = pts.Min(p => p.Item2), maxY = pts.Max(p => p.Item2) + 1;
        _scale = Math.Clamp(Math.Min(ActualWidth / Math.Max(1, maxX - minX + 2), ActualHeight / Math.Max(1, maxY - minY + 2)), 1.5, 80);
        _ox = ActualWidth / 2 - (minX + maxX) / 2.0 * _scale;
        _oy = ActualHeight / 2 + (minY + maxY) / 2.0 * _scale;
    }

    double SX(double cx) => _ox + cx * _scale;
    double SY(double cy) => _oy - cy * _scale;
    (double X, double Y) ToCell(Point p) => ((p.X - _ox) / _scale, (_oy - p.Y) / _scale);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x14)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 4) return;
        var vis = new Rect(-_scale, -_scale, ActualWidth + 2 * _scale, ActualHeight + 2 * _scale);

        // height range for the colour ramp: 2nd..98th percentile of cell means so one mountain does not flatten the rest
        if (_cells.Count > 0)
        {
            float lo = _lo, hi = _hi;
            var detailed = _scale >= 14;
            foreach (var c in _cells)
            {
                var x0 = SX(c.X); var y0 = SY(c.Y + 1);
                if (!vis.Contains(new Point(x0, y0))) continue;
                if (!detailed) dc.DrawRectangle(BrushFor(c.Mean, c.WaterHeight, lo, hi), null, new Rect(x0, y0, Math.Ceiling(_scale) + .5, Math.Ceiling(_scale) + .5));
                else
                {
                    var b = _scale / 4;
                    for (var by = 0; by < 4; by++)
                        for (var bx = 0; bx < 4; bx++)
                            dc.DrawRectangle(BrushFor(c.Blocks[by * 4 + bx], c.WaterHeight, lo, hi), null, new Rect(x0 + bx * b, y0 + (3 - by) * b, b + .5, b + .5));
                }
            }
        }

        // grid + axis labels
        var step = _scale >= 24 ? 1 : _scale >= 8 ? 5 : 10;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(_scale >= 24 ? 0x40 : 0x28), 255, 255, 255)), 1); gridPen.Freeze();
        var (cxMin, cyMax) = ToCell(new Point(0, 0)); var (cxMax, cyMin) = ToCell(new Point(ActualWidth, ActualHeight));
        for (var gx = (int)Math.Floor(cxMin / step) * step; gx <= cxMax; gx += step)
        {
            dc.DrawLine(gridPen, new Point(SX(gx), 0), new Point(SX(gx), ActualHeight));
            if (_scale >= 4) Label(dc, gx.ToString(CultureInfo.InvariantCulture), SX(gx) + 2, 2);
        }
        for (var gy = (int)Math.Floor(cyMin / step) * step; gy <= cyMax; gy += step)
        {
            dc.DrawLine(gridPen, new Point(0, SY(gy)), new Point(ActualWidth, SY(gy)));
            if (_scale >= 4) Label(dc, gy.ToString(CultureInfo.InvariantCulture), 2, SY(gy) - 14);
        }

        // marks: seams as lines on the shared edge, floating objects as dots
        foreach (var m in _marks)
        {
            var col = KindColors[m.Kind];
            var sel = ReferenceEquals(m, _selected);
            if (m.Kind == ProblemKind.FloatingObject)
            {
                var p = new Point(SX(m.X + 0.5), SY(m.Y + 0.5));
                if (!vis.Contains(p)) continue;
                dc.DrawEllipse(new SolidColorBrush(col), new Pen(Brushes.Black, 1), p, sel ? 8 : Math.Clamp(_scale / 3, 3, 7), sel ? 8 : Math.Clamp(_scale / 3, 3, 7));
                continue;
            }
            Point a, b;
            if (m.X2 != m.X) { var ex = Math.Max(m.X, m.X2); a = new Point(SX(ex), SY(Math.Min(m.Y, m.Y2))); b = new Point(SX(ex), SY(Math.Min(m.Y, m.Y2) + 1)); }
            else { var ey = Math.Max(m.Y, m.Y2); a = new Point(SX(Math.Min(m.X, m.X2)), SY(ey)); b = new Point(SX(Math.Min(m.X, m.X2) + 1), SY(ey)); }
            if (!vis.Contains(a) && !vis.Contains(b)) continue;
            var w = sel ? 5 : Math.Clamp(_scale / 5, 1.5, 4);
            var alpha = m.Kind == ProblemKind.HeightSeam ? (byte)255 : (byte)200;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, a, b);
        }

        if (_hover is { } h)
        {
            var pen = new Pen(Brushes.White, 1.5);
            dc.DrawRectangle(null, pen, new Rect(SX(h.X), SY(h.Y + 1), _scale, _scale));
        }
        if (_selected is { } s)
        {
            var pen = new Pen(Brushes.White, 2) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, new Rect(SX(s.X) - 2, SY(s.Y + 1) - 2, _scale + 4, _scale + 4));
        }
    }

    static Brush BrushFor(float h, float? water, float lo, float hi)
    {
        if (water is { } w && h < w) return WaterRamp[(int)Math.Clamp((h - (w - 6000)) / 6000 * (WaterRamp.Length - 1), 0, WaterRamp.Length - 1)];
        return LandRamp[(int)Math.Clamp((h - lo) / (hi - lo) * (LandRamp.Length - 1), 0, LandRamp.Length - 1)];
    }

    void Label(DrawingContext dc, string text, double x, double y)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x, y));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var (cx, cy) = ToCell(p);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1.5, 140);
        _ox = p.X - cx * _scale; _oy = p.Y + cy * _scale;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); _dragFrom = e.GetPosition(this); _dragged = false; CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var (cx, cy) = ToCell(p);
        var cell = ((int)Math.Floor(cx), (int)Math.Floor(cy));
        if (_hover != cell) { _hover = cell; CellHovered?.Invoke(cell.Item1, cell.Item2); InvalidateVisual(); }
        if (_dragFrom is { } f && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragged && (Math.Abs(p.X - f.X) > 3 || Math.Abs(p.Y - f.Y) > 3)) _dragged = true;
            if (_dragged) { _ox += p.X - f.X; _oy += p.Y - f.Y; _dragFrom = p; InvalidateVisual(); }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (!_dragged)
        {
            var (cx, cy) = ToCell(e.GetPosition(this));
            int x = (int)Math.Floor(cx), y = (int)Math.Floor(cy);
            // nearest mark on this cell or the cells it borders, worst first
            var near = _marks.Where(m => (m.X == x && m.Y == y) || (m.X2 == x && m.Y2 == y)).OrderByDescending(m => m.Severity).FirstOrDefault();
            _selected = near;
            MarkClicked?.Invoke(near);
            CellClicked?.Invoke(x, y);
            InvalidateVisual();
        }
        _dragFrom = null;
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.PreviousSize.Width < 10 && _cells.Count + _marks.Count > 0) Fit();
        InvalidateVisual();
    }
}
