# Inkly

A minimal, no-toolbar screen annotation tool for Windows. Hit a hotkey, draw on your screen, and pin annotated snapshots into editable windows you can minimize and return to. Built for screen recordings and quick markup.

Inkly lives in the system tray — its icon shows the current pen color.

## Install
1. Download `Inkly.exe` from the [Releases](https://github.com/Danaor/Inkly/releases) page.
2. Run it. It sits in the system tray. Press **Ctrl+Q** to start drawing.

Requires Windows 10/11 (uses the built-in .NET Framework 4.x — nothing else to install).

## Controls

| Action | Key / mouse |
|---|---|
| Start drawing · cycle color (black → red → yellow → green → blue) | **Ctrl+Q** |
| Draw | left-drag |
| Thickness | mouse wheel |
| Zoom (centered on cursor) · pan | Ctrl + wheel · middle-drag |
| Undo · exit | Ctrl+Z · Esc |
| Pin → maximized window · minimized window | **P** · **O** |
| More: pin / eraser / highlighter / clear / reset zoom / exit | right-click |

Capture is the single monitor under the cursor, and it excludes the taskbar.

**Tray icon:** start/stop drawing, highlighter, custom color, thickness.

**Pinned snip windows** stay editable — keep drawing, erase strokes, undo. Save as PNG (**Ctrl+S**) or copy (**Ctrl+C**). Inside a snip window: colors `Ctrl+2`–`Ctrl+6`, eraser `E`, reset zoom `0`, clear `Delete`, or right-click for the menu.

## Build from source
No Visual Studio or .NET SDK needed — it compiles with the .NET Framework C# compiler that ships with Windows:

```
build.bat
```

which runs:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:Inkly.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Program.cs
```
