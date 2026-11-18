# ADR 004: Extensions come from signed GitHub releases

- Date: 2026-11-18
- Status: Accepted. Built and tested against a faked GitHub; no real signed release exists yet (the owner's key does not).
- Affects: design §8.2, §8.3, §11; step 5

## Context

Design §8 says an extension is a separate program that Bifröst installs from the latest release of its repository,
checks against a public key built into Bifröst ("HTTPS to GitHub is not enough"), unpacks into a versioned folder and
switches to, keeping the version before. The manifest format and signature scheme were left to the builder.

## Decision

**Package.** `<name>-<version>.bifrost`, a zip with `manifest.json` at its root and the program under `bin/<platform>/`.
The manifest names the extension (one of the built-in names), a `1.2.3` version, the interface range it works with
(this Bifröst speaks 1), the program per platform (`linux-x64`, `linux-arm64`), the events it emits and wants, and
the folders it asks for. **Signature.** ECDSA P-256 over the SHA-256 of the whole package file, as 64 bytes
(r then s), in a file next to it called `<package>.sig`. [my call: the platform has ECDSA; the design left the scheme open.]

**Keys.** `ReleaseKeys.BuiltIn` carries the owner's public keys, one in use and one backup kept apart; a release
signed by either is accepted. **It is empty until the owner pastes theirs in, and with none every install is refused.**
The private keys are made by `bifrost release keygen` on the owner's own machine and never leave it.

**Install.** The package is spooled to disk and hashed as it arrives; the signature is checked before the zip is
opened. Then the manifest is validated, the unpacking refuses a path that leaves the folder, a link, or a repeated
name, and the program is made executable. Files go in `<data>/extensions/<name>/versions/<version>/`, and a `current`
link is switched with `rename(2)` so a reader sees the old version or the new one. Only `current` and `previous` are
kept. **A release that is not newer than the installed one is refused** even when properly signed, so an old, flawed
release cannot be replayed; `rollback` is the only way back, and only to the previous version.

**Where from.** The latest release of the owner's repository for that extension (`ExtensionUpdates.Repositories`),
over HTTPS, with the package and `.sig` as release assets. Nothing else is downloaded. GitHub is trusted for
delivery only; the signature is what is trusted.

**Updates are offered, never applied silently.** A daily check asks the admin a question ("Update Sleipnir?",
Update or Not now), once per version, and installs only on Update. Installing a first time, or updating now, is a
button on the host screen. All installing, rolling back, and starting or stopping is host only (`403 host_only`
from a remote admin), logged in the audit log, and from the command line behind the system password prompt.

**From a file.** `bifrost extension install --from <file> [--sig <file>]` uploads the package over the admin socket,
with the same signature check, for a machine with no internet.

## Consequences

- A newly installed extension starts when Bifröst restarts; an already-configured one restarts onto the new version.
- `extensions.json` still names a program by path and wins over an installed one.
- The home needs the internet only to look for and fetch releases.
- Losing the private key means no new release can be trusted until a new Bifröst carries a new public key,
  which is why a backup key is carried.
