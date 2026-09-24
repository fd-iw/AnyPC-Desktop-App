# AnyPC Desktop (Windows)

Control this Windows PC from your iPhone with the **AnyPC** iOS app ([fd-iw/AnyPC-IPA](https://github.com/fd-iw/AnyPC-IPA)).

- Live screen with mouse cursor, multi-monitor
- Tap / trackpad mouse control, right-click, drag, scroll
- Full keyboard: text, Ctrl/Alt/Shift/Win combos, Esc, Tab, arrows, F1–F12
- Browse the PC's files, download to the phone and upload from the phone
- Lock, sleep, restart, shut down, sign out, volume and media keys

The phone and the PC must be on the **same Wi-Fi / local network**.

## Install

1. Download `AnyPC-Setup.exe` from the latest successful **Build Windows app** run
   (Actions tab → run → *Artifacts* → `AnyPC-Windows`) or from Releases.
2. Run it. Windows SmartScreen may warn because the installer isn't code-signed: click
   **More info → Run anyway**.
3. AnyPC starts and shows a window with a **QR code** and a **6-digit PIN**. It keeps running
   in the notification area (tray) when you close the window.

`AnyPC-Portable.exe` runs without installing. The first time, Windows will ask to allow it
through the firewall — tick **Private networks** and click **Allow**.

## Pair your iPhone

Open AnyPC on the iPhone, then either:

- tap **Scan QR code** and point the camera at the QR code in the AnyPC window, or
- tap your PC in the *Nearby* list (or add it by IP address) and type the PIN.

After pairing, the phone reconnects automatically — no PIN needed. Remove a phone any
time from **Paired devices**.

## Security

- All traffic is encrypted with TLS. The PC's certificate is generated on first run and
  the phone *pins* it during pairing, so another machine cannot impersonate your PC.
- Pairing requires the PIN shown on the PC's screen (changes every 5 minutes and after
  each pairing). Five wrong PINs lock pairing for a minute, doubling each time.
- Each phone gets its own secret token; only a hash is stored on the PC
  (`%APPDATA%\AnyPC\devices.json`).
- **Pause remote control** (tray menu) blocks all connections immediately.

## Limitations

- Windows does not let normal apps control the **lock screen, Ctrl+Alt+Del or UAC prompts**
  (the "secure desktop"); the screen freezes on the last frame while one is shown.
- Windows run as administrator (e.g. Task Manager) ignore input from a non-elevated AnyPC.
  Right-click AnyPC → *Run as administrator* if you need to control them.
- Same network only. For remote access, put both devices on a VPN such as Tailscale and
  add the PC by its VPN IP address.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| PC doesn't appear on the phone | Make sure both are on the same Wi-Fi (not a guest network); add it manually by the IP shown in the AnyPC window. |
| Phone says it can't connect | Set the Wi-Fi network to **Private** in Windows Settings → Network & internet, or allow `AnyPC.exe` in Windows Defender Firewall. |
| "Port 47800 in use" | Another copy or program is using the port; quit it and restart AnyPC. |
| Laggy screen | Lower *Quality* in the iPhone app's settings, or use 5 GHz Wi-Fi. |

Logs: `%APPDATA%\AnyPC\anypc.log`.

## Build from source

Requires the .NET 8 SDK.

```powershell
dotnet test tests/AnyPC.Core.Tests
dotnet publish src/AnyPC.Windows -c Release -r win-x64 -o publish   # -> publish\AnyPC.exe
iscc /DSourceDir=..\publish installer\AnyPC.iss                        # -> out\AnyPC-Setup.exe
```

Layout:

- `src/AnyPC.Core` — cross-platform protocol, pairing, TLS WebSocket server, Bonjour, file service
- `src/AnyPC.Windows` — tray app, GDI screen capture, SendInput, power actions
- `tests/AnyPC.Core.Tests` — end-to-end tests with an in-process phone client
- `docs/PROTOCOL.md` — wire protocol shared with the iOS app
