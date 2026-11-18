# Product

<!-- impeccable:product-schema 1 -->

## Platform

android

## Users
Everyone in a family who has a phone: the admin (the person who installed the home) and members. The Bifröst Android app is the single app on a family member's phone. There are no separate Android apps for Sleipnir or Saga. A person pairs once with an invitation (link or QR), sets up their profile, then uses the home's extensions from here: sending download requests (Sleipnir), and later watching video streamed from Saga. The admin also answers questions and manages people and devices from the same app.

## Product Purpose
Bifröst is the home: one always-on service on a home machine that devices pair with once and that extensions plug into. The Android app is its client and the main entry point to everything the home does. Success: a person pairs in one step, understands which home they are in, and reaches each extension's screens without thinking about the machinery.

## Positioning
One pairing and one app for the whole home, with no account, password or internet needed after install. The key is pinned at pairing, so the phone talks only to its own home.

## Operating Context
- The phone reaches the home only on the home Wi-Fi; away from home it cannot connect.
- Pairing uses a `bifrost://pair?h=&fp=&t=&n=` link (or QR); the person checks the home name, address and key fingerprint before pairing.
- Today the app is a thin native shell (pairing screen, tab bar, Forget) around web pages served by the home, shown in a web view pinned to the home. Bifröst's own page is HomePage.cs in the Bifrost repo; Sleipnir's page is served by Sleipnir.
- A 401 from the home means the device was revoked: the app forgets the pairing and returns to pairing.

## Capabilities and Constraints
- Native shell: pair, switch between areas (Home, Downloads), Forget this home (asks first, signs the device out on the home).
- Not yet built: profile setup, Saga streaming, notifications of questions. Do not present them as working.
- Android only, minimum API 24, .NET 10 Android with programmatic views; ApplicationId `org.bifrost.client`.
- The web view may reach only its own area on the paired home; nothing else loads.
- The name is written Bifröst, with the ö.

## Brand Commitments
The Bifröst logo is confirmed by the owner: a dark hexagon with rainbow arcs cut out, on teal. Masters and Android launcher assets are in `asgard-icons/bifrost/` in the suite repo (not committed; ignored by git).

## Evidence on Hand
No testimonials, metrics or user research. The current UI is stock Android buttons and a plain web page.

## Product Principles
- The person always knows which home they are connected to.
- Nothing happens to a device silently: failed sign-out, revocation and forgetting are all said in words.
- The app is one entry point; extensions appear inside it, not as separate apps.
- A home that works offline: no step may need the internet or an account.
