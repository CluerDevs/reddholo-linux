using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reddholo.App
{
    /// <summary>GPU page: clock and temp dials, power limit, fan mode, stat strip, fan curve editor, live history. Port of the WPF GpuView.</summary>
    internal sealed class GpuPage : IPage
    {
        /// <summary>One model for the app. Started at launch so a fan curve left engaged by a crash is handed back right away.</summary>
        public static readonly GpuModel Model = new GpuModel();

        public string Id => "gpu";
        public string Title => "GPU";
        public string Icon => "M2,6 H20 A2,2 0 0 1 22,8 V16 A2,2 0 0 1 20,18 H4 V20 H2 Z M4,8 V16 H20 V8 Z M12,9 A3,3 0 1 1 12,15 A3,3 0 1 1 12,9 Z";
        public int Order => 35;
        public Control CreateView() => new GpuView(Model);
        public Control CreateTile() => DashboardView.GpuControlTile();
    }

    internal sealed class GpuView : UserControl
    {
        private readonly GpuModel _m;
        private readonly StackPanel _content = new();
        private readonly Border _banner, _msg;
        private readonly TextBlock _msgText = Ui.Text("", 13, "Muted");
        private readonly TextBlock _name = Ui.Text("", 16, weight: FontWeight.SemiBold);
        private readonly TextBlock _mode = Ui.Text("", 13, "Muted", mono: true);
        private readonly TextBlock _curveStatus = Ui.Text("", 13, "Muted");
        private readonly TextBlock _applyNote = Ui.Text("Apply is disabled while MSI Afterburner is running.", 13, "Muted");
        private readonly TextBlock _applyNote2 = Ui.Text("Apply is disabled while MSI Afterburner is running.", 13, "Muted");
        private readonly Button _applyCurve, _auto, _reset, _applyLimit, _defLimit, _setFixed, _fixed100;
        private readonly ToggleButton _modeAuto, _modeCurve, _modeFixed, _compactToggle;
        private readonly Slider _fanSlider = new() { IsSnapToTickEnabled = true, TickFrequency = 5, SmallChange = 5, LargeChange = 10, Maximum = 100 };
        private readonly TextBlock _fanSliderValue = Ui.Text("", 15, "Violet", mono: true, weight: FontWeight.Bold);
        private readonly Slider _slider = new() { IsSnapToTickEnabled = true, TickFrequency = 1 };
        private readonly TextBlock _sliderValue = Ui.Text("", 18, "Blue", mono: true, weight: FontWeight.Bold);
        private readonly TextBlock _limitInfo = Ui.Text("", 12, "Muted", mono: true);
        private readonly TextBlock _limitHint = Ui.Text("Lowering the limit reduces heat and noise; raising it adds a little performance.", 12, "Muted");
        private readonly Dial _clock, _temp;
        private readonly Bar _loadBar, _memBar, _vramBar;
        private readonly Stat _sVram, _sMem, _sPower;
        private readonly Stat[] _sFans;
        private readonly HistoryGraph _hist = new();
        private readonly Viewbox[] _dialBoxes;
        private readonly Border _heroCard, _barsRule;
        private readonly Grid _lower;
        private bool _sliderInit, _compact;
        private string _kMode, _kStatus, _kMsgFg, _kMsgBg, _kMsgBd;

        // Compact view: smaller dials, no bars, curve editor and history hidden. Remembered across launches.
        private static readonly string CompactMarker = Path.Combine(App.DataDir, "gpu-compact");

        public GpuView(GpuModel m)
        {
            _m = m;
            var unavailCard = Ui.Card(Ui.Text(m.Error ?? "", 16, "Danger", weight: FontWeight.SemiBold), new Thickness(0, 0, 0, 16));

            _banner = Banner(Ui.Text("Close MSI Afterburner first — two fan controllers will fight", 14, "Danger", weight: FontWeight.SemiBold));
            Ui.Res(_banner, Border.BackgroundProperty, "Danger.12");
            Ui.Res(_banner, Border.BorderBrushProperty, "Danger");
            _msg = Banner(_msgText);

            // ---- Title row ----
            var title = new Grid { Margin = new Thickness(4, 0, 4, 12), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var tl = new StackPanel { Orientation = Orientation.Horizontal };
            var gpuCaps = Ui.Caps("GPU", new Thickness(0, 0, 12, 0));
            gpuCaps.VerticalAlignment = _name.VerticalAlignment = _mode.VerticalAlignment = VerticalAlignment.Center;
            tl.Children.Add(gpuCaps); tl.Children.Add(_name);
            var tr = new StackPanel { Orientation = Orientation.Horizontal };
            tr.Children.Add(_mode);
            _compactToggle = Chip("Compact view", new Thickness(14, 0, 0, 0));
            ToolTip.SetTip(_compactToggle, "Smaller dials; hides the curve editor and history");
            _compactToggle.Click += (_, _) => ApplyCompact(!_compact, save: true);
            tr.Children.Add(_compactToggle);
            Grid.SetColumn(tr, 1);
            title.Children.Add(tl); title.Children.Add(tr);

            // ---- Dials ----
            int mx = m.MaxCoreMHz > 0 ? m.MaxCoreMHz : 3000;
            int clockStep = mx > 4000 ? 1000 : 500;
            int clockMax = (int)Math.Ceiling(mx / (double)clockStep) * clockStep;
            _clock = new Dial("GPU CLOCK", "MHz", clockMax, clockMax / clockStep, 5, v => v.ToString("0"), "Muted");
            _temp = new Dial("GPU TEMP", "°C", 110, 11, 5, v => v.ToString("0"), "Blue");

            // ---- Control stack ----
            _applyLimit = Ui.Button("Apply limit", "primary");
            _defLimit = Ui.Button("Default", "ghost", new Thickness(8, 0, 0, 0));
            _applyLimit.Click += async (_, _) => { await m.ApplyLimit(_slider.Value, false); Refresh(); };
            _defLimit.Click += async (_, _) =>
            {
                await m.ApplyLimit(0, true);
                if (m.PowerSupported && !double.IsNaN(m.LimitW)) _slider.Value = Math.Clamp(m.LimitW, _slider.Minimum, _slider.Maximum);
                Refresh();
            };
            _slider.ValueChanged += (_, _) => _sliderValue.Text = $"{_slider.Value:0} W";

            _modeAuto = Chip("Auto", new Thickness(0, 0, 8, 0));
            _modeCurve = Chip("Curve", new Thickness(0, 0, 8, 0));
            _modeFixed = Chip("Fixed", default);
            _fanSlider.Minimum = m.FloorPct;
            _fanSlider.Value = Math.Clamp(m.LastFixedPct, m.FloorPct, 100);
            _fanSliderValue.Text = $"{_fanSlider.Value:0}%";
            _fanSlider.ValueChanged += (_, _) => _fanSliderValue.Text = $"{_fanSlider.Value:0}%";
            _setFixed = Ui.Button("Set", "accent", new Thickness(10, 0, 0, 0));
            _fixed100 = Ui.Button("100%", "ghost", new Thickness(6, 0, 0, 0));
            ToolTip.SetTip(_fixed100, "Max all fans and hold them there");
            _setFixed.Click += async (_, _) => { await m.ApplyFixed((int)_fanSlider.Value); Refresh(); };
            _fixed100.Click += async (_, _) => { _fanSlider.Value = 100; await m.ApplyFixed(100); Refresh(); };
            // Each chip acts only if the mode changes; Refresh afterwards re-syncs the chips if the confirmation was cancelled.
            _modeFixed.Click += async (_, _) => { if (m.FixedPct < 0) await m.ApplyFixed((int)_fanSlider.Value); Refresh(); };
            _modeAuto.Click += (_, _) => { if (m.CurveActive) m.BackToAutomatic(); Refresh(); };
            _modeCurve.Click += async (_, _) => { if (!m.CurveActive || m.FixedPct >= 0) await m.ApplyCurve(); Refresh(); };

            _loadBar = new Bar("GPU load", "Accent");
            _memBar = new Bar("Memory controller", "Accent.99");
            _vramBar = new Bar("VRAM", "Line");

            var limitHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(_sliderValue, 1);
            limitHead.Children.Add(Ui.Text("Power limit", 13, "Muted"));
            limitHead.Children.Add(_sliderValue);
            var pbtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            pbtns.Children.Add(_applyLimit); pbtns.Children.Add(_defLimit);
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            chips.Children.Add(_modeAuto); chips.Children.Add(_modeCurve); chips.Children.Add(_modeFixed);
            var fixedRow = new Grid { Margin = new Thickness(0, 6, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,48,Auto,Auto") };
            _fanSliderValue.VerticalAlignment = _fanSlider.VerticalAlignment = VerticalAlignment.Center;
            _fanSliderValue.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(_fanSliderValue, 1); Grid.SetColumn(_setFixed, 2); Grid.SetColumn(_fixed100, 3);
            fixedRow.Children.Add(_fanSlider); fixedRow.Children.Add(_fanSliderValue); fixedRow.Children.Add(_setFixed); fixedRow.Children.Add(_fixed100);

            var stack = new StackPanel();
            stack.Children.Add(Ui.Caps("CONTROL", new Thickness(0, 0, 0, 10)));
            stack.Children.Add(limitHead);
            stack.Children.Add(_slider);
            stack.Children.Add(_limitInfo);
            stack.Children.Add(_limitHint);
            stack.Children.Add(pbtns);
            _applyNote2.Margin = new Thickness(0, 8, 0, 0);
            _applyNote2.IsVisible = false;
            stack.Children.Add(_applyNote2);
            stack.Children.Add(Rule());
            stack.Children.Add(Ui.Text("Fan mode", 13, "Muted"));
            stack.Children.Add(chips);
            stack.Children.Add(fixedRow);
            stack.Children.Add(_barsRule = Rule());
            stack.Children.Add(_loadBar);
            stack.Children.Add(_memBar);
            stack.Children.Add(_vramBar);
            var stackPanel = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 4), Margin = new Thickness(8, 0), Child = stack };
            Ui.Res(stackPanel, Border.BackgroundProperty, "Line.0F");
            Ui.Res(stackPanel, Border.BorderBrushProperty, "Line.24");

            var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("*,320,*") };
            Viewbox Boxed(Control d) => new Viewbox { Child = d, Stretch = Stretch.Uniform, MaxWidth = 300, MaxHeight = 300, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var lb = Boxed(_clock); var rb = Boxed(_temp);
            _dialBoxes = new[] { lb, rb };
            Grid.SetColumn(stackPanel, 1);
            Grid.SetColumn(rb, 2);
            hero.Children.Add(lb); hero.Children.Add(stackPanel); hero.Children.Add(rb);
            _heroCard = Ui.Card(hero, new Thickness(0, 0, 0, 16));
            _heroCard.Padding = new Thickness(16);

            // ---- Stat strip ----
            _sVram = new Stat("VRAM"); _sMem = new Stat("MEM CLOCK"); _sPower = new Stat("POWER");
            int fans = Math.Max(1, m.FanCount);
            _sFans = new Stat[fans];
            var strip = new UniformGrid { Rows = 1, Columns = 3 + fans, Margin = new Thickness(0, 0, 0, 16) };
            var tiles = new List<Stat> { _sVram, _sMem, _sPower };
            for (int i = 0; i < fans; i++) tiles.Add(_sFans[i] = new Stat(m.FanCount > 1 ? $"FAN {i + 1}" : "FAN"));
            for (int i = 0; i < tiles.Count; i++) { tiles[i].Margin = new Thickness(0, 0, i == tiles.Count - 1 ? 0 : 12, 0); strip.Children.Add(tiles[i]); }

            // ---- Fan curve card (editor + safety text) ----
            _applyCurve = Ui.Button("Apply curve", "primary");
            _auto = Ui.Button("Back to automatic", "ghost", new Thickness(8, 0, 0, 0));
            _reset = Ui.Button("Reset points", "ghost", new Thickness(8, 0, 0, 0));
            _applyCurve.Click += async (_, _) => { await m.ApplyCurve(); Refresh(); };
            _auto.Click += (_, _) => m.BackToAutomatic();
            _reset.Click += (_, _) => m.ResetPoints();
            var btns = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            btns.Children.Add(_applyCurve); btns.Children.Add(_auto); btns.Children.Add(_reset);
            var curve = new StackPanel();
            curve.Children.Add(Ui.Caps("FAN CURVE"));
            curve.Children.Add(_curveStatus);
            curve.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), Child = new CurveEditor(m) });
            curve.Children.Add(Ui.Text($"Minimum {GpuModel.FanMin}% · forced to 100% from {GpuModel.ForceFullAt}°C · only steps down 10% per second, 3°C below where it was raised. Drag points or use arrow keys.", 12, "Muted"));
            curve.Children.Add(btns);
            _applyNote.Margin = new Thickness(0, 10, 0, 0);
            _applyNote.IsVisible = false;
            curve.Children.Add(_applyNote);
            var curveCard = Ui.Card(curve, new Thickness(0, 0, 8, 0));

            var histBody = new StackPanel();
            histBody.Children.Add(Ui.Caps("LIVE HISTORY · LAST 60 S"));
            histBody.Children.Add(_hist);
            var histCard = Ui.Card(histBody, new Thickness(8, 0, 0, 0));

            _lower = new Grid { Margin = new Thickness(0, 0, 0, 16), ColumnDefinitions = new ColumnDefinitions("*,*") };
            Grid.SetColumn(histCard, 1);
            _lower.Children.Add(curveCard); _lower.Children.Add(histCard);

            _content.Children.Add(title);
            _content.Children.Add(_heroCard);
            _content.Children.Add(strip);
            _content.Children.Add(_lower);

            var root = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            root.Children.Add(unavailCard);
            root.Children.Add(_banner);
            root.Children.Add(_msg);
            root.Children.Add(_content);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 24), Content = root };

            // Subscribe only while on screen. Afterburner is re-checked on show (ViewOpen), then Refresh so the Apply buttons reflect it.
            Ui.WhileShown(this, on =>
            {
                m.ViewOpen = on;
                m.Changed -= Refresh;
                if (on) { m.Changed += Refresh; Refresh(); }
            });
            ApplyCompact(File.Exists(CompactMarker), save: false);
            unavailCard.IsVisible = !m.Available;
            _content.IsVisible = m.Available;
            Refresh();
        }

        private static Border Banner(Control child)
        {
            return new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10),
                Margin = new Thickness(0, 0, 0, 16), IsVisible = false, Child = child,
            };
        }

        private static ToggleButton Chip(string text, Thickness margin)
        {
            var t = new ToggleButton { Content = text, Margin = margin };
            t.Classes.Add("chip");
            return t;
        }

        private static Border Rule()
        {
            var b = new Border { Height = 1, Margin = new Thickness(0, 14) };
            Ui.Res(b, Border.BackgroundProperty, "Line.24");
            return b;
        }

        private void ApplyCompact(bool on, bool save)
        {
            _compact = on;
            _compactToggle.IsChecked = on;
            foreach (var v in _dialBoxes) v.MaxWidth = v.MaxHeight = on ? 220 : 300;
            _heroCard.Padding = new Thickness(on ? 10 : 16);
            _limitHint.IsVisible = _barsRule.IsVisible = _loadBar.IsVisible = _memBar.IsVisible = _vramBar.IsVisible = _lower.IsVisible = !on;
            if (!save) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CompactMarker));
                if (on) File.WriteAllText(CompactMarker, ""); else File.Delete(CompactMarker);
            }
            catch (Exception) { /* view preference only */ }
        }

        private static string Mhz(int v) => v >= 0 ? v.ToString() : "--";
        private static string Pct(int v) => v >= 0 ? v + "%" : "--";
        private static string W(double w) => double.IsNaN(w) ? "--" : $"{w:0}";
        private static string Gb(long mb) => mb >= 0 ? (mb / 1024.0).ToString("0.0") : "--";

        private void Refresh()
        {
            var m = _m;
            _name.Text = m.Name;
            _mode.Text = "Fan mode: " + Ui.Mode(m);
            Ui.Ref(_mode, TextBlock.ForegroundProperty, m.CurveActive ? "Good" : "Muted", ref _kMode);

            // Dials
            _clock.Set(m.CoreMHz >= 0 ? m.CoreMHz : double.NaN, Mhz(m.CoreMHz), "Accent", "Text",
                m.Load >= 0 ? m.Load / 100.0 : double.NaN, "LOAD " + Pct(m.Load), "Accent", $"MEM {Mhz(m.MemMHz)} MHz");
            int fan = m.Fans.Length > 0 ? m.Fans.Max() : -1;
            string tk = m.Temp >= 75 ? "Danger" : "Good";
            _temp.Set(m.Temp >= 0 ? m.Temp : double.NaN, m.Temp >= 0 ? m.Temp.ToString() : "--", tk, m.Temp >= 75 ? "Danger" : "Text",
                fan >= 0 ? fan / 100.0 : double.NaN, "FAN " + Pct(fan), "Violet", $"PWR {W(m.PowerW)} / {W(m.LimitW)} W");

            // Bars
            _loadBar.Set(m.Load >= 0 ? m.Load / 100.0 : double.NaN, Pct(m.Load));
            _memBar.Set(m.MemLoad >= 0 ? m.MemLoad / 100.0 : double.NaN, Pct(m.MemLoad));
            double vf = m.VramTotalMB > 0 && m.VramUsedMB >= 0 ? (double)m.VramUsedMB / m.VramTotalMB : double.NaN;
            _vramBar.Set(vf, $"{Gb(m.VramUsedMB)} / {Gb(m.VramTotalMB)} GB");

            // Stat tiles
            string P(double f) => double.IsNaN(f) ? "--" : $"{f * 100:0}%";
            _sVram.Set(vf, P(vf), "Line", $"{Gb(m.VramUsedMB)}/{Gb(m.VramTotalMB)} GB", "MEM CTRL " + Pct(m.MemLoad));
            double mf = m.MemMHz >= 0 && m.MaxMemMHz > 0 ? Math.Min(1.0, (double)m.MemMHz / m.MaxMemMHz) : double.NaN;
            _sMem.Set(mf, P(mf), "Accent", Mhz(m.MemMHz) + " MHz", "max " + Mhz(m.MaxMemMHz));
            double pf = !double.IsNaN(m.PowerW) && !double.IsNaN(m.LimitW) && m.LimitW > 0 ? Math.Min(1.0, m.PowerW / m.LimitW) : double.NaN;
            _sPower.Set(pf, P(pf), "Blue", W(m.PowerW) + " W", "limit " + W(m.LimitW) + " W");
            for (int i = 0; i < _sFans.Length; i++)
            {
                int f = i < m.Fans.Length ? m.Fans[i] : -1;
                _sFans[i].Set(f >= 0 ? f / 100.0 : double.NaN, Pct(f), "Violet", Pct(f), Ui.Mode(m));
            }

            _hist.Update(m);

            // Status / warnings
            _curveStatus.Text = (m.FixedPct >= 0 ? $"Fixed {m.FixedPct}% — holding {m.FanCount} fan(s); this curve is not in use"
                : m.CurveActive ? $"Curve active — driving {m.FanCount} fan(s)"
                : !string.IsNullOrEmpty(m.Status) && m.SavedWasActive && !m.EverApplied ? m.Status
                : "Automatic fan control") + (m.Dirty ? " · edited, not applied" : "");
            Ui.Ref(_curveStatus, TextBlock.ForegroundProperty, m.CurveActive ? "Good" : "Muted", ref _kStatus);

            bool warn = !string.IsNullOrEmpty(m.Warning);
            var msg = warn ? m.Warning : (m.CurveActive || m.Status.StartsWith("Saved") ? "" : m.Status);
            _msgText.Text = msg;
            _msg.IsVisible = !string.IsNullOrEmpty(msg);
            Ui.Ref(_msgText, TextBlock.ForegroundProperty, warn ? "Danger" : "Muted", ref _kMsgFg);
            Ui.Ref(_msg, Border.BackgroundProperty, warn ? "Danger.12" : "Line.0F", ref _kMsgBg);
            Ui.Ref(_msg, Border.BorderBrushProperty, warn ? "Danger.66" : "Line.24", ref _kMsgBd);

            _banner.IsVisible = _applyNote.IsVisible = _applyNote2.IsVisible = m.Afterburner;
            _applyCurve.IsEnabled = m.CanApply;
            _modeCurve.IsEnabled = _modeFixed.IsEnabled = _setFixed.IsEnabled = _fixed100.IsEnabled = m.CanApply;
            _applyLimit.IsEnabled = _defLimit.IsEnabled = m.CanApply && m.PowerSupported;
            _modeAuto.IsChecked = !m.CurveActive;
            _modeFixed.IsChecked = m.CurveActive && m.FixedPct >= 0;
            _modeCurve.IsChecked = m.CurveActive && m.FixedPct < 0;

            if (m.PowerSupported)
            {
                _slider.IsEnabled = true;
                if (!_sliderInit)
                {
                    _slider.Minimum = Math.Ceiling(m.MinW);
                    _slider.Maximum = Math.Floor(m.MaxW);
                    _slider.Value = double.IsNaN(m.LimitW) ? m.DefaultW : Math.Clamp(m.LimitW, _slider.Minimum, _slider.Maximum);
                    _sliderInit = true;
                }
                _limitInfo.Text = $"Default {m.DefaultW:0} W · range {m.MinW:0}–{m.MaxW:0} W · current {(double.IsNaN(m.LimitW) ? "—" : $"{m.LimitW:0} W")}";
            }
            else
            {
                _slider.IsEnabled = false;
                _limitInfo.Text = "Power limit is not adjustable on this GPU/driver.";
            }
        }
    }
}
