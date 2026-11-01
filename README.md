# YT Mini Remote

A tiny floating remote for YouTube on Windows, by **The Master Sahib** ([themastersahib.com](https://themastersahib.com)).

![bar](https://img.shields.io/badge/Windows-10%20%7C%2011-blue) ![size](https://img.shields.io/badge/single-exe-red)

## Download
Get the latest **`YTMiniRemote.exe`** from the [Releases page](../../releases/latest). No installation needed — just run it.

> First run: Windows may show "Windows protected your PC". Click **More info → Run anyway** (the app is not code-signed).

## Features
- Small floating bar: Previous · Play/Pause · Next · Mute · Volume − / + · Hide
- Drag it anywhere; transparent when idle, dark on hover; colour matches your taskbar
- Play/Pause icon shows whether the video is playing
- Works with YouTube in **Chrome**, or with the **built-in YouTube player** (⌃ button)
  - Mini player sits on top of the bar and moves with it; right-click it for the full YouTube window
- **Azan auto-pause:** finds your location, pauses YouTube 2 minutes before each azan for 7 minutes, then resumes
  - Karachi method, Hanafi Asr; set your mosque's times from the tray menu → *Adjust azan times*
- **Start with Windows** (asked on first run, or from the tray menu)
- Tray icon: show/hide remote, azan settings, today's azan times, exit

## Build from source
Windows already includes the C# compiler. From the `src` folder:

```bat
build.bat
```

The built-in player uses Microsoft Edge WebView2 (preinstalled on Windows 10/11). The WebView2 SDK DLLs are embedded inside the exe.

## License
MIT
