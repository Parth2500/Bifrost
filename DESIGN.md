---
name: Bifröst
description: A teal glass object for the family home; flat tonal layers in the logo's teal and ink, full colour only where a person is needed.
colors:
  teal: "#79B9B5"
  ink: "#111820"
  bar-text-secondary: "#1F3B3A"
  ground: "#EAF3F2"
  section: "#D3E7E5"
  outline: "#4D6563"
  amber: "#F3D9A4"
  on-amber: "#3B2A00"
  error: "#B3261E"
  edge: "#99FFFFFF"
  nav: "#111820"
  nav-idle: "#C9D8D6"
  ground-night: "#0E161C"
  section-night: "#1C2A33"
  outline-night: "#8FB0AD"
  on-surface-night: "#E4EEED"
  amber-night: "#E3A857"
  on-amber-night: "#1D1400"
  error-night: "#F2B8B5"
  edge-night: "#1FFFFFFF"
  nav-night: "#1C2A33"
typography:
  headline:
    fontFamily: "Roboto, system-ui, sans-serif"
    fontSize: "28sp"
    fontWeight: 400
    lineHeight: "36sp"
  title-large:
    fontFamily: "Roboto, system-ui, sans-serif"
    fontSize: "22sp"
    fontWeight: 400
    lineHeight: "28sp"
  title-medium:
    fontFamily: "Roboto, system-ui, sans-serif"
    fontSize: "16sp"
    fontWeight: 500
    lineHeight: "24sp"
  body:
    fontFamily: "Roboto, system-ui, sans-serif"
    fontSize: "14sp"
    fontWeight: 400
    lineHeight: "20sp"
  fingerprint:
    fontFamily: "monospace"
    fontSize: "16sp"
    fontWeight: 400
    lineHeight: "24sp"
  web-body:
    fontFamily: "Roboto, system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 400
    lineHeight: 1.5
rounded:
  section: "16dp"
  pill: "24dp"
  dot: "50%"
spacing:
  xs: "4dp"
  sm: "8dp"
  md: "16dp"
  lg: "24dp"
  xl: "32dp"
components:
  top-bar:
    backgroundColor: "{colors.teal}"
    textColor: "{colors.ink}"
    height: "64dp"
  section:
    backgroundColor: "{colors.section}"
    rounded: "{rounded.section}"
    padding: "4dp 16dp"
  question-card:
    backgroundColor: "{colors.amber}"
    textColor: "{colors.on-amber}"
    rounded: "{rounded.section}"
    padding: "16dp"
  button-primary:
    backgroundColor: "{colors.ink}"
    textColor: "{colors.ground}"
    rounded: "{rounded.pill}"
    height: "56dp"
  button-quiet:
    textColor: "{colors.ink}"
    rounded: "{rounded.pill}"
    height: "48dp"
  nav-bar:
    backgroundColor: "{colors.nav}"
    textColor: "{colors.nav-idle}"
  nav-item-active:
    backgroundColor: "{colors.teal}"
    textColor: "{colors.ink}"
---

# Design System: Bifröst

## Overview

**Creative North Star: "The Teal Glass Object"**

The home is one teal object seen through a phone. Surfaces are flat tinted fills in the logo's teal and ink, lifted from the ground by a one-pixel lit top edge instead of blur, gradient or drop shadow. Only what needs the person takes full colour: an amber question, the active destination, the connection arcs. Everything else stays tonal and quiet. Light and dark are both designed schemes (pale teal ground, deep ink-teal ground), not an inversion.

The bar always says which home you are in and whether it is reachable. The signature is the three connection arcs from the logo: all lit when the home is reachable, dim when not, filling one by one while connecting or pairing. State is never colour alone: a filled dot is online, a ring is offline; arcs always sit beside words.

Native shell (Material 3 Views, Roboto through the type scale) and the home's web page share one palette, so the web view reads as part of the app.

**Key Characteristics:**
- Flat tonal layers, lit top edge, no blur, no gradients, no shadows.
- Teal bar and ink bottom navigation frame a pale teal (or deep ink-teal) ground.
- Full colour is rationed: amber for a waiting question, red only for failure.
- State by shape (dot filled/ring, arcs lit/dim) plus words.
- 48dp minimum targets, sp type, edge-to-edge, system Back.

## Colors

A teal-and-ink tonal palette taken from the logo, with one warm exception for questions.

### Primary
- **Logo Teal** (`colors.teal`): top app bar, status bar, pairing hero, active navigation pill, tonal accent in dark scheme (primary and web accent).
- **Logo Ink** (`colors.ink`): text and icons on teal, primary button fill in light, progress indicator, bottom navigation in light.

### Secondary
- **Waiting Amber** (`colors.amber`, `colors.amber-night`): the question card only, with `on-amber` text. In dark it deepens to a warmer amber.

### Neutral
- **Pale Teal Ground** (`colors.ground`) and **Deep Ink-Teal Ground** (`colors.ground-night`): window background in each scheme.
- **Tonal Section** (`colors.section`, `colors.section-night`): section fills, dialogs, dark bottom nav.
- **Bar Secondary Text** (`colors.bar-text-secondary`): the "On your Wi-Fi" line on the teal bar.
- **Outline** (`colors.outline`, `colors.outline-night`): Material outline and field strokes.
- **Lit Edge** (`colors.edge`, `colors.edge-night`): the one-pixel top highlight; strong white in light, faint in dark.
- **Night Text** (`colors.on-surface-night`): body text on the dark ground.
- **Nav Idle** (`colors.nav-idle`): inactive navigation icons and labels.
- **Error** (`colors.error`, `colors.error-night`): failure messages only.

