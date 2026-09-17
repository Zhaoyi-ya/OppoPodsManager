# OPPO Pods Manager (Windows / Linux)

[中文](https://github.com/Zhaoyi-ya/OppoPodsManager/blob/main/README.md) | [English](https://github.com/Zhaoyi-ya/OppoPodsManager/blob/main/README_EN.md)

---

Manage your Bluetooth earbuds right from your desktop — check battery, switch noise cancelling, tune EQ,
and manage multi-device connections without opening the phone app.

**6 brands supported**: OPPO / OnePlus / realme (135 official models), vivo / iQOO (43 official models),
Huawei FreeBuds, Xiaomi / Redmi, Edifier, and Apple AirPods. Capabilities are auto-detected per model — the UI
only shows what your earbuds actually support.

> **v2.0.0 beta**: the app is currently undergoing a multi-brand refactor. See
> [Supported Brands & Devices](#supported-brands--devices) for per-brand progress.
> Join our QQ group **1101564539** to try the beta builds.

---

## Table of Contents

- [Features](#features)
- [Supported Brands & Devices](#supported-brands--devices)
- [Requirements](#requirements)
- [Getting Started](#getting-started)
- [Interface Guide](#interface-guide)
- [Settings Guide](#settings-guide)
- [Network Connection](#network-connection)
- [FAQ](#faq)
- [Building from Source](#building-from-source)
- [Maintainers](#maintainers)
- [Font Notice](#font-notice)
- [Acknowledgements](#acknowledgements)
- [License](#license)

---

## Features

### Battery & Wear
- **Multiple readings**: Left / Right / Case shown separately, with charging status ⚡; single-battery devices automatically switch to a single-column layout
- **Wear detection**: in-case / worn / removed, in real time
- **Live state sync**: every toggle and mode continuously reads back the real state from the earbuds and restores it after an app restart, so the UI always matches your earbuds

### Noise Cancelling
- **Dynamic ANC modes**: main modes shown per model (Off / NC / Transparency / Adaptive)
- **ANC sub-levels**: Smart / Deep / Medium / Light, listed automatically per model
- **Smart real-time level**: in Smart mode, shows the level the device computes on the fly (e.g. "Real-time: Deep"), matching the official app

### Sound
- **Master EQ**: available presets loaded per model, one-tap switching; models with custom EQ support expose band editing
- **Spatial sound**: on/off spatial soundstage
- **Spatial audio 3-mode**: Off / Fixed / Head Tracking (supported models); on connect the app reads back the mode currently selected on the earbuds and syncs the UI, restored correctly across restarts
- **Game sound**: dedicated gaming sound effect toggle (supported models), with live state read-back
- **Sound-effect mutex**: game sound and spatial sound are mutually exclusive — turning one on automatically turns the other off, matching the official app

### Other Controls
- **Game mode**: low-latency toggle, standard and compatible implementations
- **Dual-device connection**: toggle simultaneous two-device connection
- **Multi-device management**: view connected devices, switch the active one with one click
- **Device info**: firmware version and audio codec display
- **Find my earbuds**: play a locating tone on the earbuds (supported models)

### Notifications
- **Connection card**: a semi-transparent status card slides in at the bottom-right on connect / disconnect
- **Battery alerts**: tiered alerts for low (≤20%) and critical (≤10%) battery
- **Independent toggles**: low-battery alerts and connection pop-ups can be turned off separately; pop-up duration is adjustable (3–8 s)

### Desktop Experience
- **System tray**: left-click toggles the window, right-click opens a menu, hover shows live battery
- **Minimize to tray**: closing the window minimizes to tray instead of quitting (optional)
- **Auto-start**: launch with the system (optional)
- **Auto-reconnect**: reconnects automatically after a disconnection
- **Theme**: follows system light/dark, or set it manually
- **Interface customization**: custom background image (with blur strength), card opacity, language (Simplified Chinese / English / Deutsch / Русский, or follow system)
- **Window acrylic blur** (Windows): optional frosted-glass window effect

---

## Supported Brands & Devices

| Brand | Model detection | Coverage |
|-------|----------------|----------|
| **OPPO** · **OnePlus** · **realme** | 135 official models | Full: battery / ANC / sound / spatial audio / dual-device / find-my-earbuds |
| **vivo** · **iQOO** | 43 official models | Full: battery / ANC / sound / spatial audio / dual-device |
| **Huawei FreeBuds** | Per-model capability table | Adopted: battery / ANC / dual-device, shown per model |
| **Xiaomi** · **Redmi** | — | Battery read-out (controls pending) |
| **Edifier** | — | Basic info read-out |
| **Apple AirPods** | — | Basic info read-out (control channel not implemented on desktop) |

Features vary by model (ANC sub-levels, spatial audio, dual-device, Master EQ, etc.). The app
**shows only what your model supports** — unsupported features never appear. If auto-detection is wrong,
search and pick your model under **Settings → Device Model**.

> Model tables are synced from the official apps and updated with each release.

---

## Requirements

- **Windows**: Windows 10 version 2004 (build 19041) or later, 64-bit (x64 / arm64)
- **Linux**: x64, BlueZ via D-Bus (X11 / XWayland), plus `bluetoothctl` and `libbluetooth`
- **Hardware**: Bluetooth adapter + paired earbuds
- **Dependencies**: Windows self-contained builds need nothing installed. Linux needs the .NET Runtime 10.0
  unless you use a self-contained build.

---

## Getting Started

1. Download the latest build from [Releases](https://github.com/Zhaoyi-ya/OppoPodsManager/releases)
2. Unzip anywhere
3. **Pair your earbuds first** in your system's Bluetooth settings (this matters — the app doesn't handle pairing)
4. Run it — the app discovers and connects to paired earbuds automatically

Once connected, the main window shows battery and all controls. Your settings (theme, language, pop-up
preferences, model override, tray/auto-start preferences) are remembered.

---

## Interface Guide

Main window, top to bottom:

| Section | Description |
|---------|-------------|
| **Device list** (sidebar) | Connected devices; expand to view and switch the active one |
| **Battery card** | Title is the current earbud name (the dot on its left shows connection state: green = connected, red = disconnected); the row ends with the multi-device selector, refresh and reconnect buttons; below are the battery readings, charging status and wear state |
| **Noise Cancelling** | Main-mode segmented selector + sub-levels (generated per model); shows real-time level in Smart mode |
| **Spatial Audio** | 3-mode selection (only shown on supported models), auto-synced to the mode currently set on the earbuds |
| **Features** | Spatial sound, game mode, game sound, dual-device toggles + Master EQ dropdown (all shown per model, state read back live) |

**Settings page**: local device info (name / firmware / codec), connection strategy (auto-select, priority device),
minimize-to-tray, auto-start, check for updates, view logs, feedback, about.

---

## Settings Guide

**Personalization page**:

| Group | Options |
|-------|---------|
| Appearance | Language (Auto / 中文 / English / Deutsch / Русский), theme (follow system / dark / light), card opacity |
| Notifications | Low-battery alert toggle, connection pop-up toggle, pop-up duration (3–8 s) |
| Background | Custom background image (thumbnail picker, add new), background blur strength |
| Custom earbud artwork | Assign images for the main view / quick card / left / right / case, reset to default |
| Device | Custom device name |
| Advanced (Beta) | Advanced rendering, window acrylic blur (Windows only; the window reloads to apply) |

---

## Network Connection

The only network activity in this app is **update checking**: it queries the update server for the latest
version number to see if a new release is available.

- **No data is uploaded**: it does not collect or upload any device info, earbud data, usage habits, or any personal information.
- It only makes a single read-only version query when you check for updates (manually or via auto-check) — no other network communication occurs.
- No analytics, tracking, or telemetry code is built in.

If the network is unavailable, or you disable auto-check, the app runs fully offline — every local feature keeps working.

---

## FAQ

**Q: The app can't find my earbuds?**
First make sure they're paired and connected in your system's Bluetooth settings. The app only connects to already-paired devices; it doesn't handle pairing.

**Q: Connected, but some features are missing?**
The UI adapts to your model — features your earbuds don't support (no spatial audio, no ANC sub-levels, etc.) won't appear. If the model is misidentified, pick the right one in Settings.

**Q: Wrong model / incomplete features?**
Use the search box under **Settings → Device Model** to find and set your exact model.

**Q: My brand only shows battery, with no ANC or sound controls?**
That brand's protocol support is still in progress (see [Supported Brands & Devices](#supported-brands--devices)).
Read-only info such as battery and wear state works regardless.

**Q: ANC level names differ from the official app?**
The ANC level names (Smart / Deep / Medium / Light) match the official app. Smart mode switches levels based on your environment, and the UI shows the currently computed level.

**Q: Turning on game sound turned off spatial sound (or vice versa)?**
That's by design. Game sound and spatial sound are mutually exclusive on the earbuds — only one can be active at a time. Enabling one automatically disables the other and sends that to the earbuds, matching the official app.

**Q: A toggle doesn't match the earbuds / restores wrong after a restart?**
The app continuously reads back the real state from the earbuds (spatial audio mode, game sound, game mode, dual-device, etc.) and restores it on restart. If you briefly see a toggle flicker, that's the read-back syncing with your action — it settles in a moment.

**Q: I don't want the connection pop-up / battery alerts?**
Turn each one off under **Personalization → Notifications**; you can also adjust how long pop-ups stay on screen.

**Q: The app keeps running after I close the window?**
If "minimize to tray" is enabled, closing the window just hides it to the tray. Right-click the tray icon to quit.

**Q: Does it work on Linux?**
Yes. Linux talks to the earbuds over BlueZ (D-Bus) and covers the same feature set as Windows;
the frosted-glass window effect is Windows-only.

---

## Building from Source

Requires **.NET SDK 10.0** or later.

```bash
git clone https://github.com/Zhaoyi-ya/OppoPodsManager.git
cd OppoPodsManager

# Debug build (the Windows backend is selected by RID or build property)
dotnet build -c Debug

# Publish (NativeAOT self-contained single file; no .NET needed on the target machine)
dotnet publish -c Release -r win-x64      # Windows x64
dotnet publish -c Release -r linux-x64    # Linux x64
```

The backend switches by platform automatically: Windows uses the WinRT Bluetooth APIs
(`net10.0-windows10.0.19041.0`), Linux uses BlueZ over D-Bus (`net10.0` + `Tmds.DBus`).
The same codebase serves both via MSBuild conditionals — no manual code changes needed.

---

## Maintainers

- [@Zhaoyi-ya](https://github.com/Zhaoyi-ya)
- [@Dszsu](https://github.com/Dszsu)

---

## Font Notice

This software embeds and uses the MiSans font for its user interface. MiSans is provided by Xiaomi. Copyright and licensing information are subject to Xiaomi's official statements and the MiSans Font Intellectual Property License Agreement:

- MiSans official page: https://hyperos.mi.com/font/
- MiSans FAQ: https://hyperos.mi.com/font/en/faq/

According to MiSans’ official statements, this project specifies the use of the MiSans font in the software, and it does not involve any modification, further development, separate distribution, or sale of the MiSans font files or their components.

---

## Acknowledgements

- [Avalonia UI](https://avaloniaui.net/) — Cross-platform UI framework
- [SukiUI](https://github.com/kikipoulet/SukiUI) — Avalonia theme & control library
- [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) — OPPO proprietary protocol reverse engineering
- [1812z/OppoPods](https://github.com/1812z/OppoPods) — Feature implementation reference

---

## License

GPL-3.0-or-later (see [LICENSE](./LICENSE))
