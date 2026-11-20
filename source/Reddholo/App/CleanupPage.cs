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

namespace Reddholo.App
{
    /// <summary>Cleanup page: reclaimable space on Linux. Port of the WPF CleanupModule.</summary>
    internal sealed class CleanupPage : IPage
    {
        private readonly CleanupModel _model = new CleanupModel();

        public string Id => "cleanup";
        public string Title => "Cleanup";
        public string Icon => "M9,3 H15 V5 H20 V7 H4 V5 H9 Z M6,8 H18 L17,21 H7 Z";
        public int Order => 30;
        public Control CreateView() => new CleanupView(_model);
        public Control CreateTile() => CleanupView.Tile(_model);

        internal static string Fmt(long b) =>
            b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0") + " GB"
            : b >= 1L << 20 ? (b >> 20) + " MB"
            : (b >> 10) + " KB";
    }

    internal sealed class CleanupItem
    {
        public string Name, Description;
        public bool Root, Checked;
        /// <summary>No safe read-only way to measure it; the size is only known after cleaning.</summary>
        public bool Unknown;
        /// <summary>Bytes, or -1 when the item does not exist on this machine.</summary>
        public Func<long> Measure;
        public Action Clean;
        /// <summary>null until measured.</summary>
        public long? Size;

        public bool Shown => Size.HasValue && Size.Value >= 0;
        public long Bytes => Shown && !Unknown ? Size.Value : 0;
        public string SizeText => !Size.HasValue ? "measuring…" : Unknown ? "size unknown" : CleanupPage.Fmt(Size.Value);
    }

    /// <summary>One model shared by the page and the dashboard tile. All members are used on the UI thread.</summary>
    internal sealed class CleanupModel
    {
        public readonly List<CleanupItem> Items = Build();
        public event Action Changed;
        public bool Busy;
        private Task _first;

        public bool Measured => Items.All(i => i.Size.HasValue);
        public long Reclaimable => Items.Sum(i => i.Bytes);

        /// <summary>Measures once, the first time the tile or page is shown.</summary>
        public void Ensure() => _first ??= MeasureAsync();

        public async Task MeasureAsync()
        {
            Busy = true;
            Changed?.Invoke();
            var sizes = await Task.WhenAll(Items.Select(i => Task.Run(() => Safe(i.Measure))));
            for (int k = 0; k < Items.Count; k++) Items[k].Size = sizes[k];
            Done();
        }

        /// <summary>Cleans one by one, re-measuring each; returns the result line.</summary>
        public async Task<string> CleanAsync(List<CleanupItem> picked)
        {
            Busy = true;
            Changed?.Invoke();
            long freed = 0;
            var failed = new List<string>();
            foreach (var item in picked)
            {
                var before = item.Bytes;
                string error = await Task.Run(() =>
                {
                    try { item.Clean(); return null; }
                    catch (Exception ex) { return ex.Message; }
                });
                item.Size = await Task.Run(() => Safe(item.Measure));
                if (error != null) failed.Add(item.Name + " (" + error + ")");
                else freed += Math.Max(0, before - item.Bytes);
            }
            Done();
            var text = "Freed " + CleanupPage.Fmt(freed);
            return failed.Count == 0 ? text : text + " · failed: " + string.Join(", ", failed);
        }

        private void Done()
        {
            Busy = false;
            var total = Reclaimable;
            Shell.SetBadge("cleanup", total < 100L << 20 ? null : (total / (double)(1L << 30)).ToString("0.0") + " GB");
            Changed?.Invoke();
        }

        private static long Safe(Func<long> f)
        {
            try { return f(); }
            catch (Exception) { return -1; }
        }

        // ---- Definitions ----

