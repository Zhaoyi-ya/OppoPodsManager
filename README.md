# OPPO Pods For Windows

[中文](#中文) | [English](#english)

---

## English

Windows desktop OPPO earbuds Bluetooth controller, supporting Enco Free4 / X3 / Air5 / Air5 Pro / Air4 Pro / Air2 Pro series.

> Device capabilities are auto-detected from `DeviceModels.json` (based on decompiled OPPO Melody App v16.8.1). Manual override is available in Settings for unsupported or misidentified models.

Built on the OPPO proprietary RFCOMM protocol reverse-engineered by [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods), with feature matrix reference from [1812z/OppoPods](https://github.com/1812z/OppoPods).

### Features

- Battery display (L / R / Case, with charging status)
- Wear detection (in-case / worn / removed)
- ANC control (Off / Noise Cancelling / Adaptive / Transparency)
- ANC sub-modes: Smart / Light / Medium / Deep (model-dependent)
- Spatial sound toggle (Free4 / Air5 / Air5 Pro)
- Spatial audio 3-mode: Off / Fixed / Head Tracking (X3)
- Game mode (Standard / Compatible)
- Dual-device connection toggle
- Master EQ presets (model-adaptive, loaded from JSON)
- Manual device model override in Settings
- System tray: left-click toggle window, right-click function menu
- Tray hover shows real-time battery
- Win11-style battery toast on connection
- Low battery / critical battery alert
- Minimize to tray / Auto-start with Windows
- Auto-reconnect on disconnection
- Follows system light/dark theme + manual override

### Requirements

- Windows 10 / 11
- .NET 10.0 Desktop Runtime ([Download](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)) — or use the self-contained `-NET` release which bundles the runtime
- Bluetooth adapter + paired OPPO earbuds

### Quick Start

**Run directly:**

Download from [Releases](https://github.com/Zhaoyi-ya/OPPO-Pods-For-Windows/releases):

| File | Description |
|------|-------------|
| `OppoPodsWPF-NET.exe` | Self-contained, includes .NET 10 runtime (170+ MB) |
| `OppoPodsWPF.exe` | Framework-dependent, requires .NET 10 installed (~7 MB) |

**Build from source:**

```bash
git clone https://github.com/Zhaoyi-ya/OPPO-Pods-For-Windows.git
cd OPPO-Pods-For-Windows
dotnet run
```

**Publish single-file exe:**

```bash
# Self-contained (no .NET required)
dotnet publish -c Release -r win-x64 --self-contained true -o publish

# Framework-dependent (needs .NET 10)
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

### Device Support

| Model      | ANC | Adaptive | Spatial FX | 3D Audio | Dual Device | Master EQ |
|:-----------|:---:|:---:|:---:|:---:|:---:|:---:|
| Enco Free4 | ✅ | ✅ | ✅ | —  | ✅ | ✅ |
| Enco X3    | ✅ | —  | —  | ✅ | ✅ | ✅ |
| Enco Air5  | ✅ | ✅ | ✅ | —  | —  | ✅ |
| Enco Air5 Pro | ✅ | ✅ | ✅ | —  | ✅ | ✅ |
| Enco Air4 Pro | ✅ | ✅ | —  | —  | ✅ | ✅ (3 presets, tested) |
| Enco Air2 Pro | ✅ | —  | —  | —  | —  | ✅ |

Other Bluetooth devices whose name contains "OPPO" will auto-connect with a generic feature set. You can manually override the device model in Settings if auto-detection fails.

### Project Structure

```
OPPO-Pods-For-Windows/
├── OppoPodsWPF.csproj    # .NET 10 WPF + WinForms project
├── App.xaml/.cs          # Entry point & theme
├── MainWindow.xaml/.cs   # Main UI + tray + settings
├── ToastWindow.xaml/.cs  # Connection / battery / disconnect toast
├── OppoProtocol.cs       # OPPO RFCOMM protocol definitions
├── RfcommService.cs      # Winsock2 Bluetooth connection & polling
├── DeviceCapabilities.cs # Auto-detection from JSON + manual override
├── DeviceModels.json     # Device whitelist (decompiled from OPPO Melody 16.8.1)
├── EqModeNames.json      # EQ mode type → display name mapping (40+ modes)
├── PodState.cs           # State data model
└── Assets/               # Icons & images
```

### Acknowledgements

- [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) — OPPO proprietary protocol reverse engineering
- [1812z/OppoPods](https://github.com/1812z/OppoPods) — Feature implementation reference
- [lepoco/wpfui](https://github.com/lepoco/wpfui) — Windows 11 Fluent Design UI framework
- [@Dszsu](https://github.com/Dszsu) — Multi-device JSON adaptation & .NET 10 upgrade (PR #3)
- [@yuanzexiong](https://github.com/yuanzexiong) — Enco Air4 Pro support (PR #2)

### License

GPL-3.0

---

## 中文

Windows 桌面端 OPPO 耳机蓝牙控制器，支持 Enco Free4 / X3 / Air5 / Air5 Pro / Air4 Pro / Air2 Pro 系列。

> 设备能力基于 `DeviceModels.json` 自动检测（数据源：OPPO 欢律 App v16.8.1 反编译）。设置中可手动覆盖型号以应对自动识别失败的情况。

基于 [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) 逆向的 OPPO 私有 RFCOMM 协议，参考 [1812z/OppoPods](https://github.com/1812z/OppoPods) 的功能矩阵实现。

### 功能

- 电量显示（左耳 / 右耳 / 充电盒 + 充电状态 ⚡）
- 佩戴检测（入盒 / 佩戴 / 摘下）
- 降噪控制（关闭 / 降噪 / 自适应 / 通透）
- 降噪子模式：智能 / 轻度 / 中度 / 深度（按型号自适应）
- 空间音效开关（Free4 / Air5 / Air5 Pro）
- 空间音频三模式：关闭 / 固定 / 头部追踪（X3）
- 游戏模式（标准 / 兼容两种实现）
- 双设备连接开关
- 大师调音 EQ（按型号从 JSON 加载）
- 设置中可手动覆盖设备型号
- 系统托盘常驻，左键切换显隐，右键功能菜单
- 托盘悬浮提示实时电量
- 连接时弹出 Win11 风格电量提示
- 低电量 / 极低电量提醒
- 关闭到托盘 / 开机自启
- 断连自动重连
- 跟随系统深浅色主题 + 手动切换

### 系统要求

- Windows 10 / 11
- .NET 10.0 Desktop Runtime（[下载](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)）— 或下载自带运行时的 `-NET` 版本
- 蓝牙适配器 + 已配对的 OPPO 耳机

### 快速开始

**直接运行：**

从 [Releases](https://github.com/Zhaoyi-ya/OPPO-Pods-For-Windows/releases) 下载：

| 文件 | 说明 |
|------|------|
| `OppoPodsWPF-NET.exe` | 自带 .NET 10 运行时，无需安装（170+ MB） |
| `OppoPodsWPF.exe` | 需安装 .NET 10 Desktop Runtime（~7 MB） |

**从源码编译：**

```bash
git clone https://github.com/Zhaoyi-ya/OPPO-Pods-For-Windows.git
cd OPPO-Pods-For-Windows
dotnet run
```

**发布单文件 exe：**

```bash
# 自带运行时（用户无需安装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true -o publish

# 依赖框架（需用户安装 .NET 10）
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

### 设备支持

| 型号 | 降噪 | 自适应 | 空间音效 | 空间音频 | 双设备 | 大师调音 |
|:---|:---:|:---:|:---:|:---:|:---:|:---:|
| Enco Free4 | ✅ | ✅ | ✅ | — | ✅ | ✅ |
| Enco X3 | ✅ | — | — | ✅ | ✅ | ✅ |
| Enco Air5 | ✅ | ✅ | ✅ | — | — | ✅ |
| Enco Air5 Pro | ✅ | ✅ | ✅ | — | ✅ | ✅ |
| Enco Air4 Pro | ✅ | ✅ | — | — | ✅ | ✅ (3 种预设，已实测) |
| Enco Air2 Pro | ✅ | — | — | — | — | ✅ |

其他名称包含 "OPPO" 的蓝牙设备可自动连接，使用通用功能集。若自动识别失败，可在设置中手动指定设备型号。

### 项目结构

```
OPPO-Pods-For-Windows/
├── OppoPodsWPF.csproj    # .NET 10 WPF + WinForms 项目
├── App.xaml/.cs          # 应用入口，主题设置
├── MainWindow.xaml/.cs   # 主界面 + 托盘 + 设置
├── ToastWindow.xaml/.cs  # 连接电量 / 低电量 / 断连提示弹窗
├── OppoProtocol.cs       # OPPO RFCOMM 协议定义
├── RfcommService.cs      # Winsock2 蓝牙连接与轮询
├── DeviceCapabilities.cs # 从 JSON 自动检测 + 手动覆盖
├── DeviceModels.json     # 设备白名单 (欢律 16.8.1 反编译)
├── EqModeNames.json      # EQ modeType → 显示名称映射 (40+ 模式)
├── PodState.cs           # 状态数据模型
└── Assets/               # 图标 & 图片素材
```

### 致谢

- [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) — OPPO 耳机私有协议逆向
- [1812z/OppoPods](https://github.com/1812z/OppoPods) — 功能实现参考
- [lepoco/wpfui](https://github.com/lepoco/wpfui) — Windows 11 Fluent Design UI 框架
- [@Dszsu](https://github.com/Dszsu) — 多设备 JSON 适配 & .NET 10 升级 (PR #3)
- [@yuanzexiong](https://github.com/yuanzexiong) — Enco Air4 Pro 支持 (PR #2)

### 开源协议

GPL-3.0
