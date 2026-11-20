using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reddholo.App
{
    public partial class MainWindow : Window
    {
        /// <summary>Every page, rail order. The dashboard reads the others' tiles from here.</summary>
        internal static readonly List<IPage> Pages = new List<IPage>
        {
            new DashboardPage(), new GpuPage(), new SensorsPage(), new DriversPage(), new AppsPage(), new StartupPage(), new CleanupPage(),
        }.OrderBy(p => p.Order).ToList();

        private readonly Dictionary<string, Control> _views = new Dictionary<string, Control>();
        private readonly Dictionary<string, TextBlock> _badges = new Dictionary<string, TextBlock>();

        public MainWindow()
        {
            InitializeComponent();
            NodeLabel.Text = "REDD.HOLO · " + Environment.MachineName.ToUpperInvariant();

            foreach (var p in Pages)
            {
                var icon = new PathIcon { Data = Geometry.Parse(p.Icon), Width = 16, Height = 16, Margin = new Avalonia.Thickness(0, 0, 12, 0) };
                var badge = Ui.Text("", 11, "Muted", mono: true);
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.IsVisible = false;
                _badges[p.Id] = badge;
                var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
                row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center });
                Rail.Items.Add(new ListBoxItem { Content = row, Tag = p, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            Rail.SelectionChanged += (_, _) => { if (Rail.SelectedItem is ListBoxItem { Tag: IPage p }) Show(p); };
            Rail.SelectedIndex = 0;

            Shell.Navigate = id =>
            {
                var item = Rail.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((IPage)i.Tag).Id == id);
                if (item != null) Rail.SelectedItem = item;
            };
            Shell.SetBadge = (id, text) =>
            {
                if (!_badges.TryGetValue(id, out var b)) return;
                b.Text = text ?? "";
                b.IsVisible = !string.IsNullOrEmpty(text);
            };

            var menu = new MenuFlyout();
            foreach (var t in BcuHolo.Themes.Theme.All) // qualified: Window.Theme is a property
            {
                var item = new MenuItem { Header = t.Name + "  ·  " + t.Blurb };
                item.Click += (_, _) => Themes.Apply(t);
                menu.Items.Add(item);
            }
            ThemeButton.Flyout = menu;
            SetUpFrame();
        }

        private void SetUpFrame()
        {
            if (OperatingSystem.IsWindows())
            {
                // Keeps native resize borders, Snap and the DWM shadow; we draw the caption.
                ExtendClientAreaToDecorationsHint = true;
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
                ExtendClientAreaTitleBarHeightHint = -1;
            }
            else
            {
                // X11/Wayland WMs don't support an extended client area reliably: no decorations, our own grips.
                SystemDecorations = SystemDecorations.None;
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                foreach (var g in Grips.Children.OfType<Border>())
                {
                    var edge = Enum.Parse<WindowEdge>((string)g.Tag);
                    g.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(edge, e); };
                }
            }

            void Drag(object sender, PointerPressedEventArgs e)
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2) ToggleMax(); else BeginMoveDrag(e);
            }
            CaptionDrag.PointerPressed += Drag;
            Logo.PointerPressed += Drag;
            MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
            MaxButton.Click += (_, _) => ToggleMax();
            CloseButton.Click += (_, _) => Close();
            WidgetButton.Click += (_, _) => Widget.WidgetHost.Toggle();

            PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty || e.Property == ActualTransparencyLevelProperty) UpdateFrame(); };
            Opened += (_, _) => UpdateFrame();
        }

        private void ToggleMax() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void UpdateFrame()
        {
            bool max = WindowState == WindowState.Maximized;
            MaxGlyph.Data = Geometry.Parse(max ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
            ToolTip.SetTip(MaxButton, max ? "Restore" : "Maximize");
            Grips.IsVisible = !OperatingSystem.IsWindows() && !max;
            // Rounded corners and a hairline need a compositing WM; without one the corners would show black.
            bool round = !OperatingSystem.IsWindows() && !max && ActualTransparencyLevel == WindowTransparencyLevel.Transparent;
            Frame.CornerRadius = new CornerRadius(round ? 12 : 0);
            FrameLine.IsVisible = round;
        }

        private void Show(IPage p)
        {
            if (!_views.TryGetValue(p.Id, out var view)) _views[p.Id] = view = p.CreateView();
            PageTitle.Text = p.Title;
            PageHost.Content = view;
        }
    }
}
