# Changelog

## 2.5.1 – 2026-10

### New
- **Videos tab** – start video players with HDR already on, so NVIDIA RTX Video HDR converts SDR videos from the first frame:
  - finds installed players automatically (MPC-HC, MPC-BE, VLC, PotPlayer, mpv.net, Kodi) or add any other,
  - **Default app…**: registers "<Player> (with HDR)" for video files – set it as default once, then every double-click on a video plays it with HDR,
  - desktop shortcut "<Player> (HDR)" (also takes drag & drop) and a right-click entry "Play with HDR",
  - HDR switches back off about a second after the player window is closed.
  - Tested with RTX Video HDR: MPC-HC (recommended), VLC, PotPlayer – the player settings are in the README.
- **Launch with HDR** for games: HDR on first, then the game starts – for games that hide their HDR option (or where Windows Auto HDR does not start) when HDR was off at launch. Steam launch option (one-click copy) or desktop shortcut, in the game's Options. Follows launcher stubs (Ubisoft, Rockstar, …) to the real game.
- **No HDR while OBS is running** (Settings): keeps games and videos in SDR while OBS is open, and switches back to SDR when OBS starts – no more blown-out recordings. Manual override still works.
- **More game libraries:** Ubisoft Connect, EA app and Rockstar Games Launcher (no duplicates with Steam).
- **Games that switch HDR themselves** are no longer fought: when a full-screen game turns HDR on by itself, the app keeps HDR on until the game closes.
- **Log:** a line whenever Windows HDR changes without the app (game, Win+Alt+B, Windows Settings), with the window in front; a `HINT` when a game runs on a monitor that is not selected for switching.
- **Windows on ARM** builds (experimental, untested).

### Improved
- **Settings page reorganised:** General · Monitors · Switching back to SDR · YouTube in browser · Videos / RTX Video HDR · Display fixes · Advanced – each with a short description. Video detail options only show while video switching is on; greyed-out switches say why.
- **Browser script status** in Settings ("Detected ✓" / "Not detected yet") with a link to the installation guide.
- **Reset to defaults** (Settings › Advanced) – keeps your games and per-game options.
- Maximized window on large screens: content stays in a centred column – easier to read, smoother scrolling.
- "Start with Windows" and the Videos tab entries repair themselves when the app is moved or updated into another folder.
- The log file stays small (rotates to `log.old.txt` at 2 MB).
- Own taskbar identity: the app keeps its own icon even when a "(HDR)" shortcut with a game icon exists.

### Fixed
- Elden Ring, Alan Wake 2 and Cyberpunk 2077 showed up as HDR games although not installed (leftover examples) – removed for new and existing users (kept if the game really is installed).
- Premiere Pro never switched to SDR (wrong process name); After Effects added.
- mpv.net, MPC-BE and Kodi added to the video players.
- The coloured status card could stay coloured although HDR was off (when HDR turned off again within 1.5 s).
- "&" missing in Settings headings; video option rows had a smaller height on scaled displays.
- Video players: RTX Video HDR now also gets the "display changed" nudge after HDR comes on.

## 2.5.0 – 2026-10-15

First public release: automatic HDR for games (Steam, Epic, GOG, Xbox), YouTube HDR via a Tampermonkey script, local HDR videos, optional HDR for all videos for NVIDIA RTX Video HDR, per-monitor switching, manual override, keyboard shortcuts and command line.
