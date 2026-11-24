using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reddholo.App
{
    /// <summary>One rail entry. The WPF app's IHoloModule, minus plugins.</summary>
    internal interface IPage
    {
        string Id { get; }
        string Title { get; }
        /// <summary>SVG path data for the rail icon, drawn in a 24x24 box.</summary>
        string Icon { get; }
        int Order { get; }
        /// <summary>Called once; the window caches the view.</summary>
        Control CreateView();
        /// <summary>Called once for the dashboard; null for no tile. Style the root Border with class "card".</summary>
        Control CreateTile() => null;
    }

    /// <summary>What pages can ask of the window (the WPF IHoloHost).</summary>
    internal static class Shell
    {
        /// <summary>Shows the page with this Id. Set by MainWindow.</summary>
        public static Action<string> Navigate = _ => { };
        /// <summary>Small text badge next to a page in the rail (e.g. "75 GB"); null clears it. Set by MainWindow.</summary>
        public static Action<string, string> SetBadge = (_, _) => { };

        /// <summary>Modal yes/no over the main window. Closing the dialog counts as no.</summary>
        public static async Task<bool> Confirm(string title, string message, string okLabel = "Apply")
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null) return false;
            var w = new Window
            {
                Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            };
            Ui.Res(w, Window.BackgroundProperty, "Bg");
            var ok = Ui.Button(okLabel, "primary");
            var cancel = Ui.Button("Cancel", "ghost", new Thickness(8, 0, 0, 0));
            ok.IsDefault = true; cancel.IsCancel = true;
            ok.Click += (_, _) => w.Close(true);
            cancel.Click += (_, _) => w.Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var body = new StackPanel { Margin = new Thickness(24, 20) };
            body.Children.Add(Ui.Text(title, 18, weight: FontWeight.Bold));
            var msg = Ui.Text(message, 14, "Muted");
            msg.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(new ScrollViewer { Content = msg, MaxHeight = 400 }); // long lists must not push the buttons off screen
            body.Children.Add(buttons);
            w.Content = body;
            return await w.ShowDialog<bool>(owner);
        }
    }
}
