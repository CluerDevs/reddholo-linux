using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using BcuHolo.Modules.Gpu;
using BcuHolo.Modules.Sensors;

// reddholo-cli [sensors]  — print this machine's sensors once and exit.
if (args.Length > 0 && args[0] != "sensors")
{
    Console.Error.WriteLine("usage: reddholo-cli [sensors]");
    return 2;
}

if (OperatingSystem.IsWindows())
    Console.Error.WriteLine("note: CPU/board sensors on Windows come from the reddholo app (LibreHardwareMonitor); only the GPU is shown here.");
var gpu = new NvmlSensorSource();
SensorHub.AddSource(gpu);

// CPU load needs two samples one second apart, so wait for the second poll.
int polls = 0;
using var done = new ManualResetEventSlim();
Action onUpdate = () => { if (Interlocked.Increment(ref polls) >= 2) done.Set(); };
SensorHub.Subscribe(onUpdate);
done.Wait(TimeSpan.FromSeconds(10));
SensorHub.Unsubscribe(onUpdate);
SensorHub.Shutdown();

var rows = SensorHub.Snapshot;
if (rows.Count == 0) Console.Error.WriteLine("no sensors found" + (gpu.Error != null ? " (GPU: " + gpu.Error + ")" : ""));
foreach (var group in rows.GroupBy(e => e.Reading.Group))
{
    Console.WriteLine(group.Key);
    foreach (var e in group)
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-28} {1,10:0.##} {2}", e.Reading.Name, e.Reading.Value, e.Reading.Unit));
}
return 0;
