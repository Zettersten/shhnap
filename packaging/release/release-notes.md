# Shnapp __RELEASE_TAG__ for Windows 11

Shnapp now records screen video and edits it alongside screenshots. Capture a moment, add the context it needs, and return to photos and videos in one local library.

## Highlights

- Record a rectangular region, a window, or a display with `Alt+Shift+2`, `Alt+Shift+4`, or `Alt+Shift+3`. Pause or resume from the recording bar. Choose computer audio, a microphone source, an optional camera video layer, and pointer visibility in Recording settings.
- Edit recordings on a compact timeline. Trim, split, and rearrange clips; add another video, images, live text, and an audio file on separate tracks. Move, resize, or crop visual layers on the preview, then add a transition across a cut and export an MP4.
- Find screenshots and recordings together in the local library. The empty gallery now offers direct screenshot and video actions; gallery previews load as you browse.
- Keep using the full-window photo editor for annotations, privacy covers, pasted images, crop, rotation, and flips.

## Capture and create

- Capture with `Ctrl+Shift+2` (region), `Ctrl+Shift+4` (window), or `Ctrl+Shift+3` (display), or start from the tray. Optionally assign Print Screen to a preferred capture mode in Settings.
- Take a camera photo with `Ctrl+Shift+5`, choose a camera in Settings, then crop and position the photo as an editable layer.
- Record a region, window, or display with `Alt+Shift+2`, `Alt+Shift+4`, or `Alt+Shift+3`. Choose computer audio, microphone, and camera sources separately. Recording continues if an optional camera is unavailable.
- Trim and split video, layer saved photos or recordings, edit text directly on the preview, adjust independent audio tracks, and export an MP4. The original recording stays intact.
- Keep the image in a full-window canvas beneath the translucent title bar and compact footer; the acrylic options panel floats above it as you zoom and pan.
- Add rich text, numbered or lettered steps, callouts, lines and arrows, shapes, pasted images, magnified details, and Cover, Blur, or Pixelate regions. Each placement tool has a cursor that identifies its mode.
- Format individual characters in text, and adjust a magnifier's zoom, size, border, and outline color.
- Crop with a steady crosshair and live size readout. Press `Esc` to cancel crop mode. Drag a selected element's rotation handle, or right-click to flip it or rotate it by a preset angle. **Flatten** fixes its rasterized appearance in place; Undo restores the editable element.
- Select several editable elements with `Ctrl+click`, or select all visible elements with `Ctrl+A`. A group outline shows the selection. Drag or use the arrow keys to move the group; hold Shift while dragging to constrain movement to one axis, or with an arrow key to move ten pixels. Dragging past the canvas extends it. Press `Esc` to clear the selection.
- Right-click a selected group to clone, delete, or flatten its elements together. `Ctrl+D` duplicates the group and `Delete` removes it. Undo restores the group after a bulk action.
- Zoom, pan, and adjust editable marks later.
- Copy the finished image, save a PNG, or use the Windows share sheet. Search, sort, and reopen photos and videos in the local library.
- Keep the app ready in the tray and optionally enable launch at sign-in.

Velopack and portable installs store the editable library and preferences in `%LOCALAPPDATA%\Shnapp` for your Windows user. The Microsoft Store edition uses a package-specific local cache and copies an existing default library on first launch. It leaves the original library in place. Back up an editable library before uninstalling the Store edition, because Windows may remove that package's local data on uninstall. Capture and editing need no account or internet connection. For sensitive details, use opaque Cover and share the exported PNG; the editable original remains in your local library. See the [privacy page](https://shhnap.com/privacy/).

Velopack-installed copies can check for a new stable release and apply it from Settings → About & Updates. Direct ZIP copies retain their existing verified ZIP updater. Scoop, WinGet, and Chocolatey installations update through their package managers after each new version reaches their catalogs. An existing ZIP copy does not become a Velopack install automatically; use the installer to switch channels.

The Windows installer and Shnapp executables are signed by Nenvy LLC. The release checks their publisher, signing profile, and timestamp before the download is published.

## Install

1. Download the Velopack installer for your Windows 11 processor: [x64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-__RELEASE_TAG__-x64.exe) or [ARM64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-__RELEASE_TAG__-arm64.exe).
2. Run the installer, then launch Shnapp from the Start menu. The installer bundles the .NET and Windows App SDK runtime dependencies. Settings → About & Updates can check for later installer updates.
3. To verify the installer, compare its PowerShell `Get-FileHash -Algorithm SHA256` value with the adjacent [x64 checksum](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-__RELEASE_TAG__-x64.exe.sha256) or [ARM64 checksum](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-__RELEASE_TAG__-arm64.exe.sha256).

Portable ZIPs remain available for existing direct installs and package managers: [x64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-x64.zip) or [ARM64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-arm64.zip). Extract the entire ZIP and run its root `Shnapp.exe`; keep `versions` and `current-version.txt` beside it. Both distributions include Shnapp's `LICENSE` and bundled components' notices.

The [Chocolatey Community Repository](https://community.chocolatey.org/packages/shnapp) offers `choco install shnapp` and `choco upgrade shnapp`. Its approved package may be older than this GitHub release until Chocolatey reviews an update. The [WinGet catalog](https://github.com/microsoft/winget-pkgs/pulls?q=is%3Apr+Zettersten.Shnapp) may still be reviewing Shnapp's first listing. Shnapp is [available in Microsoft Store](https://apps.microsoft.com/detail/9P7LX12F1V5L). Check the [download page](https://shhnap.com/download/) for current channel availability.

The ARM64 packages were cross-published on x64 CI and have **not** been run on ARM64 hardware yet.

For help, use [bug reports](https://github.com/Zettersten/shhnap/issues/new?template=01-bug.yml) or [feature requests](https://github.com/Zettersten/shhnap/issues/new?template=02-feature-request.yml).
