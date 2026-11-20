using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reddholo.App
{
    /// <summary>Theme-aware helpers: colours are always resource keys, so a live theme switch repaints everything.</summary>
    internal static class Ui
    {
        public static void Res(Control c, AvaloniaProperty p, string key) => c.Bind(p, c.GetResourceObservable(key));

        /// <summary>Res, skipped when the control already points at <paramref name="key"/> (cheap per-tick calls).</summary>
        public static void Ref(Control c, AvaloniaProperty p, string key, ref string current)
        {
            if (current == key) return;
            current = key;
            Res(c, p, key);
        }

        public static TextBlock Text(string s = "", double size = 14, string brush = "Text", bool mono = false, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap };
            Res(t, TextBlock.ForegroundProperty, brush);
            if (mono) Res(t, TextBlock.FontFamilyProperty, "Mono");
            if (weight != null) t.FontWeight = weight.Value;
            return t;
        }

        public static TextBlock Caps(string s, Thickness margin = default)
        {
            var t = new TextBlock { Text = s, Margin = margin };
            t.Classes.Add("caps");
            return t;
        }

        public static Border Card(Control child, Thickness margin = default)
        {
            var b = new Border { Child = child, Margin = margin };
            b.Classes.Add("card");
            return b;
        }

        public static Button Button(string text, string cls, Thickness margin = default)
        {
            var b = new Button { Content = text, Margin = margin };
            b.Classes.Add("ghost");
            if (cls != "ghost") b.Classes.Add(cls);
            return b;
        }

        public static string FanText(GpuModel m) => m.Fans.Length == 0 ? "--" : string.Join(" · ", m.Fans.Select(f => $"{f}%"));
        public static string Mode(GpuModel m) => !m.CurveActive ? "Auto" : m.FixedPct >= 0 ? $"Fixed {m.FixedPct}%" : "Curve";

        /// <summary>Runs <paramref name="on"/>(true) while the control is on screen and (false) when it leaves.</summary>
        public static void WhileShown(Control c, Action<bool> on)
        {
            c.AttachedToVisualTree += (_, _) => on(true);
            c.DetachedFromVisualTree += (_, _) => on(false);
        }
    }

    /// <summary>270-degree gauge arcs, starting bottom-left and sweeping clockwise.</summary>
    internal static class Arcs
    {
        public const double Start = 135, Sweep = 270;

        public static Arc Make(double size, double thickness, string key, bool round = true)
        {
            var a = new Arc
            {
                Width = size, Height = size, StrokeThickness = thickness, StartAngle = Start, SweepAngle = Sweep,
                StrokeLineCap = round ? PenLineCap.Round : PenLineCap.Flat, IsHitTestVisible = false,
            };
            Ui.Res(a, Shape.StrokeProperty, key);
            return a;
        }

        /// <summary>Shows the first <paramref name="frac"/> of the sweep; NaN or 0 hides the arc.</summary>
        public static void SetFrac(Arc a, double frac)
        {
            a.IsVisible = !double.IsNaN(frac) && frac > 0;
            if (a.IsVisible) a.SweepAngle = Sweep * Math.Min(1, frac);
        }

        public static void Place(Canvas canvas, Control e, double canvasSize)
        {
            Canvas.SetLeft(e, (canvasSize - e.Width) / 2);
            Canvas.SetTop(e, (canvasSize - e.Height) / 2);
            canvas.Children.Add(e);
        }
    }

    /// <summary>A value arc plus a blurred copy underneath for the glow.</summary>
    internal sealed class ArcPair
    {
        public readonly Arc Main, Glow;
        private string _key;

        public ArcPair(double size, double thickness, string key)
        {
            Main = Arcs.Make(size, thickness, key);
            Glow = Arcs.Make(size + 4, thickness + 4, key); // +4 each so the centre line matches Main
            Glow.Opacity = 0.55;
            Glow.Effect = new BlurEffect { Radius = 8 };
            _key = key;
        }

        public void AddTo(Canvas c, double canvasSize)
        {
            Arcs.Place(c, Glow, canvasSize);
            Arcs.Place(c, Main, canvasSize);
        }

        public void Set(double frac, string key)
        {
            if (key != _key)
            {
                _key = key;
                Ui.Res(Main, Shape.StrokeProperty, key);
                Ui.Res(Glow, Shape.StrokeProperty, key);
            }
            Arcs.SetFrac(Main, frac);
            Arcs.SetFrac(Glow, frac);
        }
    }

    /// <summary>Dial: tick rim that lights up with the value, end-cap marker, outer glowing arc for a second metric.</summary>
    internal sealed class Dial : Grid
    {
        private const double S = 260, C = 130;
        private readonly Line[] _ticks;
        private readonly bool[] _lit, _major;
        private readonly ArcPair _outer;
        private readonly Polygon _cap;
        private readonly RotateTransform _capRot = new RotateTransform(Arcs.Start);
        private readonly TextBlock _value, _sub, _outerText;
        private readonly double _max;
        private string _tickKey, _capKey = "Accent", _textKey, _outerTextKey = "Accent";

        public Dial(string title, string unit, double max, int steps, int minor, Func<double, string> label, string subKey)
        {
            _max = max;
            Width = S; Height = S;
            var c = new Canvas { Width = S, Height = S, IsHitTestVisible = false };

            // Outer track + value arc (second metric).
            Arcs.Place(c, Arcs.Make(246, 6, "Line.24"), S);
            _outer = new ArcPair(246, 6, "Accent");
            _outer.AddTo(c, S);

            // Centre disc and thin inner rings.
            var disc = new Ellipse { Width = 130, Height = 130 };
            Ui.Res(disc, Shape.FillProperty, "Bg.4D");
            Arcs.Place(c, disc, S);
            var ring = new Ellipse { Width = 134, Height = 134, StrokeThickness = 1 };
            Ui.Res(ring, Shape.StrokeProperty, "Line.40");
            Arcs.Place(c, ring, S);
            var dots = new Ellipse { Width = 142, Height = 142, StrokeThickness = 1, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 1, 3 } };
            Ui.Res(dots, Shape.StrokeProperty, "Line.33");
            Arcs.Place(c, dots, S);

            // Tick rim (major/minor) with scale labels on the major ticks.
            int n = steps * minor;
            _ticks = new Line[n + 1]; _lit = new bool[n + 1]; _major = new bool[n + 1];
            const double rOut = 112;
            for (int i = 0; i <= n; i++)
            {
                double f = (double)i / n;
                double a = (Arcs.Start + Arcs.Sweep * f) * Math.PI / 180;
                bool maj = i % minor == 0;
                double rIn = rOut - (maj ? 12 : 6);
                var ln = new Line
                {
                    StartPoint = new Point(C + rIn * Math.Cos(a), C + rIn * Math.Sin(a)),
                    EndPoint = new Point(C + rOut * Math.Cos(a), C + rOut * Math.Sin(a)),
                    StrokeThickness = maj ? 2.5 : 1.5, IsHitTestVisible = false,
                };
                Ui.Res(ln, Shape.StrokeProperty, maj ? "Line.66" : "Line.33");
                _ticks[i] = ln; _major[i] = maj;
                c.Children.Add(ln);
                if (maj)
                {
                    var tb = Ui.Text(label(max * f), 10, "Muted", mono: true);
                    tb.Width = 34; tb.TextAlignment = TextAlignment.Center;
                    Canvas.SetLeft(tb, C + 91 * Math.Cos(a) - 17);
                    Canvas.SetTop(tb, C + 91 * Math.Sin(a) - 7);
                    c.Children.Add(tb);
                }
            }

            // End-cap marker: a wedge on the rim, rotated (about the dial centre) to the current value.
            _cap = new Polygon { Points = new List<Point> { new(C + 97, C - 5), new(C + 118, C), new(C + 97, C + 5) }, IsVisible = false };
            Ui.Res(_cap, Shape.FillProperty, _capKey);
            var capLayer = new Canvas { Width = S, Height = S, RenderTransform = _capRot, IsHitTestVisible = false };
            capLayer.Children.Add(_cap);
            c.Children.Add(capLayer);
            Children.Add(c);

            // Centre readout.
            _value = Ui.Text("", 44, weight: FontWeight.Bold);
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            var unitTb = Ui.Text(unit, 13, "Muted");
            unitTb.HorizontalAlignment = HorizontalAlignment.Center;
            _sub = Ui.Text("", 12, subKey, mono: true);
            _sub.TextWrapping = TextWrapping.NoWrap;
            _sub.HorizontalAlignment = HorizontalAlignment.Center;
            var centre = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 0), IsHitTestVisible = false };
            centre.Children.Add(_value); centre.Children.Add(unitTb); centre.Children.Add(_sub);
            Children.Add(centre);

            // Bottom gap of the sweep: outer-arc readout and the dial title.
            _outerText = Ui.Text("", 13, "Accent", mono: true);
            _outerText.TextWrapping = TextWrapping.NoWrap;
            _outerText.HorizontalAlignment = HorizontalAlignment.Center;
            var cap = Ui.Caps(title);
            cap.HorizontalAlignment = HorizontalAlignment.Center;
            var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6), IsHitTestVisible = false };
            bottom.Children.Add(_outerText); bottom.Children.Add(cap);
            Children.Add(bottom);
        }

        public void Set(double value, string text, string valueKey, string textKey, double outerFrac, string outerText, string outerKey, string sub)
        {
            Ui.Ref(_value, TextBlock.ForegroundProperty, textKey, ref _textKey);
            _value.Text = text;
            bool ok = !double.IsNaN(value);
            double f = ok ? Math.Clamp(value / _max, 0, 1) : 0;

            if (valueKey != _tickKey)
            {
                _tickKey = valueKey;
                Array.Clear(_lit); // lit ticks get re-pointed at the new colour below
            }
            int n = _ticks.Length - 1;
            for (int i = 0; i <= n; i++)
            {
                bool lit = f > 0 && (double)i / n <= f + 1e-9;
                if (lit == _lit[i]) continue;
                _lit[i] = lit;
                Ui.Res(_ticks[i], Shape.StrokeProperty, lit ? _tickKey : _major[i] ? "Line.66" : "Line.33");
            }
            _capRot.Angle = Arcs.Start + Arcs.Sweep * f;
            _cap.IsVisible = ok;
            Ui.Ref(_cap, Shape.FillProperty, valueKey, ref _capKey);

            _outer.Set(outerFrac, outerKey);
            _outerText.Text = outerText;
            Ui.Ref(_outerText, TextBlock.ForegroundProperty, outerKey, ref _outerTextKey);
            _sub.Text = sub;
        }
    }

    /// <summary>Small secondary gauge tile: mini ring with a percentage, caption, value and a sub line.</summary>
    internal sealed class Stat : Border
    {
        private readonly Arc _arc;
        private readonly TextBlock _ring, _value, _sub;
        private string _key = "Line";

        public Stat(string caption)
        {
            Classes.Add("card");
            Padding = new Thickness(14, 12);
            CornerRadius = new CornerRadius(14);

            var g = new Grid { Width = 62, Height = 62, VerticalAlignment = VerticalAlignment.Center };
            _arc = Arcs.Make(62, 6, _key);
            _ring = Ui.Text("", 12, "Text", mono: true);
            _ring.TextWrapping = TextWrapping.NoWrap;
            _ring.HorizontalAlignment = HorizontalAlignment.Center;
            _ring.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(Arcs.Make(62, 6, "Line.24")); g.Children.Add(_arc); g.Children.Add(_ring);

            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Caps(caption));
            _value = Ui.Text("", 14, "Text", mono: true, weight: FontWeight.SemiBold);
            _sub = Ui.Text("", 11, "Muted", mono: true);
            _value.TextWrapping = _sub.TextWrapping = TextWrapping.NoWrap;
            _value.TextTrimming = _sub.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(_value); text.Children.Add(_sub);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(text, 1);
            row.Children.Add(g); row.Children.Add(text);
            Child = row;
        }

        public void Set(double frac, string ringText, string key, string value, string sub)
        {
            Arcs.SetFrac(_arc, frac);
            Ui.Ref(_arc, Shape.StrokeProperty, key, ref _key);
            _ring.Text = ringText;
            _value.Text = value;
            _sub.Text = sub;
        }
    }

    /// <summary>Read-only horizontal bar with a label and a value. Fill width is a star split of two columns.</summary>
    internal sealed class Bar : StackPanel
    {
        private readonly ColumnDefinition _a = new(), _b = new();
        private readonly Border _fill;
        private readonly TextBlock _value;

        public Bar(string label, string key)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            _value = Ui.Text("", 13, "Text", mono: true);
            _value.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(_value, 1);
            head.Children.Add(Ui.Text(label, 13, "Muted"));
            head.Children.Add(_value);

            var track = new Grid { Height = 8, Margin = new Thickness(0, 5, 0, 0) };
            track.ColumnDefinitions.Add(_a); track.ColumnDefinitions.Add(_b);
            var bg = new Border { CornerRadius = new CornerRadius(4) };
            Ui.Res(bg, Border.BackgroundProperty, "Line.24");
            Grid.SetColumnSpan(bg, 2);
            _fill = new Border { CornerRadius = new CornerRadius(4) };
            Ui.Res(_fill, Border.BackgroundProperty, key);
            track.Children.Add(bg); track.Children.Add(_fill);

            Margin = new Thickness(0, 0, 0, 12);
            Children.Add(head); Children.Add(track);
            Set(double.NaN, "--");
        }

        public void Set(double frac, string text)
        {
            double f = double.IsNaN(frac) ? 0 : Math.Clamp(frac, 0, 1);
            _a.Width = new GridLength(f, GridUnitType.Star);
            _b.Width = new GridLength(1 - f, GridUnitType.Star);
            _fill.IsVisible = f > 0;
            _value.Text = text;
        }
    }

    /// <summary>Live history: temp, load, power, fan as polylines over the last 60 samples, each scaled to 0..100 of its own range.</summary>
    internal sealed class HistoryGraph : StackPanel
    {
        private static readonly string[] Names = { "TEMP", "LOAD", "POWER", "FAN" };
        private static readonly string[] Keys = { "Danger", "Accent", "Blue", "Violet" };
        private readonly Canvas _plot = new() { ClipToBounds = true, IsHitTestVisible = false };
        private readonly Polyline[] _lines = new Polyline[4];
        private readonly TextBlock[] _vals = new TextBlock[4];
        private double[][] _data = { new double[0], new double[0], new double[0], new double[0] };
        private double _powerScale = 1;
        private const double PlotH = 170;

        public HistoryGraph()
        {
            var legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
            for (int i = 0; i < 4; i++)
            {
                var sw = new Rectangle { Width = 16, Height = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                Ui.Res(sw, Shape.FillProperty, Keys[i]);
                _vals[i] = Ui.Text("--", 12, "Text", mono: true);
                _vals[i].TextWrapping = TextWrapping.NoWrap;
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
                item.Children.Add(sw);
                item.Children.Add(Ui.Text(Names[i] + " ", 12, "Muted"));
                item.Children.Add(_vals[i]);
                legend.Children.Add(item);

                var pl = new Polyline { StrokeThickness = 2, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false };
                Ui.Res(pl, Shape.StrokeProperty, Keys[i]);
                _lines[i] = pl;
                _plot.Children.Add(pl);
            }

            var gridLines = new UniformGrid { Rows = 4, Columns = 1, IsHitTestVisible = false };
            for (int i = 0; i < 4; i++)
            {
                var b = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
                Ui.Res(b, Border.BorderBrushProperty, "Line.14");
                gridLines.Children.Add(b);
            }
            var inner = new Grid();
            inner.Children.Add(gridLines); inner.Children.Add(_plot);
            var frame = new Border { Height = PlotH, BorderThickness = new Thickness(0, 0, 0, 1), Child = inner };
            Ui.Res(frame, Border.BorderBrushProperty, "Line.24");
            _plot.SizeChanged += (_, _) => Redraw();

            Children.Add(legend);
            Children.Add(frame);
            var note = Ui.Text("Each trace is scaled 0–100: °C, load %, power as % of the limit, fan %.", 12, "Muted");
            note.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(note);
        }

        public void Update(GpuModel m)
        {
            _data = new[] { m.HistTemp, m.HistLoad, m.HistPower, m.HistFan };
            double lim = double.IsNaN(m.LimitW) ? 0 : m.LimitW;
            _powerScale = Math.Max(1, Math.Max(lim, m.MaxW));
            _vals[0].Text = m.Temp >= 0 ? m.Temp + "°C" : "--";
            _vals[1].Text = m.Load >= 0 ? m.Load + "%" : "--";
            _vals[2].Text = double.IsNaN(m.PowerW) ? "--" : $"{m.PowerW:0} W";
            _vals[3].Text = Ui.FanText(m);
            Redraw();
        }

        private void Redraw()
        {
            double w = _plot.Bounds.Width > 0 ? _plot.Bounds.Width : 500, h = _plot.Bounds.Height > 0 ? _plot.Bounds.Height : PlotH;
            double step = w / (GpuModel.HistoryLen - 1);
            for (int s = 0; s < 4; s++)
            {
                var d = _data[s];
                double scale = s == 2 ? _powerScale : 100;
                double x0 = w - (d.Length - 1) * step;
                var pts = new List<Point>();
                for (int i = 0; i < d.Length; i++)
                {
                    if (double.IsNaN(d[i])) continue;
                    pts.Add(new Point(x0 + i * step, h - 2 - (h - 4) * Math.Clamp(d[i] / scale, 0, 1)));
                }
                _lines[s].Points = pts;
            }
        }
    }

    /// <summary>Canvas with 5 draggable (or arrow-key nudged) points, grid, glowing polyline and a live marker.</summary>
    internal sealed class CurveEditor : Canvas
    {
        private const double H = 240, L = 44, Tp = 12, Rm = 16, Bm = 30;
        private double PW = 496, PH = 198;
        private readonly Canvas _grid = new() { IsHitTestVisible = false };
        private readonly Ellipse _ring;
        private readonly GpuModel _m;
        private readonly Polyline _line, _glow;
        private readonly Ellipse[] _dots = new Ellipse[GpuModel.PointCount];
        private readonly Ellipse _marker;
        private readonly TextBlock _readout;
        private int _drag = -1, _focus = -1;

        public CurveEditor(GpuModel m)
        {
            _m = m;
            Height = H;
            Background = Brushes.Transparent;
            Children.Add(_grid);

            _glow = new Polyline { StrokeThickness = 6, StrokeJoin = PenLineJoin.Round, Opacity = 0.5, Effect = new BlurEffect { Radius = 8 }, IsHitTestVisible = false };
            Ui.Res(_glow, Shape.StrokeProperty, "Accent");
            Children.Add(_glow);
            _line = new Polyline { StrokeThickness = 2.5, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false };
            Ui.Res(_line, Shape.StrokeProperty, "Accent");
            Children.Add(_line);

            _marker = new Ellipse { Width = 10, Height = 10, IsHitTestVisible = false, IsVisible = false };
            Ui.Res(_marker, Shape.FillProperty, "Good");
            Children.Add(_marker);

            _readout = Ui.Text("", 11, "Muted", mono: true);
            _readout.IsHitTestVisible = false;
            SetLeft(_readout, L + 8); SetTop(_readout, Tp + 2);
            Children.Add(_readout);

            _ring = new Ellipse { Width = 28, Height = 28, StrokeThickness = 2, IsHitTestVisible = false, IsVisible = false };
            Ui.Res(_ring, Shape.StrokeProperty, "Accent");
            Children.Add(_ring);

            for (int i = 0; i < _dots.Length; i++)
            {
                int idx = i;
                var d = new Ellipse { Width = 18, Height = 18, StrokeThickness = 2, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
                Ui.Res(d, Shape.StrokeProperty, "Accent");
                Ui.Res(d, Shape.FillProperty, "Bg");
                AutomationProperties.SetName(d, $"Fan curve point {i + 1}. Arrow keys adjust.");
                d.PointerPressed += (_, e) => { _drag = idx; d.Focus(); e.Pointer.Capture(d); e.Handled = true; };
                d.PointerMoved += (_, e) => { if (_drag == idx) Drag(idx, e.GetPosition(this)); };
                d.PointerReleased += (_, e) => { _drag = -1; e.Pointer.Capture(null); };
                d.GotFocus += (_, _) => { _focus = idx; Redraw(); };
                d.LostFocus += (_, _) => { if (_focus == idx) _focus = -1; Redraw(); };
                d.KeyDown += (_, e) =>
                {
                    int dt = e.Key == Key.Right ? 1 : e.Key == Key.Left ? -1 : 0;
                    int df = e.Key == Key.Up ? 1 : e.Key == Key.Down ? -1 : 0;
                    if (dt == 0 && df == 0) return;
                    _m.SetPoint(idx, _m.T[idx] + dt, _m.F[idx] + df);
                    e.Handled = true;
                };
                _dots[i] = d;
                Children.Add(d);
            }
            SizeChanged += (_, _) => { Layout(); Redraw(); };
            Ui.WhileShown(this, on =>
            {
                m.Changed -= Redraw;
                if (on) { m.Changed += Redraw; Layout(); Redraw(); }
            });
            Layout();
            Redraw();
        }

        /// <summary>Recomputes the plot area from the current size and rebuilds grid lines and labels.</summary>
        private void Layout()
        {
            double w = Bounds.Width > 0 ? Bounds.Width : 560, h = Bounds.Height > 0 ? Bounds.Height : H;
            PW = Math.Max(120, w - L - Rm);
            PH = Math.Max(60, h - Tp - Bm);
            _grid.Children.Clear();
            for (int t = GpuModel.TempMin; t <= GpuModel.TempMax; t += 10)
            {
                GridLine(X(t), Tp, X(t), Tp + PH);
                Label(t + "°", X(t) - 10, Tp + PH + 6);
            }
            for (int f = GpuModel.FanMin; f <= GpuModel.FanMax; f += 10)
            {
                GridLine(L, Y(f), L + PW, Y(f));
                Label(f + "%", 2, Y(f) - 8);
            }
        }

        private double X(double t) => L + (t - GpuModel.TempMin) / (GpuModel.TempMax - GpuModel.TempMin) * PW;
        private double Y(double f) => Tp + PH - (f - GpuModel.FanMin) / (GpuModel.FanMax - GpuModel.FanMin) * PH;

        private void GridLine(double x1, double y1, double x2, double y2)
        {
            var ln = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), StrokeThickness = 1 };
            Ui.Res(ln, Shape.StrokeProperty, "Line.12");
            _grid.Children.Add(ln);
        }

        private void Label(string s, double x, double y)
        {
            var tb = Ui.Text(s, 10, "Muted", mono: true);
            SetLeft(tb, x); SetTop(tb, y);
            _grid.Children.Add(tb);
        }

        private void Drag(int i, Point p)
        {
            double t = GpuModel.TempMin + (p.X - L) / PW * (GpuModel.TempMax - GpuModel.TempMin);
            double f = GpuModel.FanMin + (Tp + PH - p.Y) / PH * (GpuModel.FanMax - GpuModel.FanMin);
            _m.SetPoint(i, t, f);
        }

        private void Redraw()
        {
            var pts = new List<Point> { new(X(GpuModel.TempMin), Y(_m.F[0])) };
            for (int i = 0; i < _dots.Length; i++)
            {
                pts.Add(new Point(X(_m.T[i]), Y(_m.F[i])));
                SetLeft(_dots[i], X(_m.T[i]) - 9);
                SetTop(_dots[i], Y(_m.F[i]) - 9);
            }
            pts.Add(new Point(X(GpuModel.TempMax), Y(_m.F[^1])));
            _line.Points = pts;
            _glow.Points = pts;

            int fan = _m.Fans.Length > 0 ? _m.Fans[0] : -1;
            _marker.IsVisible = _m.Temp >= 0 && fan >= 0;
            if (_marker.IsVisible)
            {
                SetLeft(_marker, X(Math.Clamp(_m.Temp, GpuModel.TempMin, GpuModel.TempMax)) - 5);
                SetTop(_marker, Y(Math.Clamp(fan, GpuModel.FanMin, GpuModel.FanMax)) - 5);
            }

            int sel = _drag >= 0 ? _drag : _focus;
            _ring.IsVisible = _focus >= 0;
            if (_focus >= 0)
            {
                SetLeft(_ring, X(_m.T[_focus]) - 14);
                SetTop(_ring, Y(_m.F[_focus]) - 14);
            }
            _readout.Text = sel >= 0 ? $"Point {sel + 1}: {_m.T[sel]:0}°C -> {_m.F[sel]:0}%" : "";
        }
    }
}
