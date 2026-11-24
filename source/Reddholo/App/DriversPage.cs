using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// <summary>Drivers page: GPU driver, kernel, BIOS/board, loaded kernel modules, PCI bindings, fwupd/ubuntu-drivers updates. Port of the WPF DriversView.</summary>
    internal sealed class DriversPage : IPage
    {
        public static readonly DriversModel Model = new DriversModel();

        public string Id => "drivers";
        public string Title => "Drivers";
        // Chip with pins.
        public string Icon => "M7,7 H17 V17 H7 Z M9,9 V15 H15 V9 Z M8.5,2 H9.5 V5 H8.5 Z M11.5,2 H12.5 V5 H11.5 Z M14.5,2 H15.5 V5 H14.5 Z M8.5,19 H9.5 V22 H8.5 Z M11.5,19 H12.5 V22 H11.5 Z M14.5,19 H15.5 V22 H14.5 Z M2,8.5 H5 V9.5 H2 Z M2,11.5 H5 V12.5 H2 Z M2,14.5 H5 V15.5 H2 Z M19,8.5 H22 V9.5 H19 Z M19,11.5 H22 V12.5 H19 Z M19,14.5 H22 V15.5 H19 Z";
        public int Order => 50;
        public Control CreateView() => new DriversView(Model);

        public Control CreateTile()
        {
            var m = Model;
            var gpu = Line(16, "Text", FontWeight.SemiBold);
            var ver = Line(13, "Accent", mono: true);
            var kernel = Line(13, "Muted");
            var upd = Line(14, "Text");
            gpu.Margin = new Thickness(0, 4, 0, 0); ver.Margin = new Thickness(0, 4, 0, 0);
            kernel.Margin = new Thickness(0, 8, 0, 0); upd.Margin = new Thickness(0, 12, 0, 0);
            var open = Ui.Button("Open drivers", "ghost", new Thickness(0, 16, 0, 0));
            open.Click += (_, _) => Shell.Navigate("drivers");
            var s = new StackPanel();
            s.Children.Add(Ui.Caps("DRIVERS"));
            s.Children.Add(gpu); s.Children.Add(ver); s.Children.Add(kernel); s.Children.Add(upd); s.Children.Add(open);
            var card = Ui.Card(s);
            void Sync()
            {
                gpu.Text = m.Loaded ? m.GpuName : "Loading…";
                ver.Text = m.GpuVersion;
                kernel.Text = "Kernel " + m.Kernel;
                upd.Text = m.UpdateLine;
                Ui.Res(upd, TextBlock.ForegroundProperty, m.UpdateBrush);
            }
            Ui.WhileShown(card, on => { if (on) { m.Changed += Sync; m.Load(); Sync(); } else m.Changed -= Sync; });
            return card;
        }

        internal static TextBlock Line(double size, string brush, FontWeight? weight = null, bool mono = false)
        {
            var t = Ui.Text("", size, brush, mono, weight);
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }
    }

    internal sealed class KernelModule
    {
        public string Name, Version = "", Description = "", License = "", FileName = "", Intree = "";
        public long Size;
        public int UsedBy;
        /// <summary>The "third-party driver" equivalent: out-of-tree, DKMS, or not GPL-compatible.</summary>
        public bool ThirdParty =>
            FileName != "" && (Intree != "Y" || FileName.Contains("/updates/") || FileName.Contains("/extra/") || FileName.Contains("dkms"))
            || License != "" && !License.Contains("GPL");
    }

    internal sealed class PciDevice { public string Slot, Ids, ClassCode, Name, Driver; }
    internal sealed class UpdateItem { public string Title, Detail, Command; }

    /// <summary>Shared by the page and the dashboard tile. All reads are one-shot; Load runs once, CheckUpdates on demand.</summary>
    internal sealed class DriversModel
    {
        public event Action Changed;
        public bool Loaded, Checking;
        public string GpuName = "n/a", GpuVersion = "n/a", Kernel = "n/a", Bios = "n/a", Board = "n/a";
        public List<KernelModule> Modules = new List<KernelModule>();
        public List<PciDevice> Pci = new List<PciDevice>();
        public List<UpdateItem> Updates;          // null = not checked
        public string UpdateNote = "";
        public DateTime LastChecked;
        private Task _load;

        public string UpdateLine => Checking ? "Checking for updates…" : Updates == null ? "Updates: not checked"
            : Updates.Count > 0 ? Updates.Count + (Updates.Count == 1 ? " update" : " updates")
            : UpdateNote != "" ? "Updates: n/a" : "Firmware up to date";
        public string UpdateBrush => Updates == null || Checking || UpdateNote != "" && Updates.Count == 0 ? "Muted" : Updates.Count > 0 ? "Violet" : "Good";

        private void Raise() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

        public void Load() => _load ??= Task.Run(() =>
        {
            try { Read(); } catch (Exception ex) { Trace.WriteLine("Drivers: " + ex.Message); }
            Loaded = true;
            Raise();
        });

        private void Read()
        {
            Kernel = Cat("/proc/sys/kernel/osrelease") ?? Run("uname", 3000, "-r")?.Trim() ?? "n/a";
            Bios = Join(Cat("/sys/class/dmi/id/bios_vendor"), Cat("/sys/class/dmi/id/bios_version"), Cat("/sys/class/dmi/id/bios_date"));
            Board = Join(Cat("/sys/class/dmi/id/board_vendor"), Cat("/sys/class/dmi/id/board_name"));
            Pci = ReadPci();
            ReadGpu();
            var mods = ParseModules(Cat("/proc/modules") ?? "");
            FillModinfo(mods);
            Modules = mods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        }

        private void ReadGpu()
        {
            var gpu = Pci.FirstOrDefault(p => p.ClassCode.StartsWith("03", StringComparison.Ordinal) && p.Driver != null)
                   ?? Pci.FirstOrDefault(p => p.ClassCode.StartsWith("03", StringComparison.Ordinal));
            string driver = gpu?.Driver;
            if (driver == null)
                foreach (var card in SafeDirs("/sys/class/drm", "card*"))
                {
                    var link = Target(Path.Combine(card, "device", "driver"));
                    if (link != null) { driver = link; break; }
                }
            string nv = Cat("/proc/driver/nvidia/version");
            string version = null;
            if (nv != null)
            {
                driver = "nvidia";
                var m = Regex.Match(nv, @"Kernel Module\s+(?:for \S+\s+)?(\d+(?:\.\d+)+)");
                version = m.Success ? m.Groups[1].Value : null;
            }
            if (version == null && driver != null) version = Nonempty(Run("modinfo", 5000, "-F", "version", driver)?.Trim());
            GpuName = gpu?.Name ?? (driver != null ? driver : "No GPU found");
            GpuVersion = driver == null ? "n/a" : driver + " " + (version ?? "in-tree (kernel " + Kernel + ")");
        }

        /// <summary>/proc/modules lines: "name size refcount deps state offset".</summary>
        internal static List<KernelModule> ParseModules(string text)
        {
            var list = new List<KernelModule>();
            foreach (var line in text.Split('\n'))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3) continue;
                long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size);
                int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int used);
                list.Add(new KernelModule { Name = f[0], Size = size, UsedBy = used });
            }
            return list;
        }

        /// <summary>One modinfo call per 100 modules; output blocks each start with "filename:".</summary>
        private static void FillModinfo(List<KernelModule> mods)
        {
            var byName = mods.ToDictionary(m => m.Name.Replace('-', '_'));
            for (int i = 0; i < mods.Count; i += 100)
            {
                string outp = Run("modinfo", 15000, mods.Skip(i).Take(100).Select(m => m.Name).ToArray());
                if (outp == null) return; // modinfo missing
                ApplyModinfo(outp, byName);
            }
        }

        internal static void ApplyModinfo(string outp, Dictionary<string, KernelModule> byName)
        {
            var block = new Dictionary<string, string>();
            void Flush()
            {
                if (block.TryGetValue("name", out var n) && byName.TryGetValue(n.Replace('-', '_'), out var m))
                {
                    m.FileName = block.GetValueOrDefault("filename", "");
                    m.Version = block.GetValueOrDefault("version", "");
                    m.Description = block.GetValueOrDefault("description", "");
                    m.License = block.GetValueOrDefault("license", "");
                    m.Intree = block.GetValueOrDefault("intree", "");
                }
                block.Clear();
            }
            foreach (var line in outp.Split('\n'))
            {
                int c = line.IndexOf(':');
                if (c <= 0 || line[0] == ' ' || line[0] == '\t') continue;
                string key = line.Substring(0, c), val = line.Substring(c + 1).Trim();
                if (key == "filename") Flush();
                if (!block.ContainsKey(key)) block[key] = val; // first wins (description, license appear once; alias/parm repeat)
            }
            Flush();
        }

        private static List<PciDevice> ReadPci()
        {
            var list = new List<PciDevice>();
            foreach (var d in SafeDirs("/sys/bus/pci/devices", "*"))
            {
                string v = Hex(Cat(Path.Combine(d, "vendor"))), dev = Hex(Cat(Path.Combine(d, "device"))), cls = Hex(Cat(Path.Combine(d, "class")));
                list.Add(new PciDevice { Slot = Path.GetFileName(d), Ids = v + ":" + dev, ClassCode = cls, Driver = Target(Path.Combine(d, "driver")) });
            }
            var names = PciNames(list.Select(p => p.Ids));
            foreach (var p in list) p.Name = names.TryGetValue(p.Ids, out var n) ? n : p.Ids;
            return list.OrderBy(p => p.Slot, StringComparer.Ordinal).ToList();
        }

        /// <summary>"vendor:device" → "Vendor Device" from pci.ids, for just the ids asked.</summary>
        private static Dictionary<string, string> PciNames(IEnumerable<string> ids)
        {
            var want = new HashSet<string>(ids);
            var res = new Dictionary<string, string>();
            string path = new[] { "/usr/share/misc/pci.ids", "/usr/share/hwdata/pci.ids", "/usr/share/pci.ids" }.FirstOrDefault(File.Exists);
            if (path == null) return res;
            try
            {
                string vid = null, vname = null;
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length < 6 || line[0] == '#') continue;
                    if (line[0] != '\t') { vid = line.Substring(0, 4); vname = line.Substring(4).Trim(); if (line.StartsWith("C ")) break; continue; }
                    if (line.Length > 1 && line[1] == '\t' || vid == null) continue;
                    string key = vid + ":" + line.Substring(1, 4);
                    if (want.Contains(key)) res[key] = vname + " " + line.Substring(5).Trim();
                }
                foreach (var k in want.Where(k => !res.ContainsKey(k))) // vendor known, device not
                    foreach (var line in File.ReadLines(path))
                        if (line.StartsWith(k.Substring(0, 4) + "  ", StringComparison.Ordinal)) { res[k] = line.Substring(4).Trim() + " " + k; break; }
            }
            catch (Exception ex) { Trace.WriteLine("Drivers pci.ids: " + ex.Message); }
            return res;
        }

        public void CheckUpdates()
        {
            if (Checking) return;
            Checking = true;
            Raise();
            Task.Run(() =>
            {
                var items = new List<UpdateItem>();
                var notes = new List<string>();
                try
                {
                    string fw = Run("fwupdmgr", 120000, "get-updates", "--json");
                    if (fw == null) notes.Add(File.Exists("/usr/bin/fwupdmgr") ? "fwupdmgr did not respond" : "fwupd not installed (sudo apt install fwupd)");
                    else items.AddRange(ParseFwupd(fw));
                    // "list" prints every applicable driver; only the recommended one from "devices" is a suggestion.
                    string ud = Run("ubuntu-drivers", 60000, "devices");
                    if (ud != null)
                        foreach (var line in ud.Split('\n').Where(l => l.TrimStart().StartsWith("driver", StringComparison.Ordinal) && l.Contains("recommended")))
                        {
                            string pkg = line.Split(':', 2)[1].Split(" - ")[0].Trim();
                            if (pkg == "" || pkg.Contains(' ') || Installed(pkg)) continue;
                            if (pkg.StartsWith("nvidia-driver-", StringComparison.Ordinal) && SafeDirs("/var/lib/dpkg/info", "nvidia-driver-*.list").Length > 0) continue; // another series installed: not an update
                            items.Add(new UpdateItem { Title = pkg, Detail = "Driver package available (ubuntu-drivers)", Command = "sudo apt install " + pkg });
                        }
                    else if (File.Exists("/etc/lsb-release")) notes.Add("ubuntu-drivers not installed");
                }
                catch (Exception ex) { notes.Add(ex.Message); }
                Dispatcher.UIThread.Post(() =>
                {
                    Updates = items;
                    UpdateNote = string.Join(" · ", notes);
                    LastChecked = DateTime.Now;
                    Checking = false;
                    Changed?.Invoke();
                });
            });
        }

        internal static IEnumerable<UpdateItem> ParseFwupd(string json)
        {
            var list = new List<UpdateItem>();
            int start = json.IndexOf('{');
            if (start < 0) return list; // "No updatable devices" and friends are plain text
            try
            {
                using var doc = JsonDocument.Parse(json.Substring(start));
                if (!doc.RootElement.TryGetProperty("Devices", out var devs)) return list;
                foreach (var d in devs.EnumerateArray())
                {
                    string name = Str(d, "Name"), cur = Str(d, "Version");
                    if (!d.TryGetProperty("Releases", out var rels) || rels.GetArrayLength() == 0) continue;
                    var r = rels[0]; // newest first
                    list.Add(new UpdateItem
                    {
                        Title = name + "  " + cur + " → " + Str(r, "Version"),
                        Detail = Str(r, "Summary"),
                        Command = "fwupdmgr update " + Str(d, "DeviceId"),
                    });
                }
            }
            catch (JsonException ex) { Trace.WriteLine("Drivers fwupd json: " + ex.Message); }
            return list;
        }

        private static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static bool Installed(string pkg) =>
            File.Exists("/var/lib/dpkg/info/" + pkg + ".list") || SafeDirs("/var/lib/dpkg/info", pkg + ":*.list").Any();

        // ---- small IO helpers: never throw ----

        internal static string Cat(string path)
        {
            try { return File.Exists(path) ? Nonempty(File.ReadAllText(path).Trim()) : null; }
            catch (Exception) { return null; }
        }

        private static string Nonempty(string s) => string.IsNullOrEmpty(s) ? null : s;
        private static string Join(params string[] parts) => parts.Any(p => p != null) ? string.Join(" · ", parts.Where(p => p != null)) : "n/a";
        private static string Hex(string s) => s == null ? "????" : s.StartsWith("0x", StringComparison.Ordinal) ? s.Substring(2) : s;

        private static string Target(string link)
        {
            try { var t = new FileInfo(link).LinkTarget; return t == null ? null : Path.GetFileName(t); }
            catch (Exception) { return null; }
        }

        private static string[] SafeDirs(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir, pattern) : Array.Empty<string>(); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        /// <summary>stdout of a read-only tool, or null when it is missing or times out.</summary>
        internal static string Run(string file, int timeoutMs, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.Environment["LC_ALL"] = "C";
                using var p = Process.Start(psi);
                var outp = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (Exception) { } return null; }
                return outp.Result;
            }
            catch (Exception) { return null; } // tool not installed
        }
    }

    internal sealed class DriversView : UserControl
    {
        private readonly DriversModel _m;
        private readonly TextBlock _gpu = DriversPage.Line(16, "Text", FontWeight.SemiBold), _gpuVer = DriversPage.Line(13, "Accent", mono: true),
            _kernel = DriversPage.Line(13, "Muted"), _bios = Ui.Text("", 14), _board = Ui.Text("", 13, "Muted"), _counts = Ui.Text("", 13, "Muted"),
            _status = Ui.Text("", 13, "Muted"), _updNote = Ui.Text("", 13, "Muted"), _modCount = Ui.Text("", 12, "Muted");
        private readonly Button _check = Ui.Button("Check for updates", "accent");
        private readonly StackPanel _updList = new StackPanel(), _modRows = new StackPanel(), _pciRows = new StackPanel();
        private readonly TextBox _search = new TextBox { Watermark = "Search modules", Width = 240 };
        private readonly ToggleButton _all = new ToggleButton { Content = "Show all modules", Margin = new Thickness(10, 0, 0, 0) };
        private int _shownModules = -1;

        public DriversView(DriversModel m)
        {
            _m = m;
            var root = new StackPanel { Margin = new Thickness(0, 12, 0, 24) };

            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var g = new StackPanel();
            g.Children.Add(Ui.Caps("GRAPHICS"));
            _gpu.Margin = new Thickness(0, 10, 0, 0); _gpuVer.Margin = new Thickness(0, 4, 0, 0); _kernel.Margin = new Thickness(0, 10, 0, 0);
            g.Children.Add(_gpu); g.Children.Add(_gpuVer); g.Children.Add(_kernel);
            var p = new StackPanel();
            p.Children.Add(Ui.Caps("PLATFORM"));
            _bios.Margin = new Thickness(0, 10, 0, 0); _board.Margin = new Thickness(0, 4, 0, 0); _counts.Margin = new Thickness(0, 10, 0, 0);
            p.Children.Add(_bios); p.Children.Add(_board); p.Children.Add(_counts);
            var pc = Ui.Card(p, new Thickness(8, 0, 0, 0));
            Grid.SetColumn(pc, 1);
            top.Children.Add(Ui.Card(g, new Thickness(0, 0, 8, 0)));
            top.Children.Add(pc);
            root.Children.Add(top);

            // Updates
            var u = new StackPanel();
            u.Children.Add(Ui.Caps("FIRMWARE AND DRIVER UPDATES"));
            var urow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var hint = Ui.Text("Runs fwupdmgr get-updates and ubuntu-drivers list. Installs nothing.", 13, "Muted");
            hint.Margin = _status.Margin = new Thickness(14, 0, 0, 0);
            hint.VerticalAlignment = _status.VerticalAlignment = VerticalAlignment.Center;
            urow.Children.Add(_check); urow.Children.Add(hint); urow.Children.Add(_status);
            _check.Click += (_, _) => m.CheckUpdates();
            u.Children.Add(urow);
            _updNote.Margin = new Thickness(0, 10, 0, 0);
            u.Children.Add(_updNote);
            u.Children.Add(_updList);
            root.Children.Add(Ui.Card(u, new Thickness(0, 12, 0, 0)));

            // Kernel modules
            var k = new StackPanel();
            var khead = new DockPanel();
            var kcaps = Ui.Caps("KERNEL MODULES");
            kcaps.VerticalAlignment = VerticalAlignment.Center;
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            _all.Classes.Add("chip");
            tools.Children.Add(_search); tools.Children.Add(_all);
            DockPanel.SetDock(tools, Dock.Right);
            khead.Children.Add(tools); khead.Children.Add(kcaps);
            k.Children.Add(khead);
            _modCount.Margin = new Thickness(0, 8, 0, 8);
            k.Children.Add(_modCount);
            var kh = Row(Ui.Caps("MODULE"), Ui.Caps("VERSION"), Ui.Caps("LICENSE"), Ui.Caps("SIZE"), Ui.Caps("USED BY"));
            kh.Margin = new Thickness(0, 0, 0, 4);
            k.Children.Add(kh);
            k.Children.Add(_modRows);
            _search.TextChanged += (_, _) => BuildModules();
            _all.IsCheckedChanged += (_, _) => BuildModules();
            root.Children.Add(Ui.Card(k, new Thickness(0, 12, 0, 0)));

            // PCI
            var pci = new StackPanel();
            pci.Children.Add(Ui.Caps("PCI DEVICES", new Thickness(0, 0, 0, 8)));
            pci.Children.Add(PciRow(Ui.Caps("SLOT"), Ui.Caps("DEVICE"), Ui.Caps("CLASS"), Ui.Caps("DRIVER")));
            pci.Children.Add(_pciRows);
            root.Children.Add(Ui.Card(pci, new Thickness(0, 12, 0, 0)));

            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
            Ui.WhileShown(this, on => { if (on) { m.Changed += Sync; m.Load(); Sync(); } else m.Changed -= Sync; });
        }

        private void Sync()
        {
            _gpu.Text = _m.Loaded ? _m.GpuName : "Loading…";
            _gpuVer.Text = _m.GpuVersion;
            _kernel.Text = "Kernel " + _m.Kernel;
            _bios.Text = "BIOS  " + _m.Bios;
            _board.Text = "Board  " + _m.Board;
            int third = _m.Modules.Count(x => x.ThirdParty);
            _counts.Text = _m.Loaded ? $"{_m.Modules.Count} modules loaded · {third} third-party · {_m.Pci.Count} PCI devices" : "";
            _check.IsEnabled = !_m.Checking;
            _status.Text = _m.Checking ? "Checking…" : _m.Updates == null ? "" : "Last checked " + _m.LastChecked.ToString("HH:mm", CultureInfo.CurrentCulture);
            BuildUpdates();
            if (_shownModules != _m.Modules.Count) { _shownModules = _m.Modules.Count; BuildModules(); BuildPci(); }
        }

        private void BuildUpdates()
        {
            _updList.Children.Clear();
            var list = _m.Updates;
            _updNote.IsVisible = list != null;
            if (list == null) return;
            _updNote.Text = list.Count == 0 ? (_m.UpdateNote != "" ? _m.UpdateNote : "Everything is up to date.") : (_m.UpdateNote != "" ? _m.UpdateNote : "");
            Ui.Res(_updNote, TextBlock.ForegroundProperty, list.Count == 0 && _m.UpdateNote == "" ? "Good" : "Muted");
            foreach (var it in list)
            {
                var s = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                s.Children.Add(Ui.Text(it.Title, 14));
                if (it.Detail != "") s.Children.Add(Ui.Text(it.Detail, 12, "Muted"));
                var cmd = Ui.Text("$ " + it.Command, 12, "Accent", mono: true);
                cmd.Margin = new Thickness(0, 2, 0, 0);
                s.Children.Add(cmd);
                _updList.Children.Add(s);
            }
        }

        private void BuildModules()
        {
            _modRows.Children.Clear();
            string q = _search.Text?.Trim() ?? "";
            bool all = _all.IsChecked == true;
            var shown = _m.Modules.Where(x => (all || x.ThirdParty) && (q == "" || Has(x.Name, q) || Has(x.Description, q) || Has(x.License, q))).ToList();
            _modCount.Text = !_m.Loaded ? "Loading…"
                : $"{shown.Count} of {_m.Modules.Count} shown" + (all ? "" : " · out-of-tree, DKMS or non-GPL only");
            foreach (var x in shown)
            {
                var name = new StackPanel();
                var nt = DriversPage.Line(14, "Text");
                nt.Text = x.Name;
                name.Children.Add(nt);
                if (x.Description != "") { var d = DriversPage.Line(12, "Muted"); d.Text = x.Description; name.Children.Add(d); }
                if (x.FileName != "") ToolTip.SetTip(name, x.FileName);
                var r = Row(name, Cell(x.Version == "" ? "—" : x.Version, "Text"), Cell(x.License == "" ? "—" : x.License, x.License.Contains("GPL") ? "Muted" : "Violet"),
                            Cell((x.Size / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB", "Muted"), Cell(x.UsedBy.ToString(CultureInfo.CurrentCulture), "Muted"));
                r.Margin = new Thickness(0, 4, 0, 4);
                _modRows.Children.Add(r);
            }
            if (_m.Loaded && shown.Count == 0)
                _modRows.Children.Add(Ui.Text(all || q != "" ? "No modules match." : "No third-party modules loaded. Everything comes from the kernel tree.", 13, "Muted"));
        }

        private void BuildPci()
        {
            _pciRows.Children.Clear();
            foreach (var d in _m.Pci)
            {
                // Bridges often run without a driver; anything else unbound is worth a look.
                bool warn = d.Driver == null && !d.ClassCode.StartsWith("06", StringComparison.Ordinal);
                var r = PciRow(Cell(d.Slot, "Muted"), Cell(d.Name, "Text", false), Cell(ClassName(d.ClassCode), "Muted", false),
                               Cell(d.Driver ?? "no driver", warn ? "Danger" : d.Driver == null ? "Muted" : "Accent"));
                r.Margin = new Thickness(0, 3, 0, 3);
                _pciRows.Children.Add(r);
            }
            if (_m.Loaded && _m.Pci.Count == 0) _pciRows.Children.Add(Ui.Text("n/a: no PCI bus visible", 13, "Muted"));
        }

        private static string ClassName(string code) => (code.Length >= 2 ? code.Substring(0, 2) : "") switch
        {
            "01" => "Storage", "02" => "Network", "03" => "Display", "04" => "Multimedia", "05" => "Memory", "06" => "Bridge",
            "07" => "Communication", "08" => "System", "09" => "Input", "0c" => "Serial bus", "0d" => "Wireless", _ => "Class " + code,
        };

        private static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        private static TextBlock Cell(string s, string brush, bool mono = true)
        {
            var t = DriversPage.Line(12, brush, mono: mono);
            t.Text = s;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        private static Grid Row(params Control[] cells) => Cells(new Grid { ColumnDefinitions = new ColumnDefinitions("*,140,130,80,70") }, cells);
        private static Grid PciRow(params Control[] cells) => Cells(new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,120,140") }, cells);

        private static Grid Cells(Grid g, Control[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i].Margin = new Thickness(0, 0, 12, 0);
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }
    }
}
