<p align="center">
  <img src="logo.png" alt="Shnapp logo" width="360">
</p>

<h1 align="center">Capture first. Think less.</h1>

<p align="center">
  Press a shortcut. Show what matters. Share a clearer picture.
</p>

<p align="center">
  <strong>Made for Windows 11 · Native WinUI · Local by default</strong>
</p>

Shnapp waits quietly in your tray until you need it. Capture part of your screen, add a little context, then copy the finished image or save a PNG. Every capture is a **shnapp**; making one is **shnapping**.

![The Shnapp editor with a captured sample window, numbered steps, an arrow, text, and an opaque redaction](src/Shnapp.Site/public/screenshots/editor.png)

*A real Shnapp editor session. The image stays front and center while the tools stay close.*

## Make a shnapp

| Shortcut | What it captures |
| --- | --- |
| `Ctrl+Shift+4` | A window you choose |
| `Ctrl+Shift+3` | The display under your pointer |
| `Ctrl+Shift+2` | A region you drag out |

You can start any capture from the tray icon or **New shnapp** in the app, too. Region capture currently uses a rectangle; a drawn free-form selection is on the [roadmap](ROADMAP.md).

## Explain it in a few clicks

Place text, numbered steps, lines and arrows, rectangles and ellipses, or an opaque redaction directly on your capture. Crop, move annotations, and undo or redo as you go. Window captures get a soft drop shadow by default.

When it looks right, **Copy** (`Ctrl+C`) puts the finished image on your clipboard, **Save PNG** (`Ctrl+S`) lets you choose where to export it, and **Done** returns Shnapp to the tray.

## Find it again

Shnapp keeps your captures in a local library, ready to search and reopen for more editing. Opt in to starting at sign-in if you want Shnapp waiting in the tray whenever Windows starts.

![The Shnapp library showing three locally saved captures with preview thumbnails, dates, and dimensions](src/Shnapp.Site/public/screenshots/library.png)

*Your shnapps stay on this PC, with previews that make the right capture easy to find.*

## Your captures stay yours

Shnapp saves its library and preferences under `%LOCALAPPDATA%\Shnapp` for your Windows user account. Capturing and editing require no account or internet connection. Copied and exported PNGs have opaque redactions baked in. The editable original remains in your local library, so share the finished PNG rather than a library file when something sensitive was covered.

## Get started

Shnapp is an early preview for Windows 11 on x64 and ARM64. A public installer or GitHub Release has not been published yet. You can [build it from source](#build-from-source) today. Follow the [roadmap](ROADMAP.md) for planned captions, resizing, richer annotation controls, blur and pixelation, true free-form capture, and optional AI assistance. The [product website source](src/Shnapp.Site) lives alongside the app.

### Build from source

On Windows 11, install the .NET SDK specified in `global.json` and a WinUI 3 development toolchain. Then run:

```powershell
dotnet build src\Shnapp.App\Shnapp.App.csproj -c Debug -p:Platform=x64
.\src\Shnapp.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Shnapp.exe
```

For ARM64, use `-p:Platform=ARM64`; its output is under `bin\ARM64\Debug\...\win-arm64`.
