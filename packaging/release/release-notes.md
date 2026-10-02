# Shnapp __RELEASE_TAG__ for Windows 11

Shnapp lives in the Windows tray until you need a screenshot. Capture a region, a selected window, or the display under your pointer, then add context and share a finished PNG.

## What you can do

- Capture with `Ctrl+Shift+2` (region), `Ctrl+Shift+4` (window), or `Ctrl+Shift+3` (display), or start from the tray.
- Add text, numbered or lettered steps, lines and arrows, shapes, pasted images, and Cover, Blur, or Pixelate regions. Crop, zoom, pan, and adjust marks later.
- Copy the finished image, save a PNG, or use the Windows share sheet. Search, sort, and reopen captures in the local library.
- Keep the app ready in the tray and optionally enable launch at sign-in.

The portable ZIP stores its editable library and preferences in `%LOCALAPPDATA%\Shnapp` for your Windows user. The Microsoft Store build uses its package-specific local cache and copies an existing default portable library on first launch. It leaves the portable library in place. Back up your editable library before uninstalling the Store build, because Windows removes that package's local data on uninstall. Capture and editing need no account or internet connection. For sensitive details, use opaque Cover and share the exported PNG; the editable original remains in your local library. See the [privacy page](https://shhnap.com/privacy/).

The portable build checks GitHub for a newer stable release at most once per day, and you can check manually in Settings. It notifies you and offers a link to the release page; it does not download or install updates.

## Install

1. Download the ZIP for your Windows 11 processor: [x64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-x64.zip) or [ARM64](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-arm64.zip).
2. Extract the **entire** ZIP to a folder you choose. Run `Shnapp.exe` from that folder. The build contains its .NET and Windows App SDK runtime dependencies, so no separate runtime install is expected.
3. Optionally verify the ZIP against its adjacent [x64 SHA-256 file](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-x64.zip.sha256) or [ARM64 SHA-256 file](https://github.com/Zettersten/shhnap/releases/download/__RELEASE_TAG__/Shnapp-win-arm64.zip.sha256). On PowerShell, run `(Get-FileHash .\Shnapp-win-x64.zip -Algorithm SHA256).Hash` and compare it with the first value in the downloaded `.sha256` file.

The ZIP includes Shnapp's `LICENSE` and the bundled components' `THIRD_PARTY_NOTICES.txt` with their license and notice files.

The ARM64 package was cross-published on x64 CI and has **not** been run on ARM64 hardware yet.

For help, use [bug reports](https://github.com/Zettersten/shhnap/issues/new?template=01-bug.yml) or [feature requests](https://github.com/Zettersten/shhnap/issues/new?template=02-feature-request.yml).
