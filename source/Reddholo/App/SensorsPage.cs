using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BcuHolo.Modules.Sensors;
using BcuHolo.Plugins;

namespace Reddholo.App
{
    /// <summary>Sensors page: highlights strip, then one collapsible card per hardware group. Port of the WPF SensorsView.</summary>
    internal sealed class SensorsPage : IPage
    {
        // Above this a desktop CPU (e.g. Raptor Lake) is being fed too much voltage.
        internal const double VcoreWarn = 1.50;

        public string Id => "sensors";
        public string Title => "Sensors";
        public string Icon => "M12,2 A3,3 0 0 1 15,5 V13.5 A5,5 0 1 1 9,13.5 V5 A3,3 0 0 1 12,2 Z M12,4 A1,1 0 0 0 11,5 V14.6 A3,3 0 1 0 13,14.6 V5 A1,1 0 0 0 12,4 Z";
        public int Order => 40;
        public Control CreateView() => new SensorsView();

        internal static string Fmt(double v, string unit)
        {
            var c = CultureInfo.CurrentCulture;
            switch (unit)
            {
                case "V": return v.ToString("0.000", c) + " V";
                case "W": return v.ToString("0.0", c) + " W";
                case "°C": return v.ToString("0.0", c) + " °C";
                case "%": return v.ToString("0", c) + " %";
                case "RPM": return v.ToString("0", c) + " RPM";
                case "MHz": return v.ToString("0", c) + " MHz";
                default: return v.ToString("0.##", c) + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
            }
        }

        private static bool Has(string s, string part) => s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        // CPU package names: LibreHardwareMonitor "CPU Package", Linux coretemp "Package id 0", k10temp "Tctl"/"Tdie".
        // Dual-socket boxes have one per socket: show the hottest.
        internal static SensorEntry CpuPackageTemp(IReadOnlyList<SensorEntry> all) =>
            all.Where(e => e.Reading.Unit == "°C" && e.Reading.Value > 0
                        && (Has(e.Reading.Name, "CPU Package") || Has(e.Reading.Name, "Package id") || e.Reading.Name is "Tctl" or "Tdie"))
               .OrderByDescending(e => e.Reading.Value).FirstOrDefault();

        // 0 means "no reading" here: LHM without its driver lists CPU sensors but leaves them at 0.
        internal static SensorEntry CpuPackagePower(IReadOnlyList<SensorEntry> all) =>
            all.FirstOrDefault(e => e.Reading.Unit == "W" && e.Reading.Value > 0 && Has(e.Reading.Name, "CPU Package"));

        /// <summary>Highest voltage seen since start on Vcore-like sensors (null if none).</summary>
        internal static double? PeakVcore(IReadOnlyList<SensorEntry> all)
        {
            double? peak = null;
            foreach (var e in all)
                if (e.Reading.Unit == "V" && (Has(e.Reading.Name, "Vcore") || Has(e.Reading.Name, "CPU Core")) && (!peak.HasValue || e.Max > peak.Value))
                    peak = e.Max;
            return peak;
        }

        // Linux drive temps come from the nvme and drivetemp hwmon drivers. Per drive use the composite sensor; NVMe
        // controller sensors run far hotter and some drives report junk, so fall back to the coolest plausible one.
        internal static SensorEntry HottestDrive(IReadOnlyList<SensorEntry> all) =>
            all.Where(e => e.Reading.Unit == "°C" && e.Reading.Value is > 0 and < 95
                        && (e.Reading.Group.StartsWith("nvme", StringComparison.Ordinal) || e.Reading.Group.StartsWith("drivetemp", StringComparison.Ordinal)))
               .GroupBy(e => e.Reading.Group)
               .Select(g => g.FirstOrDefault(e => e.Reading.Name is "Composite" or "temp1") ?? g.OrderBy(e => e.Reading.Value).First())
               .OrderByDescending(e => e.Reading.Value).FirstOrDefault();
    }

    internal sealed class SensorsView : UserControl
    {
        private const double SparkW = 120, SparkH = 16;
        private sealed class Row { public TextBlock Value, Min, Max; public Polyline Spark; }
        private sealed class Tile { public TextBlock Value, Reason; }

        private readonly TextBlock _note;
        private readonly Tile _tPkg, _tPow, _tVcore, _tDrive;
        private readonly StackPanel _groups = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        private readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>(); // user overrides, survive rebuilds
        private bool _showThreads;
        private string _signature = "";
        private readonly Action _onUpdate;

