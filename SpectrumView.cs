using System;
using System.Windows;
using System.Windows.Media;

namespace SpectrumWidget;

/// <summary>频谱绘制：柱状（带倒影和峰值帽）/ 镜像 / 平滑波形。颜色随封面主色渐变过渡。</summary>
sealed class SpectrumView : FrameworkElement
{
    float[] _level = Array.Empty<float>();
    float[] _peak = Array.Empty<float>();
    float[] _hold = Array.Empty<float>();
    float[] _vel = Array.Empty<float>();
    float[] _tmp = Array.Empty<float>();

    Color _a = Color.FromRgb(0x6E, 0xA8, 0xFE), _b = Color.FromRgb(0xC0, 0x9B, 0xFF);
    Color _ta, _tb;

    Brush? _barBrush, _reflBrush, _peakBrush, _waveFill;
    Pen? _wavePen, _wavePen2;
    Size _brushSize;
    VisualStyle _brushStyle;
    bool _brushDirty = true;
    bool _idleDrawn;

    public VisualStyle Mode { get; set; } = VisualStyle.Bars;

    public SpectrumView()
    {
        _ta = _a; _tb = _b;
        IsHitTestVisible = true;
    }

    public void SetAccent(Color a, Color b) { _ta = a; _tb = b; }

    public void Invalidate() { _brushDirty = true; _idleDrawn = false; InvalidateVisual(); }

    public void Update(float[] target, double dt)
    {
        int n = target.Length;
        if (_level.Length != n)
        {
            _level = new float[n]; _peak = new float[n]; _hold = new float[n]; _vel = new float[n]; _tmp = new float[n];
        }

        // 轻微的横向平滑，波形模式更柔一些
        float side = Mode == VisualStyle.Wave ? 0.25f : 0.12f;
        for (int i = 0; i < n; i++)
        {
            float l = target[Math.Max(0, i - 1)], r = target[Math.Min(n - 1, i + 1)];
            _tmp[i] = target[i] * (1 - 2 * side) + (l + r) * side;
        }

        float up = (float)(1 - Math.Exp(-dt * 30));
        float down = (float)(1 - Math.Exp(-dt * 7));
        bool active = false;
        for (int i = 0; i < n; i++)
        {
            float t = _tmp[i];
            _level[i] += (t - _level[i]) * (t > _level[i] ? up : down);
            if (_level[i] < 0.0005f) _level[i] = 0;

            if (_level[i] >= _peak[i]) { _peak[i] = _level[i]; _hold[i] = 0.45f; _vel[i] = 0; }
            else if (_hold[i] > 0) _hold[i] -= (float)dt;
            else
            {
                _vel[i] += (float)(dt * 1.6);
                _peak[i] = Math.Max(_level[i], _peak[i] - _vel[i] * (float)dt);
            }
            if (_level[i] > 0 || _peak[i] > 0.001f) active = true;
        }

        // 颜色过渡
        double k = 1 - Math.Exp(-dt * 3.5);
        if (!Close(_a, _ta) || !Close(_b, _tb))
        {
            _a = Lerp(_a, _ta, k); _b = Lerp(_b, _tb, k);
            _brushDirty = true;
            active = true;
        }

        if (active || !_idleDrawn)
        {
            _idleDrawn = !active;
            InvalidateVisual();
        }
    }

    static bool Close(Color x, Color y) =>
        Math.Abs(x.R - y.R) + Math.Abs(x.G - y.G) + Math.Abs(x.B - y.B) < 3;

    static Color Lerp(Color x, Color y, double t) => Color.FromRgb(
        (byte)Math.Round(x.R + (y.R - x.R) * t),
        (byte)Math.Round(x.G + (y.G - x.G) * t),
        (byte)Math.Round(x.B + (y.B - x.B) * t));

    static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    double BaseY(double h) => Mode switch
    {
        VisualStyle.Bars => h * 0.8,
        VisualStyle.Mirror => h * 0.5,
        _ => h - 1,
    };

