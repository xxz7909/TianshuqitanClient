# Tianshu Qitan Launcher

Windows IE WebBrowser shell for the Flash game entry:

```text
http://v5.t.imop.com/
```

## Requirements

- Windows
- IE WebBrowser control
- Flash ActiveX plugin registered in the system
- Prefer an isolated VM for old IE/Flash content

## Build

```powershell
.\build.ps1
```

Output:

```text
bin\Release\TianshuQitanLauncher.exe
```

## Run

```powershell
.\run.ps1
```

## Window Mode

The launcher starts as a normal bordered Windows window with a title bar. It fills the current screen working area, so it does not cover the Windows taskbar.

## Diagnostics

Navigation events are written to:

```text
bin\Release\logs\launcher.log
```

Mouse diagnostics are written to:

```text
bin\Release\logs\mouse-diagnostics.log
```

Hotkeys:

- `F8`: write one mouse diagnostic snapshot immediately
- `F9`: toggle automatic mouse diagnostic sampling

## Hotkeys

- `F11`: fit working area / restore window
- `Esc`: restore window
- `F5`: refresh
- `Ctrl+R`: refresh
- `Alt+Left`: back
- `Alt+Right`: forward

`F11`, `Esc`, and `F5` are registered as Win32 hotkeys so they can still work when Flash owns keyboard focus.

## Config

Edit `config.ini`:

```ini
[launcher]
gameUrl=http://v5.t.imop.com/
startFullScreen=false
startFitToWorkArea=true
width=1280
height=720
browserViewportMode=fixed
browserWidth=1005
browserHeight=600
browserAlign=topLeft
fixFlashPosition=true
topMost=false
scriptErrorsSuppressed=true
releaseMouseClip=true
mouseTopJumpGuardEnabled=false
mouseTopJumpThresholdPixels=120
mouseTopJumpReturnOffsetPixels=8
mouseDiagnosticsEnabled=true
mouseDiagnosticsIntervalMs=1000
```
