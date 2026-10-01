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

![The Shnapp editor with a captured sample window, numbered steps, and contextual step options](src/Shnapp.Site/public/screenshots/editor.png)

*A real Shnapp editor session. Your capture stays in view while the tools stay close.*

## Make a shnapp

| Shortcut | What it captures |
| --- | --- |
| `Ctrl+Shift+4` | A window you choose |
| `Ctrl+Shift+3` | The display under your pointer |
| `Ctrl+Shift+2` | A region you drag out |

You can start any capture from the tray icon or **New shnapp** in the app. Region capture currently uses a rectangle; a drawn free-form selection is planned.

The grabber freezes the visible desktop when capture starts, so moving content holds still while you choose. A crosshair and live pixel measurements help you draw a region; hold Shift for a square. A bright pulse frames the window or display you are about to capture. Window shnapps use the visible pixels from that frozen moment, including anything overlapping the window.

## Explain it in a few clicks

The editor gives your capture room to breathe. Place a mark and it stays selected: changes in the side panel update that mark right away. Select an earlier mark to adjust it, or drag its handles to resize it. When nothing is selected, your choices set up the next mark.

Paste with `Ctrl+V` to turn copied words into an editable text mark or a copied picture into a movable image. If either reaches past the capture, Shnapp grows the transparent canvas around it. Remove or move that mark and the unused space falls away.

- **Text:** click once to write without a box; your words can grow in width or across lines. Drag to draw a fixed width and height when text needs to fit a specific space. Choose whether overflow is clipped or ends with an ellipsis, align or justify the text, and set its line height. Both styles offer font, weight, italic style, size, color, kerning, letter spacing, and case controls. Press Enter for another line, `Ctrl+Enter` to finish, or double-click placed text to rewrite it.
- **Steps:** place dots labeled with numbers, letters, or Roman numerals. Restart the count at any dot, and adjust its size, colors, font, and weight. The next dot keeps your last size.
- **Lines and shapes:** use one line tool for solid, dashed, or dotted strokes. Choose a cap for either end: triangle, open arrow, circle, diamond, bar, or none. Draw rectangles, squares, ellipses, and circles with adjustable outlines and fills, including fill-only shapes.
- **Privacy:** choose Cover, Blur, or Pixelate for a rectangular area. Use opaque Cover for sensitive details; Blur and Pixelate only obscure the view.

Draw a crop and drag it into place, use exact X, Y, width, and height values, lock proportions, or pick a ratio such as 1:1, 9:16, or 5:7. Scroll the canvas to zoom; hold Space and drag with the hand cursor to pan with a short, gentle glide. Hold Shift while moving a mark to keep it on one axis, or while resizing to keep its proportions. Nudge a selected mark with the arrow keys, one pixel at a time or ten with Shift. Click a number field, then scroll to change it by one; hold Shift for ten. Undo or redo as you go. Window captures get a soft drop shadow by default.

Right-click any placed element to clone it, delete it, or change which marks sit in front. The same actions work from the keyboard: `Ctrl+D`, `Delete`, `Ctrl+]` (front), and `Ctrl+[` (back).

When it looks right, **Copy** (`Ctrl+C`) puts the finished image on your clipboard with its transparent areas intact, and **Save** (`Ctrl+S`) lets you export a PNG. Close the window when you're finished; Shnapp stays ready in the tray. The toolbar menu also offers Windows Share, Copy full path, and Delete shnapp.

## Find it again

Your shnapps live in a local library, ready to search and reopen. Choose a list or one of four thumbnail sizes. Each item shows its last saved date, dimensions, and PNG size; sort by any of those or by name. After you edit a shnapp, its date and preview update when you return to the library.

Right-click an item to clone, rename, share, delete, or copy its saved PNG path. In the grid, hover over a thumbnail to reveal its checkbox at the bottom right. List checkboxes are always visible. Selecting an item brings **Delete** beside search for bulk cleanup.

Back and Forward retrace the places you visited. You can also use `Alt+Left`, `Alt+Right`, or your mouse's Back and Forward buttons. Click the title above an open shnapp to rename it. An empty library offers a quick start, and you can opt in to launching Shnapp at sign-in so it is ready in the tray.

While editing, move to the canvas's left edge to reveal a small image-only gallery over your work, or open it with `Ctrl+Shift+G` or **Recent shnapps** in the menu. Hover to magnify nearby previews, use the arrows or scroll to browse, click a preview to open it, or drag one onto your canvas to add a copy as a movable image layer. Small cropped previews keep browsing quick.

![The Shnapp library showing four locally saved fictional captures with preview thumbnails, dates, dimensions, and file sizes](src/Shnapp.Site/public/screenshots/library.png)

*Your shnapps stay on this PC, with previews that make the right capture easy to find.*

## Your captures stay yours

Shnapp saves its library and preferences under `%LOCALAPPDATA%\Shnapp` for your Windows user account. Capturing and editing require no account or internet connection. Copied and exported PNGs have the chosen Cover, Blur, or Pixelate effect baked in. The editable original remains in your local library, so share the finished PNG rather than a library file when something sensitive was covered. For private information, choose opaque Cover; blurred or pixelated details may still be recognizable.

## Get started

Download [Shnapp for Windows 11](https://shhnap.com/download/) as an x64 or ARM64 ZIP. Extract the entire archive to a folder and run `Shnapp.exe` from that folder. The [latest GitHub release](https://github.com/Zettersten/shhnap/releases/latest) includes SHA-256 checksum files and release notes. You can also [build it from source](#build-from-source). Captions, output resizing, polygon shapes, true free-form capture, and optional AI assistance are planned.

The portable app checks for newer stable GitHub releases at most once a day and shows an update notice that opens **Settings → About & Updates**. You can check manually there too. Download and extract the new ZIP to update a portable copy; your library and preferences remain under `%LOCALAPPDATA%\Shnapp`. See the [download page](https://shhnap.com/download/) for current package manager availability and update commands.

### Build from source

On Windows 11, install the .NET SDK specified in `global.json` and a WinUI 3 development toolchain. Then run:

```powershell
dotnet build src\Shnapp.App\Shnapp.App.csproj -c Debug -p:Platform=x64
.\src\Shnapp.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Shnapp.exe
```

For ARM64, use `-p:Platform=ARM64`; its output is under `bin\ARM64\Debug\...\win-arm64`.

## Feedback and security

Use the [bug report](https://github.com/Zettersten/shhnap/issues/new?template=01-bug.yml) or [feature request](https://github.com/Zettersten/shhnap/issues/new?template=02-feature-request.yml) form to tell us about Shnapp. Report vulnerabilities through [private vulnerability reporting](https://github.com/Zettersten/shhnap/security/advisories/new) rather than a public issue.

## License

Shnapp's source code, website code, and original first-party artwork in this repository are licensed under the [MIT License](LICENSE). Copyright 2026 Erik Zettersten. Third-party components keep their own licenses.