        public SensorsView()
        {
            var root = new StackPanel { Margin = new Thickness(0, 12, 0, 24) };

            _note = Text("No CPU temperatures. Load the sensor driver: sudo modprobe coretemp (Intel) or k10temp (AMD), or run sensors-detect from lm-sensors.",
                         13, "Muted", new Thickness(0, 0, 0, 12));
            _note.TextWrapping = TextWrapping.Wrap;
            _note.IsVisible = false;
            root.Children.Add(_note);

            var strip = new WrapPanel { Orientation = Orientation.Horizontal };
            _tPkg = AddTile(strip, "CPU PACKAGE");
            _tPow = AddTile(strip, "PACKAGE POWER");
            _tVcore = AddTile(strip, "PEAK VCORE");
            _tDrive = AddTile(strip, "HOTTEST DRIVE");
            root.Children.Add(strip);

            var threads = new ToggleButton { Content = "Show per-thread load", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            threads.Classes.Add("chip");
            threads.IsCheckedChanged += (_, _) => { _showThreads = threads.IsChecked == true; Refresh(); };
            root.Children.Add(threads);

            root.Children.Add(_groups);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

            // Poll sensors only while this page is on screen.
            _onUpdate = Refresh;
            AttachedToVisualTree += (_, _) => { SensorHub.Subscribe(_onUpdate); Refresh(); };
            DetachedFromVisualTree += (_, _) => SensorHub.Unsubscribe(_onUpdate);
        }

        internal static TextBlock Text(string text, double size, string brush, Thickness? margin = null, bool mono = false)
        {
            var tb = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(brush));
            if (mono) tb.Bind(TextBlock.FontFamilyProperty, tb.GetResourceObservable("Mono"));
            if (margin.HasValue) tb.Margin = margin.Value;
            return tb;
        }

        private static Border Card(Control child)
        {
            var b = new Border { Child = child };
            b.Classes.Add("card");
            return b;
        }

        private static TextBlock Caps(string text)
        {
            var tb = new TextBlock { Text = text };
            tb.Classes.Add("caps");
            return tb;
        }

        private static Tile AddTile(Panel strip, string caps)
        {
            var t = new Tile { Value = Text("n/a", 30, "Text", new Thickness(0, 4, 0, 0)), Reason = Text("", 12, "Muted", new Thickness(0, 2, 0, 0)) };
            t.Value.FontWeight = FontWeight.Bold;
            var s = new StackPanel();
            s.Children.Add(Caps(caps));
            s.Children.Add(t.Value);
            s.Children.Add(t.Reason);
            var card = Card(s);
            card.MinWidth = 170;
            card.Margin = new Thickness(0, 0, 12, 12);
            strip.Children.Add(card);
            return t;
        }

        private static void SetTile(Tile t, string value, string reason, bool danger)
        {
            t.Value.Text = value ?? "n/a";
            t.Reason.Text = value == null ? reason : "";
            t.Value.Bind(TextBlock.ForegroundProperty, t.Value.GetResourceObservable(value == null ? "Muted" : danger ? "Danger" : "Text"));
        }

