using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reddholo.App
{
    public partial class MainWindow : Window
    {
        /// <summary>Every page, rail order. The dashboard reads the others' tiles from here.</summary>
        internal static readonly List<IPage> Pages = new List<IPage>
        {
            new DashboardPage(), new GpuPage(), new SensorsPage(), new DriversPage(), new AppsPage(), new StartupPage(),
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
        }

        private void Show(IPage p)
        {
            if (!_views.TryGetValue(p.Id, out var view)) _views[p.Id] = view = p.CreateView();
            PageTitle.Text = p.Title;
            PageHost.Content = view;
        }
    }
}
