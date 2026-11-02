# PocketDeck: handoff

Phone-as-Stream-Deck for Windows. Built 2026-11-02 for Rubirosa.

## Files
| What | Where |
|---|---|
| PC app source (C# 5, compiled on install by csc.exe) | `app/PocketDeck.cs` |
| Phone + PC UI (built file) | `app/index.html` |
| UI source | `dev/index.src.html` (edit this directly; `head.html` + `redesign_patch.py` were one-time generators, do not rerun) → `python3 dev/build.py <node_modules>` inlines icons (lucide-static@0.400.0), QR lib (qrcode-generator@1.4.4), keep-awake mp4, Gloock + Geist fonts |
| Installer | `Install.bat` → `install.ps1` (build exe, firewall rule via one UAC, shortcuts, HKCU Run key) |
| Tests | `dev/test-e2e.js` + `dev/test-look.js` + `dev/test-v12.js` (Playwright; run exe under mono with `--headless --dry-run --data DIR --app app`) |

## How it works
- `PocketDeck.exe` = TcpListener HTTP server on port 8765 (+ next 9 if busy) + tray icon.
- Data: `%LOCALAPPDATA%\PocketDeck\data\config.json` (port, PIN, device tokens), `profile.json` (pages/keys).
- API (all need header `X-Deck: 1`; non-localhost needs `X-Deck-Token`): `/api/hello`, `/api/pair`, `/api/profile` GET/PUT, `/api/run`, `/api/state` (volume, mute, mic, profile rev), `/api/apps`, `/api/audio-devices`, `/api/sessions` (apps playing sound), `/api/sounds` GET list / POST {name,data base64} upload, `/api/icon` (POST {path} or {url} -> PNG data URL of the app icon / site favicon), `/api/connect` + `/api/unpair-all` (localhost only).
- Security: Host header must be IP/localhost/PC name (anti DNS-rebinding); custom header blocks cross-site requests.
- Keys use SendInput; volume/mic use Core Audio COM.

## Not done / ideas
- Native iOS app (user has an Apple Developer account) for true full screen + keep-awake.
- OBS WebSocket integration (scene state on keys), per-app auto page switching, dials.
- Tested on Linux/mono with dry-run; Windows-only parts (SendInput, Core Audio, tray) need a real PC check.

## v1.1 redesign (2026-11-02)
- Design: Hallmark theme Kiln (Gloock stands in for Boska, which is blocked). Stamp + waivers at top of CSS.
- Lint: `node <hallmark>/scripts/sloplint.mjs app/index.html --genre atmospheric --scope app` -> 0 fail.
- Old v1 profiles migrate (old hex colors -> named key lights).
- App icon grab (shell image factory) only tested in dry-run; check on a real PC.

## v1.2 Stream Deck features (2026-11-02)
- Research + feature map: `RESEARCH.md`.
- PC: `Sys` (cached live state: cpu, ram, battery, brightness, activeApp, running, per-app volumes), `WinOps` (window, app focus/close/kill, mouse), `Brightness` (WMI, needs /r:System.Management.dll), `Sounds` (MCI on its own STA thread, files in data\sounds), Audio: sessions (IAudioSessionManager2), devices + IPolicyConfig default switch.
- New actions: window, mouse, appvol, output, brightness, sound; open has `mode` focus/close/kill.
- Phone: `dial` (drag, queued steps), `widget` (clock/timer/stopwatch/counter/cpu/ram/battery/weather via Open-Meteo from the phone), `key.dbl`, page `apps` for auto-switch (skipped on the PC editor).
- Untested on real Windows: per-app volume COM, IPolicyConfig, WMI brightness, MCI sounds, window focus.