        private static List<CleanupItem> Build()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cache = Path.Combine(home, ".cache");
            var thumbs = Path.Combine(cache, "thumbnails");
            var none = new string[0];
            var items = new List<CleanupItem>
            {
                Folder("Trash", "Everything in the file manager's trash. Permanent: it cannot be restored afterwards.",
                    Path.Combine(home, ".local", "share", "Trash"), none, true, false, false),
                Folder("Thumbnails", "Image and video previews. Rebuilt when you browse the folders again.",
                    thumbs, none, true, false, true),
                Folder("User cache", "Everything else in ~/.cache. Apps rebuild it; files they cannot release are skipped.",
                    cache, new[] { "-not", "-path", thumbs + "/*" }, true, false, false),
                Folder("Old temporary files", "Your own files in /tmp not changed for 7 days.",
                    "/tmp", new[] { "-user", Environment.UserName, "-mmin", "+10080" }, false, false, false), // opt-in: long-running tools keep work files here
                new CleanupItem
                {
                    Name = "APT package cache", Root = true, Checked = true,
                    Description = "Downloaded .deb packages that are already installed. Downloaded again only if needed.",
                    Measure = () => OnPath("apt-get") ? FindBytes("/var/cache/apt/archives", new[] { "-name", "*.deb" }) : -1,
                    Clean = () => Exec(false, "pkexec", "apt-get", "clean"),
                },
                new CleanupItem
                {
                    Name = "System journal", Root = true,
                    Description = "System logs. Keeps the last 14 days, so it frees at most the size shown.",
                    Measure = JournalBytes,
                    Clean = () => Exec(false, "pkexec", "journalctl", "--vacuum-time=14d"),
                },
                new CleanupItem
                {
                    Name = "Old snap revisions", Root = true,
                    Description = "Disabled revisions snap keeps for rollback.",
                    Measure = () => SnapOld()?.Sum(s => FileLen($"/var/lib/snapd/snaps/{s.Name}_{s.Rev}.snap")) ?? -1,
                    Clean = () =>
                    {
                        foreach (var s in SnapOld() ?? new List<(string Name, string Rev)>())
                            Exec(false, "pkexec", "snap", "remove", s.Name, "--revision=" + s.Rev);
                    },
                },
                new CleanupItem
                {
                    // shortcut: flatpak has no dry run for --unused, so the size is unknown; parse its listing if users want it.
                    Name = "Unused Flatpak runtimes", Unknown = true,
                    Description = "Runtimes no installed app needs any more. Flatpak asks for a password if they are system-wide.",
                    Measure = () => OnPath("flatpak") ? 0 : -1,
                    Clean = () => Exec(false, "flatpak", "uninstall", "--unused", "-y", "--noninteractive"),
                },
                Folder("Core dumps", "Memory dumps of crashed programs, kept for debugging.",
                    "/var/lib/systemd/coredump", none, true, true, false),
            };
            return items;
        }

        /// <summary>Files under a fixed root, deleted with find: it never follows symlinks, and -xdev keeps it on one filesystem.</summary>
        private static CleanupItem Folder(string name, string desc, string root, string[] filter, bool dirs, bool asRoot, bool check) => new CleanupItem
        {
            Name = name, Description = desc, Root = asRoot, Checked = check,
            Measure = () => FindBytes(root, filter),
            Clean = () =>
            {
                if (!RealDir(root)) return;
                var args = new List<string>();
                if (asRoot) args.Add("pkexec");
                args.AddRange(new[] { "find", root, "-xdev", "-mindepth", "1" });
                args.AddRange(filter);
                // -delete implies depth-first, so a folder is emptied before -empty is tested on it.
                args.AddRange(dirs ? new[] { "(", "-type", "f", "-o", "-type", "d", "-empty", ")", "-delete" } : new[] { "-type", "f", "-delete" });
                Exec(true, args.ToArray()); // find exits 1 when some files were in use or not ours: those are just skipped
            },
        };

        /// <summary>An absolute, existing directory that is not itself a symlink.</summary>
        private static bool RealDir(string path) =>
            Path.IsPathFullyQualified(path) && Directory.Exists(path) && new DirectoryInfo(path).LinkTarget == null;

        private static long FindBytes(string root, string[] filter)
        {
            if (!RealDir(root)) return -1;
            var args = new List<string> { "find", root, "-xdev", "-mindepth", "1" };
            args.AddRange(filter);
            args.AddRange(new[] { "-type", "f", "-printf", "%s\\n" });
            long total = 0;
            foreach (var line in Run(60000, args.ToArray()).Output.Split('\n'))
                if (long.TryParse(line, out var n)) total += n;
            return total;
        }

