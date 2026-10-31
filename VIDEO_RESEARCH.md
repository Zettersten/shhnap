# Video capture and acrylic research

Date: 2026-10-31
Status: research only; application implementation has not started.

## Goal and current seams

Shnapp's title bar and 28 px footer already overlay the image canvas. Both use ShnappImageChromeBrush, an in-app AcrylicBrush in src/Shnapp.App/App.xaml. The brush has high tint opacity (0.82 light / 0.76 dark) and an outer opacity below 1 (0.78 light / 0.68 dark). This can make the underlying image appear sharp through the brush even when the material blurs its own sample. It can also make a solid fallback look translucent. These are hypotheses, not a confirmed diagnosis. Win2D CanvasControl and the window Mica backdrop must be checked in a live A/B. [Microsoft's in-app acrylic guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/in-app-acrylic) says AcrylicBrush blurs content within the app window and can fall back when transparency effects are disabled, Battery Saver is active, or hardware is insufficient.

Still capture lives in src/Shnapp.App/AppController.cs and src/Shnapp.App/Capture/GraphicsCapture.cs. It takes one Windows Graphics Capture frame, copies pixels to CPU memory, and creates an image-only document. That path is appropriate for screenshots but must not be repeated for video frames. Window screenshots currently crop the visible frozen desktop, including any overlapping windows; a live window recorder would instead follow the selected HWND. The existing "free-form" selector is a draggable rectangle. [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) supplies GPU frames, capture-item resize events, and frame timestamps for a persistent recorder.

The shared title bar is owned by MainWindow, but its commands are tied to MainPage. A VideoEditorPage can occupy the same RootFrame once title commands follow the active page. The photo footer belongs to MainPage, so the video page will own a compact transport footer and a timeline. ShnappSettings, SettingsPage, TrayHost, gallery dock, clipboard import, and library storage are the integration points.

## Comparison with Jiffshot

