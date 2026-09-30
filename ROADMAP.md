# Shnapp roadmap

Shnapp's goal is a fast, small Windows 11 capture app: press a shortcut, make a shnapp, explain it, and share it. This plan closes the gaps in the original brief while keeping the editor focused on the image and its next action. `PRODUCT.md` defines the product boundary and `DESIGN.md` defines the visual and interaction system.

## Starting point

The current app has the three global shortcuts, a tray and local library, per-user storage, opt-in startup, a window shadow, crop, text/steps/lines/rectangles/squares/ellipses/circles, rectangular Cover/Blur/Pixelate effects, undo/redo, and PNG copy/export. The grabber shows a crosshair and live pixel measurements. Crop offers X/Y/width/height fields, a proportions lock, and common ratios; the canvas offers wheel zoom, Space-drag panning with a brief glide, and Shift constraints while moving or resizing marks. The library offers four grid sizes, list view, search, and an empty getting-started view. Newly placed annotations stay selected. The contextual side panel edits the selected mark immediately, and drag handles resize it on the canvas. `Ctrl+Shift+2` currently selects a **rectangle**. Saved documents still use schema version 1 and retain the unedited source image; optional annotation fields preserve older documents. GitHub Actions tests and builds on `main` and prepares x64/ARM64 archives on version tags, but no public release or Store package has been validated. The previous self-contained x64 publish measured about 174 MiB; capture latency and idle memory have no baseline yet.

Work on the product website and README can proceed in parallel with the app slices below. Each slice should land as a usable improvement, with the same result visible in the editor, copied PNG, exported PNG, saved preview, and reopened document where applicable.

## Delivery order

### 0. Product presence and install path (now, independent of editor work)

- Keep the Astro static site under `src/Shnapp.Site/` with a locked build and a GitHub Actions workflow that builds on pushes to `main` and deploys the generated output to GitHub Pages. Keep the repository and Pages settings, build, and deployment on one publish path. Configure `shnapp.com` in Pages settings; verify the required DNS records and HTTPS after DNS is configured. A successful workflow alone does not prove the custom domain works.
- Use the supplied `logo.png`, `icon.png`, and `DESIGN.md` on the site. Show the three shortcuts, a concise feature tour, Windows 11 support, an honest download state, and a clear path back to the GitHub repository. Do not advertise features or release downloads before they exist.
- Rewrite `README.md` as the product's front door: a short promise, real app screenshots, shortcuts, a short feature tour, install/download guidance, privacy and local storage, and a link to the site. Keep build instructions in a brief contributor section or a separate document. Capture screenshots from a running app with safe sample content, including the library and annotated editor. Maintain screenshot files in the repository and reuse them on the site where useful.

**Done when:** a push to `main` builds and deploys the site; its actual Pages URL and `shnapp.com` (once DNS is in place) load over HTTPS; the README and site show the real logo and real, legible screenshots; links and claims match a release that users can actually download. If no release exists, label the download as forthcoming or link to source builds.

### 1. Durable editing foundation (P0; before complex new document structures)

- Define document schema version 2 and a migration from version 1. Preserve existing local shnapps and their original pixels. Add explicit models for captions, output resizing, polygon vertices, and configurable privacy effect parameters rather than forcing all of them into `Annotation.Start` and `End` (`src/Shnapp.Core/Documents.cs`, `DocumentValidation.cs`, `DocumentEditor.cs`, `ShnappLibrary.cs`). Existing optional version 1 fields cover step number color, line end caps, and the three redaction modes for now.
- Make one render pipeline the source of truth for editor preview, thumbnail, clipboard, and PNG export (`src/Shnapp.App/Editor/ShnappRenderer.cs`). Define the layer order: source image → privacy effects → ordinary annotations → captions → window shadow/output padding, with crop and resize applied consistently. Include alpha behavior for lasso captures.
- Keep every user edit undoable; add migration, serialization, geometry, and render parity tests. Ensure malformed or unsupported documents fail clearly without deleting originals.

