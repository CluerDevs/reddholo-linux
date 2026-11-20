using System;
using System.Runtime.InteropServices;

namespace BcuHolo.Modules.Gpu
{
    public sealed class NvmlException : Exception
    {
        public int Code { get; }
        public NvmlException(string what, int code) : base(Describe(what, code)) { Code = code; }

        private static string Describe(string what, int code)
        {
            string hint = code switch
            {
                3 => "not supported by this GPU/driver",
                4 => OperatingSystem.IsWindows() ? "permission denied (run reddholo as administrator)" : "permission denied (run as root)",
                9 => "NVIDIA driver is not loaded",
                15 => "GPU is lost or unreachable",
                _ => Nvml.ErrorText(code),
            };
            return $"{what}: {hint} (NVML {code})";
        }
    }

    /// <summary>Thin P/Invoke layer over NVML: nvml.dll (System32) on Windows, libnvidia-ml.so.1 on Linux. Signatures per NVIDIA NVML API reference.</summary>
    public static class Nvml
    {
        private const string Dll = "nvml.dll";
        private static bool _inited;

        static Nvml()
        {
            // Linux drivers ship the library as libnvidia-ml.so.1 (no unversioned name without the -dev package).
            if (!OperatingSystem.IsWindows())
                NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, (name, asm, path) =>
                    name == Dll && NativeLibrary.TryLoad("libnvidia-ml.so.1", asm, path, out var h) ? h : IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { public uint gpu; public uint memory; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInfo { public ulong total; public ulong free; public ulong used; }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlInit_v2();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlShutdown();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nvmlErrorString(int result);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr dev);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetName(IntPtr dev, byte[] name, uint length);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint temp);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetNumFans(IntPtr dev, out uint n);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetFanSpeed_v2(IntPtr dev, uint fan, out uint percent);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceSetFanSpeed_v2(IntPtr dev, uint fan, uint percent);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceSetDefaultFanSpeed_v2(IntPtr dev, uint fan);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetMinMaxFanSpeed(IntPtr dev, out uint min, out uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr dev, out uint mw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr dev, out uint min, out uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr dev, out uint mw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceSetPowerManagementLimit(IntPtr dev, uint mw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Utilization u);
        // Read-only display getters: NVML_CLOCK_GRAPHICS = 0, NVML_CLOCK_MEM = 2.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr dev, int type, out uint mhz);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out MemoryInfo mem);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlSystemGetDriverVersion(byte[] version, uint length);

        public static string ErrorText(int code)
        {
            try { return Marshal.PtrToStringAnsi(nvmlErrorString(code)) ?? "unknown error"; }
            catch (Exception) { return "unknown error"; }
        }

        private static void Ck(string what, int rc) { if (rc != 0) throw new NvmlException(what, rc); }

        /// <summary>Loads NVML and initialises it. Returns an error string, or null on success.</summary>
        public static string Init()
        {
            try
            {
                int rc = nvmlInit_v2();
                if (rc != 0) return new NvmlException("nvmlInit", rc).Message;
                _inited = true;
                return null;
            }
            catch (DllNotFoundException) { return "NVIDIA driver not found"; }
            catch (EntryPointNotFoundException) { return "NVIDIA driver not found (NVML is too old)"; }
            catch (BadImageFormatException) { return "NVIDIA driver not found (NVML architecture mismatch)"; }
        }

        public static void Shutdown()
        {
            if (!_inited) return;
            _inited = false;
            try { nvmlShutdown(); } catch (Exception) { }
        }

        public static IntPtr FirstDevice(out string name)
        {
            Ck("nvmlDeviceGetCount_v2", nvmlDeviceGetCount_v2(out var n));
            if (n == 0) throw new NvmlException("No NVIDIA GPU found", 6);
            Ck("nvmlDeviceGetHandleByIndex_v2", nvmlDeviceGetHandleByIndex_v2(0, out var h));
            var buf = new byte[96];
            Ck("nvmlDeviceGetName", nvmlDeviceGetName(h, buf, (uint)buf.Length));
            int len = Array.IndexOf(buf, (byte)0);
            name = System.Text.Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len);
            return h;
        }

        /// <summary>Driver version, e.g. "580.95.05"; needs Init first.</summary>
        public static string DriverVersion()
        {
            var buf = new byte[80];
            Ck("nvmlSystemGetDriverVersion", nvmlSystemGetDriverVersion(buf, (uint)buf.Length));
            int len = Array.IndexOf(buf, (byte)0);
            return System.Text.Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len);
        }

