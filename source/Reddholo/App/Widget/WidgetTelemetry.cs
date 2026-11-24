using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BcuHolo.Modules.Sensors;

namespace Reddholo.App.Widget
{
    /// <summary>Fixed-size history (oldest first) with min/max since the widget opened. NaN = no reading.</summary>
    internal sealed class Series
    {
        public const int Len = 120;
        private readonly double[] _buf = new double[Len];
        private int _head, _count;
        public double Last = double.NaN, Min = double.NaN, Max = double.NaN;
        public int Count => _count;

        public void Push(double v)
        {
            if (double.IsInfinity(v)) v = double.NaN;
            _buf[_head] = v;
            _head = (_head + 1) % Len;
            if (_count < Len) _count++;
            Last = v;
            if (double.IsNaN(v)) return;
            if (double.IsNaN(Min) || v < Min) Min = v;
            if (double.IsNaN(Max) || v > Max) Max = v;
        }

        /// <summary>i = 0 is the oldest sample still held.</summary>
        public double this[int i] => _buf[(_head - _count + i + Len) % Len];

        /// <summary>Min/max of the samples currently held (NaN if none).</summary>
        public void Range(out double lo, out double hi)
        {
            lo = double.NaN; hi = double.NaN;
            for (int i = 0; i < _count; i++)
            {
                double v = this[i];
                if (double.IsNaN(v)) continue;
                if (double.IsNaN(lo) || v < lo) lo = v;
                if (double.IsNaN(hi) || v > hi) hi = v;
            }
        }
    }

    /// <summary>One physical core from /sys/devices/system/cpu topology.</summary>
    internal sealed class CoreInfo
    {
        public int Package, CoreId;
        public string Group = "";  // "P", "E" (hybrid Intel) or ""
        public string Label = "";  // "P3", "E5", "C2"
        public int[] Cpus;         // logical cpu numbers (threads)
        public double[] Load;      // per thread, %
        public double Temp, Clock;
        public int Threads => Cpus.Length;
    }

    /// <summary>
    /// Everything the widget shows, sampled once per tick off the UI thread: SensorHub.Snapshot, GpuModel.Current and
    /// /proc + /sys counters. Read-only. The window never reads these fields while Sample runs (it waits for it).
    /// </summary>
    internal sealed class Telemetry
    {
        public readonly Series CpuLoad = new(), GpuLoad = new(), CpuTemp = new(), GpuTemp = new(),
            CpuPower = new(), GpuPower = new(), TotalPower = new(), Vcore = new(), NetDown = new(), NetUp = new();

        public double CpuClockMax = double.NaN, CpuClockAvg = double.NaN, CpuClockPeak = double.NaN;
        public double VcorePeak = double.NaN;
        public string VcoreSource = "";
        public string CpuName = "", GpuName = "";

        public readonly List<CoreInfo> Cores = new();
        public string Topology = "";

        public bool GpuOk;
        public int[] GpuFans = new int[0];
        public double GpuCoreMHz = double.NaN, GpuMemMHz = double.NaN, GpuMaxCoreMHz = double.NaN,
            GpuMaxMemMHz = double.NaN, GpuLimitW = double.NaN, VramUsedGB = double.NaN, VramTotalGB = double.NaN;
        public double RamUsedGB = double.NaN, RamTotalGB = double.NaN;

        public readonly List<KeyValuePair<string, double>> BoardFans = new();
        public string DriveName = "";
        public double DriveTemp = double.NaN;
        public double DiskRead = double.NaN, DiskWrite = double.NaN; // bytes/s
        public int HwErrBoot = -1, HwErr30d = -1;
        public readonly uint Microcode;
        public readonly bool RaptorLake;
        public TimeSpan Uptime;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _lastT = double.NaN;
        private Dictionary<int, (ulong Idle, ulong Total)> _lastCpu = new();
        private (ulong Rx, ulong Tx)? _lastNet;
        private (ulong Rd, ulong Wr)? _lastDisk;
        private long _lastEnergy = -1;
        private string _gpuRaw;
        private int _ticks;
        private string[] _netIfaces = new string[0], _disks;
        private volatile bool _wantNet, _wantDisk, _wantErr;
        private long _errAt = long.MinValue;
        private int _errRunning;