        private static long JournalBytes()
        {
            if (!OnPath("journalctl") || !(Directory.Exists("/var/log/journal") || Directory.Exists("/run/log/journal"))) return -1;
            // "Archived and active journals take up 371.6M in the file system."
            var m = Regex.Match(Run(30000, "journalctl", "--disk-usage").Output, @"take up ([\d.]+)([BKMGT])");
            if (!m.Success) return -1;
            var unit = "BKMGT".IndexOf(m.Groups[2].Value[0]);
            return (long)(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * Math.Pow(1024, unit));
        }

        /// <summary>Disabled revisions from `snap list --all`; null when snapd is absent or not running (snap would hang).</summary>
        private static List<(string Name, string Rev)> SnapOld()
        {
            if (!OnPath("snap") || !File.Exists("/run/snapd.socket")) return null;
            var list = new List<(string, string)>();
            foreach (var line in Run(20000, "snap", "list", "--all").Output.Split('\n').Skip(1))
            {
                var c = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Validated: these go to a root command line.
                if (c.Length >= 6 && c[^1].Contains("disabled") && Regex.IsMatch(c[0], "^[a-z0-9][a-z0-9-]*$") && Regex.IsMatch(c[2], "^[0-9]+$"))
                    list.Add((c[0], c[2]));
            }
            return list;
        }

