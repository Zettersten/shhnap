# Shnapp
<!-- impeccable:product-schema 1 -->
## Platform
Native Windows 11 desktop application (WinUI 3). A static web companion is planned.
## Stack
C#, .NET 10, WinUI 3, Windows Graphics Capture, and Win2D. The planned companion will use static HTML/CSS with minimal JavaScript and GitHub Pages hosting.
## Users
Everyday Windows users who need to quickly capture, explain, paste, or save something on their screen.
## Product Purpose
A shortcut creates a shnapp. A compact editor opens immediately so the user can annotate and copy or save it with little configuration.
## Operating Context
Shnapp runs quietly in the current user's tray. Ctrl+Shift+4 selects a window, Ctrl+Shift+3 captures the monitor under the pointer, and Ctrl+Shift+2 selects a rectangular region. The library reopens saved shnapps.
## Capabilities and Constraints
The current starter includes all three capture modes, basic text/step/arrow/shape/redaction tools, crop, undo/redo, copy/save, local library, and opt-in Windows startup. Advanced styling, captions, blur/pixelation, true freehand selection, Store distribution, and AI assistance are future work, not shipping claims.
All application data belongs to the current user. No administrator privileges, account, telemetry, or network service are required for the core workflow. The app targets Windows 11 and supports x64 and ARM64 builds.
## Brand Commitments
The product is Shnapp; a capture is a shnapp; taking one is shnapping. Root DESIGN.md, logo.png, and icon.png are supplied brand authority and must be preserved. Shnapp Blue is the primary interaction color; cyan/violet belong to the identity. The editor is canvas-first with contextual controls, no permanent inspectors, and native Windows behavior.
## Evidence on Hand
The supplied logo and icon are real artwork. No customer endorsements, performance measurements, or public releases exist yet. Product previews must not pretend otherwise.
## Product Principles
- Capture first; think less.
- Strong deterministic defaults beat mandatory configuration.
- Keep the user's content dominant and preserve its pixels unless edited.
- Stay local and quiet; AI must be optional, explicit, and undoable.
## Accessibility & Inclusion
Respect Windows theme, high contrast, text scaling, keyboard navigation, visible focus, and reduced-motion preferences. Name interactive controls for UI Automation and screen readers.