        public Telemetry()
        {
            SelfCheck();
            try
            {
                string name = "";
                foreach (var l in File.ReadLines("/proc/cpuinfo"))
                {
                    if (l.StartsWith("model name") && name == "") name = l.Split(':', 2)[1].Trim();
                    else if (l.StartsWith("microcode")) { Microcode = Convert.ToUInt32(l.Split(':', 2)[1].Trim(), 16); break; }
                }
                if (Microcode == uint.MaxValue) Microcode = 0; // VMs (WSL, Hyper-V) hide the real revision
                CpuName = ShortName(name);
                RaptorLake = HealthInfo.IsRaptorLakeDesktop(name);
            }
            catch (Exception ex) { Trace.WriteLine("Widget cpuinfo: " + ex.Message); }
        }

        /// <summary>Which I/O values the current layout shows; the rest are not read. Called on the UI thread.</summary>
        public void WantIo(bool net, bool disk, bool hwErrors) { _wantNet = net; _wantDisk = disk; _wantErr = hwErrors; }

        public void Sample()
        {
            double now = _sw.Elapsed.TotalSeconds, dt = now - _lastT;
            _lastT = now;
            if (!(dt > 0.2)) dt = double.NaN; // first tick: rates need two samples
            Uptime = LinuxInfo.Uptime();

            var all = SensorHub.Snapshot;
            double cpuLoad = double.NaN, cpuCoreV = double.NaN, boardV = double.NaN;
            string boardVName = "";
            BoardFans.Clear();
            var coreTemps = new Dictionary<(int, int), double>();
            foreach (var e in all)
            {
                var r = e?.Reading;
                if (r == null || r.Name == null || r.Unit == null) continue;
                string n = r.Name;
                double v = r.Value;
                switch (r.Unit)
                {
                    case "%":
                        if (n == "CPU Total") cpuLoad = v;
                        break;
                    case "°C":
                        // coretemp: "Core N" with N = topology core_id; a second socket's device is "coretemp #2".
                        if (r.Group != null && r.Group.StartsWith("coretemp", StringComparison.Ordinal) && n.StartsWith("Core ", StringComparison.Ordinal)
                            && int.TryParse(n.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && v > 0)
                        {
                            int pkg = r.Group.Length > 10 && int.TryParse(r.Group.AsSpan(10), out int k) ? k - 1 : 0;
                            coreTemps[(pkg, id)] = v;
                        }
                        break;
                    case "V":
                        if (n == "CPU Core") { if (v > 0) cpuCoreV = v; }
                        else if ((n == "Vcore" || n == "CPU Vcore") && v > 0) { boardV = v; boardVName = r.Group + " " + n; }
                        break;
                    case "RPM":
                        if (v > 0 && !(r.Group != null && (r.Group.StartsWith("nvme", StringComparison.Ordinal) || r.Group.StartsWith("drivetemp", StringComparison.Ordinal))))
                            BoardFans.Add(new KeyValuePair<string, double>(n, v));
                        break;
                }
            }
            double cpuTemp = SensorsPage.CpuPackageTemp(all)?.Reading.Value ?? double.NaN;
            double cpuPow = SensorsPage.CpuPackagePower(all)?.Reading.Value ?? RaplWatts(dt);

            SampleCores(coreTemps);

            double vcore = !double.IsNaN(cpuCoreV) ? cpuCoreV : boardV;
            VcoreSource = !double.IsNaN(cpuCoreV) ? "hwmon \"CPU Core\"" : !double.IsNaN(boardV) ? boardVName : "";
            double? peak = SensorsPage.PeakVcore(all);
            VcorePeak = peak.HasValue && peak.Value > 0 ? peak.Value : double.NaN;

            // Stable fan order: "fan2" before "fan10".
            BoardFans.Sort((a, b) => a.Key.Length != b.Key.Length ? a.Key.Length.CompareTo(b.Key.Length) : string.CompareOrdinal(a.Key, b.Key));

            var d = SensorsPage.HottestDrive(all);
            DriveTemp = d == null ? double.NaN : d.Reading.Value;
            DriveName = d == null ? "" : d.Reading.Group ?? "";

            var g = GpuModel.Current;
            GpuOk = g != null && g.Available;
            double gTemp = double.NaN, gLoad = double.NaN, gPow = double.NaN;
            if (GpuOk)
            {
                gTemp = g.Temp >= 0 ? g.Temp : double.NaN;
                gLoad = g.Load >= 0 ? g.Load : double.NaN;
                gPow = g.PowerW > 0 ? g.PowerW : double.NaN;
                GpuLimitW = g.LimitW > 0 ? g.LimitW : double.NaN;
                GpuFans = g.Fans ?? new int[0];
                GpuCoreMHz = g.CoreMHz > 0 ? g.CoreMHz : double.NaN;
                GpuMemMHz = g.MemMHz > 0 ? g.MemMHz : double.NaN;
                GpuMaxCoreMHz = g.MaxCoreMHz > 0 ? g.MaxCoreMHz : double.NaN;
                GpuMaxMemMHz = g.MaxMemMHz > 0 ? g.MaxMemMHz : double.NaN;
                VramUsedGB = g.VramUsedMB >= 0 && g.VramTotalMB > 0 ? g.VramUsedMB / 1024.0 : double.NaN;
                VramTotalGB = g.VramTotalMB > 0 ? g.VramTotalMB / 1024.0 : double.NaN;
                if (g.Name != _gpuRaw) { _gpuRaw = g.Name; GpuName = ShortName(g.Name ?? ""); }
            }
            else
            {
                GpuLimitW = GpuCoreMHz = GpuMemMHz = GpuMaxCoreMHz = GpuMaxMemMHz = VramUsedGB = VramTotalGB = double.NaN;
                GpuFans = new int[0];
            }

            var (total, avail) = LinuxInfo.Memory();
            RamTotalGB = total > 0 ? total / 1073741824.0 : double.NaN;
            RamUsedGB = total > 0 ? (total - Math.Min(avail, total)) / 1073741824.0 : double.NaN;

            double down = double.NaN, up = double.NaN;
            if (!_wantNet) _lastNet = null; // no stale delta on re-enable
            else
            {
                if (_ticks % 30 == 0 || _netIfaces.Length == 0) _netIfaces = DefaultRouteIfaces(Read("/proc/net/route"), Read("/proc/net/ipv6_route"));
                var cur = NetBytes(Read("/proc/net/dev"), _netIfaces);
                if (cur.HasValue && _lastNet.HasValue && !double.IsNaN(dt) && cur.Value.Rx >= _lastNet.Value.Rx && cur.Value.Tx >= _lastNet.Value.Tx)
                {
                    down = (cur.Value.Rx - _lastNet.Value.Rx) * 8.0 / dt;
                    up = (cur.Value.Tx - _lastNet.Value.Tx) * 8.0 / dt;
                }
                _lastNet = cur;
            }

            DiskRead = DiskWrite = double.NaN;
            if (!_wantDisk) _lastDisk = null;
            else
            {
                _disks ??= PhysicalDisks();
                var cur = DiskSectors(Read("/proc/diskstats"), _disks);
                if (cur.HasValue && _lastDisk.HasValue && !double.IsNaN(dt) && cur.Value.Rd >= _lastDisk.Value.Rd && cur.Value.Wr >= _lastDisk.Value.Wr)
                {
                    DiskRead = (cur.Value.Rd - _lastDisk.Value.Rd) * 512.0 / dt; // diskstats sectors are always 512 bytes
                    DiskWrite = (cur.Value.Wr - _lastDisk.Value.Wr) * 512.0 / dt;
                }
                _lastDisk = cur;
            }

            if (!_wantErr) _errAt = long.MinValue;
            else if (_ticks - _errAt >= 60) { _errAt = _ticks; CountHwErrors(); }
            _ticks++;

            CpuLoad.Push(cpuLoad);
            CpuTemp.Push(cpuTemp);
            CpuPower.Push(cpuPow);
            GpuLoad.Push(gLoad);
            GpuTemp.Push(gTemp);
            GpuPower.Push(gPow);
            TotalPower.Push(double.IsNaN(cpuPow) && double.IsNaN(gPow) ? double.NaN : (double.IsNaN(cpuPow) ? 0 : cpuPow) + (double.IsNaN(gPow) ? 0 : gPow));
            Vcore.Push(vcore);
            NetDown.Push(down);
            NetUp.Push(up);
        }

        // ---------------- CPU cores ----------------

        /// <summary>Per-thread load from /proc/stat, clocks from cpufreq (or /proc/cpuinfo), temps from coretemp.</summary>
        private void SampleCores(Dictionary<(int, int), double> temps)
        {
            var stat = CpuStat(Read("/proc/stat"));
            if (stat.Count != _lastCpu.Count || Cores.Count == 0) BuildTopology(stat.Keys);
            var mhz = CpuMHz(stat.Keys);
            double clkMax = double.NaN, clkSum = 0;
            int clkN = 0;
            foreach (var c in Cores)
            {
                c.Clock = double.NaN;
                for (int i = 0; i < c.Cpus.Length; i++)
                {
                    int cpu = c.Cpus[i];
                    c.Load[i] = stat.TryGetValue(cpu, out var s) && _lastCpu.TryGetValue(cpu, out var p) && s.Total > p.Total
                        ? Math.Clamp(100.0 * (1 - (double)(s.Idle - Math.Min(s.Idle, p.Idle)) / (s.Total - p.Total)), 0, 100) : double.NaN;
                    if (mhz.TryGetValue(cpu, out var f) && (double.IsNaN(c.Clock) || f > c.Clock)) c.Clock = f;
                }
                c.Temp = temps.TryGetValue((c.Package, c.CoreId), out var t) ? t : double.NaN;
                if (!double.IsNaN(c.Clock)) { clkSum += c.Clock; clkN++; if (double.IsNaN(clkMax) || c.Clock > clkMax) clkMax = c.Clock; }
            }
            _lastCpu = stat;
            CpuClockMax = clkMax;
            CpuClockAvg = clkN > 0 ? clkSum / clkN : double.NaN;
            if (!double.IsNaN(clkMax) && (double.IsNaN(CpuClockPeak) || clkMax > CpuClockPeak)) CpuClockPeak = clkMax;
        }

        /// <summary>Groups logical cpus into cores by (package, core_id); hybrid Intel lists P and E cpus under /sys/devices/cpu_core|cpu_atom.</summary>
        private void BuildTopology(IEnumerable<int> cpus)
        {
            var p = new HashSet<int>(CpuList(Read("/sys/devices/cpu_core/cpus")));
            var e = new HashSet<int>(CpuList(Read("/sys/devices/cpu_atom/cpus")));
            var map = new Dictionary<(int, int), List<int>>();
            foreach (int cpu in cpus.OrderBy(c => c))
            {
                string topo = $"/sys/devices/system/cpu/cpu{cpu}/topology/";
                int pkg = Int(Read(topo + "physical_package_id"), 0), core = Int(Read(topo + "core_id"), cpu);
                if (!map.TryGetValue((pkg, core), out var list)) map[(pkg, core)] = list = new List<int>();
                list.Add(cpu);
            }
            bool hybrid = p.Count > 0 && e.Count > 0;
            Cores.Clear();
            foreach (var kv in map)
                Cores.Add(new CoreInfo
                {
                    Package = kv.Key.Item1, CoreId = kv.Key.Item2, Cpus = kv.Value.ToArray(), Load = new double[kv.Value.Count],
                    Group = !hybrid ? "" : p.Contains(kv.Value[0]) ? "P" : "E", Temp = double.NaN, Clock = double.NaN,
                });
            Cores.Sort((a, b) => a.Group != b.Group ? (a.Group == "P" ? -1 : 1) : a.Cpus[0].CompareTo(b.Cpus[0]));
            int np = 0, ne = 0, no = 0;
            foreach (var c in Cores) c.Label = c.Group == "P" ? "P" + (++np) : c.Group == "E" ? "E" + (++ne) : "C" + (++no);
            Topology = string.Join(",", Cores.Select(c => c.Group + c.Threads));
        }

        private static Dictionary<int, double> CpuMHz(IEnumerable<int> cpus)
        {
            var mhz = new Dictionary<int, double>();
            foreach (int cpu in cpus)
            {
                var khz = Read($"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_cur_freq");
                if (khz != null && double.TryParse(khz, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) && k > 0) mhz[cpu] = k / 1000;
            }
            if (mhz.Count > 0) return mhz;
            // No cpufreq (VMs, WSL): /proc/cpuinfo "cpu MHz" per processor.
            int cur = -1;
            try
            {
                foreach (var l in File.ReadLines("/proc/cpuinfo"))
                {
                    if (l.StartsWith("processor")) cur = Int(l.Split(':', 2)[1], -1);
                    else if (l.StartsWith("cpu MHz") && cur >= 0 && double.TryParse(l.Split(':', 2)[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0) mhz[cur] = m;
                }
            }
            catch (Exception) { }
            return mhz;
        }

        // ---------------- power, errors ----------------

        /// <summary>
        /// Package watts from the RAPL energy counter when hwmon has no package power sensor. energy_uj is root-only on most
        /// distros since 5.10 (PLATYPUS), so this is NaN for a normal user.
        /// </summary>
        private double RaplWatts(double dt)
        {
            const string Dir = "/sys/class/powercap/intel-rapl:0/";
            var s = Read(Dir + "energy_uj");
            if (s == null || !long.TryParse(s, out long uj)) { _lastEnergy = -1; return double.NaN; }
            long last = _lastEnergy;
            _lastEnergy = uj;
            if (last < 0 || double.IsNaN(dt)) return double.NaN;
            long delta = uj - last;
            if (delta < 0 && long.TryParse(Read(Dir + "max_energy_range_uj"), out long range)) delta += range; // counter wrapped
            return delta >= 0 ? delta / 1e6 / dt : double.NaN;
        }

        /// <summary>
        /// Linux's WHEA: machine-check / hardware-error lines in the kernel journal, this boot and last 30 days. journalctl is
        /// slow, so it runs on its own task and the result lands on a later tick. -1 = no journal access.
        /// </summary>
        private void CountHwErrors()
        {
            if (Interlocked.Exchange(ref _errRunning, 1) == 1) return;
            Task.Run(() =>
            {
                try
                {
                    // -q hides the access hint, so no kernel line at all this boot means no journal access.
                    HwErrBoot = JournalLines(false, "-b", "-n", "1") < 1 ? -1 : JournalLines(true, "-b");
                    HwErr30d = HwErrBoot < 0 ? -1 : JournalLines(true, "--since", "-30d");
                }
                finally { _errRunning = 0; }
            });
        }

        private static int JournalLines(bool grep, params string[] range)
        {
            try
            {
                var psi = new ProcessStartInfo("journalctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { "-k", "-q", "--no-pager" }.Concat(range).Concat(grep ? new[] { "--grep", "Hardware Error|mce: \\[" } : Array.Empty<string>())) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                var err = p.StandardError.ReadToEndAsync();
                int n = 0;
                while (p.StandardOutput.ReadLine() != null) n++;
                if (!p.WaitForExit(10000)) { p.Kill(); return -1; }
                // journalctl exits 1 when --grep matches nothing; an access problem shows on stderr instead.
                return err.Result.Contains("No journal files") || err.Result.Contains("ermission") ? -1 : n;
            }
            catch (Exception) { return -1; }
        }

        // ---------------- parsers (pure; checked in SelfCheck) ----------------

        /// <summary>"cpuN" lines of /proc/stat → (idle+iowait, total of user..steal) jiffies per logical cpu.</summary>
        internal static Dictionary<int, (ulong Idle, ulong Total)> CpuStat(string text)
        {
            var d = new Dictionary<int, (ulong, ulong)>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal) || line.Length < 4 || !char.IsDigit(line[3])) continue;
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || !int.TryParse(f[0].AsSpan(3), out int cpu)) continue;
                ulong total = 0, idle = 0;
                for (int i = 1; i < Math.Min(9, f.Length); i++)
                {
                    if (!ulong.TryParse(f[i], out var v)) break;
                    total += v;
                    if (i == 4 || i == 5) idle += v;
                }
                d[cpu] = (idle, total);
            }
            return d;
        }

        /// <summary>Interfaces carrying a default route (IPv4 destination 00000000 or IPv6 ::/0), except lo.</summary>
        internal static string[] DefaultRouteIfaces(string route, string route6)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var l in (route ?? "").Split('\n').Skip(1))
            {
                var f = l.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length > 1 && f[1] == "00000000") set.Add(f[0]);
            }
            foreach (var l in (route6 ?? "").Split('\n'))
            {
                var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 10 && f[0] == new string('0', 32) && f[1] == "00") set.Add(f[9]);
            }
            set.Remove("lo");
            return set.ToArray();
        }

        /// <summary>Summed rx/tx bytes from /proc/net/dev for <paramref name="ifaces"/> (all but lo if empty); null if unreadable.</summary>
        internal static (ulong Rx, ulong Tx)? NetBytes(string text, string[] ifaces)
        {
            if (text == null) return null;
            ulong rx = 0, tx = 0;
            bool any = false;
            foreach (var l in text.Split('\n'))
            {
                int colon = l.IndexOf(':');
                if (colon < 0) continue;
                string name = l.Substring(0, colon).Trim();
                if (name == "lo" || (ifaces.Length > 0 && Array.IndexOf(ifaces, name) < 0)) continue;
                var f = l.Substring(colon + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 9 || !ulong.TryParse(f[0], out var r) || !ulong.TryParse(f[8], out var t)) continue;
                rx += r; tx += t; any = true;
            }
            return any ? (rx, tx) : null;
        }

        /// <summary>Summed sectors read/written from /proc/diskstats for the given whole disks; null if none found.</summary>
        internal static (ulong Rd, ulong Wr)? DiskSectors(string text, string[] disks)
        {
            if (text == null) return null;
            ulong rd = 0, wr = 0;
            bool any = false;
            foreach (var l in text.Split('\n'))
            {
                var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10 || Array.IndexOf(disks, f[2]) < 0) continue;
                if (!ulong.TryParse(f[5], out var r) || !ulong.TryParse(f[9], out var w)) continue;
                rd += r; wr += w; any = true;
            }
            return any ? (rd, wr) : null;
        }

        /// <summary>"0-3,8,10-11" → 0,1,2,3,8,10,11.</summary>
        internal static IEnumerable<int> CpuList(string s)
        {
            foreach (var part in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ab = part.Split('-');
                if (!int.TryParse(ab[0], out int a)) continue;
                int b = ab.Length > 1 && int.TryParse(ab[1], out int bb) ? bb : a;
                for (int i = a; i <= b; i++) yield return i;
            }
        }

        /// <summary>Whole physical disks: /sys/block entries backed by a device (skips loop, ram, zram, dm-*, md*).</summary>
        private static string[] PhysicalDisks()
        {
            try { return Directory.EnumerateDirectories("/sys/block").Where(d => Directory.Exists(d + "/device")).Select(Path.GetFileName).ToArray(); }
            catch (Exception) { return new string[0]; }
        }

        private static string Read(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return null; } // missing, permission, EIO
        }

        private static int Int(string s, int fallback) => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static string ShortName(string s)
        {
            int gen = s.IndexOf(" Gen ", StringComparison.Ordinal); // "13th Gen Intel Core i7-13700K"
            if (gen >= 0 && gen < 6) s = s.Substring(gen + 5);
            return s.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "").Split('@')[0].Replace("Intel Core ", "").Replace("Intel ", "")
                    .Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "").Replace("AMD Ryzen ", "Ryzen ").Replace("  ", " ").Trim();
        }

        /// <summary>Parser checks on sample text (Debug builds, once per widget open; the screenshot harness calls it too).</summary>
        [Conditional("DEBUG")]
        internal static void SelfCheck()
        {
            void Ok(bool c, string what) { if (!c) throw new InvalidOperationException("Widget parser self-check failed: " + what); }

            var st = CpuStat("cpu  10 0 10 80 0 0 0 0 0 0\ncpu0 1 2 3 4 5 6 7 8 9 10\ncpu11 5 0 5 90 0 0 0 0\nintr 1 2\n");
            Ok(st.Count == 2 && st[0] == (9UL, 36UL) && st[11] == (90UL, 100UL), "proc/stat");

            const string Dev = "Inter-|   Receive |  Transmit\n face |bytes    packets errs drop fifo frame compressed multicast|bytes\n" +
                               "    lo:     705       4    0    0    0     0          0         0      705       4    0    0    0     0       0          0\n" +
                               "  eth0: 8005249706 2291036    0    0    0     0          0     10643 352219581  722847    0    0    0     0       0          0\n" +
                               " wlan0:100 1 0 0 0 0 0 0 50 1 0 0 0 0 0 0\n";
            Ok(NetBytes(Dev, new[] { "eth0" }) == (8005249706UL, 352219581UL), "net/dev eth0");
            Ok(NetBytes(Dev, new string[0]) == (8005249806UL, 352219631UL), "net/dev all but lo");
            Ok(NetBytes(Dev, new[] { "nope" }) == null, "net/dev missing iface");

            const string Route = "Iface\tDestination\tGateway \tFlags\n eth0\t00000000\t016012AC\t0003\neth0\t006012AC\t00000000\t0001\n";
            const string Route6 = "00000000000000000000000000000000 00 00000000000000000000000000000000 00 fe800000000000000000000000000001 00000400 00000001 00000000 00000003 wlan0\n" +
                                  "fe800000000000000000000000000000 40 00000000000000000000000000000000 00 00000000000000000000000000000000 00000100 00000001 00000000 00000001 eth0\n";
            Ok(string.Join(",", DefaultRouteIfaces(Route, Route6)) == "eth0,wlan0", "routes");

            const string Disk = "   7       0 loop0 10 0 999 0 0 0 0 0 0 0 0\n   8       0 sda 100 5 2000 30 40 6 3000 70 0 80 90\n   8       1 sda1 50 0 1000 0 20 0 1500 0 0 0 0\n 259 0 nvme0n1 1 0 8 0 2 0 16 0 0 0 0\n";
            Ok(DiskSectors(Disk, new[] { "sda", "nvme0n1" }) == (2008UL, 3016UL), "diskstats");

            Ok(string.Join(",", CpuList("0-3,8,10-11\n")) == "0,1,2,3,8,10,11", "cpu list");
            Ok(ShortName("13th Gen Intel(R) Core(TM) i7-13700K") == "i7-13700K", "short name");
        }
    }
}