        public static int Temp(IntPtr h) { Ck("nvmlDeviceGetTemperature", nvmlDeviceGetTemperature(h, 0, out var t)); return (int)t; }
        public static int NumFans(IntPtr h) { Ck("nvmlDeviceGetNumFans", nvmlDeviceGetNumFans(h, out var n)); return (int)n; }
        public static int FanPercent(IntPtr h, int i) { Ck("nvmlDeviceGetFanSpeed_v2", nvmlDeviceGetFanSpeed_v2(h, (uint)i, out var p)); return (int)p; }
        public static void SetFan(IntPtr h, int i, int pct) => Ck("nvmlDeviceSetFanSpeed_v2", nvmlDeviceSetFanSpeed_v2(h, (uint)i, (uint)pct));
        public static void SetDefaultFan(IntPtr h, int i) => Ck("nvmlDeviceSetDefaultFanSpeed_v2", nvmlDeviceSetDefaultFanSpeed_v2(h, (uint)i));
        public static (int Min, int Max) MinMaxFan(IntPtr h) { Ck("nvmlDeviceGetMinMaxFanSpeed", nvmlDeviceGetMinMaxFanSpeed(h, out var a, out var b)); return ((int)a, (int)b); }
        public static double PowerW(IntPtr h) { Ck("nvmlDeviceGetPowerUsage", nvmlDeviceGetPowerUsage(h, out var mw)); return mw / 1000.0; }
        public static double LimitW(IntPtr h) { Ck("nvmlDeviceGetPowerManagementLimit", nvmlDeviceGetPowerManagementLimit(h, out var mw)); return mw / 1000.0; }
        public static double DefaultLimitW(IntPtr h) { Ck("nvmlDeviceGetPowerManagementDefaultLimit", nvmlDeviceGetPowerManagementDefaultLimit(h, out var mw)); return mw / 1000.0; }
        public static (double Min, double Max) LimitRangeW(IntPtr h) { Ck("nvmlDeviceGetPowerManagementLimitConstraints", nvmlDeviceGetPowerManagementLimitConstraints(h, out var a, out var b)); return (a / 1000.0, b / 1000.0); }
        public static void SetLimitMw(IntPtr h, uint mw) => Ck("nvmlDeviceSetPowerManagementLimit", nvmlDeviceSetPowerManagementLimit(h, mw));
        public static int Load(IntPtr h) { Ck("nvmlDeviceGetUtilizationRates", nvmlDeviceGetUtilizationRates(h, out var u)); return (int)u.gpu; }
        public static (int Gpu, int Mem) Util(IntPtr h) { Ck("nvmlDeviceGetUtilizationRates", nvmlDeviceGetUtilizationRates(h, out var u)); return ((int)u.gpu, (int)u.memory); }
        public static int ClockMHz(IntPtr h, int type) { Ck("nvmlDeviceGetClockInfo", nvmlDeviceGetClockInfo(h, type, out var c)); return (int)c; }
        public static int MaxClockMHz(IntPtr h, int type) { Ck("nvmlDeviceGetMaxClockInfo", nvmlDeviceGetMaxClockInfo(h, type, out var c)); return (int)c; }
        public static (long UsedMB, long TotalMB) VramMB(IntPtr h) { Ck("nvmlDeviceGetMemoryInfo", nvmlDeviceGetMemoryInfo(h, out var m)); return ((long)(m.used >> 20), (long)(m.total >> 20)); }
    }

    /// <summary>The first NVIDIA GPU. Reads throw <see cref="NvmlException"/>; setters are only called from user-confirmed actions.</summary>
    public sealed class GpuDevice
    {
        private readonly IntPtr _h;
        public string Name { get; }
        public int FanCount { get; }

        private GpuDevice(IntPtr h, string name, int fans) { _h = h; Name = name; FanCount = fans; }

        public static GpuDevice Open()
        {
            var h = Nvml.FirstDevice(out var name);
            int fans;
            try { fans = Nvml.NumFans(h); } catch (NvmlException) { fans = 0; }
            return new GpuDevice(h, name, fans);
        }

        public int Temp() => Nvml.Temp(_h);
        public int Fan(int i) => Nvml.FanPercent(_h, i);
        public int Load() => Nvml.Load(_h);
        public (int Gpu, int Mem) Util() => Nvml.Util(_h);
        public int CoreMHz() => Nvml.ClockMHz(_h, 0);
        public int MemMHz() => Nvml.ClockMHz(_h, 2);
        public int MaxCoreMHz() => Nvml.MaxClockMHz(_h, 0);
        public int MaxMemMHz() => Nvml.MaxClockMHz(_h, 2);
        public (long UsedMB, long TotalMB) Vram() => Nvml.VramMB(_h);
        public double PowerW() => Nvml.PowerW(_h);
        public double LimitW() => Nvml.LimitW(_h);
        public double DefaultLimitW() => Nvml.DefaultLimitW(_h);
        public (double Min, double Max) LimitRangeW() => Nvml.LimitRangeW(_h);
        public (int Min, int Max) FanRange() => Nvml.MinMaxFan(_h);
        public void SetFan(int i, int pct) => Nvml.SetFan(_h, i, pct);
        public void SetDefaultFan(int i) => Nvml.SetDefaultFan(_h, i);
        public void SetLimitW(int watts) => Nvml.SetLimitMw(_h, (uint)watts * 1000u);
    }
}
