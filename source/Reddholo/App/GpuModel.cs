using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using BcuHolo.Modules.Gpu;

namespace Reddholo.App
{
    // shortcut: copy of the WPF GpuModel (BcuHolo/Modules/Gpu) with an async confirm; merge into Core when the WPF app moves to Avalonia.
    /// <summary>Single shared state for the GPU page and tile: readings, the editable curve, the curve engine, power limit.</summary>
    internal sealed class GpuModel
    {
        public const int PointCount = 5;
        public const int TempMin = 30, TempMax = 90, FanMin = 30, FanMax = 100, ForceFullAt = 85;

        private sealed class Saved { public double[] T { get; set; } public double[] F { get; set; } public bool Active { get; set; } public int Fixed { get; set; } = 100; }

        private static readonly string SavePath = Path.Combine(App.DataDir, "gpu.json");

        private bool _started;
        /// <summary>Asks the user (title, message); true = go ahead. Set by the app before any action runs.</summary>
        public static Func<string, string, Task<bool>> Confirm = (_, _) => Task.FromResult(false);
        private GpuDevice _dev;
        // The curve engine runs on a thread-pool timer, not the UI thread, so a busy or hung UI can't freeze the fans.
        private System.Threading.Timer _timer;
        private readonly object _gate = new();

        /// <summary>
        /// Exists while fans are under our curve. If the app dies without restoring (crash, End Task), the next
        /// launch finds it and hands the fans back to the driver before anything else.
        /// </summary>
        private static readonly string EngagedMarker = Path.Combine(App.DataDir, "fan-control.active");
        private bool _shutDown;

        // Editable points (what the editor shows) and the snapshot the engine is actually running.
        public double[] T = { 40, 55, 65, 75, 85 };
        public double[] F = { 30, 40, 55, 75, 100 };
        private double[] _aT, _aF;

        public string Error { get; private set; }          // null when NVML is usable
        /// <summary>The running instance, so the dashboard can read NVML instead of spawning nvidia-smi.</summary>
        public static GpuModel Current { get; private set; }

        public bool Available => _dev != null;
        public string Name => _dev?.Name ?? "";
        public int FanCount => _dev?.FanCount ?? 0;
        public int FloorPct { get; private set; } = FanMin;

        public int Temp { get; private set; } = -1;
        public int Load { get; private set; } = -1;
        public double PowerW { get; private set; } = double.NaN;
        public double LimitW { get; private set; } = double.NaN;
        public int[] Fans { get; private set; } = new int[0];

        // Read-only display telemetry (-1 = unavailable). Never feeds the fan-safety failure counter.
        public int CoreMHz { get; private set; } = -1;
        public int MemMHz { get; private set; } = -1;
        public int MaxCoreMHz { get; private set; } = -1;
        public int MaxMemMHz { get; private set; } = -1;
        public long VramUsedMB { get; private set; } = -1;
        public long VramTotalMB { get; private set; } = -1;
        public int MemLoad { get; private set; } = -1;

        /// <summary>Last <see cref="HistoryLen"/> samples, oldest first. Fresh arrays each tick (safe to read from the UI thread); NaN = failed read.</summary>
        public const int HistoryLen = 60;
        public double[] HistTemp { get; private set; } = new double[0];
        public double[] HistLoad { get; private set; } = new double[0];
        public double[] HistPower { get; private set; } = new double[0];
        public double[] HistFan { get; private set; } = new double[0];
        private readonly List<double> _hT = new(), _hL = new(), _hP = new(), _hF = new();

        public bool PowerSupported { get; private set; }
        public double MinW, MaxW, DefaultW;

        public bool CurveActive { get; private set; }
        /// <summary>Fixed-speed mode: the engine runs a flat curve at this %. -1 when a real curve (or Auto) is in charge.</summary>
        public int FixedPct { get; private set; } = -1;
        /// <summary>Last fixed % chosen, remembered for the slider.</summary>
        public int LastFixedPct { get; private set; } = 100;
        public bool EverApplied { get; private set; }
        public bool SavedWasActive { get; private set; }
        public bool Dirty { get; private set; }
        public bool Afterburner { get; private set; }

