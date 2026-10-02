# Microsoft Store listing draft (en-US, MSIX)

**Shnapp** is reserved as an MSIX app in Partner Center under the public Store publisher **Nenvy**. As of October 2, 2026, the first submission and certification are pending. The developer account shows non-compliant status after rejected verification and incomplete EU Digital Services Act details; resolve both before submitting. GitHub Actions has produced x64 and ARM64 `1.0.4.0` package candidates for the current public `v1.0.4` release. They still need device testing and Partner Center package validation; they are not certified. Use the latest validated build for the first certification once the account is compliant. [Microsoft's MSIX listing guide](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info) requires a description and at least one screenshot; the first submission should leave **What's new in this version** blank.

## Reserved product identity

| Partner Center field | Exact value |
| --- | --- |
| Package/Identity/Name | `24664Nenvy.Shnapp` |
| Package/Identity/Publisher | `CN=B431E658-A1AD-472F-8DC9-270D1AFEB32C` |
| Package/Properties/PublisherDisplayName | `Nenvy` |
| Store ID | `9P7LX12F1V5L` |

The [three manifest values](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details) must match the `STORE_IDENTITY_NAME`, `STORE_IDENTITY_PUBLISHER`, and `STORE_PUBLISHER_DISPLAY_NAME` repository variables used by the `build-store-msix` Actions operation. The Store ID identifies this reserved product; it is not a manifest field. This Store publisher choice does not change Shnapp's author or MIT copyright attribution.

## Product fields

| Field | Proposed entry |
| --- | --- |
| Product name | Shnapp (reserved) |
| Publisher display name | Nenvy (must match Partner Center product identity) |
| Price | Free; no subscriptions or in-app purchases |
| Platform | Windows 11 PC, x64 and ARM64; Windows.Desktop only |
| Language | English (United States) |
| Category | Productivity (confirm the available Partner Center category) |
| Website | https://shhnap.com |
| Support | erik@zettersten.com or https://github.com/Zettersten/shhnap/issues |
| Privacy policy | https://shhnap.com/privacy/ |
| Copyright | © 2026 Erik Zettersten |
| License | MIT for Shnapp's own code and first-party artwork; bundled components keep their own license notices in the package |
| Additional license terms | https://github.com/Zettersten/shhnap/blob/main/LICENSE (the canonical MIT terms, so Store buyers see the same grant) |
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
- The `v1.0.0` tag produced Store version `1.0.0.0`; Partner Center accepted its package shape, but that build predates Store library import and the graceful-Quit fix. Actions has now produced `1.0.4.0` x64 and ARM64 upload candidates. Actions checks their identity and contents, but Partner Center has not validated or certified these candidates. When account compliance is restored, device-test the latest candidates, replace older draft uploads, confirm Partner Center accepts both packages, then request first certification for the latest validated version. Every later submission must increase the package version. [MSIX package upload](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages).
- The ARM64 package is cross-built and structurally checked; it has not been run on ARM64 hardware. Test it when a device is available.
- The Store build uses its package-specific LocalCache folder; Windows excludes it from device backup. On first packaged launch, Shnapp copies a default portable library into that folder without deleting the portable source. Validate the import and packaged startup on a clean Windows 11 install. Export or back up editable captures before Store uninstall or reset, which may remove that package data.
- Partner Center's draft has the en-US description, six features, four screenshots with captions, canonical MIT license URL, and developer attribution. Pricing, properties, IARC age ratings, packages, Store listing, and Submission options showed Complete for the earlier draft; that does not confirm acceptance of the `1.0.4.0` candidates. Recheck each section after replacing packages. The `runFullTrust` warning is expected for a WinUI 3 desktop app; the required rationale is saved in Submission options for certification review. The reviewer testing instructions are saved under Additional Testing Info. Developer account verification and EU DSA compliance remain the external blockers to certification.

## Screenshot inventory and shot list

[Microsoft's MSIX asset requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images) call for PNG screenshots at least 1366 × 768, no larger than 50 MB, up to ten for Desktop. One is required; Microsoft recommends four or more. Use authentic app captures, with no added marketing text or logos, and keep key UI in the top two-thirds.

| Candidate | Current state | Store caption draft |
| --- | --- | --- |
| Annotated editor | [01-editor-annotated.png](screenshots/01-editor-annotated.png), 1920 × 1032. Shows steps and a covered fictional access code. | Add context with numbered steps and cover sensitive details before sharing. |
| Region capture | [02-region-capture.png](screenshots/02-region-capture.png), 1920 × 1032. Shows a captured region in the editor. | Capture just the part of the screen you need. |
| Library | [03-library.png](screenshots/03-library.png), 1920 × 1032. Shows four local captures. | Find and reopen editable captures in your local library. |
| Clean editor | [04-editor-clean.png](screenshots/04-editor-clean.png), 1920 × 1032. Shows an unannotated capture in the editor. | Start with a capture, then choose the details to emphasize. |
| Feedback | [05-feedback.png](screenshots/05-feedback.png), 1920 × 1032. Shows the Feedback Settings screen. Optional fifth image. | Report a problem or request a feature from Settings. |

These are genuine app captures with fictional fixture content. All five were visually reviewed at 1920 × 1032 and are below 50 MB. The transparent picker overlay did not render in the screenshot capture, so it is omitted. `src/Shnapp.App/Assets/Square150x150Logo.scale-200.png` is an existing 300 × 300 transparent Shnapp icon and is a candidate for the recommended Store tile icon; inspect its appearance on both light and dark backgrounds before upload and in Partner Center's package preview.
