---
version: alpha
name: Shnapp
description: "Design system for Shnapp, a fast, lightweight Windows 11 screenshot and annotation utility."
colors:
  primary: "#0A84FF"
  primary-hover: "#0878EA"
  primary-pressed: "#0668CC"
  primary-subtle: "#EAF4FF"
  primary-container: "#D9ECFF"

  accent-cyan: "#35C8FF"
  accent-violet: "#7C5CFF"
  accent-violet-soft: "#EEE9FF"

  neutral-0: "#FFFFFF"
  neutral-10: "#F8F9FB"
  neutral-20: "#F1F3F6"
  neutral-30: "#E6E9EE"
  neutral-40: "#CDD2DA"
  neutral-50: "#A7AFBA"
  neutral-60: "#7E8794"
  neutral-70: "#5A6470"
  neutral-80: "#343B44"
  neutral-90: "#1E2329"
  neutral-100: "#111418"

  surface: "#FFFFFF"
  surface-muted: "#F7F9FC"
  surface-elevated: "#FFFFFF"
  surface-overlay: "rgba(255,255,255,0.92)"
  surface-dark: "#171A1F"
  surface-dark-elevated: "#20242A"

  on-surface: "#1B1F24"
  on-surface-secondary: "#5C6672"
  on-surface-muted: "#7D8792"
  on-primary: "#FFFFFF"
  on-dark: "#F7F9FB"

  border: "#DDE2E8"
  border-strong: "#C5CBD3"
  divider: "#E8EBEF"

  success: "#16803C"
  success-surface: "#E9F7EE"
  warning: "#9A6700"
  warning-surface: "#FFF4CE"
  error: "#C42B1C"
  error-surface: "#FDECEC"
  info: "#0067C0"
  info-surface: "#E8F3FC"

  capture-mask: "rgba(0,0,0,0.42)"
  capture-selection: "#FFFFFF"
  capture-handle: "#0A84FF"
  capture-dimension-bg: "rgba(17,20,24,0.88)"
  capture-dimension-text: "#FFFFFF"

  annotation-red: "#E5484D"
  annotation-orange: "#F59E0B"
  annotation-yellow: "#EAB308"
  annotation-green: "#22C55E"
  annotation-blue: "#0A84FF"
  annotation-violet: "#7C5CFF"
  annotation-black: "#111418"
  annotation-white: "#FFFFFF"

  redaction-solid: "#111418"
  blur-overlay: "rgba(255,255,255,0.18)"
  pixelate-overlay: "rgba(17,20,24,0.18)"

  focus-ring: "#0A84FF"
  selection-fill: "rgba(10,132,255,0.14)"
  selection-stroke: "#0A84FF"

typography:
  display-lg:
    fontFamily: "Segoe UI Variable Display"
    fontSize: 40px
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: -0.02em
  display-md:
    fontFamily: "Segoe UI Variable Display"
    fontSize: 32px
    fontWeight: 700
    lineHeight: 1.12
    letterSpacing: -0.015em
  headline-lg:
    fontFamily: "Segoe UI Variable Display"
    fontSize: 28px
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: -0.01em
  headline-md:
    fontFamily: "Segoe UI Variable Display"
    fontSize: 22px
    fontWeight: 600
    lineHeight: 1.25
    letterSpacing: -0.005em
  headline-sm:
    fontFamily: "Segoe UI Variable Text"
    fontSize: 18px
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: 0em
  body-lg:
    fontFamily: "Segoe UI Variable Text"
    fontSize: 16px
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: 0em
  body-md:
    fontFamily: "Segoe UI Variable Text"
    fontSize: 14px
    fontWeight: 400
    lineHeight: 1.45
    letterSpacing: 0em
  body-sm:
    fontFamily: "Segoe UI Variable Text"
    fontSize: 12px
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: 0em
  label-lg:
    fontFamily: "Segoe UI Variable Text"
    fontSize: 14px
    fontWeight: 600
    lineHeight: 1.25
    letterSpacing: 0em
  label-md:
    fontFamily: "Segoe UI Variable Text"
    fontSize: 12px
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: 0em
  label-sm:
    fontFamily: "Segoe UI Variable Text"
    fontSize: 11px
    fontWeight: 600
    lineHeight: 1.15
    letterSpacing: 0.01em
  mono-sm:
    fontFamily: "Cascadia Mono"
    fontSize: 11px
    fontWeight: 400
    lineHeight: 1.3
    letterSpacing: 0em