        private static long FileLen(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception) { return 0; }
        }

        private static bool OnPath(string exe) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));

        /// <summary>Runs a cleaner with no timeout (pkexec waits for the password). 126/127 = pkexec refused or cancelled.</summary>
        private static void Exec(bool tolerant, params string[] cmd)
        {
            var (code, _) = Run(-1, cmd);
            if (cmd[0] == "pkexec" && code is 126 or 127) throw new InvalidOperationException("not authorised");
            if (!tolerant && code != 0) throw new InvalidOperationException($"{cmd[cmd[0] == "pkexec" ? 1 : 0]} exited {code}");
        }

        private static (int Code, string Output) Run(int timeoutMs, params string[] cmd)
        {
            var psi = new ProcessStartInfo(cmd[0])
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p.StandardInput.Close(); // nothing may wait on a prompt
            p.ErrorDataReceived += (_, _) => { }; // drained so a chatty find cannot block on a full pipe
            p.BeginErrorReadLine();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception) { }
                throw new TimeoutException(cmd[0] + " timed out");
            }
            p.WaitForExit();
            return (p.ExitCode, output.Result);
        }
    }

    internal sealed class CleanupView : UserControl
    {
        private readonly CleanupModel _m;
        private readonly TextBlock _total = Ui.Text("…", 40, "Danger", weight: FontWeight.Bold);
        private readonly TextBlock _count = Ui.Text("", 13, "Muted");
        private readonly TextBlock _result = Ui.Text("", 13, "Muted");
        private readonly StackPanel _list = new StackPanel();
        private readonly Button _rescan = Ui.Button("Rescan sizes", "ghost", new Thickness(0, 0, 10, 0));
        private readonly Button _clean = Ui.Button("Clean selected", "primary");

        public CleanupView(CleanupModel m)
        {
            _m = m;
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var hl = new StackPanel();
            hl.Children.Add(Ui.Caps("RECLAIMABLE · SELECTED"));
            _total.Margin = new Thickness(0, 4, 0, 0);
            hl.Children.Add(_total);
            head.Children.Add(hl);
            _count.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(_count, 1);
            head.Children.Add(_count);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(_rescan);
            actions.Children.Add(_clean);
            var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
            _result.VerticalAlignment = VerticalAlignment.Center;
            _result.Margin = new Thickness(4, 0, 16, 0);
            foot.Children.Add(_result);
            Grid.SetColumn(actions, 1);
            foot.Children.Add(actions);

            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 4, 0, 24) };
            root.Children.Add(Ui.Card(head, new Thickness(0, 0, 0, 16)));
            var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);
            Content = root;

            _rescan.Click += async (_, _) => { _result.Text = ""; await _m.MeasureAsync(); _result.Text = "Sizes updated."; };
            _clean.Click += async (_, _) => await Clean();
            _m.Changed += Rebuild;
            Ui.WhileShown(this, on => { if (on) _m.Ensure(); });
            Rebuild();
        }

        private List<CleanupItem> Picked() => _m.Items.Where(i => i.Checked && i.Shown).ToList();

        private async Task Clean()
        {
            var picked = Picked();
            if (picked.Count == 0) return;
            var list = string.Join("\n", picked.Select(i => $"• {i.Name}  ({i.SizeText})" + (i.Root ? "  · needs admin" : "")));
            var msg = $"Clean {picked.Count} item(s), {CleanupPage.Fmt(picked.Sum(i => i.Bytes))}?\n\n{list}\n\nFiles in use are skipped. Admin items ask for your password.";
            if (!await Shell.Confirm("Clean selected", msg)) return;
            _result.Text = "Cleaning…";
            try { _result.Text = await _m.CleanAsync(picked); }
            catch (Exception ex) { _result.Text = "Failed: " + ex.Message; }
        }

        private void Totals()
        {
            var picked = Picked();
            _total.Text = _m.Measured ? CleanupPage.Fmt(picked.Sum(i => i.Bytes)) : "…";
            _count.Text = $"{picked.Count} of {_m.Items.Count(i => i.Shown)} items checked";
            _clean.IsEnabled = !_m.Busy && picked.Count > 0;
            _rescan.IsEnabled = !_m.Busy;
            if (_m.Busy && _result.Text == "") _result.Text = "Measuring…";
            if (!_m.Busy && _result.Text == "Measuring…") _result.Text = "";
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            foreach (var i in _m.Items.Where(i => i.Shown))
            {
                var check = new CheckBox { IsChecked = i.Checked, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
                Avalonia.Automation.AutomationProperties.SetName(check, "Select " + i.Name);
                check.IsCheckedChanged += (_, _) => { i.Checked = check.IsChecked == true; Totals(); };

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Ui.Text(i.Name, 15, weight: FontWeight.SemiBold));
                var desc = Ui.Text(i.Description, 13, "Muted");
                desc.Margin = new Thickness(0, 4, 0, 0);
                text.Children.Add(desc);
                if (i.Root)
                {
                    var admin = Ui.Text("Needs admin: asks for your password", 12, "Violet");
                    admin.Margin = new Thickness(0, 4, 0, 0);
                    text.Children.Add(admin);
                }
                var size = Ui.Text(i.SizeText, 15, i.Unknown ? "Muted" : "Text", mono: true);
                size.VerticalAlignment = VerticalAlignment.Center;
                size.Margin = new Thickness(20, 0, 0, 0);

                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                g.Children.Add(check);
                Grid.SetColumn(text, 1); g.Children.Add(text);
                Grid.SetColumn(size, 2); g.Children.Add(size);
                var card = Ui.Card(g, new Thickness(0, 0, 0, 12));
                card.Padding = new Thickness(20, 16);
                _list.Children.Add(card);
            }
            Totals();
        }

        /// <summary>Dashboard tile: reclaimable total, the three biggest items, a link to the page.</summary>
        public static Control Tile(CleanupModel m)
        {
            var total = Ui.Text("…", 40, "Danger", weight: FontWeight.Bold);
            var rows = new StackPanel { Margin = new Thickness(0, 12, 0, 16) };
            var go = Ui.Button("Review cleanup", "ghost");
            go.Click += (_, _) => Shell.Navigate("cleanup");

            var panel = new StackPanel();
            panel.Children.Add(Ui.Caps("CLEANUP"));
            panel.Children.Add(total);
            panel.Children.Add(rows);
            panel.Children.Add(go);

            void Refresh()
            {
                total.Text = m.Measured ? CleanupPage.Fmt(m.Reclaimable) : "…";
                rows.Children.Clear();
                foreach (var i in m.Items.Where(i => i.Bytes > 0).OrderByDescending(i => i.Bytes).Take(3))
                {
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    var name = Ui.Text(i.Name, 14);
                    name.TextWrapping = TextWrapping.NoWrap;
                    name.TextTrimming = TextTrimming.CharacterEllipsis;
                    name.Margin = new Thickness(0, 0, 12, 0);
                    var size = Ui.Text(i.SizeText, 14, mono: true);
                    Grid.SetColumn(size, 1);
                    g.Children.Add(name);
                    g.Children.Add(size);
                    var row = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8), Child = g };
                    Ui.Res(row, Border.BorderBrushProperty, "Line.24");
                    rows.Children.Add(row);
                }
            }

            m.Changed += Refresh;
            var card = Ui.Card(panel);
            Ui.WhileShown(card, on => { if (on) m.Ensure(); });
            Refresh();
            return card;
        }
    }
}
