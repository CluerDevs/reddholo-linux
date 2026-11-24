using System;
using System.Collections.Generic;
using BcuHolo.Plugins;

namespace BcuHolo.Modules.Gpu
{
    /// <summary>First NVIDIA GPU as plain sensor readings (read-only). The Windows app uses GpuModel instead; this is for
    /// shells without the GPU page (Linux, CLI).</summary>
    public sealed class NvmlSensorSource : ISensorSource, IDisposable
    {
        private GpuDevice _dev;
        private string _error;

        public string Name => "NVML";

        public IEnumerable<SensorReading> Read()
        {
            var list = new List<SensorReading>();
            if (_error != null) return list; // no driver: don't retry every second
            try
            {
                if (_dev == null)
                {
                    _error = Nvml.Init();
                    if (_error != null) return list;
                    _dev = GpuDevice.Open();
                }
                var g = _dev.Name;
                // one unsupported reading (e.g. power or fans on laptops) must not hide the rest: partial list
                void Add(string n, Func<double> f, string u) { try { list.Add(new SensorReading(Name, g, n, f(), u)); } catch (NvmlException) { } }
                Add("GPU Core", () => _dev.Temp(), "°C");
                Add("GPU Core", () => _dev.Load(), "%");
                Add("GPU Package", () => _dev.PowerW(), "W");
                Add("GPU Core", () => _dev.CoreMHz(), "MHz");
                try
                {
                    var (used, total) = _dev.Vram();
                    list.Add(new SensorReading(Name, g, "GPU Memory Used", used / 1024.0, "GB"));
                    list.Add(new SensorReading(Name, g, "GPU Memory Total", total / 1024.0, "GB"));
                }
                catch (NvmlException) { }
                for (int i = 0; i < _dev.FanCount; i++) { int fan = i; Add($"GPU Fan {i + 1}", () => _dev.Fan(fan), "%"); }
            }
            catch (NvmlException ex) when (_dev == null) { _error = ex.Message; }
            catch (NvmlException) { }
            return list;
        }

        public string Error => _error;

        public void Dispose() => Nvml.Shutdown();
    }
}