rounded:
  none: 0px
  xs: 2px
  sm: 4px
  md: 8px
  lg: 12px
  xl: 16px
  xxl: 24px
  full: 9999px

spacing:
  0: 0px
  1: 4px
  2: 8px
  3: 12px
  4: 16px
  5: 20px
  6: 24px
  8: 32px
  10: 40px
  12: 48px
  16: 64px
  20: 80px
  24: 96px
  toolbar-gap: 4px
  control-gap: 8px
  panel-gap: 12px
  panel-padding: 12px
  card-padding: 16px
  window-padding: 20px
  content-max: 1280px
  website-max: 1200px

components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.on-primary}"
    typography: "{typography.label-lg}"
    rounded: "{rounded.md}"
    padding: 10px
    height: 36px
  button-primary-hover:
    backgroundColor: "{colors.primary-hover}"
  button-primary-pressed:
    backgroundColor: "{colors.primary-pressed}"
  new-shnapp-action:
    gradientStart: "#1670CC"
    gradientEnd: "#0753AC"
    textColor: "{colors.on-primary}"
    rounded: "{rounded.md}"

  button-secondary:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    typography: "{typography.label-lg}"
    rounded: "{rounded.md}"
    padding: 10px
    height: 36px
    borderColor: "{colors.border}"
  button-subtle:
    backgroundColor: "transparent"
    textColor: "{colors.on-surface}"
    typography: "{typography.label-lg}"
    rounded: "{rounded.md}"
    padding: 8px
    height: 32px
  button-danger:
    backgroundColor: "{colors.error}"
    textColor: "{colors.on-primary}"
    typography: "{typography.label-lg}"
    rounded: "{rounded.md}"
    padding: 10px
    height: 36px

  toolbar:
    backgroundColor: "{colors.surface-overlay}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.lg}"
    padding: 6px
    height: 44px
    borderColor: "{colors.border}"
  toolbar-button:
    backgroundColor: "transparent"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.md}"
    size: 32px
  toolbar-button-hover:
    backgroundColor: "{colors.neutral-20}"
  toolbar-button-active:
    backgroundColor: "{colors.primary-subtle}"
    textColor: "{colors.primary}"

  floating-panel:
    backgroundColor: "{colors.surface-overlay}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.lg}"
    padding: 12px
    borderColor: "{colors.border}"

  input:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    typography: "{typography.body-md}"
    rounded: "{rounded.md}"
    padding: 8px
    height: 32px
    borderColor: "{colors.border-strong}"
  input-focus:
    borderColor: "{colors.focus-ring}"

  tooltip:
    backgroundColor: "{colors.surface-dark}"
    textColor: "{colors.on-dark}"
    typography: "{typography.body-sm}"
    rounded: "{rounded.sm}"
    padding: 6px

  chip:
    backgroundColor: "{colors.neutral-20}"
    textColor: "{colors.on-surface}"
    typography: "{typography.label-md}"
    rounded: "{rounded.full}"
    padding: 8px
    height: 28px
  chip-selected:
    backgroundColor: "{colors.primary-subtle}"
    textColor: "{colors.primary}"

  library-card:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.lg}"
    padding: 8px
    borderColor: "{colors.border}"
  tray-menu:
    backgroundColor: "{colors.surface-overlay}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.lg}"
    padding: 6px

  capture-dimension-label:
    backgroundColor: "{colors.capture-dimension-bg}"
    textColor: "{colors.capture-dimension-text}"
    typography: "{typography.mono-sm}"
    rounded: "{rounded.sm}"
    padding: 4px

  step-marker:
    backgroundColor: "{colors.annotation-blue}"
    textColor: "{colors.on-primary}"
    typography: "{typography.label-md}"
    rounded: "{rounded.full}"

  selection-handle:
    backgroundColor: "{colors.capture-handle}"
    rounded: "{rounded.full}"
    size: 8px
