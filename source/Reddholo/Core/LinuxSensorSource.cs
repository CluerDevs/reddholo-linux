using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BcuHolo.Plugins;

namespace BcuHolo.Modules.Sensors
{
    /// <summary>
    /// Linux counterpart of the Windows LibreHardwareMonitor source: kernel hwmon (temps, fans, volts, power) from
    /// /sys/class/hwmon, CPU load from /proc/stat, memory from /proc/meminfo. Read-only, no root needed.
    /// </summary>
    public sealed class LinuxSensorSource : ISensorSource
    {
        private readonly string _root;
        private (long idle, long total) _lastCpu;

        /// <param name="root">Filesystem root; "/" normally, a fake tree in tests.</param>
        public LinuxSensorSource(string root = "/") { _root = root; }

        public string Name => "hwmon";

        public IEnumerable<SensorReading> Read()
        {
            var list = new List<SensorReading>();
            ReadHwmon(list);
            ReadCpuLoad(list);
            ReadMemory(list);
            return list;
        }

        // hwmon attribute prefix → (unit, divisor from the kernel's integer unit). Per kernel Documentation/hwmon/sysfs-interface.
        private static readonly (string prefix, string unit, double div)[] Kinds =
        {
            ("temp", "°C", 1000), ("fan", "RPM", 1), ("in", "V", 1000), ("power", "W", 1_000_000),
        };

        private void ReadHwmon(List<SensorReading> into)
        {
            var dir = Path.Combine(_root, "sys/class/hwmon");
            if (!Directory.Exists(dir)) return; // e.g. WSL, containers
            var seen = new Dictionary<string, int>();
            foreach (var hw in Directory.EnumerateDirectories(dir).OrderBy(Natural, StringComparer.Ordinal))
            {
                string group = Text(Path.Combine(hw, "name")) ?? Path.GetFileName(hw);
                // Dual-socket boards expose two "coretemp" devices with the same core labels: keep them apart.
                seen[group] = seen.TryGetValue(group, out var n) ? n + 1 : 1;
                if (seen[group] > 1) group += " #" + seen[group];
                foreach (var file in Directory.EnumerateFiles(hw).OrderBy(Natural, StringComparer.Ordinal))
                {
                    var fn = Path.GetFileName(file);
                    // power sensors expose *_average on some drivers instead of *_input
                    bool input = fn.EndsWith("_input", StringComparison.Ordinal);
                    if (!input && !(fn.StartsWith("power", StringComparison.Ordinal) && fn.EndsWith("_average", StringComparison.Ordinal))) continue;
                    var stem = fn.Substring(0, fn.LastIndexOf('_'));
                    if (!input && File.Exists(Path.Combine(hw, stem + "_input"))) continue; // both exist: same key twice
                    var kind = Kinds.FirstOrDefault(k => stem.StartsWith(k.prefix, StringComparison.Ordinal) && stem.Length > k.prefix.Length && char.IsDigit(stem[k.prefix.Length]));
                    if (kind.unit == null) continue;
                    if (!long.TryParse(Text(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)) continue; // EIO etc.
                    string label = Text(Path.Combine(hw, stem + "_label")) ?? stem;
                    into.Add(new SensorReading(Name, group, label, raw / kind.div, kind.unit));
                }
            }
        }

        private void ReadCpuLoad(List<SensorReading> into)
        {
            // First line: "cpu  user nice system idle iowait irq softirq steal ..." in jiffies since boot.
            var line = Text(Path.Combine(_root, "proc/stat"))?.Split('\n')[0];
            if (line == null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return;
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
                        .Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            long idle = v[3] + (v.Length > 4 ? v[4] : 0), total = v.Sum();
            var last = _lastCpu;
            _lastCpu = (idle, total);
            if (last.total == 0 || total <= last.total) return; // need two samples for a rate
            double busy = 1.0 - (double)(idle - last.idle) / (total - last.total);
            into.Add(new SensorReading(Name, "CPU", "CPU Total", Math.Clamp(busy * 100, 0, 100), "%"));
        }

        private void ReadMemory(List<SensorReading> into)
        {
            var text = Text(Path.Combine(_root, "proc/meminfo"));
            if (text == null) return;
            double Kb(string key)
            {
                var l = text.Split('\n').FirstOrDefault(x => x.StartsWith(key + ":", StringComparison.Ordinal));
                return l == null ? double.NaN : double.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
            }
            double total = Kb("MemTotal"), avail = Kb("MemAvailable");
            if (!(total > 0) || double.IsNaN(avail)) return;
            into.Add(new SensorReading(Name, "Memory", "Memory", (total - avail) / total * 100, "%"));
            into.Add(new SensorReading(Name, "Memory", "Memory Used", (total - avail) / 1048576, "GB"));
        }

        // "temp2" before "temp10", "hwmon2" before "hwmon10".
        private static string Natural(string path) => Regex.Replace(path, @"\d+", m => m.Value.PadLeft(6, '0'));

        private static string Text(string path)
        {
            try { return File.ReadAllText(path).Trim(); }
            catch (Exception) { return null; } // missing, permission, or a sensor returning EIO
        }
    }
}
