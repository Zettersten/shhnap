# Shnapp __RELEASE_TAG__ for Windows 11

Shnapp lives in the Windows tray until you need a screenshot. Capture a region, a selected window, or the display under your pointer, then add context and share a finished PNG.

## What you can do

- Capture with `Ctrl+Shift+2` (region), `Ctrl+Shift+4` (window), or `Ctrl+Shift+3` (display), or start from the tray.
- Add text, numbered or lettered steps, lines and arrows, shapes, pasted images, and Cover, Blur, or Pixelate regions. Each placement tool has a cursor that identifies its mode.
- Crop with a steady crosshair and live size readout. Press `Esc` to cancel crop mode. Right-click a placed element, including a pasted or gallery image, and choose **Flatten** to fix its rasterized appearance in place; Undo restores the editable element.
- Zoom, pan, and adjust editable marks later.
- Copy the finished image, save a PNG, or use the Windows share sheet. Search, sort, and reopen captures in the local library.
- Keep the app ready in the tray and optionally enable launch at sign-in.

Velopack and portable installs store the editable library and preferences in `%LOCALAPPDATA%\Shnapp` for your Windows user. Unpublished Microsoft Store package candidates use a package-specific local cache and copy an existing default library on first launch. They leave the original library in place. Back up an editable library before uninstalling a Store test build, because Windows removes that package's local data on uninstall. Capture and editing need no account or internet connection. For sensitive details, use opaque Cover and share the exported PNG; the editable original remains in your local library. See the [privacy page](https://shhnap.com/privacy/).

Velopack-installed copies can check for a new stable release and apply it from Settings → About & Updates. Direct ZIP copies retain their existing verified ZIP updater, and Scoop installations update through Scoop. An existing ZIP copy does not become a Velopack install automatically; use the installer to switch channels.

## Install

1. Download the Velopack installer for your Windows 11 processor: [x64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/ErikZettersten.Shnapp.x64-win-x64-Setup.exe) or [ARM64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/ErikZettersten.Shnapp.arm64-win-arm64-Setup.exe).
2. Run the installer, then launch Shnapp from the Start menu. The installer bundles the .NET and Windows App SDK runtime dependencies. Settings → About & Updates can check for later installer updates.
3. To verify the installer, compare its PowerShell `Get-FileHash -Algorithm SHA256` value with the adjacent [x64 checksum](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/ErikZettersten.Shnapp.x64-win-x64-Setup.exe.sha256) or [ARM64 checksum](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/ErikZettersten.Shnapp.arm64-win-arm64-Setup.exe.sha256).

Portable ZIPs remain available for existing direct installs and package managers: [x64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-x64.zip) or [ARM64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-arm64.zip). Extract the entire ZIP and run its root `Shnapp.exe`; keep `versions` and `current-version.txt` beside it. Both distributions include Shnapp's `LICENSE` and bundled components' notices.

WinGet's first listing is pending, Chocolatey has not approved Shnapp, and the Microsoft Store release is not ready. Check the [download page](https://shhnap.com/download/) for current channel availability.

The ARM64 packages were cross-published on x64 CI and have **not** been run on ARM64 hardware yet.

For help, use [bug reports](https://github.com/Zettersten/shhnap/issues/new?template=01-bug.yml) or [feature requests](https://github.com/Zettersten/shhnap/issues/new?template=02-feature-request.yml).