        /// <summary>
        /// Set by GpuView while it is on screen. The Afterburner check snapshots every process, and only that page shows
        /// or acts on it (all Apply actions live there), so it is skipped otherwise.
        /// </summary>
        internal bool ViewOpen
        {
            get => _viewOpen;
            set { _viewOpen = value; if (value) Afterburner = IsAfterburnerRunning(); }
        }
        private volatile bool _viewOpen;
        public string Warning { get; private set; } = "";
        public string Status { get; private set; } = "";

        public event Action Changed
        {
            add
            {
                bool first = _changed == null;
                _changed += value;
                if (first) _timer?.Change(0, 1000); // polling may have been idle: fresh values now, not stale ones for up to 1 s
            }
            remove => _changed -= value;
        }
        private Action _changed;

        /// <summary>
        /// Readers of <see cref="Current"/> that don't subscribe to Changed (dashboard poll, widget) hold demand while they
        /// read. Static so callers can register before the model starts. Without demand, a subscriber or an active curve,
        /// the tick makes no NVML calls (lets an idle dGPU power down).
        /// </summary>
        private static int _demand;
        internal static void Want(bool on)
        {
            if (Interlocked.Add(ref _demand, on ? 1 : -1) == 1 && on) Current?._timer?.Change(0, 1000); // fresh values now, not up to 1 s stale
        }

        private int _cur = -1, _lastSent = -1, _fails, _tick;
        private double _anchor;

        public GpuModel() => Load_();

        /// <summary>Idempotent; called by the first view or tile created.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            Current = this;
            Error = Nvml.Init();
            if (Error == null)
            {
                try { _dev = GpuDevice.Open(); }
                catch (NvmlException ex) { Error = ex.Message; }
            }
            if (_dev != null)
            {
                try { FloorPct = Math.Max(FanMin, _dev.FanRange().Min); } catch (NvmlException) { } // unsupported on some cards
                try
                {
                    (MinW, MaxW) = _dev.LimitRangeW();
                    DefaultW = _dev.DefaultLimitW();
                    PowerSupported = MaxW > MinW;
                }
                catch (NvmlException) { PowerSupported = false; }
                // Display-only: any failure (including a missing nvml export) just leaves the dial scale on its fallback.
                try { MaxCoreMHz = _dev.MaxCoreMHz(); } catch (Exception) { MaxCoreMHz = -1; }
                try { MaxMemMHz = _dev.MaxMemMHz(); } catch (Exception) { MaxMemMHz = -1; }
                if (SavedWasActive) Status = "Saved curve — Apply to activate";
                if (File.Exists(EngagedMarker))
                {
                    var err = RestoreAll();
                    Warning = err == null
                        ? "reddholo closed unexpectedly while the fan curve was active. Fans were handed back to automatic control."
                        : "reddholo closed unexpectedly while the fan curve was active, and restoring automatic control failed: " + err;
                }
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            // No NVIDIA device: nothing to poll; the ViewOpen setter does the Afterburner check when the page opens.
            if (_dev != null) _timer = new System.Threading.Timer(_ => Tick(), null, 0, 1000);
        }

        // ---- Polling + curve engine ----

        /// <summary>Changed is for the UI; the engine may raise it from a pool thread.</summary>
        private void RaiseChanged()
        {
            if (_changed == null) return; // no UI listening: skip the dispatcher post
            if (Dispatcher.UIThread.CheckAccess()) _changed?.Invoke();
            else Dispatcher.UIThread.Post(() => _changed?.Invoke());
        }

        private void Tick()
        {
            // Skip a tick rather than queue up if the previous one (or a user action) still holds the lock.
            if (!System.Threading.Monitor.TryEnter(_gate)) return;
            try { TickCore(); }
            catch (Exception ex) { Trace.WriteLine("GPU tick failed: " + ex); }
            finally { System.Threading.Monitor.Exit(_gate); }
        }

