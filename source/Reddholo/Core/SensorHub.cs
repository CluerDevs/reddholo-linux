using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BcuHolo.Plugins;

namespace BcuHolo.Modules.Sensors
{
    /// <summary>One sensor with its latest value, min/max since app start and recent history (oldest first, up to 60).</summary>
    public sealed class SensorEntry
    {
        public SensorReading Reading;
        public double Min, Max;
        public double[] History;
    }

    /// <summary>
    /// Owns every ISensorSource. Polls once a second on a background thread only while someone is subscribed.
    /// Plugins and the platform shell add sources with AddSource. Handlers run via <see cref="Post"/>
    /// (the WPF app sets it to its dispatcher; otherwise they run on the polling thread).
    /// </summary>
    public static class SensorHub
    {
        private const int HistoryLen = 60;
        private static readonly object Gate = new object();
        private static readonly List<ISensorSource> Sources = OperatingSystem.IsLinux()
            ? new List<ISensorSource> { new LinuxSensorSource() }
            : new List<ISensorSource>(); // Windows: the app adds LibreHardwareMonitor at startup
        private static readonly Dictionary<(string, string, string, string), (double min, double max, Queue<double> hist)> Stats = new();
        private static IReadOnlyList<SensorEntry> _snapshot = new SensorEntry[0];
        private static Action _handlers;
        private static int _subscribers;
        private static ManualResetEventSlim _stop;
        private static int _notifyPending;
        private static readonly object PollGate = new object(); // a restarted loop must not overlap a lingering old poll

        public static void AddSource(ISensorSource source) { lock (Gate) Sources.Add(source); }

        /// <summary>Runs a notification on the UI thread. Null: handlers run on the polling thread.</summary>
        public static Action<Action> Post;

        public static IReadOnlyList<SensorEntry> Snapshot => _snapshot;

        public static void Subscribe(Action onUpdated)
        {
            lock (Gate)
            {
                _handlers += onUpdated;
                if (++_subscribers != 1) return;
                _stop = new ManualResetEventSlim(false);
                var stop = _stop;
                new Thread(() => Loop(stop)) { IsBackground = true, Name = "SensorHub" }.Start();
            }
        }

        public static void Unsubscribe(Action onUpdated)
        {
            lock (Gate)
            {
                _handlers -= onUpdated;
                if (_subscribers == 0 || --_subscribers != 0) return;
                _stop?.Set();
            }
        }

        /// <summary>Current value of the first sensor whose group and name contain the given text (case-insensitive).</summary>
        public static bool TryGet(string group, string name, out double value) => TryGet(group, name, null, out value);

        /// <summary>As above, also matching the unit exactly (e.g. "CPU Package" exists as both °C and W).</summary>
        public static bool TryGet(string group, string name, string unit, out double value)
        {
            value = 0;
            foreach (var e in _snapshot)
            {
                var r = e.Reading;
                if ((unit == null || r.Unit == unit) && Has(r.Group, group) && Has(r.Name, name)) { value = r.Value; return true; }
            }
            return false;
        }

        /// <summary>Close sources that hold drivers (call from app exit; optional, the process exit also releases them).</summary>
        public static void Shutdown()
        {
            lock (Gate)
            {
                _stop?.Set();
                foreach (var d in Sources.OfType<IDisposable>()) { try { d.Dispose(); } catch (Exception ex) { Trace.WriteLine(ex.Message); } }
            }
        }

        private static bool Has(string s, string part) =>
            string.IsNullOrEmpty(part) || (s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);

        private static void Loop(ManualResetEventSlim stop)
        {
            while (!stop.IsSet)
            {
                try { Poll(); }
                catch (Exception ex) { Trace.WriteLine("SensorHub: " + ex.Message); }
                stop.Wait(1000);
            }
        }

        private static void Poll()
        {
            lock (PollGate) PollLocked();
        }

        private static void PollLocked()
        {
            ISensorSource[] sources;
            lock (Gate) sources = Sources.ToArray();

            var list = new List<SensorEntry>();
            foreach (var src in sources)
            {
                IEnumerable<SensorReading> readings;
                try { readings = src.Read()?.ToList(); }
                catch (Exception ex) { Trace.WriteLine("SensorHub source " + src.Name + ": " + ex.Message); continue; }
                if (readings == null) continue;
                foreach (var r in readings)
                {
                    if (r == null || double.IsNaN(r.Value) || double.IsInfinity(r.Value)) continue;
                    var key = (r.Source, r.Group, r.Name, r.Unit);
                    // Only °C and W rows draw a sparkline (SensorsView); nothing else reads History.
                    bool spark = r.Unit == "°C" || r.Unit == "W";
                    if (!Stats.TryGetValue(key, out var st)) st = (r.Value, r.Value, spark ? new Queue<double>() : null);
                    st.min = Math.Min(st.min, r.Value);
                    st.max = Math.Max(st.max, r.Value);
                    if (st.hist != null)
                    {
                        st.hist.Enqueue(r.Value);
                        while (st.hist.Count > HistoryLen) st.hist.Dequeue();
                    }
                    Stats[key] = st;
                    list.Add(new SensorEntry { Reading = r, Min = st.min, Max = st.max, History = st.hist?.ToArray() });
                }
            }
            _snapshot = list;

            // Coalesce: skip if the UI hasn't handled the previous notification yet.
            if (Interlocked.Exchange(ref _notifyPending, 1) == 1) return;
            Action notify = () =>
            {
                _notifyPending = 0;
                try { _handlers?.Invoke(); } catch (Exception ex) { Trace.WriteLine("SensorHub handler: " + ex.Message); }
            };
            var post = Post;
            if (post == null) notify(); else post(notify);
        }
    }
}