**Done when:** version 1 library items reopen and export unchanged, version 2 items round-trip, undo/redo covers each new operation, and preview, copy, and export use the same composition and dimensions.

### 2. Complete annotation controls (P1)

**Current status:** The editor has a compact tool bar and contextual inspector. A newly placed mark stays selected; selecting an older one loads its settings, and sidebar changes apply to it immediately. Drag handles resize text, steps, lines, shapes, and privacy rectangles. Text font, weight, italic style, size, color, and existing-text editing are present. Steps have separate dot and label colors, dot size, label font and weight, decimal/letter/Roman labels, and an optional count restart. One line tool offers solid, dashed, or dotted strokes plus six independent choices for each end cap. Rectangle, square, ellipse, and circle tools have outline color, thickness, fill color, and opacity. Polygon placement is still outstanding. Final keyboard, pointer, persistence, and export checks remain part of the release gate.

- Finish polygon placement with add-point, finish, and cancel interactions, then expose its outline color, thickness, fill color, and fill opacity beside the existing shape controls.
- Verify automatic step numbering through insert, delete, reorder, save, and reopen; keep numbers stable when annotations are moved or styled.
- Finish keyboard, pointer, screen-reader, persistence, and export checks for every tool. Verify selected-item editing and drag handles across the supported annotation types, including zoomed and cropped views.

**Done when:** every control in the original annotation list changes both live preview and exported pixels, persists in the library, and remains editable and undoable. Keyboard and pointer flows work without obscuring the canvas.

### 3. Privacy, dimensions, and captions (P1)

- Harden the current rectangular Cover, Blur, and Pixelate modes: verify editor, clipboard, export, preview, undo, and saved-document parity with representative text and overlapping effects. Describe Blur and Pixelate as visual obscuring; Cover is the safer choice for sensitive details. Make clear that the local editable source still contains the original pixels.
- Add non-destructive output resize: width/height in pixels, aspect lock, percentage and common presets. Apply it after crop and before final output; keep annotation placement stable in source coordinates. Show resulting dimensions before copy/export.
- Add captions with a one-action default (bottom overlay, focused text field). Support overlay or outside-canvas placement, top/bottom/left/right, background color and opacity, caption font family/style/weight/size/color, padding, and margins. Prevent accidental clipping when a caption grows or the image is narrow.
- Complete the broad “style” request as a small set of useful output presets (for example background/padding and shadow choices) after the precise controls above work; keep source pixels untouched unless the user chooses an effect.

**Done when:** privacy effects, output resize, and captions survive save/reopen and undo/redo; output dimensions and caption layout match preview; exported Cover is opaque; defaults produce a shareable image without opening an options panel.

### 4. Capture semantics and reliability (P1)

- Make `Ctrl+Shift+2` genuinely free-form with a pointer-drawn/lasso outline and transparent pixels outside the selection; keep a fast rectangle option for precise crops. Use the existing overlay in `src/Shnapp.App/Capture/SelectionWindow.cs` as the starting point. Handle cancel/retry, minimum size, and keyboard access to a rectangular fallback.
- Verify window and full-screen capture on multiple monitors, mixed DPI, scaled displays, unusual window bounds, minimized/occluded windows, and HDR content. Keep the window shadow on window captures and ensure it is not clipped after crop or resize.
- Surface global shortcut conflicts clearly and let users reassign shortcuts with validation and reset, as specified in `DESIGN.md`. Preserve the existing defaults and tray actions.

**Done when:** all three shortcuts invoke the named mode reliably after startup, true free-form selection exports with expected transparency, captures use correct physical pixels on mixed-DPI monitors, and a conflicting shortcut has a visible recovery path.

### 5. Speed, size, design, and accessibility gate (P1, ongoing)

