using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reddholo.App
{
    /// <summary>One rail entry. The WPF app's IHoloModule, minus plugins and dashboard tiles (not ported yet).</summary>
    internal interface IPage
    {
        string Id { get; }
        string Title { get; }
        /// <summary>SVG path data for the rail icon, drawn in a 24x24 box.</summary>
        string Icon { get; }
        int Order { get; }
        /// <summary>Called once; the window caches the view.</summary>
        Control CreateView();
    }

    public partial class MainWindow : Window
    {
        private readonly List<IPage> _pages = new List<IPage> { new GpuPage(), new SensorsPage() };
        private readonly Dictionary<string, Control> _views = new Dictionary<string, Control>();

        public MainWindow()
        {
            InitializeComponent();
            NodeLabel.Text = "REDD.HOLO · " + Environment.MachineName.ToUpperInvariant();

            foreach (var p in _pages.OrderBy(p => p.Order))
            {
                var icon = new PathIcon { Data = Geometry.Parse(p.Icon), Width = 16, Height = 16, Margin = new Avalonia.Thickness(0, 0, 12, 0) };
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center });
                Rail.Items.Add(new ListBoxItem { Content = row, Tag = p, VerticalContentAlignment = VerticalAlignment.Center });
            }
            Rail.SelectionChanged += (_, _) => { if (Rail.SelectedItem is ListBoxItem { Tag: IPage p }) Show(p); };
            Rail.SelectedIndex = 0;

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