        private void Refresh()
        {
            try
            {
                var all = SensorHub.Snapshot;
                var t = SensorsPage.CpuPackageTemp(all);
                var p = SensorsPage.CpuPackagePower(all);
                var v = SensorsPage.PeakVcore(all);
                var d = SensorsPage.HottestDrive(all);
                _note.IsVisible = all.Count > 0 && t == null;
                SetTile(_tPkg, t == null ? null : SensorsPage.Fmt(t.Reading.Value, "°C"), "no CPU sensor", false);
                SetTile(_tPow, p == null ? null : SensorsPage.Fmt(p.Reading.Value, "W"), "not reported", false);
                SetTile(_tVcore, v.HasValue && v.Value > 0 ? SensorsPage.Fmt(v.Value, "V") : null, "not reported", v.HasValue && v.Value > SensorsPage.VcoreWarn);
                SetTile(_tDrive, d == null ? null : SensorsPage.Fmt(d.Reading.Value, "°C"), "no sensor", false);

                var shown = all.Where(e => _showThreads || e.Reading.Name.IndexOf("Thread", StringComparison.OrdinalIgnoreCase) < 0).ToList();
                string sig = (_showThreads ? "T" : "t") + string.Join("\n", shown.Select(e => Key(e.Reading)));
                if (sig != _signature) { _signature = sig; Rebuild(shown, all.Count); }
                foreach (var e in shown)
                {
                    if (!_rows.TryGetValue(Key(e.Reading), out var row)) continue;
                    row.Value.Text = SensorsPage.Fmt(e.Reading.Value, e.Reading.Unit);
                    row.Min.Text = SensorsPage.Fmt(e.Min, e.Reading.Unit);
                    row.Max.Text = SensorsPage.Fmt(e.Max, e.Reading.Unit);
                    if (row.Spark != null) row.Spark.Points = Points(e.History);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("SensorsView: " + ex.Message); }
        }

        private static string Key(SensorReading r) => r.Source + "|" + r.Group + "|" + r.Name + "|" + r.Unit;

        // Temperature, Power, Voltage, Clock, Load, Fan, other.
        private static int TypeRank(string unit) => unit switch
        {
            "°C" => 0, "W" => 1, "V" => 2, "MHz" => 3, "%" => 4, "RPM" => 5, _ => 6,
        };

        // "Core 2" before "Core 10".
        private static string NatKey(string s) => Regex.Replace(s ?? "", @"\d+", m => m.Value.PadLeft(6, '0'));

        private static Grid RowGrid() => new Grid { ColumnDefinitions = new ColumnDefinitions($"360,90,90,90,{SparkW}") };

        private static TextBlock Num(string brush, double size)
        {
            var tb = Text("", size, brush, mono: true);
            tb.HorizontalAlignment = HorizontalAlignment.Right;
            tb.Margin = new Thickness(0, 0, 8, 0);
            return tb;
        }

        private static void Place(Grid g, Control el, int col) { Grid.SetColumn(el, col); g.Children.Add(el); }

        private void Rebuild(IReadOnlyList<SensorEntry> shown, int total)
        {
            _groups.Children.Clear();
            _rows.Clear();
            foreach (var grp in shown.GroupBy(e => e.Reading.Group))
            {
                string name = grp.Key;
                var items = grp.OrderBy(e => TypeRank(e.Reading.Unit)).ThenBy(e => NatKey(e.Reading.Name), StringComparer.OrdinalIgnoreCase).ToList();
                bool open = _expanded.TryGetValue(name, out var o) ? o
                    : !(name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0 || items.Count > 30);

                var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0), IsVisible = open };
                var head = RowGrid();
                head.Children.Add(Caps("SENSOR"));
                int c = 1;
                foreach (var h in new[] { "NOW", "MIN", "MAX" })
                {
                    var tb = Caps(h);
                    tb.HorizontalAlignment = HorizontalAlignment.Right;
                    tb.Margin = new Thickness(0, 0, 8, 0);
                    Place(head, tb, c++);
                }
                body.Children.Add(head);

                foreach (var e in items)
                {
                    var r = e.Reading;
                    var row = new Row { Value = Num("Text", 13), Min = Num("Muted", 12), Max = Num("Muted", 12) };
                    var g = RowGrid();
                    g.Margin = new Thickness(0, 3, 0, 3);
                    g.Children.Add(Text(r.Name, 13, "Muted"));
                    Place(g, row.Value, 1); Place(g, row.Min, 2); Place(g, row.Max, 3);
                    if (r.Unit == "°C" || r.Unit == "W")
                    {
                        row.Spark = new Polyline { StrokeThickness = 1.2, Width = SparkW, Height = SparkH, StrokeJoin = PenLineJoin.Round };
                        row.Spark.Bind(Shape.StrokeProperty, row.Spark.GetResourceObservable("Accent"));
                        Place(g, row.Spark, 4);
                    }
                    body.Children.Add(g);
                    _rows[Key(r)] = row;
                }

                var glyph = Text(open ? "▾" : "▸", 14, "Muted");
                glyph.Width = 18;
                var title = Text(name, 15, "Text");
                title.FontWeight = FontWeight.SemiBold;
                var count = Text(items.Count + " sensors", 12, "Muted", new Thickness(10, 0, 0, 0));
                count.VerticalAlignment = VerticalAlignment.Center;
                var hp = new StackPanel { Orientation = Orientation.Horizontal };
                hp.Children.Add(glyph); hp.Children.Add(title); hp.Children.Add(count);
                var header = new Border { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = hp };
                header.PointerReleased += (_, _) =>
                {
                    bool now = !body.IsVisible;
                    body.IsVisible = now;
                    glyph.Text = now ? "▾" : "▸";
                    _expanded[name] = now;
                };

                var stack = new StackPanel();
                stack.Children.Add(header);
                stack.Children.Add(body);
                var card = Card(stack);
                card.Margin = new Thickness(0, 0, 0, 12);
                card.HorizontalAlignment = HorizontalAlignment.Left;
                _groups.Children.Add(card);
            }
            if (total == 0)
                _groups.Children.Add(Text("No readings yet.", 13, "Muted"));
        }

        // SparkW x SparkH box, 2px per sample, newest at the right edge; flat line if constant.
        private static IList<Point> Points(double[] h)
        {
            var pc = new List<Point>();
            if (h == null || h.Length == 0) return pc;
            double lo = h.Min(), hi = h.Max(), span = hi - lo;
            for (int i = 0; i < h.Length; i++)
            {
                double y = span < 1e-9 ? SparkH / 2 : (SparkH - 1) - (h[i] - lo) / span * (SparkH - 2);
                pc.Add(new Point(SparkW - 2.0 * (h.Length - 1 - i) - 1, y));
            }
            return pc;
        }
    }
}