Reviewed [Devolutions/jiffshot at commit 13f2e765](https://github.com/Devolutions/jiffshot/tree/13f2e7658f452315b293ae9d664fcb1f74e33af1) on 2026-10-31. Its README describes area/window/full-screen recording, system and microphone audio, a floating recording toolbar, trim/cut, GIF export, and a media library. The code is a useful product-flow reference, but the requested independent tracks go beyond its current editor.

| Area | What Jiffshot currently does | Shnapp decision |
| --- | --- | --- |
| Targeting | Window mode converts the selected window to a [fixed screen rectangle](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/crates/jiffshot-workflow/src/tray.rs#L538-L587). The recorder [grabs that same rectangle](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/crates/jiffshot-workflow/src/recorder.rs#L416-L439) on every frame. | Use a live window source that follows the HWND; use the existing rectangular picker for region mode. |
| Capture and encode | Its [GDI BitBlt grabber](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/crates/jiffshot-capture/src/screen.rs#L128-L152) copies frames into CPU memory; the [Media Foundation sink writer](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/crates/jiffshot-capture/src/media.rs#L97-L203) copies each frame again and enables hardware transforms only through an environment switch. | Keep the sound Media Foundation idea, but benchmark a GPU capture and hardware encode path for Shnapp's speed goal. |
| Audio | It [mixes system and microphone PCM](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/crates/jiffshot-workflow/src/recorder.rs#L503-L540) into one AAC stream. | Persist two synchronized source stems so users can edit them independently. |
| Video editor | Its session stores [one trim interval](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/app/src/video.rs#L30-L51); cutting [exports a new MP4](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/app/src/video.rs#L321-L337). Timed image overlays exist in the [export engine](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/crates/jiffshot-capture/src/media/post.rs#L34-L68), but [GUI export passes none](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/app/src/video.rs#L303-L318). | Build a persistent timeline with independently editable video, both captured audio sources, timed pasted/gallery images, and imported audio. |

Further Jiffshot video features worth tracking for later parity are [pause/resume, webcam picture-in-picture, pointer effects, live drawing, frame extraction, combine, narrated slideshows, and GIF export](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/README.md#L119-L130). They need separate product and performance decisions; the first implementation order below covers the explicit Shnapp requirements.

Useful workflow patterns include a separate recording worker, a small live pause/stop toolbar, and [transactional export](https://github.com/Devolutions/jiffshot/blob/13f2e7658f452315b293ae9d664fcb1f74e33af1/crates/jiffshot-capture/src/media/post.rs#L730-L810) that validates inputs and publishes a completed staging file. Its Rust/Rustolonia UI and CPU frame pipeline are not direct fits for Shnapp's WinUI 3 app. The inspected repository has no license file and GitHub [reports no license](https://api.github.com/repos/Devolutions/jiffshot), so this plan uses feature ideas only and does not copy code or assets.

## Recommended stack

| Need | First choice | Why and gate |
| --- | --- | --- |
| Live window/display/rectangle capture | [ScreenRecorderLib 7.0.1](https://www.nuget.org/packages/ScreenRecorderLib/7.0.1) using Windows Graphics Capture and Media Foundation | MIT; supports x64/ARM64, window/display sources, source rectangles, hardware H.264 encoding, microphone and WASAPI loopback. Prove packaged/portable deployment and video quality before adoption. |
| Separate computer and microphone audio | Per-source PCM callback from [ScreenRecorderLib v7](https://github.com/sskodje/ScreenRecorderLib/wiki/Quickstart-guide-v7.x.x-and-newer), written to separate sidecar files | Its MP4 has one mixed audio stream. The callback exposes per-source packets but no explicit timestamp; synchronization, silence, pause/resume, and callback cost need a focused spike. |
| Audio fallback | [NAudio.Wasapi 3.3.0](https://www.nuget.org/packages/NAudio.Wasapi/) | Capture loopback and microphone independently if the recorder callback cannot produce trustworthy stems; align their clocks to video. |
| Timeline preview and export | [Windows.Media.Editing MediaComposition](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/media-compositions-and-editing) | Native clip trimming, separate delayed audio tracks, timed image overlays, preview stream, and MP4 rendering. Benchmark frequent edits, scrubbing, and export on x64/ARM64. |
| Escape hatch for capture/encode | Native [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) plus [Media Foundation sink writer](https://learn.microsoft.com/en-us/windows/win32/medfound/sink-writer-attributes), with [Vortice MediaFoundation](https://www.nuget.org/packages/Vortice.MediaFoundation) interop if useful | More control but substantially more code and device-loss/clock handling. Use only if the recorder package fails the gates. |

[FFMpegCore 5.5.0](https://www.nuget.org/packages/FFMpegCore/5.5.0) wraps external FFmpeg executables; [FFmpeg.AutoGen 9.0.1.1](https://www.nuget.org/packages/FFmpeg.AutoGen/9.0.1.1) wraps native libraries. Neither supplies an editor or removes native deployment work. FFmpeg's [LGPL/GPL conditions depend on the binary build](https://ffmpeg.org/legal.html); it does not [ship official Windows binaries](https://ffmpeg.org/download.html). Start with Windows media APIs and revisit FFmpeg only for a demonstrated export gap. Do not use GDI-based gdigrab as the primary recorder when a GPU capture path is available.

## Recording flow

1. Keep the current compact toolbar. Its capture menu can offer Screenshot and Video, each with Region, Full screen, and Window. Existing screenshot shortcuts remain Ctrl+Shift+2/3/4. Register Alt+Shift+2 for video region, Alt+Shift+3 for video display, and Alt+Shift+4 for video window; surface hotkey conflicts through the existing tray/settings path.
2. Reuse the existing display/window/rectangle selection UI, then close it before live capture starts. For a full-screen shortcut, use the display under the pointer, matching still capture. For window mode, record the selected window content through its HWND and document that it differs from the still screenshot's visible-desktop crop. Test moving, resizing, minimizing, and occlusion. A region spanning monitors needs an explicit multi-source test.
3. Keep video frames on the GPU through encoding. Use a bounded queue, release frames promptly, handle device loss and source resize, and timestamp the audio/video streams. Do not call GetPixelBytes on every frame. Record to a temporary file and finalize or recover it cleanly on stop/failure.
4. Expose system audio and microphone toggles in Settings, plus microphone device selection, cursor inclusion, and an initial 30/60 fps quality choice. Start both audio toggles off for privacy; make current recording sources visible during capture. Packaged Shnapp currently declares webcam but not microphone, so add the [microphone device capability](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/enable-device-capabilities) when implementation begins. Handle denied permission and unplugged devices.
5. Keep an obvious recording indicator with pause, resume, and stop controls, guarded so only one recording can run at a time. Preserve Windows' capture border; [removing it requires additional user consent/capability](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired). Exclude Shnapp's selection and recording controls from the captured video where the OS supports it. Do not bypass protected content.

## Video project and editor

Store the original MP4 unchanged, independent computer and mic audio files, imported media assets, and a small versioned Shnapp timeline file. Do not squeeze video into the image-only ShnappDocument or its image storage rules. The project file records each clip's source in/out, timeline start, gain/mute, and each image's position, duration, and stacking order. Copy validated pasted/gallery images and imported audio into the project so edits remain reproducible. Extend library summaries to distinguish photos and videos.

The new viewer uses a full-size preview, compact transport/footer, left hover gallery, and the existing floating inspector language. A timeline underneath the preview has lanes for Video, Computer audio, Microphone, Images, and Added audio. Trim adjusts a clip's source bounds; Split creates two segments; Cut removes a selected segment or range. The same commands work independently on either captured audio lane. Pasted or gallery images become timed overlay clips. Imported audio becomes an additional independent lane. All edits are reversible and nondestructive.

Translate timeline segments into [trimmed or cloned MediaClips](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.mediaclip), [delayed BackgroundAudioTracks](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.backgroundaudiotrack), and [delayed MediaOverlays](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.mediaoverlay). Mute the recording clip's mixed embedded audio when separate stems play, so it is not doubled. Regenerate preview after committed edits rather than for every pointer movement; export with [MediaComposition.RenderToFileAsync](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.mediacomposition.rendertofileasync). Verify cut/trim accuracy and audio sync in rendered MP4.

## Verification before implementation choice

- Acrylic: enable Windows transparency and load a high-frequency bright/dark image crossing both chrome bands. Compare current brush with outer opacity 1 and reduced tint, in light/dark/high contrast. If no blur appears, compare a normal XAML Image with Win2D CanvasControl and isolate the Mica backdrop. The final solution must blur the live pan/zoom image, not a stale crop. If WinUI acrylic cannot sample this canvas, use a shared-transform blurred render clipped to the chrome strips.
- Recorder spike: prove window, display, region, cursor, hardware encode, stop/finalize, and 10- to 30-minute sessions on portable and Store packages for x64/ARM64. Record CPU/GPU, memory, dropped frames, start latency, and disk use at 1080p60 and 4K30; degrade gracefully when hardware cannot sustain the choice.
- Audio spike: inspect the per-source PCM format and sample counts; write both stems, then compare each against the mixed MP4 after long runs, silence, device reconnect, and pause/resume. Reject the callback path if offset/drift or callback overhead is not acceptable.
- Editor spike: verify MediaComposition preview seeking, split/trim, separate track gain/mute, image overlay timing, and export quality/performance with real recordings. Keep source media immutable through failures.
- Usability: keyboard and screen-reader names for every recording control, contrast on bright/dark frames, reduced-motion handling, consent/error messages, visible source badges, and safe recovery when disk space runs out.

## Implementation order after the research gates

1. Resolve the chrome blur with live visual comparisons and commit that isolated shell change.
2. Build a disposable recorder spike that proves all three sources, separate audio stems, x64/ARM64, and Store/portable packaging. Keep it out of the product until the audio and performance gates pass.
3. Add video settings, Alt+Shift shortcuts, recording lifecycle, visible controls, and recoverable storage.
4. Add the video project model, library entries, and the separate viewer with transport and timeline.
5. Add independent audio editing, timed image overlays, imported audio, export, accessibility, and long-session verification.

## Confirmed scope

The user supplied Jiffshot as the comparison project and confirmed that "freeform" video means Shnapp's current draggable rectangular region for the first version.
