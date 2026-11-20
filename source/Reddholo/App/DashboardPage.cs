using Avalonia.Controls;

namespace Reddholo.App
{
    /// <summary>Home page. Placeholder until the port lands.</summary>
    internal sealed class DashboardPage : IPage
    {
        public string Id => "dashboard";
        public string Title => "Dashboard";
        public string Icon => "M3,3 H10 V12 H3 Z M14,3 H21 V8 H14 Z M14,12 H21 V21 H14 Z M3,16 H10 V21 H3 Z";
        public int Order => 0;
        public Control CreateView() => new TextBlock { Text = "Dashboard" };
    }
}
