# Katana Air Win Host

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![MIT License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

**An unofficial Windows host for the BOSS KATANA-AIR / KATANA-AIR EX controller frontend.**

> [!NOTE]
> The KATANA-AIR and KATANA-AIR EX use the same controller app, so this might work with the EX too. I don't have one to test, though, so no guarantees! 🤔

> [!WARNING]
> This is an unofficial community project. It is not affiliated with, endorsed by, or supported by Roland Corporation or BOSS. It does not include the proprietary frontend resources.

## Overview

Katana Air Win Host runs the BOSS KATANA-AIR controller frontend from the official Android application in a Windows desktop window and connects to the amplifier over Bluetooth MIDI. It provides the local HTTP server, the `$$` native bridge, file and dialog services, storage, and the Bluetooth transport the frontend uses. Tone parameters and editor logic remain in the frontend.

## How It Works

1. **Controller frontend** — the HTML, CSS, and JavaScript interface from the official Android application, supplied under `web/`.
2. **Windows host** — a C#/.NET 10 service that serves the frontend, implements the `$$` native bridge, and handles file, dialog, storage, and proxy requests.
3. **Bluetooth MIDI transport** — a direct Bluetooth LE GATT connection to the amplifier.

```text
Controller UI (browser window)
    ↕
Injected scripts + $$ native bridge
    ↕
Windows host (HTTP server, bridges, BLE transport)
    ↕
Bluetooth LE / GATT
    ↕
BOSS KATANA-AIR
```

The host injects `head.js` and `tail.js` into the HTML served to the browser. The original frontend files on disk remain unchanged. It transports MIDI and SysEx messages without interpreting tone parameters. Interfaces are documented in [HOST-INTERFACES.md](HOST-INTERFACES.md).

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- A Bluetooth LE adapter and the Windows Bluetooth stack.
- A BOSS KATANA-AIR amplifier.
- The controller frontend resources from the official Android application.
- The .NET 10 SDK to build, or the .NET 10 Runtime for a framework-dependent build.

## Official Frontend Resources

The controller frontend is not included in this repository; it is the web frontend located under assets/html. The host has been tested with the frontend from BOSS TONE STUDIO for KATANA-AIR for Android, version 2.1.0 (build 74; [third-party XAPK reference](https://apkpure.net/bts-for-katana-air/jp.co.roland.boss_katana_air_editor)).

Place the frontend in web/ beside the executable or repository root, or specify its location via --web-root, KAWH_WEB_ROOT, or the first-run folder picker.

Other frontend versions may work if the $$ command set and event encoding remain compatible. Users are responsible for license compliance.

## Getting Started

1. Build the host or use a published build.
2. Pair the amplifier: power it on, hold its Bluetooth button until the indicator flashes rapidly, add **KATANA-AIR MIDI** in Windows **Settings → Bluetooth & devices**, and close the official application on any phone (the amplifier accepts only one BLE MIDI connection at a time).
3. Wait for the editor to load with parameter values instead of `--`.

### Build

```powershell
dotnet build src/KatanaAirWinHost -c Release
```

Framework-dependent publish:

```powershell
dotnet publish src/KatanaAirWinHost -c Release -r win-x64 --self-contained false -o .publish
```

The publish directory does not contain `web/`; copy the frontend resources next to it or set `KAWH_WEB_ROOT`.

### Run

```powershell
.\src\KatanaAirWinHost\bin\Release\net10.0-windows10.0.19041.0\KatanaAirWinHost.exe
```

The host starts a local HTTP server and opens the interface in an Edge application window (the default browser when Edge is not installed). The host exits when that window closes, when **Exit** is clicked, or on `Ctrl+C`. The status page is at `/__status`; `/__debug` exposes the command, event, and BLE state.

| Option             | Effect                                     |
| ------------------ | ------------------------------------------ |
| `--dev`            | Open a normal browser window with DevTools |
| `--no-browser`     | Start the server without a browser         |
| `--web-root <dir>` | Use the specified frontend directory       |
| `--addr <HEX12>`   | Use a known Bluetooth address for this run |
| `--help`           | Print the full option list                 |

All options, environment variables, exit codes, and diagnostics are documented in [HOST-INTERFACES.md](HOST-INTERFACES.md).

## Limitations

The core editor functions are available, including tone editing, SET file import and export, and synchronization with the amplifier. The following limitations apply to features outside the core editing workflow:

- **Interface:** The editor retains its phone-oriented layout.
- **Unsupported features:** Local audio playback, recording, audio conversion, sampler, multi-client MIDI, cryptographic helpers, and cloud store synchronization are not implemented.
- **Embedded pages:** Tone Exchange does not support message exchange with the editor. Tone Central is unavailable in the current frontend configuration.

## License

The original code and documentation in this repository are licensed under the **MIT License**.

The MIT License applies only to original project code and documentation that the project is entitled to license. It does not grant rights to third-party software, frontend resources, trademarks, or other copyrighted materials.

## Notice

BOSS and KATANA-AIR are trademarks of Roland Corporation, referenced solely to identify the compatible product. The software is provided "as is", without warranty of any kind; use it at your own risk. It writes parameter data to the amplifier over Bluetooth and is not an official replacement for Roland's software or support.
