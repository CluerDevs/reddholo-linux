using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace Reddholo.App
{
    /// <summary>Apps page: installed packages (apt/rpm, flatpak, snap) with search, sort and uninstall. Linux stand-in for the WPF AppsView.</summary>
    internal sealed class AppsPage : IPage
    {
        public string Id => "apps";
        public string Title => "Apps";
        public string Icon => "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z M5,5 V8 H8 V5 Z M16,5 V8 H19 V5 Z M5,16 V19 H8 V16 Z M16,16 V19 H19 V16 Z";
        public int Order => 10;
        public Control CreateView() => new AppsView();
    }

    /// <summary>One installed package. Properties are bound by the list rows.</summary>
    internal sealed class Pkg
    {
        public string Source { get; set; }   // apt, rpm, flatpak, snap
        public string Id { get; set; }       // what the remove command takes
        public string Name { get; set; }
        public string Version { get; set; }
        public string Summary { get; set; }
        public string Scope { get; set; } = ""; // flatpak --user / --system
        public long SizeKb { get; set; }
        public bool User { get; set; }       // shown in the default (user-installed) view
        public string SizeText => SizeKb > 0 ? Packages.FormatKb(SizeKb) : "—";
    }

    internal static class Packages
    {
        // Rejects option-looking or odd names before they reach a privileged command.
        private static readonly Regex SafeId = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.+_:~@-]*$");

        public static string FormatKb(long kb)
        {
            var c = CultureInfo.CurrentCulture;
            if (kb >= 1024 * 1024) return (kb / 1048576.0).ToString("0.0", c) + " GB";
            if (kb >= 1024) return (kb / 1024.0).ToString("0", c) + " MB";
            return kb.ToString(c) + " KB";
        }

        public static bool Has(string cmd) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, cmd)));

        /// <summary>Runs a program without a shell. Exit code -1 on timeout or start failure.</summary>
        public static (int Code, string Out, string Err) Run(string file, IEnumerable<string> args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LC_ALL"] = "C.UTF-8"; // stable number and size formats to parse
            try
            {
                using var p = Process.Start(psi);
                p.StandardInput.Close(); // nothing can wait on a prompt
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    return (-1, "", "no response, stopped after " + timeoutMs / 1000 + " s");
                }
                return (p.ExitCode, o.Result, e.Result);
            }
            catch (Exception ex) { return (-1, "", ex.Message); }
        }

        /// <summary>Everything installed, plus which sources were read and any that failed.</summary>
        public static (List<Pkg> All, List<string> Sources, List<string> Errors) Load()
        {
            var all = new List<Pkg>();
            var sources = new List<string>();
            var errors = new List<string>();

            if (Has("dpkg-query"))
            {
                sources.Add("apt");
                var r = Run("dpkg-query", new[] { "-W", "-f=${db:Status-Abbrev}\t${binary:Package}\t${Version}\t${Installed-Size}\t${binary:Summary}\n" }, 30000);
                if (r.Code != 0) errors.Add("dpkg: " + r.Err.Trim());
                var manual = new HashSet<string>(Has("apt-mark") ? Lines(Run("apt-mark", new[] { "showmanual" }, 30000).Out) : new string[0]);
                foreach (var line in Lines(r.Out))
                {
                    var f = line.Split('\t');
                    if (f.Length < 5 || !f[0].StartsWith("ii", StringComparison.Ordinal)) continue; // skip removed-but-configured
                    string name = f[1].Split(':')[0];
                    long.TryParse(f[3], out var kb);
                    all.Add(new Pkg { Source = "apt", Id = f[1], Name = name, Version = f[2], SizeKb = kb, Summary = f[4],
                                      User = manual.Contains(f[1]) || manual.Contains(name) });
                }
            }
            else if (Has("rpm"))
            {
                sources.Add("rpm");
                var r = Run("rpm", new[] { "-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{SIZE}\t%{SUMMARY}\n" }, 60000);
                if (r.Code != 0) errors.Add("rpm: " + r.Err.Trim());
                // shortcut: user-installed comes from dnf only; on zypper/other rpm systems all packages count as system.
                var user = new HashSet<string>(Has("dnf") ? Lines(Run("dnf", new[] { "repoquery", "--userinstalled", "--qf", "%{name}\n" }, 120000).Out) : new string[0]);
                foreach (var line in Lines(r.Out))
                {
                    var f = line.Split('\t');
                    if (f.Length < 4) continue;
                    long.TryParse(f[2], out var bytes);
                    all.Add(new Pkg { Source = "rpm", Id = f[0], Name = f[0], Version = f[1], SizeKb = bytes / 1024, Summary = f[3], User = user.Contains(f[0]) });
                }
            }

            if (Has("flatpak"))
            {
                sources.Add("flatpak");
                var r = Run("flatpak", new[] { "list", "--app", "--columns=application,name,version,size,installation" }, 30000);
                if (r.Code != 0) errors.Add("flatpak: " + r.Err.Trim());
                foreach (var line in Lines(r.Out))
                {
                    var f = line.Split('\t');
                    if (f.Length < 5) continue;
                    all.Add(new Pkg { Source = "flatpak", Id = f[0], Name = f[1].Length > 0 ? f[1] : f[0], Version = f[2], SizeKb = ParseHuman(f[3]),
                                      Summary = f[0], Scope = f[4].Trim() == "user" ? "--user" : "--system", User = true });
                }
            }

            // snap list hangs when snapd isn't running (WSL without systemd), so require its socket and keep a short timeout.
            if (Has("snap") && File.Exists("/run/snapd.socket"))
            {
                sources.Add("snap");
                var r = Run("snap", new[] { "list" }, 8000);
                if (r.Code != 0) errors.Add("snap: " + (r.Err.Trim().Length > 0 ? r.Err.Trim() : "not available"));
                foreach (var line in Lines(r.Out).Skip(1))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 5) continue;
                    string notes = f.Length > 5 ? f[5] : "";
                    var file = new FileInfo($"/var/lib/snapd/snaps/{f[0]}_{f[2]}.snap");
                    all.Add(new Pkg { Source = "snap", Id = f[0], Name = f[0], Version = f[1], Summary = f[4].TrimEnd('*', '✓'),
                                      SizeKb = file.Exists ? file.Length / 1024 : 0,
                                      User = !(notes.Contains("base") || notes.Contains("snapd") || notes.Contains("core")) });
                }
            }
            return (all, sources, errors);
        }

        private static IEnumerable<string> Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // flatpak sizes are decimal units ("12.3 kB", "1.2 GB").
        internal static long ParseHuman(string s)
        {
            var m = Regex.Match(s ?? "", @"([\d.]+)\s*([kMGT]?)B", RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
            int pow = "kmgt".IndexOf(char.ToLowerInvariant(m.Groups[2].Value.FirstOrDefault('b'))) + 1;
            return (long)(v * Math.Pow(1000, pow) / 1024);
        }

        /// <summary>The remove command for one group of packages from one source, or null if this system can't remove them.</summary>
        public static string[] RemoveCommand(string source, string scope, IList<string> ids)
        {
            if (ids.Any(i => !SafeId.IsMatch(i))) return null;
            string[] head = source switch
            {
                "apt" => new[] { "pkexec", "apt-get", "remove", "-y" },
                "rpm" when Has("dnf") => new[] { "pkexec", "dnf", "remove", "-y" },
                "flatpak" => new[] { "flatpak", "uninstall", "-y", "--noninteractive", scope },
                "snap" => new[] { "pkexec", "snap", "remove" },
                _ => null,
            };
            return head == null ? null : head.Concat(ids).ToArray();
        }

        /// <summary>apt removes reverse dependencies too; a dry run (no root needed) names every package that would go.</summary>
        public static List<string> AptWouldRemove(IList<string> ids)
        {
            var r = Run("apt-get", new[] { "-s", "remove" }.Concat(ids), 60000);
            return Lines(r.Out).Where(l => l.StartsWith("Remv ", StringComparison.Ordinal)).Select(l => l.Split(' ')[1]).ToList();
        }
    }

    internal sealed class AppsView : UserControl
    {
        private List<Pkg> _all = new List<Pkg>();
        private bool _loaded, _busy, _showAll, _bySize;
        private readonly ListBox _list = new ListBox { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, Background = Brushes.Transparent };
        private readonly TextBox _search = new TextBox { Watermark = "Search packages…", Width = 240, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _caption = Ui.Text("", 15, "Muted");
        private readonly TextBlock _sources = Ui.Text("", 12, "Muted");
        private readonly TextBlock _status = Ui.Text("Loading packages…", 15, "Muted");
        private readonly TextBlock _selText = Ui.Text("", 15);
        private readonly TextBlock _resultTitle = Ui.Text("", 15, weight: FontWeight.SemiBold);
        private readonly SelectableTextBlock _resultOut = new SelectableTextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };
        private readonly Border _selBar, _result;
        private readonly Button _uninstall, _reload;
        private readonly ToggleButton _chipAll, _chipName, _chipSize;

        public AppsView()
        {
            // ---- Toolbar ----
            _caption.VerticalAlignment = VerticalAlignment.Center;
            _caption.TextWrapping = TextWrapping.NoWrap;
            _caption.TextTrimming = TextTrimming.CharacterEllipsis;
            _search.TextChanged += (_, _) => Refresh();
            _chipAll = Chip("All packages", new Thickness(10, 0, 0, 0));
            ToolTip.SetTip(_chipAll, "Off: only apps you installed (apt manual, flatpak, snap). On: every package.");
            _chipAll.IsCheckedChanged += (_, _) => { _showAll = _chipAll.IsChecked == true; Refresh(); };
            _chipName = Chip("Name", new Thickness(10, 0, 0, 0));
            _chipSize = Chip("Size", new Thickness(6, 0, 0, 0));
            _chipName.IsChecked = true;
            _chipName.Click += (_, _) => SetSort(false);
            _chipSize.Click += (_, _) => SetSort(true);
            _reload = Ui.Button("Reload", "ghost", new Thickness(10, 0, 0, 0));
            _reload.Click += async (_, _) => await LoadAsync();

            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in new Control[] { _search, _chipAll, _chipName, _chipSize, _reload }) tools.Children.Add(c);
            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(tools, Dock.Right);
            toolbar.Children.Add(tools);
            toolbar.Children.Add(_caption);

            _sources.Margin = new Thickness(2, 0, 0, 12);

            // ---- Last uninstall result ----
            Ui.Res(_resultOut, SelectableTextBlock.FontFamilyProperty, "Mono");
            Ui.Res(_resultOut, SelectableTextBlock.ForegroundProperty, "Muted");
            var closeResult = Ui.Button("Close", "ghost");
            closeResult.Click += (_, _) => _result.IsVisible = false;
            var resHead = new DockPanel();
            DockPanel.SetDock(closeResult, Dock.Right);
            resHead.Children.Add(closeResult);
            _resultTitle.VerticalAlignment = VerticalAlignment.Center;
            resHead.Children.Add(_resultTitle);
            var resBody = new StackPanel();
            resBody.Children.Add(resHead);
            resBody.Children.Add(new ScrollViewer { Content = _resultOut, MaxHeight = 180, Margin = new Thickness(0, 10, 0, 0) });
            _result = Ui.Card(resBody, new Thickness(0, 0, 0, 12));
            _result.IsVisible = false;

            // ---- Selection bar ----
            var clear = Ui.Button("Clear", "ghost", new Thickness(16, 0, 10, 0));
            clear.Click += (_, _) => _list.SelectedItems?.Clear();
            _uninstall = Ui.Button("Uninstall", "primary");
            _uninstall.Click += async (_, _) => await UninstallAsync();
            _selText.VerticalAlignment = VerticalAlignment.Center;
            var sel = new StackPanel { Orientation = Orientation.Horizontal };
            sel.Children.Add(_selText); sel.Children.Add(clear); sel.Children.Add(_uninstall);
            _selBar = Ui.Card(sel, new Thickness(0, 12, 0, 16));
            _selBar.Padding = new Thickness(20, 10, 10, 10);
            _selBar.HorizontalAlignment = HorizontalAlignment.Center;
            _selBar.IsVisible = false;
            _list.SelectionChanged += (_, _) => UpdateSelection();

            // ---- List ----
            _list.ItemTemplate = new FuncDataTemplate<Pkg>((_, _) => Row(), supportsRecycling: true);
            // Marked for removal reads as danger-tinted, as in the WPF app; Fluent's solid accent hides the muted text.
            foreach (var state in new[] { "", ":pointerover", ":pressed", ":focus" })
            {
                var st = new Style(x => (state.Length > 0 ? x.OfType<ListBoxItem>().Class(":selected").Class(state) : x.OfType<ListBoxItem>().Class(":selected"))
                    .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
                st.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("Danger.24")));
                _list.Styles.Add(st);
            }
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextAlignment = TextAlignment.Center;
            var head = RowGrid();
            head.Margin = new Thickness(12, 0, 12, 6);
            head.Children.Add(Ui.Caps("PACKAGE"));
            Place(head, Ui.Caps("SOURCE"), 1);
            Place(head, Ui.Caps("VERSION"), 2);
            var sizeCaps = Ui.Caps("SIZE");
            sizeCaps.HorizontalAlignment = HorizontalAlignment.Right;
            Place(head, sizeCaps, 3);
            var listArea = new Grid();
            listArea.Children.Add(_list);
            listArea.Children.Add(_status);
            var listPanel = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            listPanel.Children.Add(head);
            listPanel.Children.Add(listArea);
            var listCard = Ui.Card(listPanel);
            listCard.Padding = new Thickness(8, 14, 8, 8);

            var root = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var c in new Control[] { toolbar, _sources, _result }) { DockPanel.SetDock(c, Dock.Top); root.Children.Add(c); }
            DockPanel.SetDock(_selBar, Dock.Bottom);
            root.Children.Add(_selBar);
            root.Children.Add(listCard);
            listCard.Margin = new Thickness(0, 0, 0, 16);
            Content = root;

            Ui.WhileShown(this, async on => { if (on && !_loaded) { _loaded = true; await LoadAsync(); } });
        }

        private static ToggleButton Chip(string text, Thickness margin)
        {
            var t = new ToggleButton { Content = text, Margin = margin };
            t.Classes.Add("chip");
            return t;
        }

        private void SetSort(bool bySize)
        {
            _bySize = bySize;
            _chipName.IsChecked = !bySize;
            _chipSize.IsChecked = bySize;
            Refresh();
        }

        private static Grid RowGrid() => new Grid { ColumnDefinitions = new ColumnDefinitions("*,100,200,90") };
        private static void Place(Grid g, Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }

        private static TextBlock Cell(string path, double size, string brush, bool mono = false)
        {
            var t = Ui.Text("", size, brush, mono);
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Bind(TextBlock.TextProperty, new Binding(path));
            return t;
        }

        // Built once per recycled container; content follows DataContext through bindings.
        private static Control Row()
        {
            var g = RowGrid();
            g.Margin = new Thickness(0, 4, 0, 4);
            var name = Cell(nameof(Pkg.Name), 14, "Text");
            name.FontWeight = FontWeight.SemiBold;
            var who = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            who.Children.Add(name);
            who.Children.Add(Cell(nameof(Pkg.Summary), 12, "Muted"));
            g.Children.Add(who);
            var src = Cell(nameof(Pkg.Source), 12, "Accent", mono: true);
            var chip = new Border { Child = src, CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            Ui.Res(chip, Border.BackgroundProperty, "AccentSoft");
            Place(g, chip, 1);
            var ver = Cell(nameof(Pkg.Version), 12, "Muted", mono: true);
            ver.Margin = new Thickness(0, 0, 12, 0);
            Place(g, ver, 2);
            var size = Cell(nameof(Pkg.SizeText), 13, "Text", mono: true);
            size.HorizontalAlignment = HorizontalAlignment.Right;
            Place(g, size, 3);
            return g;
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "Loading packages…");
            var (all, sources, errors) = await Task.Run(Packages.Load); // await resumes on the UI thread
            _all = all;
            _sources.Text = (sources.Count == 0 ? "No package manager found." : "SOURCES  " + string.Join(" · ", sources))
                          + (errors.Count > 0 ? "     " + string.Join("  ·  ", errors) : "");
            SetBusy(false, null);
            Refresh();
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            _status.Text = text ?? "";
            _list.IsEnabled = _reload.IsEnabled = _uninstall.IsEnabled = !busy;
            _list.Opacity = busy ? 0.3 : 1;
            _status.IsVisible = busy;
        }

        private IEnumerable<Pkg> Visible()
        {
            string q = _search.Text?.Trim() ?? "";
            var v = _all.Where(p => _showAll || p.User);
            if (q.Length > 0)
                v = v.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || (p.Summary ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
            return _bySize ? v.OrderByDescending(p => p.SizeKb).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                           : v.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        }

        private void Refresh()
        {
            if (_busy) return;
            var shown = Visible().ToList();
            _list.ItemsSource = shown;
            long kb = shown.Sum(p => p.SizeKb);
            _caption.Text = $"{shown.Count:N0} {(_showAll ? "packages" : "user-installed apps")} · {Packages.FormatKb(kb)}";
            if (shown.Count == 0)
            {
                _status.Text = _all.Count == 0 ? "No packages found." : "Nothing matches.";
                _status.IsVisible = true;
            }
            else _status.IsVisible = false;
            UpdateSelection();
        }

        private List<Pkg> Selected() => _list.SelectedItems?.OfType<Pkg>().ToList() ?? new List<Pkg>();

        private void UpdateSelection()
        {
            var sel = Selected();
            _selBar.IsVisible = sel.Count > 0;
            _selText.Text = $"{sel.Count} selected · {Packages.FormatKb(sel.Sum(p => p.SizeKb))}";
        }

        private async Task UninstallAsync()
        {
            // Only packages from the loaded list are ever passed on.
            var known = new HashSet<Pkg>(_all);
            var picked = Selected().Where(known.Contains).ToList();
            if (picked.Count == 0 || _busy) return;
            var groups = picked.GroupBy(p => (p.Source, p.Scope)).ToList();

            SetBusy(true, "Checking what would be removed…");
            var plan = await Task.Run(() => groups.Select(g =>
            {
                var ids = g.Select(p => p.Id).ToList();
                var cmd = Packages.RemoveCommand(g.Key.Source, g.Key.Scope, ids);
                var names = g.Select(p => p.Name).ToHashSet();
                var extra = cmd != null && g.Key.Source == "apt"
                    ? Packages.AptWouldRemove(ids).Where(n => !names.Contains(n.Split(':')[0])).ToList()
                    : new List<string>();
                return (Ids: ids, Cmd: cmd, Extra: extra, g.Key.Source);
            }).ToList());
            SetBusy(false, null);

            var bad = plan.Where(p => p.Cmd == null).ToList();
            var run = plan.Where(p => p.Cmd != null).ToList();
            var msg = new List<string>();
            foreach (var p in run)
            {
                msg.Add($"{p.Source}: " + string.Join(", ", p.Ids));
                if (p.Extra.Count > 0) msg.Add("  also removed, they depend on it: " + string.Join(", ", p.Extra));
                msg.Add("  $ " + string.Join(" ", p.Cmd));
            }
            if (bad.Count > 0) msg.Add("Skipped, can't remove here: " + string.Join(", ", bad.SelectMany(p => p.Ids)));
            if (run.Count == 0)
            {
                ShowResult("Nothing to uninstall", string.Join("\n", msg));
                return;
            }
            int count = run.Sum(p => p.Ids.Count + p.Extra.Count);
            if (!await Shell.Confirm($"Uninstall {count} package{(count == 1 ? "" : "s")}?", string.Join("\n", msg), "Uninstall")) return;

            SetBusy(true, "Uninstalling… approve the password prompt if one appears.");
            var results = await Task.Run(() => run.Select(p =>
            {
                var r = Packages.Run(p.Cmd[0], p.Cmd.Skip(1), 30 * 60 * 1000);
                return (p.Cmd, r.Code, Text: (r.Out + r.Err).Trim());
            }).ToList());

            int ok = results.Count(r => r.Code == 0);
            var output = string.Join("\n\n", results.Select(r => $"$ {string.Join(" ", r.Cmd)}   (exit {r.Code})\n{Tail(r.Text, 40)}"));
            ShowResult(ok == results.Count ? "Uninstall finished" : $"Uninstall failed for {results.Count - ok} of {results.Count} command(s)", output);
            _list.SelectedItems?.Clear();
            await LoadAsync();
        }

        private static string Tail(string s, int lines)
        {
            var l = s.Split('\n');
            return l.Length <= lines ? s : "…\n" + string.Join("\n", l.Skip(l.Length - lines));
        }

        private void ShowResult(string title, string output)
        {
            _resultTitle.Text = title;
            _resultOut.Text = output;
            _result.IsVisible = true;
        }
    }
}