        private void TickCore()
        {
            if (_shutDown) return;
            if (_tick++ % 3 == 0 && _viewOpen) Afterburner = IsAfterburnerRunning();
            if (!CurveActive && _changed == null && Volatile.Read(ref _demand) == 0) return; // nobody reading: skip NVML
            if (_dev == null) { RaiseChanged(); return; }

            bool bad = false;
            string err = null;
            try
            {
                Temp = _dev.Temp();
                var fans = new int[_dev.FanCount];
                for (int i = 0; i < fans.Length; i++) fans[i] = _dev.Fan(i);
                Fans = fans;
            }
            catch (NvmlException ex) { bad = true; err = ex.Message; }

            // Display-only reads fail soft: they must not trip the curve safety fallback.
            try { var u = _dev.Util(); Load = u.Gpu; MemLoad = u.Mem; } catch (Exception) { Load = -1; MemLoad = -1; }
            try { PowerW = _dev.PowerW(); } catch (NvmlException) { PowerW = double.NaN; }
            try { LimitW = _dev.LimitW(); } catch (NvmlException) { LimitW = double.NaN; }
            // Catch Exception, not just NvmlException: a missing export must not escape before Step()/fallback below.
            try { CoreMHz = _dev.CoreMHz(); } catch (Exception) { CoreMHz = -1; }
            try { MemMHz = _dev.MemMHz(); } catch (Exception) { MemMHz = -1; }
            try { var v = _dev.Vram(); VramUsedMB = v.UsedMB; VramTotalMB = v.TotalMB; } catch (Exception) { VramUsedMB = VramTotalMB = -1; }
            PushHistory();

            if (CurveActive)
            {
                if (!bad)
                {
                    try { Step(); }
                    catch (NvmlException ex)
                    {
                        if (ex.Code == 3 || ex.Code == 4) { Fallback("Fan control refused: " + ex.Message + ". Restored automatic fan control."); RaiseChanged(); return; }
                        bad = true; err = ex.Message;
                    }
                }
                if (bad)
                {
                    if (++_fails >= 3) Fallback("GPU read failed 3 times in a row (" + err + "). Restored automatic fan control.");
                }
                else _fails = 0;
            }
            RaiseChanged();
        }

        private void PushHistory()
        {
            static void Add(List<double> l, double v) { l.Add(v); if (l.Count > HistoryLen) l.RemoveAt(0); }
            Add(_hT, Temp >= 0 ? Temp : double.NaN);
            Add(_hL, Load >= 0 ? Load : double.NaN);
            Add(_hP, PowerW);
            Add(_hF, Fans.Length > 0 ? Fans.Max() : double.NaN);
            HistTemp = _hT.ToArray(); HistLoad = _hL.ToArray(); HistPower = _hP.ToArray(); HistFan = _hF.ToArray();
        }

        private void Step()
        {
            double t = Temp;
            int target;
            if (t >= ForceFullAt) { target = 100; _cur = 100; _anchor = t; }
            else
            {
                target = (int)Math.Round(Math.Clamp(Eval(_aT, _aF, t), FloorPct, 100));
                if (_cur < 0 || target > _cur) { _cur = target; _anchor = t; }
                else if (target < _cur)
                {
                    // Hysteresis: only come down once 3 C below the temp that set the current speed, max 10% per second.
                    if (t <= _anchor - 3)
                    {
                        _cur = Math.Max(target, _cur - 10);
                        if (_cur == target) _anchor = t;
                    }
                }
                else _anchor = Math.Max(_anchor, t);
            }
            if (_cur != _lastSent || _tick % 10 == 0)
            {
                for (int i = 0; i < _dev.FanCount; i++) _dev.SetFan(i, _cur);
                _lastSent = _cur;
            }
        }

