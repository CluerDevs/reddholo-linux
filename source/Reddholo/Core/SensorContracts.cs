using System.Collections.Generic;

// Kept in the BcuHolo.Plugins namespace and type-forwarded from BcuHolo.Plugins.dll, so existing plugins still load.
namespace BcuHolo.Plugins
{
    /// <summary>A source of hardware or environment readings (temperatures, loads, a smart plug's watts…).</summary>
    public interface ISensorSource
    {
        string Name { get; }

        /// <summary>Polled about once a second from a background thread; must not block for long.</summary>
        IEnumerable<SensorReading> Read();
    }

    /// <param name="Group">e.g. "CPU", "GPU", "Drive C:"</param>
    /// <param name="Unit">e.g. "°C", "%", "W", "V", "RPM"</param>
    public sealed record SensorReading(string Source, string Group, string Name, double Value, string Unit);
}