---

# Shnapp Design System

## Overview

Shnapp is a Windows 11 screenshot utility designed around one idea: **capture first, think less**.

The interface should feel immediate, light, native, and almost invisible until the user needs it. Shnapp is not a full graphics editor. It is a capture utility with just enough editing power to turn a screenshot into something ready to paste, send, document, or publish.

The design language combines **Windows 11 familiarity** with a slightly more expressive product identity: clean whites and cool neutrals, bright blue interaction color, restrained cyan and violet accents, rounded geometry, translucent floating controls, and compact layouts.

### Brand personality

Shnapp should feel:

- **Fast:** every interaction should look and feel instantaneous.
- **Lightweight:** avoid visual density, chrome, nested toolbars, or modal workflows.
- **Confident:** defaults should be strong enough that users rarely need to adjust settings.
- **Friendly:** rounded geometry and simple language should make the app approachable without becoming playful or toy-like.
- **Native:** controls should feel at home on Windows 11.
- **Precise:** capture boundaries, measurements, annotation handles, alignment, and export states should be crisp.
- **Quiet:** Shnapp should stay out of the way when not in use.

### Product principles

1. **Capture is the primary action.** Everything else exists to support capture.
2. **One click after capture should usually be enough.** Copy, save, share, or close.
3. **Annotations should be direct manipulation.** Select a tool, place it, adjust only if necessary.
4. **Defaults beat configuration.** Choose sensible font sizes, line widths, colors, shadows, and export settings automatically.
5. **The canvas is the hero.** UI should float around the capture rather than compete with it.
6. **No permanent sidebars during editing.** Tool options appear contextually.
7. **Keyboard-first, mouse-friendly.** Every common action should have a shortcut, but nothing should require memorization.
8. **No admin aesthetic.** Shnapp is a consumer utility, not a system management tool.
9. **Motion explains state; it does not decorate it.**
10. **The app should disappear when the task is complete.**

### Primary experiences

Shnapp has five visual modes:

- **Background/tray state** — no visible primary window.
- **Capture overlay** — full-screen dimming layer with selectable capture target.
- **Editor** — compact annotation UI around the newly created shnapp.
- **Library** — browsable history of prior shnapps.
- **Settings** — infrequent, minimal configuration.
- **Website** — marketing expression of the same design system.

### Naming

Use the product vocabulary consistently:

- **Shnapp** — product name or a saved screenshot.
- **Shnapp** — noun: "Open this shnapp."
- **Shnapp** — verb: "Shnapp this window."
- **Shnapping** — action: "Shnapping full screen."
- **Library** — saved shnapps.
- **Capture** — acceptable technical/UI term where clarity matters.

Avoid replacing the brand language everywhere with generic terms such as "image", "screenshot", or "snip". Use those terms only when necessary for discoverability or accessibility.

## Colors

Shnapp uses a bright blue primary color anchored by cool Windows-like neutrals. Cyan and violet are secondary brand accents and should never compete with the primary interaction color.

### Primary

**Shnapp Blue (`#0A84FF`)** is the main interactive color.

Use it for:

- selected tools
- primary buttons
- focus states
- capture handles
- active filters
- selection outlines
- key brand moments

Do not flood large surfaces with blue. The product should remain predominantly neutral.

### Accent colors

**Cyan (`#35C8FF`)** and **Violet (`#7C5CFF`)** reinforce the product identity established by the Shnapp icon.

Use them selectively for:

- the Shnapp logo
- website graphics
- subtle gradients
- AI-assisted features
- onboarding illustrations
- empty-state art
- premium visual moments

