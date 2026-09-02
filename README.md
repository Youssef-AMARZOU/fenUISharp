# DynamicISLAND_windows

Dynamic Island for Windows - like iPhone / macOS notch, now stable on Windows.

Forked from [FlorianButz/DynamicWin](https://github.com/FlorianButz/DynamicWin) and [FlorianButz/fenUISharp](https://github.com/FlorianButz/fenUISharp) - fixed crash and z-order.

![DynamicWin](https://img.shields.io/badge/Windows-11-blue) ![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)

## What is it?
A small notch at the top center of your screen. Shows media controls, calendar, file tray, bluetooth - stays on top until you close it.

## What's fixed?
- **Crash after ~5s** (`WaitForGpu` D3D12 fence) - now stays alive until you kill it
- **Behind other windows** - now stays on top, no need to minimize everything to see it
- Build with .NET 9, tested on Windows 11

Original DynamicWin V2 was marked unstable - this fork makes it usable daily.

## Install
1. Go to [Releases](../../releases)
2. Download `DynamicWinPortable.zip` or `DynamicWinSetup.exe`
3. Run `DynamicWinV2.exe` - notch appears top center

Or build yourself:
```bash
dotnet build fenUI.sln -c Release
```

## Usage
- Hover/click the notch to expand
- Scroll to switch views (media, calendar, etc.)
- Drag files to tray

## Credits
Original by [Florian Butz](https://github.com/FlorianButz) - [fenUISharp](https://github.com/FlorianButz/fenUISharp) - licensed CC BY-SA 4.0

Fix by [@Youssef-AMARZOU](https://github.com/Youssef-AMARZOU) - PR https://github.com/FlorianButz/fenUISharp/pull/6
