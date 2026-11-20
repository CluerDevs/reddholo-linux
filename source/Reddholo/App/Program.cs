using System;
using Avalonia;

namespace Reddholo.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Also used by the Avalonia previewer.
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
