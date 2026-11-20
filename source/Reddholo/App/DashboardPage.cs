using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using BcuHolo.Modules.Gpu;
using BcuHolo.Modules.Sensors;

namespace Reddholo.App
{
    /// <summary>Home page: live CPU, GPU, memory; one-off CPU health and storage; then the other pages' tiles. Port of the WPF DashboardView.</summary>
    internal sealed class DashboardPage : IPage
    {
        public string Id => "dashboard";
        public string Title => "Dashboard";
        public string Icon => "M3,3 H10 V12 H3 Z M14,3 H21 V8 H14 Z M14,12 H21 V21 H14 Z M3,16 H10 V21 H3 Z";
        public int Order => 0;
        public Control CreateView() => new DashboardView(MainWindow.Pages.Where(p => p != this));

        internal static string Size(double bytes) =>
            bytes >= 1L << 40 ? $"{bytes / (1L << 40):0.0} TB" : bytes >= 1L << 30 ? $"{bytes / (1L << 30):0.0} GB" : $"{bytes / (1L << 20):0} MB";
    }

    /// <summary>Linux readers for the dashboard (all /proc and /sys, no tools needed).</summary>
    internal static class LinuxInfo
    {
        /// <summary>(idle, total) jiffies from the first line of /proc/stat.</summary>
        public static (ulong idle, ulong total) CpuTimes()
        {
            try
            {
                var f = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
                ulong total = 0;
                for (int i = 0; i < Math.Min(8, f.Length); i++) total += f[i]; // user..steal; guest is already in user
                return (f[3] + (f.Length > 4 ? f[4] : 0), total);                   // idle + iowait
            }
            catch (Exception) { return (0, 0); }
        }

        /// <summary>MemTotal and MemAvailable in bytes.</summary>
        public static (double total, double avail) Memory()
        {
            double total = 0, avail = 0;
            try
            {
                foreach (var l in File.ReadLines("/proc/meminfo"))
                {
                    if (l.StartsWith("MemTotal:")) total = Kb(l);
                    else if (l.StartsWith("MemAvailable:")) { avail = Kb(l); break; }
                }
            }
            catch (Exception) { }
            return (total, avail);
        }

        private static double Kb(string line) => double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;

        public static TimeSpan Uptime()
        {
            try { return TimeSpan.FromSeconds(double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture)); }
            catch (Exception) { return TimeSpan.FromMilliseconds(Environment.TickCount64); }
        }