- Measure warm shortcut-to-overlay, capture-to-edit, copy/export time, idle memory, and release archive/unpacked size on named reference hardware. Store the baseline and measurement method in the repository, set explicit budgets from those results, and profile regressions before claiming “extremely fast” or “tiny.” Reduce image copies, synchronous UI-thread work, and dependency/publish weight where measurement shows a benefit.
- Refine the shell, selection overlay, contextual controls, library, empty states, and icon use against `DESIGN.md`. Review light, dark, high contrast, narrow windows, text scaling, touch/pen targets, and reduced motion. Verify the editor still gives the image priority and the main flow takes few decisions.
- Exercise a full keyboard-only flow and screen reader names/focus order for capture, edit, copy, save, library, and settings. Add targeted UI smoke coverage for actual WinUI launch, tray, shortcuts, and export; retain core unit tests for document behavior.

**Done when:** measured values and budgets are published, the agreed budgets pass on release builds, a responsive top-level window launches on Windows 11 x64 and ARM64, and the primary flow passes keyboard, high contrast, and screen reader review.

### 6. Public release and later Store delivery (P1 release; P2 Store)

- Exercise the existing version-tag release workflow end to end: clean x64 and ARM64 publish, archive checksums, GitHub Release assets, a first-run smoke test from extracted archives, and installation/upgrade/rollback instructions. Validate ARM64 on ARM64 hardware; cross-publishing on x64 is not runtime verification. Add code signing and a trustworthy distribution story before broad promotion.
- Confirm the app runs without elevation, writes only to the current user's data directory, and behaves correctly at sign-in and when closed to the tray. Link the actual release from README and site only after it exists.
- For Microsoft Store distribution later, plan the packaged identity, signing, startup/tray behavior, capabilities, update path, and migration of existing `%LOCALAPPDATA%\Shnapp` libraries before submitting a package. Keep the portable GitHub release path working unless a deliberate product decision changes it.

**Done when:** a user can download a tagged release, verify its checksum, run it on a supported Windows 11 device without admin rights, and use capture → annotate → copy/save. Store work is done only after an installed Store build passes the same workflow and preserves an existing library.

### 7. Optional AI assistance (P2; never a dependency of capture)

- Start with “write/shorten/rewrite caption” and title suggestions. Offer an explicit preview and Apply/Cancel so the user controls every change; applying creates an undoable edit. Keep the manual caption and annotation flows complete without a model or account.
- Decide provider, cost, and credential model before implementation. Explain exactly when screenshot pixels or text leave the device, request consent for each remote operation (or a clear remembered preference), and keep credentials out of the repository and exported images. Consider a local-model path if it meets the speed/size goals.
- Only then evaluate image-edit assistance and sensitive-content suggestions. Treat suggestions as fallible and require the user to review privacy edits before export.

**Done when:** AI is opt-in and unavailable states leave the app fully functional, remote sends are transparent, no silent model edit occurs, and every applied result can be undone.

## Cross-cutting release checks

For every slice: build the WinUI app, run it to a responsive window, test the edited flow with a real capture, reopen the saved shnapp, compare preview and copied/exported PNGs, and run the core test suite and CI. Keep product claims, screenshots, README, and website synchronized with what the release actually does.

## Decisions to record before the relevant slice

1. **Full screen scope:** the current behavior captures the monitor under the pointer. Decide whether that remains the named mode or whether users also need the entire virtual desktop.
2. **Free-form output:** choose the exact lasso smoothing and transparent-edge rules; keep rectangular selection available for crisp pixel bounds.
3. **Privacy semantics:** decide whether local editable originals can contain redacted pixels indefinitely or whether a destructive “remove source pixels” option is needed for sensitive workflows.
4. **Performance budgets:** establish them from measured hardware and a representative 4K capture instead of inferring speed from a successful build.
5. **AI service:** choose provider, cost, consent, and credential handling only when starting the optional AI slice.
