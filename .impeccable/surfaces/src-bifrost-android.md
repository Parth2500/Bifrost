---
version: 1
slug: "src-bifrost-android"
primary_target: "src/Bifrost.Android"
related_targets: []
---

# Surface: Bifröst Android app

Scope and mode: the whole Android client (native shell plus Bifröst's own home page shown in it). Operate. Light and dark are both full schemes. Navigation: Home and Downloads only; no fake Watch or Profile.

Audience and task: a family member pairs the phone once, then reaches the home's extensions from one app; the admin also answers questions and manages devices. Constraints: Material 3 structure, 48 dp targets, system Back, edge-to-edge, sp type; the web view reaches only its own area.

## Direction contract

THESIS: The home is a teal glass object. Flat tonal layers in the logo's teal and ink, and only what needs the person (a question, an active destination) takes full colour. Refuses the grey-screen-plus-blue-accent default and blur-as-decoration.

OWN-WORLD: Teal #79B9B5 bar and tonal fills, ink #111820, pale teal ground in light and deep ink-teal ground in dark, amber for a waiting question, red only for failure. Surfaces are flat tinted fills with a one-pixel lit top edge; no blur, no gradients. State is never colour alone: filled dot online, ring offline. Roboto through the Material type scale.

STORY: The person sees which home they are in and that they are connected (three-arc mark in the bar), answers what is waiting, and moves between Home and Downloads. Signature move: the three connection arcs, lit when the home is reachable and dim when it is not; they also light one by one through pairing.

FIRST VIEWPORT: Teal top app bar with the hexagon mark, home name, "On your Wi-Fi", the arcs at the right and an overflow menu holding Forget this home; below it the amber question card (when one is waiting), then Devices, People and Extensions as tonal sections; a two-destination Material navigation bar at the bottom whose active item sits in a teal pill.

FORM: Tinted Glass, assigned, seed:a2232baeb8006f8d. Raised from the Saville sleeve (only the active item carries full colour), Ikeda (key fingerprint in tabular groups), labanotation (state by shape) and the lexicon (the bar names the home and the area).

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