        public static double Eval(double[] ts, double[] fs, double t)
        {
            if (t <= ts[0]) return fs[0];
            for (int i = 1; i < ts.Length; i++)
            {
                if (t > ts[i]) continue;
                double dt = ts[i] - ts[i - 1];
                return dt <= 0 ? fs[i] : fs[i - 1] + (fs[i] - fs[i - 1]) * (t - ts[i - 1]) / dt;
            }
            return fs[^1];
        }

        private void Fallback(string warning)
        {
            RestoreAll();
            CurveActive = false;
            FixedPct = -1;
            Warning = warning;
            Persist();
        }

        /// <summary>Hands every fan back to the driver. Returns an error text or null.</summary>
        private string RestoreAll()
        {
            if (_dev == null) return null;
            string first = null;
            for (int i = 0; i < _dev.FanCount; i++)
            {
                try { _dev.SetDefaultFan(i); }
                catch (NvmlException ex) { first ??= ex.Message; }
            }
            _cur = _lastSent = -1;
            if (first == null) TryDeleteMarker();
            return first;
        }

        /// <summary>On app exit and module disable: restore automatic fans if a curve was ever applied this session.</summary>
        public void Shutdown()
        {
            if (_shutDown) return;
            _shutDown = true;
            _timer?.Dispose();
            lock (_gate)
            {
                if (EverApplied || File.Exists(EngagedMarker)) RestoreAll();
            }
            CurveActive = false;
            Nvml.Shutdown();
        }