Do not use cyan or violet as competing primary action colors.

### Neutral surfaces

The application should be primarily white in light mode and deep charcoal in dark mode.

Use neutral tonal separation before adding borders or shadows.

Preferred hierarchy in light mode:

1. `neutral-10` for application backgrounds.
2. `surface` for working surfaces.
3. `surface-elevated` for floating controls.
4. `border` only where separation remains ambiguous.

### Annotation colors

Annotation colors are intentionally more saturated than application chrome. They belong to user-created markup, not interface surfaces.

Default annotation color: **red** for arrows, shapes, and attention marks.

Default step-marker color: **blue**.

Default redaction color: **near-black**.

### Capture overlay

The capture overlay uses a dark translucent mask over non-selected regions. The selected region should remain visually faithful to the original screen.

Rules:

- Never tint the selected content.
- Dim everything outside the selection using `capture-mask`.
- Use a 1px or 2px high-contrast selection border.
- Show exact pixel dimensions near the active selection.
- Keep measurement labels opaque enough to remain readable against any background.

### Dark mode

Dark mode is supported, but screenshots themselves must never be modified to match the app theme.

In dark mode:

- Use `surface-dark` for app-level background.
- Use `surface-dark-elevated` for floating controls.
- Preserve Shnapp Blue as the interaction color.
- Prefer off-white text rather than pure white for body content.
- Maintain the same semantic color roles used in light mode.

### Accessibility

All interface text must meet WCAG AA contrast.

For screenshot annotations, the user controls color choices, so strict contrast cannot always be enforced. Where possible:

- provide a high-contrast default palette
- show an optional contrasting text background
- use outline/stroke treatment for step numbers over varied images
- ensure resize handles remain visible over both dark and light content

## Typography

Shnapp uses **Segoe UI Variable** as the primary typeface to maintain a native Windows 11 feel.

### Font families

- **Segoe UI Variable Display** — large headings and marketing display text.
- **Segoe UI Variable Text** — interface text, labels, body copy, menus, buttons.
- **Cascadia Mono** — pixel dimensions, technical capture information, keyboard hints when monospace treatment is useful.

Do not bundle decorative fonts with the core application.

### Hierarchy

Use typography compactly inside the desktop application.

- `headline-lg` — empty states, welcome views, major settings pages.
- `headline-md` — library section titles and dialogs.
- `headline-sm` — panel headings.
- `body-lg` — onboarding and explanatory copy.
- `body-md` — standard interface text.
- `body-sm` — metadata and secondary descriptions.
- `label-lg` — buttons and strong control labels.
- `label-md` — toolbar/menu labels.
- `label-sm` — compact metadata.
- `mono-sm` — dimensions and technical indicators.

### Annotation text defaults

Text placed on a shnapp should use **Segoe UI Variable Text** unless changed by the user.

Default text annotation:

- 18px
- 600 weight
- white text on dark images when auto-contrast determines it
- near-black text on light images
- no background by default
- optional background pill/box when needed

When auto-contrast cannot confidently choose a color, use annotation red for emphasis or offer the most recent user choice.

### Caption defaults

Captions are more editorial than annotations.

Default caption:

- font: Segoe UI Variable Text
- size: 18px
- weight: 600
- padding: 12px
- placement: bottom
- background: 82% opaque near-black
- text: white
- line length: no more than roughly 70 characters before wrapping

### Marketing typography

The website may use larger display sizes than the desktop app, but should continue to use Segoe UI Variable wherever possible to preserve product continuity.

Hero headlines should be concise and visually strong rather than oversized for spectacle.

## Layout

Shnapp follows an **8px primary spacing rhythm with a 4px micro-step**.

The application should feel compact without becoming cramped.

### Editor layout

The editor is canvas-first.

Preferred hierarchy:

1. Capture centered in the available viewport.
2. Main annotation toolbar floating near the top-center.
3. Collapsible contextual property panel on the right for the active tool or selected element.
4. Save/copy/export controls located consistently near the top-right.
5. Compact capture date, canvas dimensions, and saved PNG size in the footer.

