using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BcuHolo.Modules.Gpu;
using BcuHolo.Modules.Sensors;

namespace Reddholo.App
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            Themes.LoadSaved(); // palette must exist before any window builds
            // Linux sources (hwmon, /proc) are built into SensorHub; the GPU is added so the Sensors page lists it too.
            SensorHub.AddSource(new NvmlSensorSource());
            SensorHub.Post = a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background);
            GpuModel.Confirm = (t, m) => Shell.Confirm(t, m);
            GpuPage.Model.Start(); // at launch, so fans left on a curve by a crash go back to automatic straight away

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
                desktop.Exit += (_, _) => { GpuPage.Model.Shutdown(); SensorHub.Shutdown(); };
            }
            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>Settings and data: %LOCALAPPDATA%\reddholo on Windows, ~/.local/share/reddholo on Linux.</summary>
        internal static readonly string DataDir =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "reddholo");
    }
}