### Named Rules
**The Full Colour Rule.** Only what needs the person takes full colour: the open question, the active destination, the lit arcs. Anything else is a tonal fill.
**The Failure Red Rule.** Red appears only for failure text. A waiting question is amber, never red.
**The Same Palette Rule.** The web page declares the same values as custom properties (`--ground`, `--section`, `--amber`, `--err`, `--edge`); change a colour in both places.

## Typography

**Display Font:** Roboto (Material type scale; system-ui fallback on the web page)
**Body Font:** Roboto
**Label/Mono Font:** system monospace, for the key fingerprint only

**Character:** Plain, native, unforced. Hierarchy comes from the Material scale and weight 500, not from a second face.

### Hierarchy
- **Headline** (Material headlineMedium, 28sp): the app name on the pairing hero.
- **Title** (titleLarge 22sp on pairing and unreachable screens; titleMedium 16sp/500 for the home name in the bar and the checked home on pairing): names the place.
- **Body** (bodyMedium 14sp, bodyLarge 16sp, bodySmall 12sp): explanations, addresses, the bar's area line (bodySmall).
- **Fingerprint** (monospace, bodyLarge): the pinned key, shown in groups for checking.
- **Web page** (16px/1.5 body; 24px/500 person name; 20px/500 section headings; 14px meta): the home page inside the web view.

### Named Rules
**The Scale Only Rule.** Sizes come from the Material text appearances in sp; do not set ad hoc pixel sizes in the shell.

## Layout

Single-column, scrolling, edge-to-edge. The shell is a vertical stack: 64dp teal toolbar (mark 40dp, home name over area line, arcs 40x24dp at the right, overflow menu), a 3dp indeterminate progress line under it, content, and a bottom navigation bar with two destinations (Home, Downloads). Pairing is a scroll view: teal hero (24dp side padding, 40dp top, 32dp bottom), then a 24dp padded form. Spacing steps: 4, 8, 12, 16, 24, 32dp. On the web page, 16px body padding, 24px above section headings, 56px minimum row height, 12px gap inside rows. The web view reaches only its own area on the paired home.

## Elevation & Depth

Tonal layering, no shadows. A section is a darker, tinted fill over the ground, with a one-pixel lit top edge (an edge-coloured layer visible above the fill) in the native `section_bg` drawable and an `inset 0 1px 0` highlight on the web. Depth beyond that comes from colour only. The app bar is not lifted on scroll.

### Named Rules
**The Lit Edge Rule.** A raised surface is a flat fill plus a one-pixel top highlight. No blur, gradient or drop shadow.

## Shapes

Soft, consistent rounding: 16dp for sections, cards and the pairing field; full pill (24dp on web, Material default native) for buttons; circles for status dots (12px, 2px ring). The three arcs are round-capped strokes (stroke = height / 7), half circles of three radii.

## Components

### Buttons
- **Shape:** pill (24dp), 56dp tall native, 48dp minimum on web.
- **Primary:** ink fill with ground text in light; teal fill with ink text in dark. On the amber card the button inverts: on-amber fill, amber text.
- **Quiet:** no fill, 1px ink-secondary ring, ink text (Sign out).
- **Focus:** web buttons get a 3px ink outline with 2px offset.

### Section (tonal container)
Section fill, 16dp corners, lit top edge, 4dp 16dp padding on web; 16dp padding for the pairing check card. Rows inside are 56dp minimum, divided by a 1px translucent ink line.

### Question Card
Amber fill, on-amber text, 16dp corners, lit edge, 16dp padding; choices as inverted pill buttons in a wrapping row. Appears only while a question is open, above the sections.

### Inputs / Fields
Material 3 outlined text field, 16dp corners, clear-text end icon. Errors are red text in a polite live region below the field.

### Navigation
Bottom bar in ink (light) or section tone (dark); two items. The active item sits in a teal pill with ink icon and ground label; inactive items are nav-idle. Top bar is teal in both schemes with ink icons.

### Status Dot
12px circle: filled ink when online or running, ring only when offline. Always followed by a name and meta text.

### Connection Arcs (signature)
Three concentric half-arcs in ink (light ink in dark when drawn on the ground). Connected: all full opacity. Off: dim (about 35% on bar, 43% on ground). Searching: lit one at a time, 1.2s loop, linear, respects reduced animation. Content descriptions say "Connected to your home", "Connecting", "Not connected".

## Do's and Don'ts

### Do:
- **Do** keep surfaces flat: tonal fill plus a one-pixel lit top edge.
- **Do** show state by shape plus words (dot filled or ring, arcs lit or dim).
- **Do** keep the bar teal with ink icons in both schemes.
- **Do** keep targets at 48dp or more and type in sp.
- **Do** use the same hex values in `colors.xml` and the web page's custom properties.

### Don't:
- **Don't** use blur, gradients or drop shadows for depth.
- **Don't** use colour as the only signal of state.
- **Don't** use red for anything but failure, or amber for anything but a waiting question.
- **Don't** add navigation destinations for features that do not exist (no fake Watch or Profile).
- **Don't** fall back to the grey-screen-plus-blue-accent default.