Keep the right inspector contextual and collapsible so the capture remains dominant.
The editor stage begins directly beneath the main command bar. Its quiet checkerboard
stays fixed as the capture zooms or pans, and a one-pixel neutral outline marks the
actual canvas extent, including transparent padding. Neither guide appears in copied
or exported images.

### Floating toolbar

The primary annotation toolbar should:

- remain compact
- use icon-first controls
- support keyboard access
- show labels through tooltips
- group related tools
- visually separate destructive actions
- move or collapse if it would obscure a small capture

Recommended groups:

1. Select
2. Text
3. Step
4. Line / Arrow
5. Shape
6. Blur / Pixelate / Redact
7. Crop / Resize
8. Caption
9. Undo / Redo

Copy and Save should remain separate from annotation tools. Keep Share in the main command menu; closing the window returns Shnapp to the tray.

### Context panels

Tool-specific options appear only when the tool is active or an object is selected.

Examples:

- Text → font, size, weight, color, style
- Step → dot size, fill, number font, font weight, number color
- Line → color, thickness, start cap, end cap
- Shape → type, stroke, thickness, fill
- Privacy → blur, pixelate, redact
- Caption → placement, font, padding, margins, background, transparency

Context panels should never consume more visual space than the capture itself.
The options panel floats over the stage in a compact inset surface, rather than
resizing the canvas. Keep its toggle visible, its controls scrollable, and its
translucent surface readable in light and dark themes.

### Library layout

The library offers a responsive thumbnail grid and a compact list. Captures stay visually dominant in the grid; metadata supports scanning without competing with the image.

Desktop defaults:

- 3–6 cards per row depending on width and selected thumbnail size
- 16px card gap
- 8px internal card padding
- thumbnail aspect ratio preserved
- file name secondary to visual content
- last-saved date, dimensions, and PNG size displayed quietly

Grid selection checkboxes sit inside the thumbnail's bottom-right corner and appear on hover or keyboard focus. Once any item is selected, the library reveals bulk Delete beside search. In list view, checkboxes remain visible; each row's hover highlight reaches the content edges while its checkbox and size column retain inner padding. Leave space below the thumbnail before the row divider, and omit the divider after the last visible row.

The library should support:

- search
- sort by date, name, file size, and dimensions
- open
- clone, rename, share, and copy the saved PNG path
- select and delete multiple items

Advanced filtering should not be shown unless the product grows to justify it.

### Capture overlay layout

Capture mode removes all normal application chrome.

Freeze the visible desktop before presenting the picker. Window selection uses the pixels visible in that snapshot, including any overlapping windows; a window hidden behind another app cannot be reconstructed from the frozen frame. Keep the selection overlay out of the captured image.

Only essential indicators appear:

- capture mode hint
- current selection
- pixel dimensions
- minimal cancel guidance
- optional magnifier near precise edges

For window capture, the hovered window receives a clear pulsing border before click.

For free-form capture, the user should draw the selection directly.

For full-screen capture, highlight the candidate display with a pulsing border before confirmation.

### Multi-monitor behavior

Each monitor should respect its own scaling and coordinate space.

UI hints should appear on the monitor containing the pointer.

The capture overlay must not introduce visible seams between monitors.

### Settings layout

Settings should be a single-window, shallow hierarchy.

Recommended categories:

- General
- Capture
- Editor
- Shortcuts
- Saving
- AI
- About

Avoid nested settings pages unless a future feature truly requires them.

### Website layout

The marketing site uses:

- max content width: 1200px
- generous 64–96px vertical section spacing
- clear left-aligned copy
- product screenshots as primary visuals
- light backgrounds with restrained gradients
- simple pricing-free product storytelling unless monetization is introduced

Hero sections should explain the product in one sentence and show it immediately.

## Elevation & Depth

Shnapp uses **subtle tonal depth**, not heavy drop shadows.

### Application chrome

For floating controls:

- prefer a translucent or near-opaque surface
- use a 1px border
- use a soft shadow only where the panel overlaps complex image content
- avoid multiple stacked shadow levels

Recommended visual feel:

- Y offset: 4–8px
- blur: 16–24px
- low-opacity black
- no hard shadow edge

### Captured window shadow

Window captures automatically receive a polished drop shadow inspired by macOS screenshot output.

The shadow belongs to the exported shnapp, not the editor chrome.

Default window-capture shadow:

- soft, neutral black
- broad blur
- low opacity
- minimal horizontal offset
- subtle downward offset
- enough transparent padding around the captured window to avoid clipping

The effect should suggest physical separation without looking decorative.

### Selection elevation

Selected annotations should use:

- selection outline
- resize handles
- optional rotation handle when rotation is supported

Do not add drop shadows to selected objects merely to indicate selection.

### Modal surfaces

Prefer inline panels, teaching tips, or flyouts to modal dialogs.

Use modal dialogs only for:

- irreversible deletion
- unsaved destructive close when necessary
- permissions or account-level AI configuration
- errors requiring explicit acknowledgement

## Shapes

Shnapp uses rounded geometry throughout the application but preserves crisp edges where precision matters.

### Application geometry

- Buttons: 8px
- Inputs: 8px
- Floating panels: 12px
- Cards: 12px
- Large onboarding surfaces: 16px
- Chips: pill-shaped
- Selection handles: circular
- Color swatches: circular or softly rounded squares

### Capture geometry

Capture selections are precise and should not round the actual selected pixel boundary.

The capture overlay may use rounded dimension labels, but the selection itself remains geometrically exact.

### Annotation geometry

Shapes must honor the user's selected shape exactly.

Supported:

- circle
- ellipse
- square
- rectangle
- polygon

Avoid applying interface corner-radius conventions to user annotations.

### Step markers

Step markers are circular by default.

Default characteristics:

- blue fill
- white number
- 28px diameter
- 2px optional white outer ring when placed on visually busy content
- 600 font weight

Step numbering increments automatically.

Deleting a step should intelligently re-number later steps unless the user explicitly freezes numbering.

### Lines and arrows

Line endings at either end:

- none
- triangle arrow
- open arrow
- circle
- diamond

Lines support solid, dashed, and dotted patterns.

Arrowheads should scale proportionally with line thickness.

Default arrow:

- annotation red
- 3px line
- arrow end
- no start cap

### Resize handles

Handles should be visually clear but unobtrusive.

- 8px default diameter
- Shnapp Blue fill
- contrasting border when necessary
- larger invisible pointer target for usability

## Components

### Capture mode indicator

A compact temporary hint shown when capture mode begins.

Examples:

- "Window"
- "Full screen"
- "Free form"

The shortcut may appear beneath the label in secondary text.

The hint fades once the user begins interacting.

### Main annotation toolbar

The toolbar is the primary editor control.

Behavior:

- centered above the capture when space allows
- draggable only if obstruction becomes a real problem
- collapses into overflow at narrow widths
- selected tool uses `toolbar-button-active`
- tooltips appear after a short hover delay
- keyboard shortcuts appear in tooltip secondary text

Do not show text labels permanently for every tool.

### Tool buttons

Tool buttons must have:

- 32px visual button size
- at least 40px effective pointer target where practical
- clear hover state
- selected state
- accessible name
- disabled state where unavailable

Icons should use Fluent-style geometry and consistent stroke weight.

### Text tool

Clicking the canvas with the Text tool should immediately create editable text.

No intermediate dialog.

The contextual panel exposes:

- font family
- font size
- weight
- italic
- color
- optional text background
- alignment when multiline

The user should be able to type immediately after placement.

### Step tool

Clicking places the next numbered marker.

Default workflow:

1. activate Step
2. click multiple locations
3. numbers increment automatically
4. Escape exits the tool

Context options:

- size
- fill
- font family
- number weight
- number color

### Line tool

Click-drag places a line.