        private static void WriteMarker()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EngagedMarker));
                File.WriteAllText(EngagedMarker, DateTime.Now.ToString("o"));
            }
            catch (Exception ex) { Trace.WriteLine("Fan marker write failed: " + ex.Message); }
        }

        private static void TryDeleteMarker()
        {
            try { File.Delete(EngagedMarker); } catch (Exception) { }
        }

        private static bool IsAfterburnerRunning()
        {
            if (!OperatingSystem.IsWindows()) return false; // Windows-only tool: skip the process scan
            try
            {
                var ps = Process.GetProcessesByName("MSIAfterburner");
                foreach (var p in ps) p.Dispose();
                return ps.Length > 0;
            }
            catch (Exception) { return false; }
        }

        // ---- User actions (all setters are behind host.Confirm) ----

        public void SetPoint(int i, double t, double f)
        {
            double tLo = i > 0 ? T[i - 1] : TempMin, tHi = i < PointCount - 1 ? T[i + 1] : TempMax;
            double fLo = i > 0 ? F[i - 1] : FanMin, fHi = i < PointCount - 1 ? F[i + 1] : FanMax;
            T[i] = Math.Clamp(Math.Round(t), tLo, tHi);
            F[i] = Math.Clamp(Math.Round(f), fLo, fHi);
            Dirty = true;
            RaiseChanged();
        }

        public void ResetPoints()
        {
            T = new double[] { 40, 55, 65, 75, 85 };
            F = new double[] { 30, 40, 55, 75, 100 };
            Dirty = true;
            RaiseChanged();
        }

        public bool CanApply => Available && !Afterburner;

        public async Task ApplyCurve()
        {
            if (!CanApply) return;
            if (FanCount == 0) { Warning = "This GPU reports no controllable fans."; RaiseChanged(); return; }
            var pts = string.Join(", ", T.Zip(F, (t, f) => $"{t:0}°C→{Math.Max(f, FloorPct):0}%"));
            var from = CurveActive ? "Custom curve" : "Automatic";
            if (!await Confirm("Apply fan curve",
                    $"Fan control: {from} -> Custom curve\n{pts}\n\nFloor {FloorPct}%. Forced to 100% at {ForceFullAt}°C and above.\n" +
                    "Automatic control returns on \"Back to automatic\" or when reddholo exits.")) return;
            WriteMarker(); // before the first fan write, so a crash right after is still recoverable
            lock (_gate)
            {
                _aT = (double[])T.Clone();
                _aF = (double[])F.Clone();
                _cur = _lastSent = -1;
                _fails = 0;
                CurveActive = true;
                FixedPct = -1;
            }
            EverApplied = true;
            Dirty = false;
            Warning = "";
            Status = "";
            Persist();
            Tick();
        }

        /// <summary>
        /// Holds every fan at one speed until changed. Runs through the same engine as the curve (a flat curve), so it keeps
        /// the floor, the forced 100% at ForceFullAt, the read-failure fallback, the crash marker and restore-on-exit.
        /// </summary>
        public async Task ApplyFixed(int pct)
        {
            if (!CanApply) return;
            if (FanCount == 0) { Warning = "This GPU reports no controllable fans."; RaiseChanged(); return; }
            pct = Math.Clamp(pct, FloorPct, 100);
            var from = !CurveActive ? "Automatic" : FixedPct >= 0 ? $"Fixed {FixedPct}%" : "Custom curve";
            if (!await Confirm("Set fixed fan speed",
                    $"Fan control: {from} -> Fixed {pct}%\n\nAll {FanCount} fan(s) held at {pct}% at every temperature" +
                    (pct < 100 ? $" (still forced to 100% at {ForceFullAt}°C and above)" : "") + ".\n" +
                    "Automatic control returns on \"Auto\" or when reddholo exits.")) return;
            WriteMarker(); // before the first fan write, so a crash right after is still recoverable
            lock (_gate)
            {
                _aT = (double[])T.Clone();
                _aF = Enumerable.Repeat((double)pct, PointCount).ToArray();
                _cur = _lastSent = -1;
                _fails = 0;
                CurveActive = true;
                FixedPct = LastFixedPct = pct;
            }
            EverApplied = true;
            Warning = "";
            Status = "";
            Persist();
            Tick();
        }

        public void BackToAutomatic()
        {
            string err;
            lock (_gate)
            {
                err = RestoreAll();
                CurveActive = false;
                FixedPct = -1;
            }
            Warning = err == null ? "" : "Could not restore automatic control: " + err;
            Status = "";
            Persist();
            RaiseChanged();
        }

        public async Task ApplyLimit(double watts, bool toDefault)
        {
            if (!CanApply || !PowerSupported) return;
            int w = (int)Math.Round(Math.Clamp(toDefault ? DefaultW : watts, MinW, MaxW));
            double old = double.NaN;
            try { old = _dev.LimitW(); } catch (NvmlException) { }
            if (!await Confirm("Apply power limit", $"Power limit: {old:0} W -> {w} W")) return;
            try
            {
                _dev.SetLimitW(w);
                Warning = "";
                Status = $"Power limit set to {w} W.";
            }
            catch (NvmlException ex) { Warning = ex.Message; }
            try { LimitW = _dev.LimitW(); } catch (NvmlException) { }
            RaiseChanged();
        }

        // ---- Persistence ----

        private void Load_()
        {
            try
            {
                if (!File.Exists(SavePath)) return;
                var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavePath));
                if (s?.T == null || s.F == null || s.T.Length != PointCount || s.F.Length != PointCount) return;
                var t = new double[PointCount];
                var f = new double[PointCount];
                for (int i = 0; i < PointCount; i++)
                {
                    t[i] = Math.Clamp(double.IsFinite(s.T[i]) ? s.T[i] : TempMin, i > 0 ? t[i - 1] : TempMin, TempMax);
                    f[i] = Math.Clamp(double.IsFinite(s.F[i]) ? s.F[i] : FanMin, i > 0 ? f[i - 1] : FanMin, FanMax);
                }
                T = t; F = f;
                SavedWasActive = s.Active;
                LastFixedPct = Math.Clamp(s.Fixed, FanMin, FanMax);
            }
            catch (Exception) { } // corrupt file: keep defaults
        }

        private void Persist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
                File.WriteAllText(SavePath, JsonSerializer.Serialize(new Saved { T = T, F = F, Active = CurveActive && FixedPct < 0, Fixed = LastFixedPct }));
            }
            catch (Exception) { }
        }
    }
}
