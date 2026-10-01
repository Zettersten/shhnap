# Microsoft Store listing draft (en-US, MSIX)

This is copy for Partner Center once **Shnapp** is reserved. The Store technical package identity and certification result are still pending. [Microsoft's MSIX listing guide](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info) requires a description and at least one screenshot; the first submission should leave **What's new in this version** blank.

## Product fields

| Field | Proposed entry |
| --- | --- |
| Product name | Shnapp (reserve this name first) |
| Publisher display name | Erik Zettersten (must match Partner Center product identity) |
| Price | Free; no subscriptions or in-app purchases |
| Platform | Windows 11 PC, x64 and ARM64; Windows.Desktop only |
| Language | English (United States) |
| Category | Productivity (confirm the available Partner Center category) |
| Website | https://shhnap.com |
| Support | erik@zettersten.com or https://github.com/Zettersten/shhnap/issues |
| Privacy policy | https://shhnap.com/privacy/ (verify published before submission) |
| Copyright | © 2026 Erik Zettersten |
| License | MIT for Shnapp's own code and first-party artwork; bundled components keep their own license notices in the package |
| What's new in this version | Leave blank for the first Store submission; use this field for later updates |

**Short description**

Capture, annotate, and share screenshots from a Windows 11 tray app. Keep editable captures in a local library.

**Full description**

Shnapp helps you turn a screenshot into something you can explain and share. Start a capture from the tray or with a keyboard shortcut, choose a region, a window, or the display under your pointer, then edit the image before exporting it.

Add text, arrows, lines, shapes, numbered or lettered steps, and pasted images. Crop the result or hide details with Cover, Blur, and Pixelate. Copy the finished image, save it as a PNG, or open the Windows share sheet.

Your editable captures stay in a searchable local library on your Windows device. Shnapp works without an account or cloud service. Launch at sign-in is optional. Updates to the Store edition are delivered through Microsoft Store.

**Product features** (enter each as a separate feature, without a bullet prefix)

1. Capture a region, window, or display from the tray or keyboard.
2. Annotate with text, arrows, shapes, steps, and pasted images.
3. Cover, blur, or pixelate sensitive areas before sharing.
4. Copy, save a PNG, or use the Windows share sheet.
5. Search, sort, and reopen captures from a local library.
6. Optionally start Shnapp when you sign in.

## Capabilities and review notes

- The MSIX manifest declares `runFullTrust` for the WinUI desktop app and a disabled-by-default `windows.startupTask` extension. Launch at sign-in is user controlled in Settings.
- The app captures pixels from windows and displays only when the user starts a capture. Export and sharing are user initiated. The optional Store update check contacts Microsoft Store; the portable edition checks GitHub Releases.
- The app has no Shnapp account, hosted capture library, analytics, or in-app purchases. Confirm the privacy answers against the final build. Microsoft says a privacy policy is required when the app accesses, collects, or transmits personal information or has capabilities that could do so: [MSIX support info](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info).
- Store version `1.0.0.0` is derived from `v1.0.0`; every subsequent submission must increase the package version. Submit distinct x64 and ARM64 MSIX packages and confirm Partner Center accepts them: [MSIX package upload](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages).
- The ARM64 package is cross-built and structurally checked; it has not been run on ARM64 hardware. Test it when a device is available.
- Before Store certification, test portable-to-Store library import/migration and packaged startup on a clean Windows 11 install. Packaged AppData writes may be redirected to a per-package location.

## Screenshot inventory and shot list

[Microsoft's MSIX asset requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images) call for PNG screenshots at least 1366 × 768, no larger than 50 MB, up to ten for Desktop. One is required; Microsoft recommends four or more. Use authentic app captures, with no added marketing text or logos, and keep key UI in the top two-thirds.

| Candidate | Current state | Store caption draft |
| --- | --- | --- |
| Annotated editor | [01-editor-annotated.png](screenshots/01-editor-annotated.png), 1920 × 1032. Shows steps and a covered fictional access code. | Add context with numbered steps and cover sensitive details before sharing. |
| Region capture | [02-region-capture.png](screenshots/02-region-capture.png), 1920 × 1032. Shows a captured region in the editor. | Capture just the part of the screen you need. |
| Library | [03-library.png](screenshots/03-library.png), 1920 × 1032. Shows four local captures. | Find and reopen editable captures in your local library. |
| Clean editor | [04-editor-clean.png](screenshots/04-editor-clean.png), 1920 × 1032. Shows an unannotated capture in the editor. | Start with a capture, then choose the details to emphasize. |
| Feedback | [05-feedback.png](screenshots/05-feedback.png), 1920 × 1032. Shows the Feedback Settings screen. Optional fifth image. | Report a problem or request a feature from Settings. |

These are genuine app captures with fictional fixture content. All five were visually reviewed at 1920 × 1032 and are below 50 MB. The transparent picker overlay did not render in the screenshot capture, so it is omitted. `src/Shnapp.App/Assets/Square150x150Logo.scale-200.png` is an existing 300 × 300 transparent Shnapp icon and is a candidate for the recommended Store tile icon; inspect its appearance on both light and dark backgrounds before upload. Check the package icon and tile art with the Windows App Certification Kit.
