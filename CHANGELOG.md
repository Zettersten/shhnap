# Changelog

## Unreleased

### Added

- A rotation handle above each selected editable element, including lines and photo layers.
- A **Transform** submenu for flipping selected elements horizontally or vertically and rotating them 45°, 90°, 135°, or 180° clockwise or counterclockwise. Group transforms can be undone in one step.

### Changed

- The editor canvas fills the window behind the translucent title bar, floating options panel, and compact footer. Opening the panel does not resize the canvas; zoom and pan keep the image continuous behind the chrome.
- The compact title bar groups navigation and editing tools on the left, with undo, redo, library, capture, and the actions menu on the right. The footer places metadata on the left, the info-panel toggle in the center, and fit, zoom, and full-screen controls on the right.
- Tool options use an acrylic panel over the canvas and animate when the panel or selected tool changes.
- The website and README include new v2 screenshots and a feature tour with licensed music and photography.

## 2.0.0

### Added

- Numbered callouts with text outside the capture and leader lines back to the selected point. Callouts are placed around existing content and reflow when their text changes.
- Magnify tool with adjustable zoom, size, border, and outline color.
- Per-character bold, italic, and underline styling for text, Windows spelling assistance during editing, and vertical text alignment.
- Optional Print Screen capture shortcut with full-screen, window, or region mode.
- `Ctrl+A` to select library results in both grid and list views.
- Camera photo capture with `Ctrl+Shift+5`, camera selection in Settings, and photo layers that can be cropped in the editor.

### Changed

- The main action is now **Capture**. Copy and Save sit in the reorganized toolbar menu.
- Tool options float over the canvas and animate from the lower-right corner.
- New lines use a triangle end cap. Existing elements show move and resize cursors on hover while a drawing tool is active.
- The font picker shows a larger preview and supports arrow-key browsing with live selection preview.
- The library layout, navigation states, thumbnail-size icon, and Settings dialog have been refined.
- The app chrome uses Windows Mica and acrylic materials where supported, with theme-aware fallbacks.
- Windows release executables and Velopack installers are signed by Nenvy LLC through Azure Artifact Signing.
