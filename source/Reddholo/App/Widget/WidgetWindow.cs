using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BcuHolo.Modules.Sensors;

namespace Reddholo.App.Widget
{
    /// <summary>Persisted widget configuration (App.DataDir/widget.json), same format as the Windows app. Unknown/missing items are fine.</summary>
    internal sealed class WidgetSettings
    {
        public string Layout { get; set; } = "Compact";   // Compact | Stack | Strip
        public List<string> Items { get; set; } = WidgetCatalog.Enthusiast.ToList();
        public double Opacity { get; set; } = 1.0;
        public double Scale { get; set; } = 1.0;
        public bool OnTop { get; set; } = true;            // false = "pinned": not topmost (see WidgetWindow.ApplyWindowSettings)
        public bool Locked { get; set; }
        public bool Open { get; set; }                      // reopen with the app
        public double? Left { get; set; }                   // screen pixels on Linux (Windows stored DIPs)
        public double? Top { get; set; }
        public int Version { get; set; }

        // Settable so the screenshot harness can point it at a scratch dir.
        internal static string FilePath = Path.Combine(App.DataDir, "widget.json");

        public static WidgetSettings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new(); }
            catch (Exception) { /* bad file: defaults */ }
            return new();
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
            catch (Exception) { /* settings are a convenience; the widget still works */ }
        }
    }

    /// <summary>Entry points for the shell: rail button, startup restore, app exit.</summary>
    internal static class WidgetHost
    {
        private static WidgetWindow _w;

        /// <summary>Opens the widget, or closes it and forgets it was open.</summary>
        public static void Toggle()
        {
            if (_w != null) { _w.RememberOpen(false); _w.Close(); }
            else Open();
        }

        /// <summary>Call at startup: reopens the widget if it was open when the app last closed.</summary>
        public static void RestoreIfWasOpen()
        {
            if (_w == null && WidgetSettings.Load().Open) Open();
        }

        /// <summary>Call at app exit: closes the window but keeps the "reopen with the app" flag.</summary>
        public static void Close() => _w?.Close();

        private static void Open()
        {
            var w = new WidgetWindow();
            w.Closed += (_, _) => { if (_w == w) _w = null; };
            _w = w;
            w.RememberOpen(true);
            w.Show();
        }
    }

    /// <summary>
    /// Floating glass telemetry HUD. Everything is configured from its right-click menu. Reads the same live sources as the
    /// app (GpuModel.Current, SensorHub) plus /proc and /sys counters, never writes to hardware. Samples at 1 Hz off the UI
    /// thread, then mutates existing elements; the visual tree is only rebuilt when the configuration changes.
    /// </summary>
    internal sealed class WidgetWindow : Window
    {
        private const double Gap = 8;
        private readonly WidgetSettings _s = WidgetSettings.Load();
        private readonly HudFrame _chrome;
        private readonly LayoutTransformControl _scaler;
        private readonly Border _content = new();
        private readonly List<Part> _parts = new();
        private readonly Telemetry _tel = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Action _onSensors = () => { };
        private bool _sampling, _closed, _wantGpu;

        public WidgetWindow()
        {
            Title = "reddholo widget";
            SystemDecorations = SystemDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;
            CanResize = false; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;

            var layers = new Grid();
            layers.Children.Add(WUi.DotGrid());
            layers.Children.Add(_content);
            _chrome = new HudFrame(layers, new Thickness(14));
            _scaler = new LayoutTransformControl { Child = _chrome };
            Content = new Border { Padding = new Thickness(12), Child = _scaler }; // room for the shadow

            PointerPressed += (_, e) =>
            {
                var pt = e.GetCurrentPoint(this);
                if (pt.Properties.IsLeftButtonPressed && !_s.Locked) BeginMoveDrag(e);
            };
            PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right) return;
                var menu = BuildMenu();
                _chrome.ContextMenu = menu;
                menu.Open(_chrome);
            };
            PositionChanged += (_, _) => { _s.Left = Position.X; _s.Top = Position.Y; };
            Closed += (_, _) =>
            {
                _closed = true;
                _timer.Stop();
                WantGpu(false);
                SensorHub.Unsubscribe(_onSensors);
                _s.Save();
            };
            Opened += (_, _) => PlaceOnScreen();

            Migrate();
            Rebuild();
            ApplyWindowSettings();
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (_s.Left is double l && _s.Top is double t) Position = new PixelPoint((int)l, (int)t);

            SensorHub.Subscribe(_onSensors); // keeps the sensor loop alive while the widget is open
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();
        }

        /// <summary>Remember whether the widget is open, so it comes back with the app.</summary>
        public void RememberOpen(bool open) { _s.Open = open; _s.Save(); }

        /// <summary>
        /// First-version settings: an untouched old default becomes the Enthusiast set; a customised list keeps its choices
        /// (old ids mapped to their replacements) and gains the header.
        /// </summary>
        private void Migrate()
        {
            if (_s.Items == null) _s.Items = WidgetCatalog.Enthusiast.ToList();
            if (_s.Version < 2)
            {
                bool untouched = _s.Items.Count == WidgetCatalog.OldDefault.Length && !WidgetCatalog.OldDefault.Except(_s.Items).Any();
                if (untouched) _s.Items = WidgetCatalog.Enthusiast.ToList();
                else if (!_s.Items.Contains("Header") && _s.Items.Count > 0) _s.Items.Insert(0, "Header");
                _s.Version = 2;
            }
            _s.Items = WidgetCatalog.Normalize(_s.Items);
            if (_s.Layout != "Stack" && _s.Layout != "Strip") _s.Layout = "Compact";
            _s.Save();
        }

        // ---------------- layout ----------------

        /// <summary>
        /// Compact: wide sections full width (420), small ones paired two per row. Stack: one 300 px column.
        /// Strip: one horizontal row, small sections stacked two per column.
        /// </summary>
        private void Rebuild()
        {
            _parts.Clear();
            var on = new HashSet<string>(_s.Items);
            string layout = _s.Layout;
            double wide = layout == "Stack" ? 300 : 420, half = layout == "Stack" ? 300 : 206;

            var wides = new List<Func<double, Part>>();
            var halves = new List<Func<double, Part>>();
            bool Has(string id) => on.Contains(id);

            if (Has("CpuDial") || Has("GpuDial")) wides.Add(w => new ThermalPart(w, Has("CpuDial"), Has("GpuDial")));
            if (Has("Threads") || Has("CoreTemps")) wides.Add(w => new CoresPart(w, Has("Threads"), Has("CoreTemps")));
            var sparks = WidgetCatalog.Sparks.Where(d => Has(d.Id)).ToList();
            if (sparks.Count > 0) wides.Add(w => new TrendsPart(w, sparks));
            if (Has("Clocks")) halves.Add(w => new ClocksPart(w));
            if (Has("Power")) halves.Add(w => new PowerPart(w));
            if (Has("Vcore") || Has("Whea")) halves.Add(w => new RaptorPart(w, Has("Vcore"), Has("Whea")));
            if (Has("Ram") || Has("Vram")) halves.Add(w => new MemoryPart(w, Has("Ram"), Has("Vram")));
            if (Has("Fans") || Has("Drive")) halves.Add(w => new CoolingPart(w, Has("Fans"), Has("Drive")));
            if (Has("Net") || Has("Disk")) halves.Add(w => new IoPart(w, Has("Net"), Has("Disk")));
            _tel.WantIo(Has("Net"), Has("Disk"), Has("Whea"));
            WantGpu(Has("GpuDial") || Has("Clocks") || Has("Power") || Has("Vram") || Has("Fans") || sparks.Any(d => d.Id.StartsWith("SparkGpu")));

            var root = new StackPanel();
            if (Has("Header")) root.Children.Add(Track(new HeaderPart()));

            if (layout == "Strip")
            {
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                foreach (var make in wides) AddH(row, Track(make(wide)));
                for (int i = 0; i < halves.Count; i += 2)
                {
                    var col = new StackPanel();
                    AddV(col, Track(halves[i](half)));
                    if (i + 1 < halves.Count) AddV(col, Track(halves[i + 1](half)));
                    AddH(row, col);
                }
                if (row.Children.Count > 0) AddV(root, row);
            }
            else if (layout == "Stack")
            {
                foreach (var make in wides.Concat(halves)) AddV(root, Track(make(wide)));
            }
            else
            {
                foreach (var make in wides) AddV(root, Track(make(wide)));
                for (int i = 0; i < halves.Count; i += 2)
                {
                    if (i + 1 >= halves.Count) { AddV(root, Track(halves[i](wide))); break; } // odd one out spans the row
                    var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                    AddH(row, Track(halves[i](half)));
                    AddH(row, Track(halves[i + 1](half)));
                    AddV(root, row);
                }
            }

            if (_parts.Count == 0)
                root.Children.Add(WUi.Label("Right-click to choose what to show", 13, "Muted"));

            _content.Child = root;
            if (!_sampling) UpdateParts(); // mid-sample, the sample's completion updates the new parts
        }

        /// <summary>Holds GpuModel polling demand while the layout shows GPU readings; balanced across Rebuild and Closed.</summary>
        private void WantGpu(bool on)
        {
            if (on == _wantGpu) return;
            _wantGpu = on;
            GpuModel.Want(on);
        }

        private Control Track(Part p) { _parts.Add(p); return p.Root; }

        private static void AddV(Panel host, Control e)
        {
            if (host.Children.Count > 0) e.Margin = new Thickness(0, Gap, 0, 0);
            host.Children.Add(e);
        }

        private static void AddH(Panel host, Control e)
        {
            if (host.Children.Count > 0) e.Margin = new Thickness(Gap, 0, 0, 0);
            host.Children.Add(e);
        }

        // ---------------- data ----------------

        /// <summary>Sample on a pool thread (file reads), then update on the UI thread. Parts never read telemetry mid-sample.</summary>
        private void Tick()
        {
            if (_sampling || _closed) return;
            _sampling = true;
            Task.Run(() =>
            {
                try { _tel.Sample(); }
                catch (Exception ex) { Trace.WriteLine("Widget telemetry: " + ex); }
                Dispatcher.UIThread.Post(() =>
                {
                    _sampling = false;
                    if (!_closed) UpdateParts();
                });
            });
        }

        private void UpdateParts()
        {
            foreach (var p in _parts)
            {
                try { p.Update(_tel); }
                catch (Exception ex) { Trace.WriteLine("Widget " + p.GetType().Name + ": " + ex); }
            }
        }

        // ---------------- window ----------------

        /// <summary>
        /// "Pinned to desktop" is Topmost = false only: X11/Wayland have no portable keep-below call (X11's _NET_WM_STATE_BELOW
        /// needs native interop, Wayland none), so the widget sits in the normal window stack.
        /// </summary>
        private void ApplyWindowSettings()
        {
            _scaler.Opacity = _s.Opacity; // on the content, not the window: window opacity needs a compositor on X11
            _scaler.LayoutTransform = new ScaleTransform(_s.Scale, _s.Scale);
            Topmost = _s.OnTop;
            _s.Save();
        }

        /// <summary>Remembered position if it still lands on a screen, else top-right of the primary work area.</summary>
        private void PlaceOnScreen()
        {
            var screens = Screens?.All;
            if (screens == null || screens.Count == 0) return;
            if (_s.Left is double l && _s.Top is double t && screens.Any(s => s.Bounds.Contains(new PixelPoint((int)l + 50, (int)t + 50)))) return;
            var wa = (Screens.Primary ?? screens[0]).WorkingArea;
            var scaling = (Screens.Primary ?? screens[0]).Scaling;
            Position = new PixelPoint(wa.Right - (int)(500 * scaling), wa.Y + (int)(40 * scaling));
        }

        // ---------------- menu ----------------

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu { CornerRadius = new CornerRadius(12) };
            Ui.Res(menu, ContextMenu.BackgroundProperty, "Bg.EB");
            Ui.Res(menu, ContextMenu.BorderBrushProperty, "GlassLine");
            Ui.Res(menu, ContextMenu.ForegroundProperty, "Text");
            // Submenu popups use Fluent's presenter keys; point them at the current theme (the menu is rebuilt per open).
            var res = Application.Current.Resources;
            if (res.TryGetResource("Bg.EB", null, out var bg)) menu.Resources["MenuFlyoutPresenterBackground"] = bg;
            if (res.TryGetResource("GlassLine", null, out var line)) menu.Resources["MenuFlyoutPresenterBorderBrush"] = line;

            var preset = new MenuItem { Header = "Preset" };
            foreach (var (name, ids) in WidgetCatalog.Presets)
            {
                bool current = ids.Length == _s.Items.Count && !ids.Except(_s.Items).Any();
                preset.Items.Add(Check(name, current, () => { _s.Items = ids.ToList(); Rebuild(); }));
            }
            menu.Items.Add(preset);

            var show = new MenuItem { Header = "Show" };
            foreach (var section in WidgetCatalog.Items.GroupBy(i => i.Section))
            {
                var sub = new MenuItem { Header = section.Key };
                foreach (var item in section)
                {
                    string id = item.Id;
                    sub.Items.Add(Check(item.Label, _s.Items.Contains(id), () =>
                    {
                        if (!_s.Items.Remove(id)) _s.Items.Add(id);
                        Rebuild();
                    }, staysOpen: true));
                }
                show.Items.Add(sub);
            }
            menu.Items.Add(show);

            var layout = new MenuItem { Header = "Layout" };
            foreach (var (id, label) in new[] { ("Compact", "Compact (two columns)"), ("Stack", "Stack (narrow column)"), ("Strip", "Strip (horizontal row)") })
                layout.Items.Add(Check(label, _s.Layout == id, () => { _s.Layout = id; Rebuild(); }));
            menu.Items.Add(layout);

            var size = new MenuItem { Header = "Size" };
            foreach (var (v, label) in new[] { (0.8, "Small"), (1.0, "Normal"), (1.25, "Large") })
                size.Items.Add(Check(label, Math.Abs(_s.Scale - v) < 0.01, () => { _s.Scale = v; ApplyWindowSettings(); }));
            menu.Items.Add(size);

            var opacity = new MenuItem { Header = "Transparency" };
            foreach (var (v, label) in new[] { (1.0, "Solid"), (0.85, "85%"), (0.7, "70%"), (0.55, "55%") })
                opacity.Items.Add(Check(label, Math.Abs(_s.Opacity - v) < 0.01, () => { _s.Opacity = v; ApplyWindowSettings(); }));
            menu.Items.Add(opacity);

            menu.Items.Add(new Separator());
            menu.Items.Add(Check("Always on top", _s.OnTop, () => { _s.OnTop = !_s.OnTop; ApplyWindowSettings(); }));
            menu.Items.Add(Check("Not on top (stays behind other windows)", !_s.OnTop, () => { _s.OnTop = !_s.OnTop; ApplyWindowSettings(); }));
            menu.Items.Add(Check("Lock position", _s.Locked, () => _s.Locked = !_s.Locked));
            // "Start with Windows" (a schtasks logon task) is not ported: the widget comes back with the app via "Open".

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open reddholo", ShowMain));
            menu.Items.Add(Item("Close widget", () => { RememberOpen(false); Close(); }));
            return menu;
        }

        private static void ShowMain()
        {
            var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (main == null) return;
            main.Show();
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Activate();
        }

        private static MenuItem Item(string header, Action click)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => click();
            return m;
        }

        private MenuItem Check(string header, bool on, Action click, bool staysOpen = false)
        {
            var m = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on, StaysOpenOnClick = staysOpen };
            m.Click += (_, _) => { click(); _s.Save(); };
            return m;
        }
    }
}
