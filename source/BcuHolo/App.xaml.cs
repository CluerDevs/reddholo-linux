using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace BcuHolo
{
    public partial class App : Application
    {
        /// <summary>%LOCALAPPDATA%\reddholo: settings, widget layout, fan curve, startup-entry backups, error log.</summary>
        internal static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "reddholo");
        private static readonly string LogPath = Path.Combine(DataDir, "errors.log");

        // One instance only: two copies would run two GPU fan engines fighting over the same fans.
        private static System.Threading.Mutex _single;
        private static readonly System.Threading.EventWaitHandle ShowSignal =
            new(false, System.Threading.EventResetMode.AutoReset, @"Local\reddholo-Show");

        /// <summary>
        /// Renamed from BCU Holo: carry its data folder over once (it holds the fan-control crash marker the GPU engine
        /// checks at start). If the move fails the old folder is left untouched and the app starts with defaults.
        /// </summary>
        private static void MoveLegacyData()
        {
            var old = Path.Combine(Path.GetDirectoryName(DataDir), "BCU-Holo");
            if (!Directory.Exists(old) || Directory.Exists(DataDir)) return;
            try { Directory.Move(old, DataDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            MoveLegacyData();
            _single = new System.Threading.Mutex(false, @"Local\reddholo-Single");
            bool owned;
            // "--restart" (Sensors page) waits for the old instance to exit instead of handing off to it.
            try { owned = _single.WaitOne(Array.IndexOf(e.Args, "--restart") >= 0 ? 15000 : 0); }
            catch (System.Threading.AbandonedMutexException) { owned = true; } // previous instance crashed
            if (!owned)
            {
                if (Array.IndexOf(e.Args, "--widget") < 0) ShowSignal.Set(); // bring the running copy's window up
                Shutdown();
                return;
            }
            new System.Threading.Thread(() => { while (ShowSignal.WaitOne()) Dispatcher.BeginInvoke(new Action(ShowMain)); })
                { IsBackground = true, Name = "ShowSignal" }.Start();

            // Last-resort net: an elevated tool that can be mid-uninstall or driving fans must not vanish on one bad exception.
            DispatcherUnhandledException += OnUiException;
            TaskScheduler.UnobservedTaskException += (_, args) => { Log("Unobserved task", args.Exception); args.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("Fatal", args.ExceptionObject as Exception);
            foreach (var key in new[] { "Display", "Mono" })
                if (Resources[key] is FontFamily f) Resources[key] = UsableFont(f);
            Themes.OverlayScrollbars.Register();
            Themes.ThemeManager.LoadSaved(); // palette must exist before any window parses
            base.OnStartup(e);

            // The main window and the widget each keep the app alive; it exits when neither is showing.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Before MainWindow: LHM stays first in the list, ahead of any plugin sources.
            BcuHolo.Modules.Sensors.SensorHub.AddSource(new BcuHolo.Modules.Sensors.LhmSensorSource());
            BcuHolo.Modules.Sensors.SensorHub.Post = a => Dispatcher.BeginInvoke(DispatcherPriority.Background, a);
            Exit += (_, _) => BcuHolo.Modules.Sensors.SensorHub.Shutdown(); // release the sensor driver cleanly
            var main = new MainWindow(); // always built: it owns the modules (GPU engine, sensors) the widget reads
            MainWindow = main;
            main.Closing += (_, args) =>
            {
                if (_widget == null) { Shutdown(); return; }
                args.Cancel = true; // widget still open: hide, so the GPU engine keeps one owner
                main.Hide();
            };
            bool widgetOnly = Array.IndexOf(e.Args, "--widget") >= 0;
            if (!widgetOnly) main.Show();
            if (widgetOnly || Widget.WidgetWindow.WasOpen()) ShowWidget();
        }

        private Widget.WidgetWindow _widget;

        public void ShowMain()
        {
            MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        }

        public void ShowWidget()
        {
            if (_widget != null) { _widget.Activate(); return; }
            _widget = new Widget.WidgetWindow();
            _widget.Closed += (_, _) => { _widget = null; if (!MainWindow.IsVisible) Shutdown(); };
            _widget.Show();
            _widget.RememberOpen(true);
        }

        public void ToggleWidget()
        {
            if (_widget == null) ShowWidget();
            else { _widget.RememberOpen(false); _widget.Close(); }
        }

        /// <summary>
        /// WPF only falls back down a "A, B" font list when A is missing, not when A throws. A damaged Windows font cache
        /// made Cascadia Mono throw UnauthorizedAccessException on load (2026-11-15), killing the first layout pass.
        /// Drop leading families that fail to load so the next one in the list is used.
        /// </summary>
        internal static FontFamily UsableFont(FontFamily family)
        {
            var names = family.Source.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
            int dropped = 0;
            while (names.Count > 1 && !Loads(names[0])) { Log("Font", new Exception($"'{names[0]}' failed to load; using '{names[1]}' instead")); names.RemoveAt(0); dropped++; }
            return dropped == 0 ? family : new FontFamily(string.Join(", ", names));
        }

        private static bool Loads(string name)
        {
            try
            {
                foreach (var weight in new[] { FontWeights.Normal, FontWeights.Bold })
                    new Typeface(new FontFamily(name), FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out _);
                return true; // a missing family returns false without throwing; WPF's own fallback handles that
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log("UI", e.Exception);
            e.Handled = true; // keep running; the failing action is abandoned
            MessageBox.Show(MainWindow,
                $"Something went wrong, but reddholo is still running.\n\n{e.Exception.Message}\n\nDetails were saved to:\n{LogPath}",
                "reddholo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static void Log(string kind, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}\n\n");
            }
            catch (Exception)
            {
                // Logging must never throw from inside an exception handler.
            }
        }
    }
}
