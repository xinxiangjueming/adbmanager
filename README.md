# AdbManager

**[简体中文](#简体中文) | [English](#english)**

![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-blue)
![.NET](https://img.shields.io/badge/.NET-8.0--windows-purple)
![WinUI](https://img.shields.io/badge/WinUI%203-WindowsAppSDK%201.7-0078D4)
![License](https://img.shields.io/badge/license-MIT-green)

---

# 简体中文

> 基于 WinUI 3 的 Windows 桌面 ADB / Fastboot 管理工具。单文件免安装，内置谷歌官方 platform-tools，支持有线与无线连接、应用管理、文件管理、Fastboot 刷入与镜像提取、反向网络共享等。

## 特性

### 设备连接

- 设备列表每 3 秒自动刷新，有线 / 无线统一管理
- Android 11+ 无线调试：配对码两段式连接（pair → connect），自动区分配对端口与连接端口
- 原生 mDNS 局域网发现（Zeroconf，`_adb-tls-connect` / `_adb-tls-pairing`），不依赖 adb server 自带的 mDNS 后端（该后端在 Windows 上经常不可用）
- USB 一键转无线：自动查询手机 WLAN IP → `tcpip 5555` → 自动连接，全程免手输
- 可连接 Recovery / Sideload 等特殊模式设备

### 应用管理

- 安装 APK / APKS 拆分包（`.apks` / `.xapk` / `.zip` 自动解包并按 base → split 顺序安装）
- 提取 APK（含拆分包）到电脑
- 卸载、冻结 / 解冻（`pm disable-user`）、强制停止、清除数据
- 按第三方 / 系统 / 已冻结过滤，支持包名搜索

### 文件管理

- 浏览设备目录（正确处理 `/sdcard` 等符号链接，点击即可进入）
- 上传 / 下载、重命名 / 删除 / 新建文件夹、复制 / 剪切 / 粘贴

### Fastboot

- 刷入镜像、擦除分区、`getvar` 全量信息、重启到各模式
- 通过 adb `dd` 提取分区镜像到电脑（需 root 权限的分区会明确报错提示）

### 工具箱

- 截图直接保存到电脑；录屏（最长 180 秒）结束后自动回传
- Shell 命令执行，输出实时回显
- 反向共享电脑网络：HTTP 代理 + `adb reverse` + 自动设置设备系统代理
- 一键激活 Shizuku / Scene
- WiFi 检测修复（重置 captive portal 检测源并重启 WiFi）
- TWRP Recovery 高级操作：一键清除锁屏密码、跳过谷歌开机验证

### 设备信息

- 品牌 / 型号 / 代号 / Android 版本 / SDK / 安全补丁 / 指纹 / ABI
- 电池（电量、健康度、温度、电压、当前与设计容量）、屏幕、内存、存储、网络、内核、开机时长
- 设备连上后自动刷新，无需手动点击

### 界面

- miuix / HyperOS 设计语言：大圆角卡片、胶囊按钮、成对深浅色画笔
- 深浅色跟随系统，也可手动固定浅色 / 深色，实时切换无需重启
- 页面切换、列表增删、长操作加载覆盖层均有过渡动画
- 内置 7 种语言：简体中文 / English / 日本語 / 한국어 / Deutsch / Français / Español（跟随系统自动选择，可在设置中手动指定）

## 下载

前往 [Releases](../../releases) 页面下载 `AdbManager.exe`（约 276 MB）：

- 单文件、免安装、双击即用（自包含 .NET 8 + WinUI 运行时，无需安装任何依赖）
- 内置谷歌官方 platform-tools（adb 37.0.1 + fastboot），首次运行时释放到 `%LOCALAPPDATA%\AdbManager\adb\`
- 首次启动需自解压，略慢属正常现象
- 个别杀毒软件可能对单文件自解压程序误报，请加入白名单或从源码自行构建

## 使用说明

### 无线调试（Android 11+）

1. 手机：设置 → 开发者选项 → 开启「无线调试」→ 点进该菜单 →「使用配对码配对设备」
2. 本工具「设备」页：填入**配对地址**（IP:端口）与 **6 位配对码** → 点「配对并连接」。注意：配对弹窗里的端口每次都会变，它不是连接端口
3. 配对成功后，回手机无线调试主页面查看「IP 地址和端口」，填入连接地址 →「连接」。配对只需一次，之后直接连接
4. 也可以直接点「扫描局域网」自动发现设备，点击扫描结果即可连接
5. USB 已连接的情况下，直接点「USB 一键转无线」最省事

### 无线调试（Android 10 及以下）

设备页选中 USB 连接的设备 → 切换到 TCP 模式(5555) → 拔线 → 连接 `IP:5555`。

### Fastboot

手机重启到 Bootloader（Fastboot 模式）后，「Fastboot」页会列出设备；选择分区、选择镜像文件后刷入。**刷机有风险，操作前请确认分区与镜像匹配。**

## 从源码构建

### 环境要求

- Windows 10 17763+
- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)（Windows App SDK 与依赖包由 NuGet 自动还原）
- **无需安装 Windows SDK / Visual Studio**：本项目的 UI 全部以纯 C# 代码构建，不使用 XAML 文件，XAML 编译器不参与构建

### 准备官方 platform-tools（构建前置）

adb / fastboot 二进制不随仓库分发，需自行从谷歌官方下载并放入 `tools/adb/`：

```powershell
# 下载
Invoke-WebRequest https://dl.google.com/android/repository/platform-tools-latest-windows.zip -OutFile platform-tools.zip
# 解压后，将以下 5 个文件复制到 tools/adb/
# adb.exe、AdbWinApi.dll、AdbWinUsbApi.dll、fastboot.exe、libwinpthread-1.dll
```

### 构建

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
# 或手动执行：
dotnet publish src/AdbManager -c Release -r win-x64 --self-contained true -o dist
```

产物：`dist/AdbManager.exe`（单文件）。

## 项目结构

```
repo/
├─ src/AdbManager/        应用源码（WinUI 3，纯 C# 构建 UI）
│  ├─ Services/           adb / fastboot 调用、mDNS 发现、多语言、反向共享代理等
│  ├─ Views/              各页面（设备 / 信息 / 工具 / 应用 / 文件 / Fastboot / 日志 / 设置）
│  ├─ Models/             设备、文件、应用包等数据模型
│  ├─ Ui/                 miuix 设计体系：配色画笔与控件工厂
│  └─ Strings/            7 种语言的 resw 资源
├─ tools/adb/             谷歌官方 platform-tools（自行下载，见上）
├─ publish.ps1            一键发布脚本
└─ dist/                  发布输出
```

## 技术要点

- **无 XAML 的 WinUI 3**：全部 UI 由 C# 代码构建（`App` 实现 `IXamlMetaDataProvider` 并在 `OnLaunched` 挂载 `XamlControlsResources`），彻底绕开 XAML 编译器与 Windows SDK winmd 依赖，任何装了 .NET SDK 的机器都能直接构建
- **单文件发布的关键配置**：`WindowsAppSDKSelfContained` + `EnableMsixTooling`（把 resources.pri 嵌入 exe）+ `IncludeNativeLibrariesForSelfExtract`；入口需设置 `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` 指向单文件解压目录，否则 MRM 找不到框架资源
- **多语言实现**：resw 以嵌入资源随程序分发，运行时直接解析 XML（不依赖 PRI/MRT）；MSBuild 会把资源目录名 `zh-CN` 规范化为 `zh_CN`，加载时已做归一化；同语言多 resw 文件逐键合并
- **mDNS 发现**：Zeroconf 直接在局域网查询 `_adb-tls-connect` / `_adb-tls-pairing`，与 Android 端 NsdManager 同思路；`adb mdns check` 在多数 Windows 环境返回 `0.0.0`（不可用），因此不作为主要发现手段
- **NavigationView 左栏闪屏规避**：官方缺陷 [microsoft-ui-xaml#9370](https://github.com/microsoft/microsoft-ui-xaml/issues/9370)（窗格与内容宽度动画不同步），通过统一窗格 / 内容 / 窗体为同一不透明底色规避

## FAQ

**Q：扫描不到无线设备？**

手机必须打开「无线调试」开关（它与 USB 调试是两个独立开关，开关不开就不会广播 mDNS）；电脑与手机需在同一 Wi-Fi，路由器不能开 AP 隔离；首次扫描请在 Windows 防火墙弹窗中选择「允许」（mDNS 依赖 UDP 5353 组播）。USB 已连接时直接用「USB 一键转无线」。

**Q：和 Android Studio 或其它 adb 冲突？**

启动时检测到 adb 版本冲突会自动结束残留 adb 进程并重启服务（注意：会一并关闭 Android Studio 正在使用的 adb server）。

**Q：提取分区镜像失败？**

`dd` 读取部分分区（如 modem、xbl 等）需要 root 权限，未 root 的设备会失败。

**Q：程序首次启动慢？**

单文件 exe 启动时需自解压运行时，属于正常现象，后续启动会快一些。

## 免责声明

刷入或擦除分区可能导致设备无法启动；冻结或卸载系统应用可能导致系统不稳定。请在充分了解操作后果的前提下使用，作者不对任何设备损坏或数据丢失负责。本工具仅用于管理你拥有合法授权的设备。

## 许可证

[MIT](LICENSE)

内置的 Google platform-tools（adb / fastboot）遵循 [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0)；Zeroconf、Windows App SDK、.NET 等第三方组件遵循其各自的开源协议。

---

# English

> A Windows desktop ADB / Fastboot manager built with WinUI 3. Ships as a single install-free executable with Google's official platform-tools bundled — wired and wireless connections, app management, file management, Fastboot flashing with partition image extraction, reverse tethering, and more.

## Features

### Device connection

- Device list auto-refreshes every 3 seconds; wired and wireless devices managed in one place
- Android 11+ wireless debugging: two-step pairing-code flow (pair → connect), with pairing port and connect port handled separately
- Native mDNS discovery on the LAN (Zeroconf, `_adb-tls-connect` / `_adb-tls-pairing`), independent of the adb server's built-in mDNS backend (which is often broken on Windows)
- One-click USB-to-Wi-Fi: detects the phone's WLAN IP automatically → `tcpip 5555` → auto-connect, no manual typing
- Connects to devices in special modes such as Recovery / Sideload

### App management

- Install APK / split APKS bundles (`.apks` / `.xapk` / `.zip` auto-unpacked and installed base → split)
- Extract APK (including splits) to the PC
- Uninstall, freeze / unfreeze (`pm disable-user`), force stop, clear data
- Filter by third-party / system / disabled, with package name search

### File management

- Browse device directories (handles symlinks like `/sdcard` correctly — click to enter)
- Upload / download, rename / delete / new folder, copy / cut / paste

### Fastboot

- Flash images, erase partitions, full `getvar` dump, reboot to every mode
- Extract partition images to the PC via adb `dd` (root-only partitions report a clear error)

### Toolbox

- Screenshots saved straight to the PC; screen recording (up to 180 s) pulled back automatically when finished
- Shell command execution with live output
- Reverse tethering: HTTP proxy + `adb reverse` + automatic device system-proxy setup
- One-click Shizuku / Scene activation
- Wi-Fi check fix (resets captive portal URLs and restarts Wi-Fi)
- TWRP Recovery utilities: clear the screen lock or skip Google setup (FRP) in one click

### Device info

- Brand / model / codename / Android version / SDK / security patch / fingerprint / ABI
- Battery (level, health, temperature, voltage, current & design capacity), display, memory, storage, network, kernel, uptime
- Refreshes automatically when a device connects — no manual clicking

### Interface

- miuix / HyperOS design language: large-radius cards, pill buttons, paired light/dark brushes
- Theme follows the system, or pin light / dark manually — switches instantly, no restart
- Animated page transitions, list add/remove animations, and a busy overlay for long operations
- 7 built-in languages: 简体中文 / English / 日本語 / 한국어 / Deutsch / Français / Español (follows the system, manually selectable in Settings)

## Download

Grab `AdbManager.exe` (~276 MB) from the [Releases](../../releases) page:

- Single file, no installation, just double-click (self-contained .NET 8 + WinUI runtime, no dependencies to install)
- Bundles Google's official platform-tools (adb 37.0.1 + fastboot), extracted to `%LOCALAPPDATA%\AdbManager\adb\` on first run
- First launch is slower due to self-extraction — this is normal
- Some antivirus products may flag single-file self-extracting executables; whitelist it or build from source

## Usage

### Wireless debugging (Android 11+)

1. On the phone: Settings → Developer options → enable "Wireless debugging" → open that menu → "Pair device with pairing code"
2. In the app's "Devices" page: enter the **pairing address** (IP:port) and the **6-digit pairing code** → "Pair & connect". Note: the port shown in the pairing dialog changes every time — it is not the connect port
3. Once paired, check "IP address & port" on the phone's wireless debugging main screen, enter it as the connect address → "Connect". Pairing is one-time; afterwards just connect
4. Or simply click "Scan LAN" to discover devices automatically and click a result to connect
5. If the phone is plugged into USB, the easiest path is the "USB to Wi-Fi" one-click button

### Wireless debugging (Android 10 and below)

Select the USB-connected device on the Devices page → switch to TCP mode (5555) → unplug the cable → connect to `IP:5555`.

### Fastboot

After rebooting the phone to the Bootloader (Fastboot mode), devices appear on the "Fastboot" page; pick a partition and an image file, then flash. **Flashing is risky — double-check that the partition matches the image before proceeding.**

## Build from source

### Requirements

- Windows 10 17763+
- [.NET SDK 8.0+](https://dotnet.microsoft.com/download) (Windows App SDK and NuGet packages restore automatically)
- **No Windows SDK / Visual Studio required**: the entire UI is built in pure C# without XAML files — the XAML compiler never runs

### Prepare the official platform-tools (build prerequisite)

The adb / fastboot binaries are not committed to the repository. Download them from Google and place them in `tools/adb/`:

```powershell
# Download
Invoke-WebRequest https://dl.google.com/android/repository/platform-tools-latest-windows.zip -OutFile platform-tools.zip
# After extracting, copy these 5 files into tools/adb/:
# adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll, fastboot.exe, libwinpthread-1.dll
```

### Build

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
# or manually:
dotnet publish src/AdbManager -c Release -r win-x64 --self-contained true -o dist
```

Output: `dist/AdbManager.exe` (single file).

## Project layout

```
repo/
├─ src/AdbManager/        App source (WinUI 3, pure C# UI)
│  ├─ Services/           adb / fastboot invocation, mDNS discovery, localization, reverse-tether proxy, etc.
│  ├─ Views/              Pages (Devices / Info / Tools / Apps / Files / Fastboot / Logs / Settings)
│  ├─ Models/             Data models: devices, files, packages
│  ├─ Ui/                 miuix design system: palette brushes and control factory
│  └─ Strings/            resw resources for 7 languages
├─ tools/adb/             Google's official platform-tools (download yourself, see above)
├─ publish.ps1            One-click publish script
└─ dist/                  Publish output
```

## Technical notes

- **No-XAML WinUI 3**: the whole UI is built from C# code (`App` implements `IXamlMetaDataProvider` and attaches `XamlControlsResources` in `OnLaunched`), completely bypassing the XAML compiler and Windows SDK winmd dependencies — any machine with the .NET SDK can build it
- **Key single-file publish settings**: `WindowsAppSDKSelfContained` + `EnableMsixTooling` (embeds resources.pri into the exe) + `IncludeNativeLibrariesForSelfExtract`; the entry point must set `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` to the single-file extraction directory, otherwise MRM cannot locate framework resources
- **Localization**: resw files ship as embedded resources and are parsed at runtime (no PRI/MRT dependency); MSBuild normalizes the folder name `zh-CN` to `zh_CN`, which is converted back at load time; multiple resw files per language are merged key by key
- **mDNS discovery**: Zeroconf queries `_adb-tls-connect` / `_adb-tls-pairing` directly on the LAN — the same idea as NsdManager on Android; `adb mdns check` returns `0.0.0` (unavailable) on most Windows setups, so it is not used as the primary discovery path
- **NavigationView pane-flicker workaround**: upstream bug [microsoft-ui-xaml#9370](https://github.com/microsoft/microsoft-ui-xaml/issues/9370) (pane and content width animations out of sync), mitigated by using one opaque background color for the pane, the content area, and the window

## FAQ

**Q: Wireless scan finds nothing?**

The phone's "Wireless debugging" switch must be on (it is a separate switch from USB debugging — no broadcast without it); the PC and phone must be on the same Wi-Fi with no AP isolation on the router; click "Allow" on the first Windows Firewall prompt (mDNS needs UDP 5353 multicast). If the phone is plugged into USB, just use the "USB to Wi-Fi" one-click button.

**Q: Conflicts with Android Studio or another adb?**

On startup, adb version conflicts are detected automatically — stale adb processes are killed and the server restarted (note: this also kills the adb server used by Android Studio).

**Q: Partition image extraction fails?**

`dd` requires root to read some partitions (modem, xbl, etc.), so it fails on non-rooted devices.

**Q: First launch is slow?**

The single-file exe self-extracts the runtime on first start — normal, and subsequent launches are faster.

## Disclaimer

Flashing or erasing partitions can leave a device unbootable; freezing or uninstalling system apps may destabilize the system. Use this tool only if you fully understand the consequences; the author is not liable for any device damage or data loss. For managing devices you are authorized to control only.

## License

[MIT](LICENSE)

The bundled Google platform-tools (adb / fastboot) are licensed under the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0); third-party components such as Zeroconf, Windows App SDK, and .NET remain under their respective licenses.
