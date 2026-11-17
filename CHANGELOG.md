# Changelog

## Unreleased

- **Android:** when the home refuses the device's token (it was signed out, or the key changed) the app drops
  the pairing and shows the pairing screen again. A **Forget** button does the same on purpose.

### Added: Bifröst's own page, and areas in the client (step 4)

- **`/ui/`** is a first plain page: devices with who is online (seen in the last 3 minutes), sign out a
  device, the people, the extensions, and open questions with their answer buttons. A member sees only
  their own device; the people and extensions are left out for them. It is only the page: its data comes
  from the API with the device's token.
- **`Area`** in `Bifrost.Client` says which part of the home a web view may reach: an extension's folder, or
  the home pages (`/ui/` and the API they call). The Android app has a Home and a Downloads button; each
  has its own web view limited to its area. A path starting with `/` is now from the home's root, and
  outside the area it is refused.

- **Decision, from a real phone:** the Android web view cannot carry a WebSocket to a self-signed home, so
  extension screens use server-sent events for live updates. `FAKE_MODE=page` in the fake extension
  serves a page that tests this (script, style, image, fetch, off-site block, WebSocket, events).

### Added: `bifrost invite`, a home name, a LAN-wide failure budget (step 3)

- **`bifrost invite [person]`** prints an invitation link and a QR code to scan with the app (the admin's
  own person without a name). A link made over loopback now names this machine's address on the home
  network, not `localhost`.
- **`"name": "Our home"` in `lan.json`** is what links and the mDNS announcement call the home (the machine
  name by default).
- **One failure budget for the whole network:** 300 wrong tokens or secrets a minute across all sources, on
  top of the per-source one, so a flood from many addresses slows down even when the per-source table is full.

### Added: the Android app, first cut (step 4)

- `src/Bifrost.Android`: a thin .NET for Android shell, no MAUI. It pairs by invitation link (also from
  a tapped `bifrost://pair` link), then shows `/x/sleipnir/` in a web view whose requests all go through
  `BifrostClient.GetHomeAsync`, so the key is pinned and the token is sent on every sub-request. It is
  built outside the solution: `dotnet build src/Bifrost.Android -p:JavaSdkDirectory=<JDK 21>`.
- Joining by compared code is audited now: `join.approved` and `join.rejected` are in the audit log.

### Added: mDNS announcement (step 3)

- While the home network listener is on, Bifröst announces `_bifrost._tcp` through Avahi
  (`avahi-publish`), with TXT `name`, `api=v1`, `port` and `fp` (the first 16 characters of the
  fingerprint). It stops with the daemon. Without Avahi it logs one warning and carries on.

### Added: the shared client library (step 4, first part)

- **`Bifrost.Client`**: `PairingLink.Parse`, `BifrostPairing.PairWithLinkAsync` (the secret goes only to
  a server whose key matches the link), `BifrostPairing.JoinAsync` (shows the code, waits for the admin,
  drops the join if the key changes between steps) and `PinnedHttp`, an `HttpClient` that trusts one key
  by its fingerprint and nothing else. The code arithmetic moved to `Bifrost.Core.Api.JoinCode`, shared
  with the server.
- **`BifrostClient`** (pinned, signed in): `GetMeAsync`, `GetExtensionsAsync`, `ExtensionAddress(name)` and
  `GetFromExtensionAsync`. A web view must send the token in a header and pin the key itself.
- Rotating the home key now stops all pairing first and signs devices out before and after the rotation.

### Added: the view-only browser fallback, `identity rotate`, the polkit prompt (step 3)

- **Browser fallback.** `"fallbackPort": 7782` in `lan.json` opens a plain-HTTP listener, off by default.
  A browser pairs by the compared code and gets a `browser-fallback` token. That token can only read
  (GET/HEAD), on every listener. An app token is refused on the fallback port.
- **`bifrost identity rotate`** asks the system password prompt (polkit), then has the daemon make a new
  home key over the admin socket. Restart to use it; every device is signed out at once and pairs again. `service install` writes
  the polkit policy; `service uninstall` removes it.
- Starting join requests is limited to 10 a minute per source.

### Added: pairing by a compared 8-digit code (step 3)

- A device with no camera: `POST /api/v1/join {name, platform, commit}` (commit = SHA-256 of its nonce)
  returns a request id and the server's nonce; then `POST …/{id}/reveal {nonce}` shows the nonce. Commit
  before reveal stops a middleman choosing a nonce to make the two codes match. The code is 8 digits of SHA-256(fingerprint the device saw | device nonce |
  server nonce), so a device in the middle shows a different one. Admins see it in
  `GET /api/v1/join-requests`, then `POST …/{id}/approve {personId}` or `DELETE …/{id}`.
- The device asks `POST /api/v1/join/{id}/collect {nonce}`: 202 waiting, 403 refused, 200 with its
  token once. At most 5 requests wait, each for 5 minutes. Not yet in the audit log.

### Added: pairing by invitation link (step 3)

- `POST /api/v1/invitations {personId}` (admin) returns a `bifrost://pair?h=…&fp=…&t=…&n=…` link,
  single-use, valid 10 minutes, kept in memory as a hash. Needs the home network listener on.