        /// <summary>Top 3 programs by resident memory, summing processes with the same name; and the process count.</summary>
        public static (List<(string Name, string Size)>, int) TopProcesses()
        {
            var sums = new Dictionary<string, double>();
            int n = 0;
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var pid = System.IO.Path.GetFileName(dir);
                if (!char.IsDigit(pid[0])) continue;
                n++;
                try
                {
                    string name = null; double rss = 0;
                    foreach (var l in File.ReadLines(dir + "/status"))
                    {
                        if (l.StartsWith("Name:")) name = l.Substring(5).Trim();
                        else if (l.StartsWith("VmRSS:")) { rss = Kb(l); break; }
                    }
                    if (name != null && rss > 0) sums[name] = sums.GetValueOrDefault(name) + rss;
                }
                catch (Exception) { /* exited, or not ours to read */ }
            }
            return (sums.OrderByDescending(kv => kv.Value).Take(3).Select(kv => (kv.Key, DashboardPage.Size(kv.Value))).ToList(), n);
        }

        // Real disks only: skips proc, tmpfs, overlay, squashfs (snaps), WSL's 9p shares and the like.
        private static readonly HashSet<string> DiskFs = new HashSet<string> { "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "ntfs", "ntfs3", "fuseblk", "vfat", "exfat", "jfs", "reiserfs", "bcachefs" };

        public static List<DriveInfo> Drives()
        {
            var seen = new HashSet<(long, long)>();
            var list = new List<DriveInfo>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || !DiskFs.Contains(d.DriveFormat) || d.TotalSize < (1L << 30)) continue;
                    if (d.Name.StartsWith("/boot") || d.Name.StartsWith("/snap")) continue;
                    if (!seen.Add((d.TotalSize, d.TotalFreeSpace))) continue; // btrfs subvolumes and bind mounts of the same disk
                    list.Add(d);
                }
                catch (Exception) { }
            }
            return list;
        }
    }

    internal sealed class HealthInfo
    {
        public string CpuShort, Bios = "unknown";
        public uint Microcode;
        public bool RaptorLake;
        public int HwErrors = -1, Crashes = -1, RamGb;

        public static HealthInfo Read()
        {
            var h = new HealthInfo();
            try
            {
                string name = "";
                foreach (var l in File.ReadLines("/proc/cpuinfo"))
                {
                    if (l.StartsWith("model name") && name == "") name = l.Split(':', 2)[1].Trim();
                    else if (l.StartsWith("microcode")) { h.Microcode = Convert.ToUInt32(l.Split(':', 2)[1].Trim(), 16); break; }
                }
                if (h.Microcode == uint.MaxValue) h.Microcode = 0; // VMs (WSL, Hyper-V) hide the real revision
                h.CpuShort = name.Replace("(R)", "").Replace("(TM)", "").Replace("13th Gen ", "").Replace("14th Gen ", "").Replace("Intel Core", "Intel")
                                 .Replace(" CPU", "").Split('@')[0].Trim();
                h.RaptorLake = IsRaptorLakeDesktop(name);
            }
            catch (Exception ex) { Trace.WriteLine("CPU info failed: " + ex.Message); }

            var maker = Dmi("board_vendor").Split(' ')[0];
            var bios = Dmi("bios_version");
            if (bios != "") h.Bios = $"{maker} {bios}".Trim();
            h.RamGb = (int)Math.Round(LinuxInfo.Memory().total / (1L << 30));

            // Machine-check errors in the kernel log are Linux's WHEA. Needs journal access (adm/systemd-journal group); -1 = can't tell.
            h.HwErrors = CountLines("journalctl", "-k", "-q", "--no-pager", "--since", "-30d", "--grep", "Hardware Error|mce: \\[");
            // Kernel crashes leave dumps in pstore, or a timestamped folder from kdump in /var/crash
            // (the *.crash files there are apport's app crash reports, not kernel crashes).
            var since = DateTime.UtcNow.AddDays(-30);
            int Count(IEnumerable<string> e) { try { return e.Count(f => File.GetLastWriteTimeUtc(f) > since); } catch (Exception) { return 0; } }
            h.Crashes = new[] { "/var/lib/systemd/pstore", "/sys/fs/pstore" }.Where(Directory.Exists).Sum(d => Count(Directory.EnumerateFileSystemEntries(d)))
                      + (Directory.Exists("/var/crash") ? Count(Directory.EnumerateDirectories("/var/crash")) : 0);
            return h;
        }

        /// <summary>
        /// 13th/14th-gen desktop Core i5/i7/i9 (Raptor Lake / Refresh), the chips with Intel's voltage issue. Linux names them
        /// "Intel(R) Core(TM) i7-14700F" (no "14th Gen"), so go by model number; mobile H/HX/U/P parts aren't affected.
        /// </summary>
        internal static bool IsRaptorLakeDesktop(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\bi[579]-1[34]\d{3}([A-Z]*)\b");
            return m.Success && !m.Groups[1].Value.Contains('H') && !m.Groups[1].Value.Contains('U') && !m.Groups[1].Value.Contains('P');
        }

        private static string Dmi(string f)
        {
            try { return File.ReadAllText("/sys/class/dmi/id/" + f).Trim(); } catch (Exception) { return ""; }
        }

        private static int CountLines(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                int n = 0;
                while (p.StandardOutput.ReadLine() != null) n++;
                var err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(10000)) { p.Kill(); return -1; }
                // journalctl exits 1 when --grep matches nothing; an access problem shows on stderr instead.
                return err.Contains("No journal files") || err.Contains("ermission") ? -1 : n;
            }
            catch (Exception) { return -1; }
        }
    }

    internal sealed class DashboardView : UserControl
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly Queue<double> _cpuHist = new(), _gpuHist = new();
        private (ulong idle, ulong total) _lastCpu;
        private bool _polling, _staticLoaded;
        private int _pollCount;
        private List<(string Name, string Size)> _top = new();
        private int _procCount;
        private string _driver;
        private readonly Action _onSensors;

        private readonly Border _statusChip = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _statusText = Ui.Text("● Checking…", 12, "Good");
        private readonly TextBlock _specText = Ui.Text("", 12);
        // CPU
        private readonly TextBlock _cpuName = Ui.Caps("CPU"), _cpuCores = Ui.Text("", 11), _cpuBig = Big(24);
        private readonly TextBlock _cpuProcs = Val(), _cpuUptime = Val(), _cpuTemp = Val("n/a", "Muted");
        private readonly Arc _cpuArc = Arcs.Make(96, 9, "AccentViolet");
        private readonly Path _cpuSpark = Spark("Accent");
        // GPU
        private readonly TextBlock _gpuName = Ui.Caps("GPU"), _gpuDriver = Ui.Text("", 11), _gpuBig = Big(24);
        private readonly TextBlock _gpuLoad = Val(), _gpuPower = Val(), _gpuFan = Val(), _gpuVram = Val();
        private readonly Arc _gpuArc = Arcs.Make(96, 9, "Good");
        private readonly Path _gpuSpark = Spark("Good");
        // Memory
        private readonly TextBlock _memUsed = Big(34), _memTotal = Ui.Text("", 15, "Muted");
        private readonly ColumnDefinition _memFill = new(new GridLength(0, GridUnitType.Star)), _memRest = new(new GridLength(1, GridUnitType.Star));
        private readonly StackPanel _memTop = new();
        // Health
        private readonly Border _healthChip = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2) };
        private readonly TextBlock _healthChipText = Ui.Text("…", 11, "Good"), _healthNote = Ui.Text("", 13, "Muted");
        private readonly PathIcon _healthIcon = new() { Width = 30, Height = 30, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _hMicro = Val(), _hErrors = Val(), _hCrash = Val(), _hBios = Val();
        // Storage
        private readonly TextBlock _storageCaps = Ui.Caps("STORAGE"), _sysPct = Big(24), _sysRoot = Ui.Text("", 11, "Muted"), _sysFree = Big(28), _sysOf = Ui.Text("", 14, "Muted");
        private readonly Arc _sysRing = new() { Width = 116, Height = 116, StrokeThickness = 12, StartAngle = -90, StrokeLineCap = PenLineCap.Round };
        private readonly UniformGrid _otherDrives = new() { Columns = 2 };

        private const string Check = "M9,16.2 L4.8,12 L3.4,13.4 L9,19 L21,7 L19.6,5.6 Z";
        private const string Warn = "M1,21 H23 L12,2 Z M13,18 H11 V16 H13 Z M13,14 H11 V10 H13 Z";

        public DashboardView(IEnumerable<IPage> pages)
        {
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 16) };
            _statusChip.Child = _statusText;
            Ui.Res(_statusChip, Border.BackgroundProperty, "Good.1A");
            var spec = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4), Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)), Child = _specText };
            status.Children.Add(_statusChip); status.Children.Add(spec);

            var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("1.15*,16,1.15*,16,0.85*,16,*"), Margin = new Thickness(0, 0, 0, 16) };
            Place(row1, CpuTile(), 0); Place(row1, GpuTile(), 2); Place(row1, MemoryTile(), 4); Place(row1, HealthTile(), 6);

            // Storage, then every other page's tile, 3 per row.
            var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -16, 0) };
            tiles.Children.Add(StorageTile());
            foreach (var p in pages)
            {
                Control tile = null;
                try { tile = p.CreateTile(); }
                catch (Exception ex) { Trace.WriteLine($"Tile for {p.Id} failed: {ex}"); }
                if (tile == null) continue;
                if (!tile.Classes.Contains("card")) tile = Ui.Card(tile); // page returned bare content: give it the glass card
                if (!tile.Classes.Contains("tile")) tile.Classes.Add("tile");
                tile.Margin = new Thickness(0, 0, 16, 16);
                tiles.Children.Add(tile);
            }

            var root = new StackPanel();
            root.Children.Add(status); root.Children.Add(row1); root.Children.Add(tiles);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 0, 24), Content = root };

            _timer.Tick += async (_, _) => await PollAsync();
            _onSensors = UpdateCpuTemp;
            Ui.WhileShown(this, async on =>
            {
                _timer.IsEnabled = on;
                GpuModel.Want(on); // the poll reads the GPU model
                if (on) SensorHub.Subscribe(_onSensors); else SensorHub.Unsubscribe(_onSensors);
                if (!on) return;
                _ = PollAsync();
                if (_staticLoaded) return;
                _staticLoaded = true;
                LoadStorage();
                await LoadHealthAsync();
            });
        }

        // ---------- building blocks ----------

        private static void Place(Grid g, Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }

        private static TextBlock Big(double size) => Ui.Text("—", size, weight: FontWeight.Bold);

        private static TextBlock Val(string text = "", string brush = "Text")
        {
            var t = Ui.Text(text, 12, brush, mono: true);
            t.TextWrapping = TextWrapping.NoWrap;
            t.Margin = new Thickness(6, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        /// <summary>Key/value row, 30 px, with a hairline under it unless it is the last.</summary>
        private static Control Row(string key, TextBlock value, bool line = true)
        {
            var d = new DockPanel { Height = 30 };
            DockPanel.SetDock(value, Dock.Right);
            d.Children.Add(value);
            var k = Ui.Text(key, 13, "Muted");
            k.TextWrapping = TextWrapping.NoWrap; k.TextTrimming = TextTrimming.CharacterEllipsis;
            k.VerticalAlignment = VerticalAlignment.Center;
            d.Children.Add(k);
            if (!line) return d;
            var b = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = d };
            Ui.Res(b, Border.BorderBrushProperty, "RowLine");
            return b;
        }

        private static Border Pill(TextBlock t)
        {
            var b = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2), Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)), Child = t };
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }

        private static Border Tile(Control child)
        {
            var b = Ui.Card(child);
            b.Classes.Add("tile");
            return b;
        }

        private static Path Spark(string key)
        {
            var p = new Path { Height = 34, Margin = new Thickness(0, 10, 0, 0), Stretch = Stretch.Fill, StrokeThickness = 1.6 };
            Ui.Res(p, Shape.StrokeProperty, key);
            Ui.Res(p, Shape.FillProperty, key + ".1A");
            return p;
        }

        private static Grid Gauge(Arc arc, TextBlock big, string caption)
        {
            var g = new Grid { Width = 96, Height = 96 };
            g.Children.Add(Arcs.Make(96, 9, "Track"));
            arc.IsVisible = false;
            g.Children.Add(arc);
            var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            big.HorizontalAlignment = HorizontalAlignment.Center;
            var cap = Ui.Text(caption, 11, "Muted");
            cap.HorizontalAlignment = HorizontalAlignment.Center;
            s.Children.Add(big); s.Children.Add(cap);
            g.Children.Add(s);
            return g;
        }

        private static Grid GaugeRow(Grid gauge, Control rows)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), Margin = new Thickness(0, 12, 0, 0) };
            rows.Margin = new Thickness(8, 0, 0, 0);
            rows.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rows, 1);
            g.Children.Add(gauge); g.Children.Add(rows);
            return g;
        }

        private static DockPanel Head(TextBlock caps, Control right)
        {
            var d = new DockPanel();
            if (right != null) d.Children.Add(right);
            caps.TextTrimming = TextTrimming.CharacterEllipsis;
            caps.VerticalAlignment = VerticalAlignment.Center;
            d.Children.Add(caps);
            return d;
        }

        private Control CpuTile()
        {
            var rows = new StackPanel();
            rows.Children.Add(Row("Processes", _cpuProcs));
            rows.Children.Add(Row("Uptime", _cpuUptime));
            rows.Children.Add(Row("CPU temp", _cpuTemp, false));
            var s = new StackPanel();
            s.Children.Add(Head(_cpuName, Pill(_cpuCores)));
            s.Children.Add(GaugeRow(Gauge(_cpuArc, _cpuBig, "load"), rows));
            s.Children.Add(_cpuSpark);
            return Tile(s);
        }

        private Control GpuTile()
        {
            var rows = new StackPanel();
            rows.Children.Add(Row("Load", _gpuLoad));
            rows.Children.Add(Row("Power", _gpuPower));
            rows.Children.Add(Row("Fan", _gpuFan));
            rows.Children.Add(Row("VRAM", _gpuVram, false));
            var s = new StackPanel();
            s.Children.Add(Head(_gpuName, Pill(_gpuDriver)));
            s.Children.Add(GaugeRow(Gauge(_gpuArc, _gpuBig, "core"), rows));
            s.Children.Add(_gpuSpark);
            return Tile(s);
        }

        private Control MemoryTile()
        {
            var s = new StackPanel();
            s.Children.Add(Ui.Caps("MEMORY"));
            var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _memTotal.VerticalAlignment = VerticalAlignment.Bottom;
            _memTotal.Margin = new Thickness(0, 0, 0, 6);
            big.Children.Add(_memUsed); big.Children.Add(_memTotal);
            s.Children.Add(big);
            var bar = new Grid { Height = 10, Margin = new Thickness(0, 12) };
            bar.ColumnDefinitions.Add(_memFill); bar.ColumnDefinitions.Add(_memRest);
            var track = new Border { CornerRadius = new CornerRadius(5) };
            Ui.Res(track, Border.BackgroundProperty, "Track");
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { CornerRadius = new CornerRadius(5) };
            Ui.Res(fill, Border.BackgroundProperty, "AccentViolet");
            bar.Children.Add(track); bar.Children.Add(fill);
            s.Children.Add(bar);
            s.Children.Add(_memTop);
            return Tile(s);
        }

        private Control HealthTile()
        {
            _healthChip.Child = _healthChipText;
            DockPanel.SetDock(_healthChip, Dock.Right);
            Ui.Res(_healthChip, Border.BackgroundProperty, "Good.1F");
            var s = new StackPanel();
            s.Children.Add(Head(Ui.Caps("CPU HEALTH"), _healthChip));
            var note = new DockPanel { Margin = new Thickness(0, 12, 0, 6) };
            _healthIcon.Data = Geometry.Parse(Check);
            Ui.Res(_healthIcon, PathIcon.ForegroundProperty, "Good");
            note.Children.Add(_healthIcon);
            _healthNote.VerticalAlignment = VerticalAlignment.Center;
            note.Children.Add(_healthNote);
            s.Children.Add(note);
            s.Children.Add(Row("Microcode", _hMicro));
            s.Children.Add(Row("Hardware errors · 30 days", _hErrors));
            s.Children.Add(Row("Kernel crashes · 30 days", _hCrash));
            _hBios.TextTrimming = TextTrimming.CharacterEllipsis;
            _hBios.MaxWidth = 170;
            s.Children.Add(Row("BIOS", _hBios, false));
            return Tile(s);
        }

        private Control StorageTile()
        {
            var open = Ui.Button("Open apps", "ghost");
            open.MinHeight = 32; open.Padding = new Thickness(12, 0); open.FontSize = 12;
            open.Click += (_, _) => Shell.Navigate("apps");
            DockPanel.SetDock(open, Dock.Right);
            var s = new StackPanel();
            s.Children.Add(Head(_storageCaps, open));

            var ring = new Grid { Width = 116, Height = 116 };
            var track = new Ellipse { StrokeThickness = 12 };
            Ui.Res(track, Shape.StrokeProperty, "Track");
            Ui.Res(_sysRing, Shape.StrokeProperty, "Accent");
            _sysRing.SweepAngle = 0;
            ring.Children.Add(track); ring.Children.Add(_sysRing);
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            _sysPct.HorizontalAlignment = _sysRoot.HorizontalAlignment = HorizontalAlignment.Center;
            mid.Children.Add(_sysPct); mid.Children.Add(_sysRoot);
            ring.Children.Add(mid);
            var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(_sysFree); right.Children.Add(_sysOf);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 12) };
            Grid.SetColumn(right, 1);
            g.Children.Add(ring); g.Children.Add(right);
            s.Children.Add(g);
            s.Children.Add(_otherDrives);
            var t = Tile(s);
            t.Margin = new Thickness(0, 0, 16, 16);
            return t;
        }

        /// <summary>GPU control tile: fan mode, temp, fan, power, and a small curve preview.</summary>
        internal static Control GpuControlTile()
        {
            var m = GpuPage.Model;
            var mode = Big(30);
            TextBlock temp = Val(), fan = Val(), power = Val();
            var err = Ui.Text("", 13, "Danger");
            var rows = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
            rows.Children.Add(Row("Temp", temp));
            rows.Children.Add(Row("Fan", fan));
            rows.Children.Add(Row("Power / limit", power, false));
            const double PW = 220, PH = 56;
            var line = new Polyline { StrokeThickness = 2, StrokeJoin = PenLineJoin.Round };
            Ui.Res(line, Shape.StrokeProperty, "Accent");
            var preview = new Canvas { Width = PW, Height = PH, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12), IsHitTestVisible = false };
            preview.Children.Add(line);
            var button = Ui.Button("Edit curve", "ghost");
            button.Click += (_, _) => Shell.Navigate("gpu");
            var s = new StackPanel();
            s.Children.Add(Ui.Caps("GPU CONTROL"));
            s.Children.Add(mode); s.Children.Add(err); s.Children.Add(rows); s.Children.Add(preview); s.Children.Add(button);
            string modeKey = null;
            void Refresh()
            {
                if (!m.Available)
                {
                    mode.Text = "—"; err.Text = m.Error ?? "";
                    rows.IsVisible = preview.IsVisible = button.IsVisible = false;
                    return;
                }
                mode.Text = Ui.Mode(m);
                Ui.Ref(mode, TextBlock.ForegroundProperty, m.CurveActive ? "Good" : "Text", ref modeKey);
                temp.Text = m.Temp >= 0 ? $"{m.Temp}°C" : "—";
                fan.Text = Ui.FanText(m);
                power.Text = (double.IsNaN(m.PowerW) ? "—" : $"{m.PowerW:0} W") + " / " + (double.IsNaN(m.LimitW) ? "—" : $"{m.LimitW:0} W");
                line.Points = m.T.Select((t, i) => new Point((t - GpuModel.TempMin) / (GpuModel.TempMax - GpuModel.TempMin) * PW,
                    PH - (m.F[i] - GpuModel.FanMin) / (GpuModel.FanMax - GpuModel.FanMin) * PH)).ToList();
            }
            var tile = Tile(s);
            tile.Margin = new Thickness(0, 0, 16, 16);
            Ui.WhileShown(tile, on => { m.Changed -= Refresh; if (on) { m.Changed += Refresh; Refresh(); } });
            return tile;
        }

        /// <summary>Sensors tile: CPU package temperature and the highlights.</summary>
        internal static Control SensorsTile()
        {
            var temp = Big(34);
            var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            TextBlock pow = Val(), vcore = Val(), drive = Val(), fan = Val();
            Control rPow = Row("Package power", pow), rV = Row("Peak Vcore", vcore), rD = Row("Hottest drive", drive), rF = Row("Fan", fan, false);
            var waiting = Ui.Text("Waiting for sensors. On Linux, CPU readings need the coretemp or k10temp driver.", 12, "Muted");
            foreach (var c in new[] { rPow, rV, rD, rF, (Control)waiting }) { c.IsVisible = false; rows.Children.Add(c); }
            var all = Ui.Button("All sensors", "ghost", new Thickness(0, 12, 0, 0));
            all.Click += (_, _) => Shell.Navigate("sensors");
            var s = new StackPanel();
            s.Children.Add(Ui.Caps("SENSORS")); s.Children.Add(temp); s.Children.Add(Ui.Text("CPU package", 12, "Muted"));
            s.Children.Add(rows); s.Children.Add(all);
            string vKey = null;
            void Set(Control row, TextBlock v, string text) { row.IsVisible = text != null; if (text != null) v.Text = text; }
            void Refresh()
            {
                var a = SensorHub.Snapshot;
                var t = SensorsPage.CpuPackageTemp(a);
                temp.Text = t == null ? "--" : $"{t.Reading.Value:0} °C";
                var p = SensorsPage.CpuPackagePower(a);
                Set(rPow, pow, p == null ? null : SensorsPage.Fmt(p.Reading.Value, "W"));
                var v = SensorsPage.PeakVcore(a);
                Set(rV, vcore, v.HasValue && v.Value > 0 ? SensorsPage.Fmt(v.Value, "V") : null);
                Ui.Ref(vcore, TextBlock.ForegroundProperty, v.HasValue && v.Value > SensorsPage.VcoreWarn ? "Danger" : "Text", ref vKey);
                var d = SensorsPage.HottestDrive(a);
                Set(rD, drive, d == null ? null : SensorsPage.Fmt(d.Reading.Value, "°C"));
                var f = a.Where(e => e.Reading.Unit == "RPM" && e.Reading.Value > 0).OrderByDescending(e => e.Reading.Value).FirstOrDefault();
                Set(rF, fan, f == null ? null : SensorsPage.Fmt(f.Reading.Value, "RPM"));
                waiting.IsVisible = t == null && !rPow.IsVisible && !rV.IsVisible && !rD.IsVisible && !rF.IsVisible;
            }
            var tile = Tile(s);
            tile.Margin = new Thickness(0, 0, 16, 16);
            Ui.WhileShown(tile, on => { if (on) { SensorHub.Subscribe(Refresh); Refresh(); } else SensorHub.Unsubscribe(Refresh); });
            return tile;
        }

        // ---------- live: CPU load, memory, GPU ----------

        private void UpdateCpuTemp()
        {
            var t = SensorsPage.CpuPackageTemp(SensorHub.Snapshot);
            if (t == null) return;
            _cpuTemp.Text = $"{t.Reading.Value:0} °C";
            Ui.Res(_cpuTemp, TextBlock.ForegroundProperty, t.Reading.Value >= 90 ? "Danger" : "Text");
        }

        private async Task PollAsync()
        {
            if (_polling) return;
            _polling = true;
            try
            {
                var now = LinuxInfo.CpuTimes();
                var prev = _lastCpu;
                _lastCpu = now;
                double cpu = prev.total == 0 || now.total <= prev.total ? -1 : 100.0 * (1 - (double)(now.idle - prev.idle) / (now.total - prev.total));
                var mem = LinuxInfo.Memory();
                if (_pollCount++ % 5 == 0) (_top, _procCount) = await Task.Run(LinuxInfo.TopProcesses); // process scan every 10 s

                if (cpu >= 0) { _cpuBig.Text = $"{cpu:0}%"; Arcs.SetFrac(_cpuArc, cpu / 100); Push(_cpuHist, cpu, _cpuSpark); }
                _cpuProcs.Text = _procCount.ToString();
                var up = LinuxInfo.Uptime();
                _cpuUptime.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m";

                if (mem.total > 0)
                {
                    var used = mem.total - mem.avail;
                    _memUsed.Text = (used / (1L << 30)).ToString("0.0");
                    _memTotal.Text = $" / {mem.total / (1L << 30):0} GB";
                    _memFill.Width = new GridLength(used / mem.total, GridUnitType.Star);
                    _memRest.Width = new GridLength(1 - used / mem.total, GridUnitType.Star);
                }
                _memTop.Children.Clear();
                for (int i = 0; i < _top.Count; i++)
                {
                    var name = Val(_top[i].Size);
                    _memTop.Children.Add(Row(_top[i].Name, name, i < _top.Count - 1));
                }

                var g = GpuPage.Model;
                if (g.Available && g.Temp >= 0)
                {
                    _gpuName.Text = g.Name.Replace("NVIDIA ", "").Replace("GeForce ", "").ToUpperInvariant();
                    if (_driver == null) { try { _driver = Nvml.DriverVersion(); } catch (Exception) { _driver = ""; } }
                    _gpuDriver.Text = _driver == "" ? "NVIDIA" : "driver " + _driver;
                    _gpuBig.Text = $"{g.Temp}°";
                    Arcs.SetFrac(_gpuArc, Math.Min(1, g.Temp / 100.0));
                    _gpuLoad.Text = g.Load >= 0 ? $"{g.Load}%" : "—";
                    _gpuPower.Text = $"{g.PowerW:0}/{g.LimitW:0} W";
                    _gpuFan.Text = g.Fans.Length > 0 ? $"{g.Fans.Average():0}%" : "—";
                    _gpuVram.Text = g.VramTotalMB > 0 ? $"{g.VramUsedMB / 1024.0:0.0}/{g.VramTotalMB / 1024.0:0} GB" : "—";
                    if (g.Load >= 0) Push(_gpuHist, g.Load, _gpuSpark);
                }
                else if (_gpuBig.Text == "—")
                {
                    _gpuLoad.Text = g.Error != null ? "no NVIDIA GPU" : "—";
                }
            }
            catch (Exception ex) { Trace.WriteLine("Dashboard poll failed: " + ex); }
            finally { _polling = false; }
        }

        /// <summary>Filled sparkline of the last 60 samples (0..100), newest at the right; Stretch=Fill scales it to the tile.</summary>
        private static void Push(Queue<double> hist, double v, Path path)
        {
            hist.Enqueue(v);
            while (hist.Count > 60) hist.Dequeue();
            const double w = 300, h = 34, step = w / 59;
            var pts = hist.ToArray();
            double x0 = w - (pts.Length - 1) * step;
            var fig = new PathFigure { StartPoint = new Point(x0, h), IsClosed = true, Segments = new PathSegments() };
            for (int i = 0; i < pts.Length; i++)
                fig.Segments.Add(new LineSegment { Point = new Point(x0 + i * step, h - h * Math.Clamp(pts[i], 0, 100) / 100) });
            fig.Segments.Add(new LineSegment { Point = new Point(x0 + (pts.Length - 1) * step, h) });
            // Anchor the full 0..w x 0..h box so Stretch doesn't zoom into a short history.
            var anchor = new PathFigure { StartPoint = new Point(0, 0), IsClosed = false, IsFilled = false, Segments = new PathSegments { new LineSegment { Point = new Point(0, 0) } } };
            var anchor2 = new PathFigure { StartPoint = new Point(w, h), IsClosed = false, IsFilled = false, Segments = new PathSegments { new LineSegment { Point = new Point(w, h) } } };
            path.Data = new PathGeometry { Figures = new PathFigures { anchor, fig, anchor2 } };
        }

        // ---------- one-off: storage and CPU health ----------

        private void LoadStorage()
        {
            var drives = LinuxInfo.Drives();
            var sys = drives.FirstOrDefault(d => d.Name == "/") ?? drives.FirstOrDefault();
            if (sys == null) { _storageCaps.Text = "STORAGE · no disks found"; return; }
            double used = (double)(sys.TotalSize - sys.TotalFreeSpace) / sys.TotalSize;
            _sysRing.SweepAngle = 360 * used;
            _sysPct.Text = $"{used * 100:0}%";
            _sysRoot.Text = sys.Name + " used";
            _sysFree.Text = DashboardPage.Size(sys.TotalFreeSpace);
            _sysOf.Text = $" free of {DashboardPage.Size(sys.TotalSize)}";
            _storageCaps.Text = $"STORAGE · {drives.Count} {(drives.Count == 1 ? "DISK" : "DISKS")}";
            Shell.SetBadge("apps", null);

            foreach (var d in drives.Where(d => d != sys))
            {
                double f = (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                var head = new DockPanel();
                var detail = Ui.Text($"{DashboardPage.Size(d.TotalFreeSpace)} free", 11, "Muted");
                DockPanel.SetDock(detail, Dock.Right);
                head.Children.Add(detail);
                var title = Ui.Text(d.Name, 12, weight: FontWeight.SemiBold);
                title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
                head.Children.Add(title);
                var bar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0), ColumnDefinitions = new ColumnDefinitions($"{f.ToString(CultureInfo.InvariantCulture)}*,{(1 - f).ToString(CultureInfo.InvariantCulture)}*") };
                var track = new Border { CornerRadius = new CornerRadius(2) };
                Ui.Res(track, Border.BackgroundProperty, "Track");
                Grid.SetColumnSpan(track, 2);
                var fill = new Border { CornerRadius = new CornerRadius(2) };
                Ui.Res(fill, Border.BackgroundProperty, f > 0.9 ? "Danger" : "Accent");
                bar.Children.Add(track); bar.Children.Add(fill);
                var s = new StackPanel();
                s.Children.Add(head); s.Children.Add(bar);
                _otherDrives.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 8, 8), Child = s });
            }
        }

        private async Task LoadHealthAsync()
        {
            var h = await Task.Run(HealthInfo.Read);
            var gpu = GpuPage.Model.Available ? GpuPage.Model.Name.Replace("NVIDIA ", "") : null;
            _specText.Text = string.Join(" · ", new[] { h.CpuShort, gpu, h.RamGb > 0 ? $"{h.RamGb} GB" : null }.Where(s => !string.IsNullOrEmpty(s)));
            _cpuName.Text = (h.CpuShort ?? "CPU").ToUpperInvariant();
            _cpuCores.Text = $"{Environment.ProcessorCount} threads";
            _hMicro.Text = h.Microcode == 0 ? "unknown" : $"0x{h.Microcode:X}";
            _hErrors.Text = h.HwErrors < 0 ? "n/a" : h.HwErrors.ToString();
            _hCrash.Text = h.Crashes < 0 ? "n/a" : h.Crashes.ToString();
            _hBios.Text = h.Bios;
            if (h.HwErrors < 0) ToolTip.SetTip(_hErrors, "Reading the kernel log needs the adm or systemd-journal group.");

            const string green = "Good", warn = "Danger";
            void Chip(string label, string key)
            {
                _healthChipText.Text = label;
                Ui.Res(_healthChipText, TextBlock.ForegroundProperty, key);
                Ui.Res(_healthChip, Border.BackgroundProperty, key + ".14");
            }
            if (!h.RaptorLake)
            {
                Chip("Not affected", green);
                _healthNote.Text = "This CPU isn't part of Intel's 13th/14th-gen voltage issue.";
            }
            else if (h.Microcode == 0)
            {
                Chip("Can't check", "Muted");
                Ui.Res(_healthIcon, PathIcon.ForegroundProperty, "Muted");
                _healthNote.Text = "This CPU is in Intel's 13th/14th-gen voltage issue, but its microcode revision is hidden (virtual machine?). Check on the host.";
            }
            else if (h.Microcode >= 0x129)
            {
                Chip("Protected", green);
                _healthNote.Text = "Microcode has Intel's voltage fix. Watching for new errors.";
                _hMicro.Text += " ✓";
                Ui.Res(_hMicro, TextBlock.ForegroundProperty, green);
            }
            else
            {
                Chip("Needs BIOS update", warn);
                _healthNote.Text = "Microcode is older than 0x129: the CPU can keep degrading. Update the BIOS (or the intel-microcode package).";
                _healthIcon.Data = Geometry.Parse(Warn);
                Ui.Res(_healthIcon, PathIcon.ForegroundProperty, warn);
                Ui.Res(_hMicro, TextBlock.ForegroundProperty, warn);
            }
            Ui.Res(_hErrors, TextBlock.ForegroundProperty, h.HwErrors > 0 ? warn : h.HwErrors < 0 ? "Muted" : green);
            Ui.Res(_hCrash, TextBlock.ForegroundProperty, h.Crashes > 0 ? warn : green);

            // The pill must be able to go amber: any hardware error, crash, or unpatched Raptor Lake counts.
            int issues = (h.RaptorLake && h.Microcode != 0 && h.Microcode < 0x129 ? 1 : 0) + (h.HwErrors > 0 ? 1 : 0) + (h.Crashes > 0 ? 1 : 0);
            _statusText.Text = issues == 0 ? "● System healthy"
                : h.Crashes > 0 && issues == 1 ? $"● {h.Crashes} kernel crash(es) in 30 days"
                : $"● {issues} things need attention";
            Ui.Res(_statusText, TextBlock.ForegroundProperty, issues == 0 ? green : warn);
            Ui.Res(_statusChip, Border.BackgroundProperty, issues == 0 ? "Good.1A" : "DangerSoft");
        }
    }
}