Context options:

- stroke color
- thickness
- start cap
- end cap

Holding Shift constrains angle when practical.

### Shape tool

Click-drag places the selected shape.

Context options:

- shape type
- stroke color
- stroke thickness
- fill
- fill opacity

Default fill should be transparent.

### Blur tool

Click-drag places a rectangular blur region.

Default blur should make underlying text unreadable without looking excessively smeared.

### Pixelate tool

Click-drag places a pixelated privacy region.

Pixel size should scale intelligently with the region.

### Redact tool

Click-drag places an opaque rectangular block.

Default redaction is near-black.

Export must permanently bake redaction into the final output.

### Crop tool

Cropping uses direct handles around the canvas.

Rules:

- dim discarded area
- show live pixel dimensions
- Enter confirms
- Escape cancels
- preserve undo history

### Resize tool

Resize is a lightweight dialog or flyout, not a full image-size editor.

Support:

- pixel dimensions
- lock aspect ratio
- scale percentage
- common scale presets

Do not expose DPI unless future workflows require it.

### Caption tool

Caption creation should be nearly instant.

Initial behavior:

1. choose Caption
2. Shnapp inserts a default caption bar
3. text field receives focus
4. user types
5. Enter or click outside commits

Options:

- top / bottom / left / right placement
- overlay vs outside-canvas caption
- background color
- background opacity
- font family
- size
- weight/style
- text color
- padding
- margins

The default should be **bottom overlay** unless it would obscure important content.

### AI action

AI is additive, never central to the editing workflow.

AI actions may include:

- Write caption
- Shorten caption
- Rewrite caption
- Summarize visible content
- Suggest title
- Identify sensitive information
- Suggest redactions
- Remove or replace simple visual elements when model support is available

AI entry points should use a subtle sparkle treatment with the cyan/violet accent family.

AI must never silently alter a shnapp.

Every AI edit should create an undoable operation or preview.

### Undo / redo

Undo and redo must be available:

- from toolbar
- through Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z
- across annotation operations
- across crop and resize operations
- across AI edits when technically possible

### Save and copy

Primary completion actions:

- Copy
- Save

The window's Close button returns Shnapp to the tray when its tray icon is available.

After capture, copying to clipboard may happen automatically according to user preference.

Save status should be quiet and non-blocking.

### Toasts

Use small transient toasts for:

- copied to clipboard
- saved
- export complete
- shortcut conflict
- AI operation completed
- recoverable errors

Avoid toast spam during rapid capture sessions.

### Library cards

Each card includes:

- thumbnail
- last-saved date
- dimensions
- optional short caption/title
- file size

Refresh the thumbnail and last-saved date after a document changes. Keep the capture's original creation time available as document metadata.

Right-click opens item actions across the entire card or list row.

Single-click opens the editor.

### Empty states

Empty states should be concise.

Library example:

**Your shnapps will show up here.**

Press Ctrl+Shift+4 to capture a window.

Avoid illustrations when plain guidance is clearer.

### Tray experience

The tray icon should remain monochrome-compatible at small sizes.

Tray menu:

- New window shnapp
- New full-screen shnapp
- New free-form shnapp
- Open Library
- Settings
- Quit Shnapp

If Windows supports a native quick action affordance, prefer the native surface.

### Settings controls

Use standard WinUI control patterns wherever possible.

Prefer:

- toggles
- combo boxes
- radio groups
- shortcut recorder controls
- directory picker
- inline helper text

Avoid custom controls where the native equivalent is sufficient.

### Shortcut recorder

A shortcut field should:

- listen for key combinations
- reject unsupported single-key shortcuts
- detect conflicts
- display the normalized key sequence
- support reset to default

Defaults:

- `Ctrl+Shift+4` — window
- `Ctrl+Shift+3` — full screen
- `Ctrl+Shift+2` — free form

### Focus and keyboard navigation

Every control must have an obvious focus state.

Focus rings use Shnapp Blue.

Keyboard navigation order follows visual order.