- `POST /api/v1/pair {secret, name, platform}` (no token) returns a device token. A wrong secret counts
  against the caller's failure budget. Showing the link as a QR code is left to the screens.

- `bifrost desk`: `BIFROST_DESK_EXPIRE_SECONDS` sets how long a notification stays (default 0: until answered).
  For the installed unit use `systemctl --user edit bifrost-desk` and add `Environment=BIFROST_DESK_EXPIRE_SECONDS=60`.

### Added: the home key and HTTPS on the home network (step 3, first part)

- **Home key.** An ECDSA P-256 key and a 10-year self-signed certificate, made on first use as
  `home-key.pfx` in the data folder (owner-only). Its fingerprint is SHA-256 over the public key.
- **Home network listener.** HTTPS on all interfaces, port 7781, **off** until `lan.json` in the
  settings folder says `{"enabled": true, "port": 7781}`. The `Host` check also accepts the machine's
  own name, `<name>.local` and its own addresses; anything else is still refused.
- Not yet: pairing, so no device has a token on this listener yet; mDNS; `bifrost identity rotate`
  (the rotation code exists, the command does not).

### Added: asking the admin (step 2d)

- **Questions.** Bifröst or an extension asks the admin a question with 2 to 4 choices and an
  expiry. It reaches every place the admin is at once, and the first answer wins. A later answer
  gets `409 already_closed`. Every answer is in the audit log. Questions are kept in memory and do
  not survive a restart; extensions ask again.
- **For extensions:** `POST /v1/questions`, `GET /v1/questions/<id>?wait=<s>` and
  `DELETE /v1/questions/<id>` on the extension API socket.
- **For admins:** `GET /api/v1/questions` (long-poll with `?after=<seq>&wait=<s>`) and
  `POST /api/v1/questions/<id>/answer`, on the loopback API and the admin socket.
- **`bifrost desk`** shows each open question as a desktop notification with a button per choice,
  and takes it down when it is answered elsewhere. `bifrost desk install` runs it in your desktop
  session. It needs `notify-send` 0.8 or later.
- Sleipnir uses this for pending downloads: by default a member's download waits for the admin.

### Changed: the host admin is the OS account, not a token file (step 2c)

- **Breaking: `admin.token` is gone.** The host admin reaches Bifröst on the admin socket,
  `admin.sock` in the `bifrost-admin` runtime folder, with no token:
  `curl --unix-socket /run/bifrost-admin/admin.sock http://localhost/api/v1/me`. The operating
  system says which account is calling. Any other account gets `403 not_the_admin`. A token file
  left by an older version is deleted at start.
- **`bifrost service install | uninstall | status`** runs Bifröst as a sandboxed systemd service
  under its own `bifrost` account. Install needs `sudo`, records who ran it as the admin (or
  `--admin <account>`, never root) in `/etc/bifrost/admin.json`, and prints each command first.
  Uninstall keeps the data unless `--purge`.
- The runtime folder is set to `0700` at every start. The loopback port still needs a device token.

### Added: extensions (step 2b)

- **Extensions run under the daemon.** Each one named in `extensions.json` (in the settings folder)
  starts as a child process after Bifröst's own listener and stops with it: SIGTERM, then SIGKILL
  after 10 seconds. Only the built-in names start: `sleipnir`, `saga`, `files`, `norns`. A broken
  file or entry is logged and skipped.
- **Restarts:** a crashed extension restarts after 1, 2, 4 … 60 seconds. Five crashes in 10 minutes
  mark it `failed` until Bifröst restarts. Bifröst keeps answering either way.
- **`/x/<name>/…`** forwards to the extension over a private Unix socket. Bifröst checks the device
  token, removes the client's `Authorization`, `Cookie`, `Forwarded`, `X-Forwarded-*` and
  `X-Bifrost-*` headers, and adds `X-Bifrost-Person-Id`, `-Role`, `-Device-Id`, `-Device-Kind` and
  `X-Forwarded-Prefix`. An extension that is not running answers `503 extension_unavailable`.
- **`GET /api/v1/extensions`** lists each extension with its state, pid and restart count.
- **The extension API:** a second private socket per extension (`bifrost.sock`), where it looks up
  a person or a device by id.

### Added: people, devices and the loopback API (step 1)

- **`bifrost daemon`** runs the home as one instance per machine. It listens on `127.0.0.1` and
  `::1`, port 7780 or the next free one, recorded in `daemon.json`. It stops cleanly on SIGTERM,
  SIGINT, SIGHUP or `POST /api/v1/server/shutdown` (admin only).
- **People and devices** live in a private SQLite file, `bifrost.db`. The first start creates an
  admin, named after the operating-system account, and the "This computer" device. Each device has its own token. Only its hash is stored. Revoking works on the next
  request.
- **Roles:** `admin` and `member`. Admins manage people (`/people`). Everyone sees and revokes their
  own devices (`/devices`) and sees who they are (`/me`). The last admin cannot be demoted or deleted.
- **Audit log:** every change to a person or a device, with who made it.
- **Folders:** under systemd, the `STATE_DIRECTORY`, `RUNTIME_DIRECTORY` and
  `CONFIGURATION_DIRECTORY` that the unit sets. Otherwise per-user XDG folders.
- **API docs:** OpenAPI at `/openapi/v1.json`, browsable at `/scalar`.
