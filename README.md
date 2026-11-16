# Bifröst

> Named after the bridge between the worlds, guarded by Heimdall.

Bifröst is **the home**: the one service on a home machine that the family's devices pair with,
that extensions plug into, and that links the home to the online group (Heimdall and Huginn).

> **Status: step 2 of 7 is built.** `bifrost daemon` keeps people, devices and their tokens, serves
> them on a loopback API, and runs extensions: it starts them, restarts them and routes
> `/x/<name>/…` to them with the caller's identity. `bifrost service install` runs it as a sandboxed
> system service, the host admin reaches it on an admin socket without a token, and `bifrost desk`
> shows the admin's questions on the host screen. Two checks on a real machine are still open: the
> service install and a click on a real notification. Pairing and the LAN (step 3) come next. Read
> [`docs/design.md`](docs/design.md), and [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) to run it.

## What it does

- **Pairs devices once.** A phone or TV pairs with the home by QR code or a compared code, and pins
  the home's key. It then reaches every extension.
- **Runs extensions.** Sleipnir (downloads), Saga (video), Files and Norns are separate programs.
  Bifröst installs them from their signed GitHub releases, starts them and routes requests to them.
- **Knows people and folders.** Each person has one folder with a `private` and a `public`
  subfolder. Bifröst decides where a file goes and who may see it.
- **Asks the admin.** One question goes to the host screen, the admin's phone and Huginn at once.
  The first answer wins.
- **Links the home online.** The home signs in to Heimdall as a client and always dials out.
  Nothing online can reach into the home.

## What it is not

It holds no downloads, no media and no app screens. It is not an identity provider, and it has no
passwords. It does not connect two homes.

## Where it fits

Bifröst is part of the Asgard suite's LAN group. The suite-level decisions behind this design are
in the suite's `docs/decisions.md` (S1 to S14).

## Plan

Bifröst is built by extracting people, devices and the request pipeline from Sleipnir, with
Sleipnir as its first extension. The order of work is in the design, section 12.
