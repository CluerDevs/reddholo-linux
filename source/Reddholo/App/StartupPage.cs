using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Reddholo.App
{
    /// <summary>Startup page: XDG autostart entries plus systemd user (and optionally system) services. Port of the WPF StartupView.</summary>
    internal sealed class StartupPage : IPage
    {
        public string Id => "startup";
        public string Title => "Startup";
        // Power symbol.
        public string Icon => "M11,2 H13 V12 H11 Z M6.3,5.3 L7.7,6.7 A7,7 0 1 0 16.3,6.7 L17.7,5.3 A9,9 0 1 1 6.3,5.3 Z";
        public int Order => 60;
        public Control CreateView() => new StartupView();
        public Control CreateTile() => new StartupTile();
    }

    internal sealed class StartupItem
    {
        public string Name, Source, Command, Comment;
        public string File;   // autostart: bare .desktop file name
        public string Unit;   // services: unit name exactly as systemctl listed it
        public bool Enabled, Applies = true, Essential, Template;
        public bool IsService => Unit != null;
    }

    /// <summary>One model for tile and page. Loads on first use; Load() again after a change.</summary>
    internal static class StartupModel
    {
        // Overridable so the parse/write logic can be checked against a scratch directory.
        internal static string UserDir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart");
        internal static string SystemDir = "/etc/xdg/autostart";

        public static List<StartupItem> Rows = new List<StartupItem>();
        public static string Note;          // e.g. systemd missing
        public static bool Loading, ShowSystem;
        public static event Action Changed;
        private static bool _loaded;

        public static void EnsureLoaded() { if (!_loaded) _ = Load(); }

        public static async Task Load()
        {
            if (Loading) return;
            _loaded = Loading = true;
            Changed?.Invoke();
            bool sys = ShowSystem;
            var (rows, note) = await Task.Run(() => Collect(sys));
            Rows = rows;
            Note = note;
            Loading = false;
            Changed?.Invoke();
        }

        private static (List<StartupItem>, string) Collect(bool sys)
        {
            var rows = new List<StartupItem>();
            var notes = new List<string>();
            try { rows.AddRange(ReadAutostart()); }
            catch (Exception ex) { notes.Add("Autostart folder unreadable: " + ex.Message); }
            bool noBus = false;
            var user = Services(true, ref noBus);
            if (user == null) notes.Add("systemd user session not available (common on WSL), so user services are not listed.");
            else rows.AddRange(user);
            if (sys)
            {
                var s = Services(false, ref noBus);
                if (s == null) notes.Add("systemd not running, so system services are not listed.");
                else rows.AddRange(s);
            }
            if (noBus) notes.Add("systemd is not running here (on WSL set systemd=true in /etc/wsl.conf), so service descriptions are missing and changes apply only once it runs.");
            return (rows, notes.Count == 0 ? null : string.Join(" ", notes));
        }

        // ---------- XDG autostart ----------

        public static bool ValidFile(string f) => !string.IsNullOrEmpty(f) && f.EndsWith(".desktop", StringComparison.Ordinal)
            && f.IndexOf('/') < 0 && f.IndexOf('\\') < 0 && f != ".desktop" && !f.StartsWith("..", StringComparison.Ordinal);

        internal static List<StartupItem> ReadAutostart()
        {
            var byFile = new Dictionary<string, string>(); // file name -> path; user entries override system ones
            foreach (var dir in new[] { SystemDir, UserDir })
                if (Directory.Exists(dir))
                    foreach (var p in Directory.GetFiles(dir, "*.desktop")) byFile[Path.GetFileName(p)] = p;

            var desktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
            var list = new List<StartupItem>();
            foreach (var kv in byFile.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var e = Parse(File.ReadAllText(kv.Value));
                string Get(string k) => e.TryGetValue(k, out var v) ? v : null;
                bool hidden = Get("Hidden") == "true", gnomeOff = Get("X-GNOME-Autostart-enabled") == "false";
                var only = (Get("OnlyShowIn") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
                var not = (Get("NotShowIn") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
                list.Add(new StartupItem
                {
                    Name = Get("Name") ?? Path.GetFileNameWithoutExtension(kv.Key),
                    Source = "Autostart",
                    Command = Get("Exec") ?? "",
                    Comment = Get("Comment"),
                    File = kv.Key,
                    Enabled = !hidden && !gnomeOff,
                    Applies = (only.Length == 0 || only.Intersect(desktops).Any()) && !not.Intersect(desktops).Any(),
                });
            }
            return list;
        }

        /// <summary>Keys of the [Desktop Entry] group (unlocalized only).</summary>
        internal static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>();
            bool inMain = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal)) { inMain = line == "[Desktop Entry]"; continue; }
                int eq = line.IndexOf('=');
                if (!inMain || eq <= 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var k = line.Substring(0, eq).Trim();
                if (!d.ContainsKey(k)) d[k] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        /// <summary>Sets keys inside [Desktop Entry], replacing existing lines or adding them after the header.</summary>
        internal static string SetKeys(string text, IDictionary<string, string> keys)
        {
            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
            int header = lines.FindIndex(l => l.Trim() == "[Desktop Entry]");
            if (header < 0) { lines.Insert(0, "[Desktop Entry]"); header = 0; }
            int end = lines.FindIndex(header + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
            if (end < 0) end = lines.Count;
            var todo = new Dictionary<string, string>(keys);
            for (int i = header + 1; i < end; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                var k = lines[i].Substring(0, eq).Trim();
                if (todo.TryGetValue(k, out var v)) { lines[i] = k + "=" + v; todo.Remove(k); }
            }
            lines.InsertRange(header + 1, todo.Select(kv => kv.Key + "=" + kv.Value));
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>Writes the user override for this entry. Returns null on success, else the error.</summary>
        internal static string SetAutostart(string file, bool on)
        {
            if (!ValidFile(file)) return "Bad file name.";
            try
            {
                string user = Path.Combine(UserDir, file), sys = Path.Combine(SystemDir, file);
                string src = File.Exists(user) ? user : File.Exists(sys) ? sys : null;
                if (src == null) return "Entry no longer exists.";
                var keys = new Dictionary<string, string> { ["Hidden"] = on ? "false" : "true" };
                if (on && Parse(File.ReadAllText(src)).TryGetValue("X-GNOME-Autostart-enabled", out var g) && g == "false")
                    keys["X-GNOME-Autostart-enabled"] = "true";
                var text = SetKeys(File.ReadAllText(src), keys);
                Directory.CreateDirectory(UserDir);
                File.WriteAllText(user, text);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------- systemd ----------

        private static readonly Regex UnitName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9@._:\\-]*\.service$");

        // Boot plumbing a user should not switch off from here.
        private static readonly Regex EssentialUnit = new Regex(
            @"^(dbus|dbus-broker|systemd-.*|NetworkManager.*|ssh|sshd|getty@|serial-getty@|console-getty|polkit|udisks2|apparmor|cron|rsyslog|snapd.*|keyboard-setup|console-setup|setvtrgb|networkd-dispatcher|wpa_supplicant|accounts-daemon|gdm.*|sddm|lightdm|display-manager|user@|wsl-.*)\.service$");

        /// <summary>Enabled and disabled services, or null when systemctl is missing or refuses.</summary>
        private static List<StartupItem> Services(bool user, ref bool noBus)
        {
            var args = new List<string>();
            if (user) args.Add("--user");
            args.AddRange(new[] { "list-unit-files", "--type=service", "--state=enabled,disabled", "--no-legend", "--no-pager" });
            var (code, outp) = Run("systemctl", args, 15000);
            if (code != 0) return null;
            var items = new List<StartupItem>();
            foreach (var line in outp.Split('\n'))
            {
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || !UnitName.IsMatch(p[0])) continue;
                items.Add(new StartupItem
                {
                    Name = p[0].Substring(0, p[0].Length - ".service".Length),
                    Source = user ? "User service" : "System service",
                    Command = p[0],
                    Unit = p[0],
                    Enabled = p[1] == "enabled",
                    Essential = !user && EssentialUnit.IsMatch(p[0]),
                    Template = p[0].Contains("@."),
                });
            }
            // One call for every description.
            var real = items.Where(i => !i.Template).ToList();
            if (real.Count > 0)
            {
                args = new List<string>();
                if (user) args.Add("--user");
                args.AddRange(new[] { "show", "-p", "Id", "-p", "Description", "--no-pager" });
                args.AddRange(real.Select(i => i.Unit));
                var (c2, o2) = Run("systemctl", args, 15000);
                if (c2 == 0)
                {
                    string id = null;
                    var desc = new Dictionary<string, string>();
                    foreach (var l in o2.Split('\n'))
                    {
                        if (l.StartsWith("Id=", StringComparison.Ordinal)) id = l.Substring(3).Trim();
                        else if (l.StartsWith("Description=", StringComparison.Ordinal) && id != null) desc[id] = l.Substring(12).Trim();
                    }
                    foreach (var i in real) if (desc.TryGetValue(i.Unit, out var d) && d.Length > 0) i.Comment = d;
                }
                else noBus = true;
            }
            return items;
        }

        /// <summary>Enables or disables a unit that Load listed. Returns null on success, else the error.</summary>
        internal static async Task<string> SetService(StartupItem it, bool on)
        {
            // Only names systemctl itself listed, never anything typed or edited.
            if (it?.Unit == null || !UnitName.IsMatch(it.Unit) || !Rows.Contains(it) || it.Essential || it.Template) return "Not allowed for this unit.";
            var args = new List<string>();
            string file;
            if (it.Source == "User service") { file = "systemctl"; args.Add("--user"); }
            else { file = "pkexec"; args.Add("systemctl"); }
            args.Add(on ? "enable" : "disable");
            args.Add(it.Unit);
            var (code, outp) = await Task.Run(() => Run(file, args, 300000));
            return code == 0 ? null : string.IsNullOrWhiteSpace(outp) ? $"{file} exited with {code}." : outp.Trim();
        }

        private static (int, string) Run(string file, IEnumerable<string> args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.Environment["SYSTEMD_COLORS"] = "0";
                using var p = Process.Start(psi);
                var err = p.StandardError.ReadToEndAsync();
                var outp = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
                return (p.ExitCode, p.ExitCode == 0 ? outp.Result : err.Result + outp.Result);
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        /// <summary>Confirm, apply, reload. Every change on the page goes through here.</summary>
        public static async Task Toggle(StartupItem it, bool on)
        {
            string verb = on ? "Enable" : "Disable";
            string what, undo;
            if (!it.IsService)
            {
                what = on ? $"Writes {Path.Combine(UserDir, it.File)} with Hidden=false, so \"{it.Name}\" starts at your next login."
                          : $"Writes {Path.Combine(UserDir, it.File)} with Hidden=true, so \"{it.Name}\" no longer starts at login. Nothing else is touched.";
                undo = on ? "Undo: switch it off again here." : "Undo: switch it back on here, or delete that file" + (File.Exists(Path.Combine(SystemDir, it.File)) ? "." : " and its Hidden line.");
            }
            else if (it.Source == "User service")
            {
                what = $"Runs systemctl --user {verb.ToLowerInvariant()} {it.Unit}. " + (on ? "It starts at your next login." : "It stops starting at login; a running instance keeps running until logout.");
                undo = $"Undo: systemctl --user {(on ? "disable" : "enable")} {it.Unit}, or switch it here.";
            }
            else
            {
                what = $"Runs pkexec systemctl {verb.ToLowerInvariant()} {it.Unit} (asks for your password). " + (on ? "It starts at next boot." : "It stops starting at boot; the running service is not stopped now.");
                undo = $"Undo: sudo systemctl {(on ? "disable" : "enable")} {it.Unit}, or switch it here.";
            }
            if (!await Shell.Confirm($"{verb} {it.Name}?", what + "\n\n" + undo)) { Changed?.Invoke(); return; }

            string error = it.IsService ? await SetService(it, on) : await Task.Run(() => SetAutostart(it.File, on));
            await Load();
            if (error != null) { Note = $"Could not {verb.ToLowerInvariant()} {it.Name}: {error}"; Changed?.Invoke(); }
        }
    }

    internal sealed class StartupView : UserControl
    {
        private readonly TextBlock _count = Ui.Text("…", 30, weight: FontWeight.Bold);
        private readonly TextBlock _note = Ui.Text("", 13, "Danger");
        private readonly StackPanel _list = new StackPanel();
        private readonly Action _changed;

        public StartupView()
        {
            var refresh = Ui.Button("Refresh", "ghost");
            refresh.VerticalAlignment = VerticalAlignment.Center;
            refresh.Click += (_, _) => _ = StartupModel.Load();
            DockPanel.SetDock(refresh, Dock.Right);
            var head = new StackPanel();
            head.Children.Add(Ui.Caps("STARTUP ITEMS"));
            _count.Margin = new Thickness(0, 4, 0, 0);
            head.Children.Add(_count);
            _note.Margin = new Thickness(0, 6, 0, 0);
            head.Children.Add(_note);
            var dock = new DockPanel();
            dock.Children.Add(refresh);
            dock.Children.Add(head);

            var sys = new ToggleButton { Content = "Show system services", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
            sys.Classes.Add("chip");
            sys.IsCheckedChanged += (_, _) => { StartupModel.ShowSystem = sys.IsChecked == true; _ = StartupModel.Load(); };

            var root = new StackPanel { Margin = new Thickness(0, 12, 0, 24) };
            root.Children.Add(Ui.Card(dock, new Thickness(0, 0, 0, 16)));
            root.Children.Add(sys);
            var listCard = Ui.Card(_list);
            listCard.Padding = new Thickness(20, 8);
            root.Children.Add(listCard);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

            _changed = () => Dispatcher.UIThread.Post(Redraw);
            Ui.WhileShown(this, on =>
            {
                if (on) { StartupModel.Changed += _changed; StartupModel.EnsureLoaded(); Redraw(); }
                else StartupModel.Changed -= _changed;
            });
        }

        private void Redraw()
        {
            var rows = StartupModel.Rows;
            var login = rows.Where(r => r.Source != "System service").ToList();
            _count.Inlines.Clear();
            _count.Inlines.Add(new Avalonia.Controls.Documents.Run($"{login.Count(r => r.Enabled && r.Applies)} on"));
            var total = new Avalonia.Controls.Documents.Run($" / {login.Count} at login") { FontSize = 16, FontWeight = FontWeight.Normal };
            total.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, _count.GetResourceObservable("Muted"));
            _count.Inlines.Add(total);
            _note.Text = StartupModel.Note ?? "";
            _note.IsVisible = StartupModel.Note != null;

            _list.Children.Clear();
            if (StartupModel.Loading && rows.Count == 0) { _list.Children.Add(Pad(Ui.Text("Loading startup items…", 14, "Muted"))); return; }
            if (rows.Count == 0) { _list.Children.Add(Pad(Ui.Text("Nothing starts at login.", 14, "Muted"))); return; }
            for (int i = 0; i < rows.Count; i++) _list.Children.Add(Row(rows[i], i < rows.Count - 1));
        }

        private static Control Pad(Control c) { c.Margin = new Thickness(0, 12); return c; }

        private static Control Row(StartupItem r, bool rule)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,124,1.6*,110"), MinHeight = 56 };
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            var name = Ui.Text(r.Name, 15, weight: FontWeight.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            names.Children.Add(name);
            string sub = r.Applies ? r.Comment : "Not run on this desktop (OnlyShowIn/NotShowIn)";
            if (!string.IsNullOrEmpty(sub))
            {
                var c = Ui.Text(sub, 12, "Muted");
                c.TextWrapping = TextWrapping.NoWrap; c.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(c, sub);
                names.Children.Add(c);
            }
            g.Children.Add(names);

            var pill = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 14, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = Ui.Text(r.Source, 12) };
            Ui.Res(pill, Border.BackgroundProperty, r.Source == "System service" ? "Violet.24" : r.IsService ? "Blue.24" : "Line.14");
            Grid.SetColumn(pill, 1);
            g.Children.Add(pill);

            var cmd = Ui.Text(r.Command, 12, "Muted", mono: true);
            cmd.TextWrapping = TextWrapping.NoWrap; cmd.TextTrimming = TextTrimming.CharacterEllipsis;
            cmd.VerticalAlignment = VerticalAlignment.Center; cmd.Margin = new Thickness(0, 0, 14, 0);
            ToolTip.SetTip(cmd, r.File != null ? r.File + "\n" + r.Command : r.Command);
            Grid.SetColumn(cmd, 2);
            g.Children.Add(cmd);

            Control state;
            if (r.Essential || r.Template)
            {
                var t = Ui.Text(r.Essential ? "system — leave on" : "template unit", 12, "Muted");
                t.VerticalAlignment = VerticalAlignment.Center; t.TextAlignment = TextAlignment.Right;
                state = t;
            }
            else
            {
                var tb = new ToggleButton { Content = r.Enabled ? "On" : "Off", IsChecked = r.Enabled, Width = 72, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
                tb.Classes.Add("chip");
                Avalonia.Automation.AutomationProperties.SetName(tb, "Toggle " + r.Name);
                // The click flips IsChecked at once; put it back and let the model decide after confirmation.
                tb.Click += (_, _) => { tb.IsChecked = r.Enabled; _ = StartupModel.Toggle(r, !r.Enabled); };
                state = tb;
            }
            Grid.SetColumn(state, 3);
            g.Children.Add(state);
            if (!r.Enabled || !r.Applies) { names.Opacity = 0.55; cmd.Opacity = 0.55; }

            var line = new Border { Child = g, BorderThickness = new Thickness(0, 0, 0, rule ? 1 : 0) };
            Ui.Res(line, Border.BorderBrushProperty, "Line.24");
            return line;
        }
    }

    /// <summary>Dashboard tile: enabled login items, the first three, link to the page.</summary>
    internal sealed class StartupTile : Border
    {
        private readonly TextBlock _count = Ui.Text("…", 34, weight: FontWeight.Bold);
        private readonly StackPanel _names = new StackPanel();
        private readonly TextBlock _status = Ui.Text("", 13, "Muted");
        private readonly Action _changed;

        public StartupTile()
        {
            Classes.Add("card");
            var s = new StackPanel();
            s.Children.Add(Ui.Caps("STARTUP"));
            _count.Margin = new Thickness(0, 6, 0, 10);
            s.Children.Add(_count);
            s.Children.Add(_names);
            _status.Margin = new Thickness(0, 8, 0, 0);
            s.Children.Add(_status);
            var manage = Ui.Button("Manage startup", "ghost", new Thickness(0, 14, 0, 0));
            manage.Click += (_, _) => Shell.Navigate("startup");
            s.Children.Add(manage);
            Child = s;

            _changed = () => Dispatcher.UIThread.Post(Redraw);
            Ui.WhileShown(this, on =>
            {
                if (on) { StartupModel.Changed += _changed; StartupModel.EnsureLoaded(); Redraw(); }
                else StartupModel.Changed -= _changed;
            });
        }

        private void Redraw()
        {
            var on = StartupModel.Rows.Where(r => r.Source != "System service" && r.Enabled && r.Applies).ToList();
            _count.Text = StartupModel.Loading && StartupModel.Rows.Count == 0 ? "…" : on.Count.ToString();
            _names.Children.Clear();
            foreach (var r in on.Take(3))
            {
                var d = new DockPanel { Margin = new Thickness(0, 4) };
                var k = Ui.Text(r.IsService ? "Service" : "Autostart", 12, "Muted");
                k.Margin = new Thickness(12, 0, 0, 0);
                DockPanel.SetDock(k, Dock.Right);
                var n = Ui.Text(r.Name, 14, weight: FontWeight.SemiBold);
                n.TextWrapping = TextWrapping.NoWrap; n.TextTrimming = TextTrimming.CharacterEllipsis;
                d.Children.Add(k); d.Children.Add(n);
                _names.Children.Add(d);
            }
            _status.Text = StartupModel.Loading && StartupModel.Rows.Count == 0 ? "Loading…"
                : StartupModel.Note != null ? StartupModel.Note
                : on.Count == 0 ? "Nothing starts at login."
                : on.Count > 3 ? $"+{on.Count - 3} more at login" : "";
            _status.IsVisible = _status.Text.Length > 0;
        }
    }
}