    void EnsureBrushes(Size size)
    {
        if (!_brushDirty && size == _brushSize && _brushStyle == Mode) return;
        _brushDirty = false; _brushSize = size; _brushStyle = Mode;
        double h = size.Height, baseY = BaseY(h);

        if (Mode == VisualStyle.Mirror)
        {
            var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, h) };
            g.GradientStops.Add(new GradientStop(_b, 0));
            g.GradientStops.Add(new GradientStop(_a, 0.5));
            g.GradientStops.Add(new GradientStop(_b, 1));
            g.Freeze();
            _barBrush = g;
        }
        else
        {
            var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, baseY), EndPoint = new Point(0, 0) };
            g.GradientStops.Add(new GradientStop(_a, 0));
            g.GradientStops.Add(new GradientStop(_b, 1));
            g.Freeze();
            _barBrush = g;
        }

        var r = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, baseY), EndPoint = new Point(0, h) };
        r.GradientStops.Add(new GradientStop(WithA(_a, 90), 0));
        r.GradientStops.Add(new GradientStop(WithA(_a, 0), 1));
        r.Freeze();
        _reflBrush = r;

        var p = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        p.Freeze();
        _peakBrush = p;

        var wf = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, h) };
        wf.GradientStops.Add(new GradientStop(WithA(_b, 170), 0));
        wf.GradientStops.Add(new GradientStop(WithA(_a, 90), 0.6));
        wf.GradientStops.Add(new GradientStop(WithA(_a, 0), 1));
        wf.Freeze();
        _waveFill = wf;

        var hz = new LinearGradientBrush(_a, _b, 0);
        hz.Freeze();
        _wavePen = new Pen(hz, 2) { LineJoin = PenLineJoin.Round };
        _wavePen.Freeze();
        _wavePen2 = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1) { LineJoin = PenLineJoin.Round };
        _wavePen2.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int n = _level.Length;
        if (w <= 0 || h <= 0 || n == 0) return;
        EnsureBrushes(new Size(w, h));
        // 透明底，便于拖动时命中
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        switch (Mode)
        {
            case VisualStyle.Bars: DrawBars(dc, w, h, n); break;
            case VisualStyle.Mirror: DrawMirror(dc, w, h, n); break;
            case VisualStyle.Wave: DrawWave(dc, w, h, n); break;
        }
    }

    void DrawBars(DrawingContext dc, double w, double h, int n)
    {
        double slot = w / n, bw = Math.Max(2, slot * 0.62);
        double baseY = BaseY(h), maxH = baseY - 5;
        double rad = Math.Min(bw / 2, 2.5);
        for (int i = 0; i < n; i++)
        {
            double x = i * slot + (slot - bw) / 2;
            double bh = Math.Max(2.5, _level[i] * maxH);
            dc.DrawRoundedRectangle(_barBrush, null, new Rect(x, baseY - bh, bw, bh), rad, rad);

            double rh = Math.Min(h - baseY - 2, bh * 0.4);
            if (rh > 0.5)
                dc.DrawRoundedRectangle(_reflBrush, null, new Rect(x, baseY + 2, bw, rh), rad, rad);

            if (_peak[i] > 0.01)
            {
                double py = baseY - _peak[i] * maxH - 4;
                dc.DrawRoundedRectangle(_peakBrush, null, new Rect(x, py, bw, 2), 1, 1);
            }
        }
    }

    void DrawMirror(DrawingContext dc, double w, double h, int n)
    {
        double slot = w / n, bw = Math.Max(2, slot * 0.55);
        double mid = h / 2, maxH = mid - 2;
        double rad = Math.Min(bw / 2, 2.5);
        for (int i = 0; i < n; i++)
        {
            // 低频放在中间，向两侧展开
            int src = Math.Abs(i - n / 2) * 2;
            if (src >= n) src = n - 1;
            float lv = _level[src];
            double x = i * slot + (slot - bw) / 2;
            double bh = Math.Max(1.5, lv * maxH);
            dc.DrawRoundedRectangle(_barBrush, null, new Rect(x, mid - bh, bw, bh * 2), rad, rad);
        }
    }

    void DrawWave(DrawingContext dc, double w, double h, int n)
    {
        double baseY = BaseY(h), maxH = baseY - 6;
        var pts = new Point[n + 2];
        double slot = w / n;
        pts[0] = new Point(0, baseY - _level[0] * maxH * 0.6);
        for (int i = 0; i < n; i++) pts[i + 1] = new Point(i * slot + slot / 2, baseY - _level[i] * maxH);
        pts[n + 1] = new Point(w, baseY - _level[n - 1] * maxH * 0.6);

        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, baseY), true, true);
            ctx.LineTo(pts[0], false, false);
            Spline(ctx, pts);
            ctx.LineTo(new Point(w, baseY), false, false);
        }
        fill.Freeze();
        dc.DrawGeometry(_waveFill, null, fill);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            Spline(ctx, pts);
        }
        line.Freeze();
        dc.DrawGeometry(null, _wavePen, line);

        // 峰值包络细线
        var peak = new StreamGeometry();
        var pp = new Point[n + 2];
        pp[0] = new Point(0, baseY - _peak[0] * maxH * 0.6);
        for (int i = 0; i < n; i++) pp[i + 1] = new Point(i * slot + slot / 2, baseY - _peak[i] * maxH - 3);
        pp[n + 1] = new Point(w, baseY - _peak[n - 1] * maxH * 0.6);
        using (var ctx = peak.Open())
        {
            ctx.BeginFigure(pp[0], false, false);
            Spline(ctx, pp);
        }
        peak.Freeze();
        dc.PushOpacity(0.5);
        dc.DrawGeometry(null, _wavePen2, peak);
        dc.Pop();
    }

    // Catmull-Rom → 三次贝塞尔
    static void Spline(StreamGeometryContext ctx, Point[] p)
    {
        for (int i = 0; i < p.Length - 1; i++)
        {
            Point p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(p.Length - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.BezierTo(c1, c2, p2, true, true);
        }
    }
}
