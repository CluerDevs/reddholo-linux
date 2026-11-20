# reddholo-linux

The Linux port of **reddholo**, a hardware monitor and PC clean-up tool. It is being moved off Windows-only code
one piece at a time.

> **Status: preview.** Released: `reddholo-cli`, a command-line tool that prints the machine's sensors.
> The desktop app (Avalonia) now has every page of the Windows app, rebuilt on Linux sources; run it from source
> for now. The Windows app stays at [CluerDevs/reddholo](https://github.com/CluerDevs/reddholo).

## Download and run

Get `reddholo-cli-linux-x64` from the [latest release](https://github.com/CluerDevs/reddholo-linux/releases/latest).
It is a single file with .NET bundled, so there is nothing to install.

```sh
curl -LO https://github.com/CluerDevs/reddholo-linux/releases/latest/download/reddholo-cli-linux-x64
chmod +x reddholo-cli-linux-x64
./reddholo-cli-linux-x64
```

Example output from a dual-Xeon Proxmox host:

```
coretemp
  Package id 0                         46 °C
  Core 0                               39 °C
  ...
coretemp #2
  Package id 1                         49 °C
  ...
CPU
  CPU Total                          0.39 %
Memory
  Memory                            70.83 %
  Memory Used                       89.02 GB
```

### What it reads

| Source | Readings | Needs |
|---|---|---|
| Kernel hwmon (`/sys/class/hwmon`) | CPU, board, NVMe and NIC temperatures, fans, voltages, power | Nothing; works as a normal user |
| `/proc/stat`, `/proc/meminfo` | CPU load, memory use | Nothing |
| NVIDIA NVML | GPU temperature, load, power, clock, VRAM, fans | NVIDIA driver (`libnvidia-ml.so.1`) |

The tool is read-only: it never writes to hardware, and makes no network connections.

### Limits

- **Virtual machines, WSL and containers** usually have no hwmon sensors, so only CPU load, memory and a
  passed-through GPU show up.
- **Server fans and power supplies** (Dell iDRAC, HPE iLO and similar) report through the management controller,
  not through Linux, so they don't appear here. The fleet plan below reads them over Redfish.
- **Missing CPU temperatures:** load the driver: `coretemp` (Intel) or `k10temp` (AMD). Running
  `sudo sensors-detect` from `lm-sensors` finds the rest.
- **Hardware support:** x86-64 only for now.

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```sh
dotnet publish source/Reddholo/Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
./out/reddholo-cli
```

### Desktop app

```sh
dotnet run --project source/Reddholo/App -c Release
```

| Page | On Linux it uses |
|---|---|
| Dashboard | `/proc` for CPU, memory and processes; NVML for the GPU; kernel log for machine-check errors; disk usage |
| Apps | dpkg/apt, rpm/dnf, flatpak and snap; uninstall runs through `pkexec` after a dry run |
| Cleanup | trash, caches, thumbnails, apt cache, journal, old snaps, unused flatpak runtimes, core dumps |
| GPU | NVML: dials, power limit, fan curve, live history |
| Sensors | hwmon (`coretemp`, `k10temp`, `nvme`, `drivetemp`, …) |
| Drivers | GPU driver, kernel, BIOS, third-party kernel modules, PCI devices, `fwupd` firmware updates (list only) |
| Startup | XDG autostart entries and systemd user/system services |

Plus the desktop widget, six colour themes and its own window frame. Anything that changes the system asks
first; changes outside your home folder (packages, system services, power limit, fan control) need root and go
through `pkexec`. Settings live in `~/.local/share/reddholo/`, in the same format as the Windows app.

## Layout

| Path | What |
|---|---|
| `source/Reddholo/Core` | Platform-neutral code: sensor contracts, sensor hub, NVML, Linux sensor reader |
| `source/Reddholo/Cli` | `reddholo-cli` |
| `source/Reddholo/App` | Cross-platform desktop app (Avalonia) |
| `source/BcuHolo` | The Windows app (WPF). It uses `Reddholo.Core` for sensors. |
| `source/UninstallTools`, others | Windows uninstall engine from Bulk Crap Uninstaller |

## Roadmap

1. **Shared core:** sensors and GPU done. Apps, startup, clean-up and drivers have Linux versions in the app;
   their Windows versions still live in the WPF app.
2. **Cross-platform app:** done on Linux: every page, the widget and the custom frame. Next: plug the Windows
   sources (LibreHardwareMonitor, the uninstall engine) into the same app so one build serves both.
3. **Packages:** AppImage for Linux, and a portable zip for Windows.
4. **Fleet:** watch many machines from one place, over the local network and SSH, with alerts when something is
   out of normal. See [doc/fleet-plan.md](doc/fleet-plan.md).

## Credits and licence

Built on [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller) by Marcin Szeniak, and on
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (Windows sensors). Licensed
under Apache 2.0; see [Licence.txt](Licence.txt) and [NOTICE](NOTICE). This is not an official BCU release, so
please don't report issues with it upstream.