Escape behavior should be predictable:

- first Escape exits active drawing mode
- second Escape closes transient editor state or cancels capture
- dialogs follow Windows conventions

### Iconography

Use Fluent System Icons or visually compatible custom icons.

Rules:

- 16px icons inside compact controls
- 20px icons for standard toolbar actions
- 24px only for prominent empty states or onboarding
- consistent stroke weight
- no mixed filled/outlined icon styles in one toolbar unless state requires it

The Shnapp brand icon is reserved for app identity, onboarding, About, website, and marketing.

Do not use the brand icon as a generic capture glyph.

### Motion

Motion should communicate state.

Recommended:

- toolbar appearance: 100–140ms fade/slide
- panel change: 120–160ms
- hover: 80–120ms
- selection handles: immediate
- toast: quick fade/slide
- capture flash: subtle, under 120ms

Respect Windows Reduce Motion settings.

Avoid springy or playful animation.

The **New shnapp** action is the small brand exception: a blue gradient with sparse white sparkles that drift upward, fade, and return at varied positions. Keep the focus outline aligned to the painted button bounds. Stop decorative animation when the window is hidden, system animations are disabled, or high contrast is active.

### Cursor behavior

Use tool-specific cursors when helpful:

- crosshair for free-form capture
- text cursor for text placement
- crosshair/precision pointer for shapes
- move cursor for repositioning annotations
- resize cursors for handles

Cursor changes should make the active mode obvious without relying on instructional text.

### Error states

Errors should answer three questions:

1. What happened?
2. Did I lose my capture?
3. What can I do next?

Prefer recovery over blame.

If saving fails, keep the shnapp in memory and provide retry or alternate save location.

### Website components

The website should reuse the product system while allowing slightly more expressive brand treatment.

Recommended components:

- hero
- keyboard shortcut strip
- feature cards
- editor screenshot
- capture mode demo
- annotation tool demo
- AI assistance section
- download CTA
- FAQ
- GitHub link
- privacy statement

The site should look like the product, not like a generic startup landing page.

## Do's and Don'ts

- **Do** make the captured content visually dominant.
- **Don't** build a Photoshop-style editor around the capture.
- **Do** use Windows 11-native conventions where they reduce learning cost.
- **Don't** mimic macOS chrome even though window captures use a macOS-like export shadow.
- **Do** keep the primary toolbar compact and contextual.
- **Don't** expose every customization at all times.
- **Do** use intelligent defaults for fonts, colors, line widths, shadows, and caption placement.
- **Don't** force users through setup screens before their first capture.
- **Do** preserve exact screenshot pixels unless the user explicitly edits them.
- **Don't** tint, compress, sharpen, or color-correct captures automatically.
- **Do** make annotation placement direct and immediate.
- **Don't** require confirmation dialogs for routine edits.
- **Do** use one dominant interaction color: Shnapp Blue.
- **Don't** turn cyan and violet brand accents into competing action colors.
- **Do** maintain crisp, high-contrast handles and selection outlines.
- **Don't** rely on shadows alone to communicate selection.
- **Do** keep library metadata secondary to thumbnails.
- **Don't** make filenames the main visual hierarchy.
- **Do** make global shortcuts discoverable in the tray, Library, and Settings.
- **Don't** require memorizing shortcuts.
- **Do** use Segoe UI Variable for the native application experience.
- **Don't** introduce decorative UI fonts into the desktop app.
- **Do** treat AI as an optional accelerator.
- **Don't** make AI a gate in the core capture workflow.
- **Do** preview or make AI edits undoable.
- **Don't** silently send captures to remote AI services without clear consent.
- **Do** honor system theme, scaling, accessibility, and reduced-motion preferences.
- **Don't** override Windows accessibility conventions for branding.
- **Do** optimize every visual decision for speed and clarity.
- **Don't** add decorative complexity merely to make the app look more designed.
- **Do** let the product personality come from polish, restraint, and responsiveness.
- **Don't** let branding interfere with the capture task.
