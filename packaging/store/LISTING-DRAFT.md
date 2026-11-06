# Microsoft Store listing (en-US, MSIX)

**Shnapp** is [publicly available in Microsoft Store](https://apps.microsoft.com/detail/9P7LX12F1V5L) under the publisher **Nenvy**. The first public submission used validated x64 and ARM64 `2.1.0.0` packages. For the video release, create an update submission with `3.0.0.0` packages and the listing text and screenshots below. The [Store candidate workflow](https://github.com/Zettersten/shhnap/actions/workflows/store-candidates.yml) builds upload files; Partner Center validation, certification, and publication are separate steps. [Microsoft's MSIX listing guide](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info) requires a description and at least one screenshot.

## Product identity

| Partner Center field | Exact value |
| --- | --- |
| Package/Identity/Name | `24664Nenvy.Shnapp` |
| Package/Identity/Publisher | `CN=B431E658-A1AD-472F-8DC9-270D1AFEB32C` |
| Package/Properties/PublisherDisplayName | `Nenvy` |
| Store ID | `9P7LX12F1V5L` |

The [three manifest values](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details) must match the `STORE_IDENTITY_NAME`, `STORE_IDENTITY_PUBLISHER`, and `STORE_PUBLISHER_DISPLAY_NAME` repository variables used by `.github/workflows/store-candidates.yml`. The Store ID identifies this reserved product; it is not a manifest field. This Store publisher choice does not change Shnapp's author or MIT copyright attribution.

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
| What's new in this version | Use the v3.0.0 update text below |

**Short description**

Capture screenshots and screen video on Windows 11. Annotate images, edit clips, and keep both in one local library.

**Full description**

Shnapp captures the moment you need to explain. Take a screenshot or record a rectangular region, a window, or a display from the tray or a keyboard shortcut. Take a photo from a connected camera when you need one.

For screenshots, add text, arrows, shapes, numbered steps, and pasted images on a full-window canvas. Rotate or flip editable marks, crop the result, or cover private details. Copy the finished image, save a PNG, or use the Windows share sheet.

For video, trim or split clips on a timeline, add another video or image, edit text directly on the preview, and place transitions across cuts. Keep computer audio and microphone audio on separate tracks, import your own audio, and export an MP4. Recording settings let you choose audio sources, pointer visibility, and an optional camera video layer.

Screenshots and videos appear together in a searchable local library. The original recording stays intact while you edit. The core capture and editing flow works without a Shnapp account or cloud service. Launch at sign-in is optional; Microsoft Store delivers updates to this edition.

**What's new in this version (v3.0.0)**

Record a region, window, or display with optional computer audio, microphone, and camera video. Edit clips, layered images and video, live text, audio, and transitions in the new video editor. Screenshots and videos now share one library, with a direct Record video action on the empty gallery. Gallery previews load as you browse.

**Product features** (enter each as a separate feature, without a bullet prefix)

1. Capture a screenshot of a region, window, or display, or take a camera photo.
2. Record a region, window, or display with optional computer audio, microphone, and camera video.
3. Trim and split recordings; layer clips, images, live text, and transitions on a timeline.
4. Edit separate audio tracks, import your own sound, and export an MP4.
5. Annotate screenshots with text, shapes, steps, and pasted images; rotate, flip, or crop marks.
6. Cover private details and export a finished PNG.
7. Search, sort, and reopen screenshots and videos together in a local library.

## Capabilities and review notes

- The MSIX manifest declares `runFullTrust` for the WinUI desktop app and a disabled-by-default `windows.startupTask` extension. Launch at sign-in is user controlled in Settings.
- The app captures screen pixels only when the user starts a still or video capture. Optional computer audio, microphone, and camera capture are controlled in Recording settings; audio capture is off by default. Export and sharing are user initiated. The optional Store update check contacts Microsoft Store; the portable edition checks GitHub Releases.
- The app has no Shnapp account, hosted capture library, analytics, or in-app purchases. Confirm the privacy answers against the final build. Microsoft says a privacy policy is required when the app accesses, collects, or transmits personal information or has capabilities that could do so: [MSIX support info](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info).
- The public Store version is `2.1.0.0`. The v3 candidate must increase it to `3.0.0.0` for both x64 and ARM64. The workflow checks identity and contents but does not sign, submit, or certify packages. [MSIX package upload](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages).
- The ARM64 package is cross-built and structurally checked; it has not been run on ARM64 hardware. Test it when a device is available.
- The Store build uses its package-specific LocalCache folder; Windows excludes it from device backup. On first packaged launch, Shnapp copies a default portable library into that folder without deleting the portable source. Validate the import and packaged startup on a clean Windows 11 install. Export or back up editable captures before Store uninstall or reset, which may remove that package data.
- The public en-US listing has the description, six features, four screenshots with captions, canonical MIT license URL, and developer attribution. Pricing, properties, IARC age ratings, packages, Store listing, and Submission options all showed Complete before certification was requested. The `runFullTrust` warning is expected for a WinUI 3 desktop app; its rationale is saved in Submission options. Reviewer instructions are saved under Additional Testing Info.

## Screenshot inventory and shot list

[Microsoft's MSIX asset requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images) call for PNG screenshots at least 1366 × 768, no larger than 50 MB, up to ten for Desktop. One is required; Microsoft recommends four or more. Use authentic app captures, with no added marketing text or logos, and keep key UI in the top two-thirds.

| Upload order and candidate | File and content | Store caption draft |
| --- | --- | --- |
| 1. Video editor | [05-video-editor.png](screenshots/05-video-editor.png), 1920 × 1064. Video preview with editable text and image overlays above video and audio timeline tracks. | Trim clips and layer video, images, live text, and audio on a compact timeline. |
| 2. Photo canvas | [02-photo-canvas.png](screenshots/02-photo-canvas.png), 1586 × 893. The image continues behind the translucent title bar, footer, and floating options panel. | Keep the whole picture in view while adjusting tools and zoom. |
| 3. Mixed library | [03-library.png](screenshots/03-library.png), 1920 × 1064. Three fictional screenshots and one sample video together in the same gallery. | Find screenshots and recordings together in one searchable local library. |
| 4. Recording settings | [06-recording-settings.png](screenshots/06-recording-settings.png), 1920 × 1064. Separate computer audio, microphone, camera, and pointer controls. | Choose audio sources, camera video, and pointer visibility for screen recordings. |
| 5. Annotated editor | [01-editor-annotated.png](screenshots/01-editor-annotated.png), 1586 × 893. Numbered steps and a covered fictional access code. | Add context with numbered steps and cover sensitive details before sharing. |
| 6. Empty gallery | [07-empty-gallery.png](screenshots/07-empty-gallery.png), 1920 × 1064. Direct Capture a region and Record video actions over the new illustration. | Start a screenshot or screen recording directly from the empty library. |
| 7. Transform | [04-transform.png](screenshots/04-transform.png), 1600 × 900. The selected element's context menu offers flips and preset rotations. | Rotate and flip editable elements right on the canvas. |

Use genuine app captures made with an isolated `--data-root` and fictional sample content. Validate every final PNG against the 1366 × 768 Desktop minimum and 50 MB limit before uploading; captions must be 200 characters or fewer. The sample landscape is [“Mountain lake in the autumn” by sayid he](https://commons.wikimedia.org/wiki/File:Mountain_lake_in_the_autumn_(Unsplash).jpg), available under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/). `src/Shnapp.App/Assets/Square150x150Logo.scale-200.png` is an existing 300 × 300 transparent Shnapp icon and is a candidate for the recommended Store tile icon; inspect its appearance on both light and dark backgrounds before upload and in Partner Center's package preview.
