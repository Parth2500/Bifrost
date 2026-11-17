# Bifröst: design

*Status: draft, 2026-11-14. The owner answered every open question on the same day (§14). No code yet.*

Bifröst is the bridge between the worlds. In the suite it is **the home**: the one service on the
home machine that devices pair with, that extensions plug into, and that links the home to the
online group.

Decisions are marked **[owner]** when the owner made them (see the suite's [`decisions.md`](../../docs/decisions.md), S1
to S14). They are marked **[my call]** when the designer chose a default the owner has not
reviewed. Parts marked **[from Sleipnir]** are taken from Sleipnir's service design of 2026-11-13,
which the owner reviewed for Sleipnir.

## 1. Intent

A person installs Bifröst on one always-on machine at home. They pair the family's devices with it
once. They then add what they want the home to do: download (Sleipnir), stream video (Saga), share
files, approve requests (Norns). Each of those is an extension.

Bifröst gives an extension people and devices. An extension gives Bifröst a capability [owner].

## 2. Goals, non-goals, anti-goals

**Goals**
- One pairing for the whole home. A device pairs with Bifröst, not with each extension [owner].
- Install, start, stop and update extensions from their GitHub releases [owner].
- People, roles and devices, with revocation.
- Discovery on the Wi-Fi without typing addresses.
- Local events between extensions ("download finished" reaches Saga).
- The home's folders: where a person's files go, and who may see them [owner: S11].
- One outbound link to the online group, for notices and their answers [owner].
- Works with no internet after install.

**Designed for, built later**
- A courier for Huginn messages and call set-up between devices in one home (decision S9).
- A named HTTPS address for linked homes. Parked: native clients make it unnecessary.
- Windows and macOS hosts.

**Non-goals**
- Downloads, media, workflows or any app screen. Extensions own those. Bifröst knows folders and
  people. It never reads what is inside a folder.
- A user database with passwords. Devices are paired. Nobody types a password into Bifröst.
- Access from outside the home network.
- A connection between two homes [owner].
- Extensions from anyone but the owner's repositories [my call].
- Becoming an OIDC provider. That is Heimdall's job, online.

**Anti-goals** (failure even if it works)
- Anything on the home network can use the home without pairing.
- Anything online can make the home act. The online group only answers the home's questions [owner].
- Bifröst runs an extension it has not verified.
- Bifröst needs the internet, an account or telemetry to run.
- An extension can read another extension's data or secrets.
- A person sees a file from another person's private folder through any extension.
- An extension writes to a path that Bifröst did not hand it.
- A stopped or crashed extension stops Bifröst or another extension.
- Sleipnir alone on one machine stops working [owner].

## 3. Constraints

- .NET 10, pinned SDK. Build rules, API style, tests and docs as in Heimdall and Sleipnir: minimal
  APIs, OpenAPI with Scalar, problem+json with a stable `code`, `TreatWarningsAsErrors`, NuGet audit,
  xUnit, `FakeTimeProvider`, CsCheck, a threat model with numbered decisions, ADRs [my call].
- EF Core with SQLite and snake_case names [my call, from Sleipnir].
- Heimdall's secret rules by number: D3 (256-bit secrets, stored as SHA-256), D4 (constant-time
  comparison), D5 (one error per failure kind), D7 (never log secrets) [from Sleipnir].
- **A system service** under its own unprivileged account, always on [owner: S12]. Linux first, as
  a systemd system unit. Installing needs administrator rights once. Running needs none.
- On Fedora, SELinux confines a system service. The admin chooses the home root (§8.9). Bifröst
  checks that a service may read and write there, and says what to change if it may not.
- **Licence:** must allow MIT (Sleipnir) and GPL-2.0 (Saga) extensions to run beside it as separate
  programs. MIT is suggested [my call]. No GPL code may be copied into Bifröst or its client app.
- The extension interface is additive within a version, because extensions ship on different dates.

## 4. Architecture

```
                         online group (Heimdall, Huginn)
                                     ▲
                                     │ HTTPS, outbound only (§9)
 ┌───────────────────────────── Bifröst (home machine) ─────────────────────────────┐
 │  home key · people · devices · pairing · discovery · events · extension manager   │
 │  LAN listener (HTTPS, pinned)      loopback listener (HTTP)                       │
 │        │ routes /x/<name>/…  with the caller's identity attached                  │
 │        ▼ one private socket per extension                                         │
 │   Sleipnir        Saga         Files        Norns        (separate programs)      │
 └────────▲──────────────────────────────────────▲────────────────────────────────────┘
          │ LAN HTTPS, key pinned at pairing      │ localhost
   Bifröst client app (phones, TVs)        browser on the host
```

| Part | What it is |
|---|---|
| `bifrost` | The daemon. One per home machine |
| Extension | A separate program that Bifröst installs, starts and routes to |
| Bifröst client app | The one app on other devices. Pairs, pins, and shows each extension's screens |
| Online link | Bifröst's outbound connection to Heimdall and Huginn |
| Desk companion | A small program in the admin's desktop session. Shows notifications and opens Bifröst's screen. A system service cannot do either |

## 5. People, devices, roles

Taken from Sleipnir's design, moved here [owner: S6].

- **Person:** id, display name, role, created. Optionally a linked online identity
  (`issuer` + `subject`), used only for notices (§9). A person has no password.
- **Device:** id, owning person, name, platform, kind (`app` | `browser-fallback`), token hash,
  created, last seen, revoked.
- **Roles:** `admin` and `member`. The person who installs Bifröst is the admin.
- **Credentials:** each device holds a random 256-bit token. Bifröst stores its SHA-256. Revocation
  takes effect on the next request [from Sleipnir].
- **The host [owner: S12]:** signing in on the host means being logged in to the operating system
  as the admin's account. Bifröst has no password. The installer records which operating-system
  account is the admin. Bifröst's local admin socket accepts only that account, checked by the
  operating system (peer credentials on Linux) [my call].
  - While the admin's desktop session is unlocked, questions can be answered directly.
  - A host-only action asks for the operating system's own password prompt (polkit on Linux) [my call].

**Admin model [owner: S12].**

| Action | Where |
|---|---|
| Settings, install or remove an extension, turn the LAN listener on, rotate the home key, link the home online | Host only |
| Approve a pairing, revoke a device | Host, or the admin's paired device on the LAN |
| Answer a request (for example a download) | Host, the admin's paired device, or Huginn |

A pairing cannot be approved from Huginn: the admin must compare the code on the new device.

There is one admin person by default. More can be added later without changing the model.

### 5.1 Asking the admin [owner: S12]

One facility, used by Bifröst itself (pairing, updates) and by any extension (a pending download).

1. The asker gives Bifröst a question: a title, a short text, the choices, and an expiry.
2. Bifröst sends it to **every place the admin can be reached, at once**:
   - the host screen, through the desk companion, when the admin has a desktop session
   - the client app on the admin's paired devices, when they are on the home network
   - Huginn, as a notice with actions, when the home is linked (§9) and the question allows it
3. **The first answer wins.** Bifröst withdraws the question everywhere else and tells the asker.
4. With no answer before the expiry, the asker gets "expired" and decides what that means.

- A plain approve or reject needs no Norns. When Norns is installed, an extension gives the request
  to Norns, which applies its rules and history and uses this same facility to reach the admin.
- On a machine with no desktop, the client app and Huginn carry the question.
- The client app holds a connection only while it is open or the phone allows it. Away from home it
  cannot reach the home at all. Huginn is the only channel that reaches the admin anywhere.

## 6. Networking

### 6.1 Listeners

| Listener | Address | Protocol | Default |
|---|---|---|---|
| Loopback | `127.0.0.1`, `[::1]` | HTTP | Always on |
| Home network | LAN interfaces | HTTPS only | Off until the admin finishes first-run setup [my call] |
| Browser fallback | LAN interfaces | HTTP | **Off.** View-only when on [owner: S8] |

Ports are fixed and configurable, so a pinned client can reconnect [from Sleipnir]. Exact numbers
are left to the builder.

### 6.2 The home key [from Sleipnir]

- On first start Bifröst creates an ECDSA P-256 key pair and a long-lived self-signed certificate.
- The **fingerprint** is SHA-256 over the public key (SPKI). Renewing the certificate with the same
  key keeps the fingerprint.
- Clients pin the fingerprint, not the address or the name. A new IP address changes nothing.
- `bifrost identity rotate` makes a new key. Every device pairs again.

### 6.3 Discovery [from Sleipnir]

- mDNS / DNS-SD, service type `_bifrost._tcp`, announced only while the LAN listener is on.
- TXT record: home name, API version, port, a short fingerprint prefix. Nothing about people or content.
- Publish through the operating system's responder where there is one (Avahi, Bonjour).
- Discovery is a convenience. A QR code or a typed address always works.

### 6.4 Request rules [from Sleipnir]

- Reject a request whose `Host` is not `localhost`, a loopback address, or one of the home's own
  LAN names and addresses (DNS rebinding).
- Tokens travel in the `Authorization` header only. No cookies on the pinned listeners.
- No CORS headers by default.

## 7. Pairing

One ceremony for the whole LAN group: **scan or compare a code, check the fingerprint, get a
credential.**

### 7.1 Path A: the device has a camera

1. The admin creates an invitation for a person. It is single-use and expires after 10 minutes.
2. Bifröst shows a QR code and a link:
   `bifrost://pair?h=<address:port>&fp=<fingerprint>&t=<secret>&n=<home name>` [my call: the
   parameter names follow Bragi's `mpsp://pair?h=…&fp=…&t=…`, so the two could converge].
3. The device connects over HTTPS, checks the certificate against `fp`, and only then sends the secret.
4. It receives its device token. Creating the invitation was the approval.

### 7.2 Path B: the device has no camera (a TV)

1. The device finds the home by mDNS and opens HTTPS. It trusts the certificate for this request only.
2. It sends a join request: name, platform, a random nonce.
3. Both sides show a **6-digit code** derived from the fingerprint the device actually saw and both
   nonces. A device in the middle shows a different certificate, so the codes differ.
4. An admin sees the request on the host or a paired device, compares the code, picks the person
   and approves.
5. The device receives its token and pins the fingerprint.

Limits: at most 5 pending requests, each expires after 5 minutes, rate limits per source address,
and every rejection is audited [from Sleipnir].

### 7.3 Browser fallback

When the admin turns the fallback on, a browser on another device pairs by path B and receives a
session with kind `browser-fallback`. Extensions must treat that kind as view-only. Its traffic is
not encrypted, and the pairing screen says so.

## 8. Extensions

### 8.1 What an extension is

A separate program with its own repository, licence and release dates. Bifröst installs it, starts
it, routes requests to it and stops it. The list is built into Bifröst [my call]:

| Extension | Needs Bifröst | Notes |
|---|---|---|
| Sleipnir | No. Runs alone on one machine [owner] | Under Bifröst it drops its own users and devices |
| Saga | Yes | Reads media folders only |
| Files | Yes | Device-to-device sharing, if it is split from Sleipnir [owner] |
| Norns | Yes, in a home. It can also be installed online (S10) | Holds the home's requests |

### 8.2 Manifest

Each release carries a manifest. Exact format is left to the builder. It must state:

- name, version, and the range of Bifröst interface versions it works with
- the program to start, per platform
- the events it emits and the events it wants
- whether it accepts the `browser-fallback` kind, and for which routes
- the folders it needs, and whether it writes to them

### 8.3 Install and update

1. Bifröst reads the latest release of the extension's repository on GitHub.
2. It downloads the package and its signature.
3. It checks the signature against a public key built into Bifröst. **HTTPS to GitHub is not
   enough.** A package that fails the check is deleted and the failure is audited.
   - The private key is offline. The owner signs each release by hand [owner: S14].
   - Bifröst carries two public keys: the one in use and a backup kept somewhere separate. A release
     signed by either is accepted.
   - The client app's Android signing key is a different key. It needs the same care and a backup:
     without it, nobody can update the app.
4. It unpacks into a versioned folder and switches a pointer. The previous version stays for rollback.

- Updates are offered, not applied silently. The admin confirms on the host [my call], through §5.1.
- `bifrost extension install --from <file>` installs a package from disk with the same signature
  check, for a machine with no internet.
- Bifröst updates itself the same way.

### 8.4 Running

- Bifröst starts each extension as a child process, under Bifröst's own service account. So Saga
  can read what Sleipnir wrote.
- A folder the admin adds from elsewhere (existing media) must be readable by the service account.
  Bifröst checks this when the folder is added and says exactly what is missing.
- Each extension listens on a **private socket** that Bifröst creates for it: a Unix domain socket
  with `0600` in the runtime folder, or the platform's equivalent [my call]. An extension opens no
  network port of its own.
- A crash restarts the extension with a growing delay. After repeated crashes Bifröst stops trying
  and tells the admin.
- Each extension has its own data folder. Bifröst gives it nothing else.

### 8.5 Routing and identity

- Clients call `https://<home>/x/<name>/…`. Bifröst authenticates the device, then forwards the
  request to that extension's socket.
- Bifröst removes any identity headers the client sent and adds its own: person id, role, device
  id and device kind.
- The extension trusts those headers because nothing but Bifröst can reach its socket.
- An extension has no accounts, sessions or pairing of its own.

### 8.6 What an extension can ask Bifröst

A small API on a second private socket [my call]:

| Call | Purpose |
|---|---|
| Publish an event | §8.7 |
| Subscribe to events | §8.7 |
| Look up a person or device by id | Display names |
| Send a notice, and wait for its answer | §9 |
| Resolve a folder for a person | §8.9 |
| List the folders a person may see | §8.9 |

### 8.7 Events

- Envelope [from Sleipnir]: `{ "v": 1, "seq": 123, "type": "sleipnir.download.finished", "at": "…", "data": { … } }`.
  The type starts with the extension's name.
- Bifröst passes an event to each extension that listed it in its manifest, and to client apps
  over a WebSocket, filtered by what the person may see.
- **No replay is promised.** After a restart an extension asks the other one for its current state.
  Saga must still work by watching its folders when no event arrives.
- A client opens the WebSocket with a single-use, 30-second ticket [from Sleipnir].

### 8.8 Sleipnir alone and under Bifröst

| | Alone | Under Bifröst |
|---|---|---|
| Started by | The person (`sleipnir`) | Bifröst |
| Listens on | Loopback, with a local token | Bifröst's private socket |
| Who is calling | The person at the machine | Headers from Bifröst |
| Owner of a download | None | A Bifröst person id |
| Other devices | Not possible | Through Bifröst |

Sleipnir decides at start-up which mode it is in, from how it was started. It keeps one engine and
one API. Only the front door differs.

### 8.9 Folders [owner: S11]

Bifröst is the one place that knows the home's folders and who may see each.

**Model**
- **Folder:** id, name, path, owner (a person, or the home), and which extensions may write to it.
- **Each person has one folder,** created with the person, with two subfolders [owner]:

  ```
  <home root>/<person>/private/    seen by that person, and the admin
  <home root>/<person>/public/     seen by everyone in the home
  ```
  The home root is a setting (below).
- **Visibility comes from the subfolder, never from a record about the file** [my call]. A file is
  private because of where it is. Nothing has to stay in step with the disk.
- The person picks private or public for each download. With no choice given, it is private [my call].
- **There is no file manager for members yet** [owner]. So the choice is made at download time, and
  only the admin, on the host, can move a file between the two. A later Files extension, or
  Ratatoskr on the host, could do this.
- The admin can add a folder that already holds media, owned by the home and public. Sleipnir does
  not write to it.

**The home root [owner]**

The admin chooses the drive and path where files are kept, in Bifröst's settings, on the host. A
dedicated storage disk is the expected case.

- Every person's folder lives under the root. Bifröst stores paths relative to it, so the root can
  change without changing any folder id.
- **Checks when the root is set** [my call]. Bifröst tests what it can actually do there: create,
  write, rename and delete a file as its service account. It does not judge by the file system's
  name. It refuses the path, with the exact reason and the fix, when a test fails or the root is
  mounted read-only.
- **NTFS is supported** [owner: the storage disk is NTFS]. Private and public are enforced by
  Bifröst and its extensions, not by file permissions, so they work on any file system. What NTFS
  lacks is permissions per file, so access for the whole disk is set once, when it is mounted:
  - Mount it with the service account as owner, the admin's group as group, and no access for
    others. Bifröst prints the line to add to `/etc/fstab`.
  - If the disk is also used from Windows, turn off Windows' fast start-up. A hibernated disk
    mounts read-only on Linux, and Bifröst then reports the root as unavailable.
  - File names follow Windows' rules under an NTFS root (no `< > : " | ? *`, no trailing dot or
    space), so the disk stays readable from Windows.
  - To test before relying on it: reserving space for a large download, and recovery after a
    power cut. Linux cannot fully repair NTFS. A damaged disk needs Windows' `chkdsk`.
- **The root is a folder on the disk, not the whole disk** [my call]. A storage disk usually holds
  other things. On NTFS the mount gives the service account the whole disk, so Bifröst's service
  unit hides everything outside the root from Bifröst and its extensions (systemd's file-system
  sandboxing: the root is bound read-write, the rest of the mount is not visible).
- **Start-up order.** The service unit names the root's mount as required
  (`RequiresMountsFor=`), so Bifröst starts after a disk that is mounted at boot. A disk that
  fails to mount leaves the root unavailable, as below.
- **A disk that is not mounted** [my call]. Bifröst writes a small marker file in the root. At
  start-up, and before each download, it checks for the marker. Without it, the root is
  "unavailable": downloads wait, and Saga shows the library as offline. Bifröst never writes into
  an empty mount point, which would fill the system disk.
- **Partial files sit on the same disk** as their final place, so finishing a download is a rename,
  not a copy.
- **Bifröst's own data stays on the system disk** [my call]: its database, the extensions and their
  databases. Only people's files go under the root.
- **Free space** is checked before a download of known size starts.
- **Changing the root later** [my call]: allowed only when nothing is downloading. Bifröst does not
  move files. The admin moves them, then points Bifröst at the new place, and Saga rescans.

**Where a download goes**
1. A client asks Sleipnir for a download and says private or public.
2. Sleipnir asks Bifröst to resolve it. Bifröst takes the person from the request, never from the
   client, resolves the path through symbolic links, and returns the final path.
3. Sleipnir writes only to a path it received this way. It never takes a path from a client.

**After a download**
- Sleipnir publishes `sleipnir.download.finished` with the folder id, the subfolder, and the
  file's path inside it.
- Saga subscribes, and scans that subfolder only.
- Bifröst routes the event. It does not know that the receiver is Saga, or what a scan is.

**Who sees what**
- An extension asks Bifröst which paths the calling person may see: their own folder, every
  person's `public` subfolder, and the home's folders. It shows nothing from anywhere else. Saga
  applies this to its library, and Sleipnir to its download list.
- **The admin sees everything** [owner]. The admin runs the home machine and can read the disk, so
  the apps do not pretend otherwise.
- Private means hidden from other members in the apps. It is not encryption. All files belong to
  Bifröst's service account, and the admin's own account reads them through a group. Hiding files from the admin would need Bifröst
  to encrypt them with a key only that person holds, which would also stop Saga from transcoding
  them on the home machine. Not planned.

**Path safety,** written once here: canonicalize, resolve symbolic links, then check that the final
path lies inside the folder. Refuse any `..`. Never compare path strings by prefix.

**Sleipnir alone** has no folders and no people. It downloads into one folder, as today.

## 9. The online link

### 9.1 Linking [owner: S7]

1. On the host, the admin chooses "Link this home".
2. Bifröst opens the system browser at Heimdall with the authorization code flow and PKCE. The
   redirect goes to `http://127.0.0.1:<port>/…`. Heimdall's redirect rules already allow loopback HTTP.
3. Bifröst stores the refresh token in its data folder (`0600`) and records the admin's
   `issuer` + `subject` on the admin person.
4. The Heimdall address is a setting. A home can stay unlinked, or point at another Heimdall.

Bifröst is a public client: it holds no client secret, and every home uses the same client id [my
call]. A member links their own account the same way, from the client app, so that notices can
reach them. This is optional.

### 9.2 Notices

The only thing a home sends online is a **notice**, and the only thing it reads back is the
**answer** to one.

- Bifröst gets an access token for Huginn's audience (Heimdall's `resource` parameter).
- It posts a notice: recipient (a linked person's `subject`), title, text, and optional actions
  such as Approve and Reject. Bifröst chooses the notice id.
- It keeps one outbound connection to Huginn, or polls, and reads answers to its own notice ids.

**Rules**
- Bifröst accepts an answer only for a notice it sent, that is still pending, from the `subject`
  it was sent to. Anything else is dropped and audited.
- An answer carries a choice, never a command. What the choice means is decided at home.
- A notice holds no secrets and no file paths. Keep the text short: Huginn stores it.
- With no internet, notices queue and expire. The same question stays answerable on the LAN.
- Unlinking revokes the token and drops pending notices.

### 9.3 What the online group must provide

| App | Needs |
|---|---|
| Heimdall | A registered public client for Bifröst with a loopback redirect, and Huginn as an allowed audience. For other households: sign-up, email verification, password reset (S3) |
| Huginn | A notices API (send, read answers) that accepts a Heimdall access token, and a place in the app to show notices and their actions. Not chat messages (S9) |

## 10. The client app

- One app. It discovers the home, pairs, pins the key, and shows each installed extension's web
  screens inside itself [owner: S8].
- **.NET for Android, as a thin shell, without MAUI** [owner: S13]. It shares the pairing code and
  API types with Bifröst. Later it can run Sleipnir's engine on the phone.
- Android phones and Android TV first [owner].
- Extension screens are Angular, as in Heimdall's ADR 001. The same screens serve the host browser
  and the app.
- Keys and the device token go in the Android Keystore [from Sleipnir].
- On a TV, pairing uses path B, and every screen must work with a remote.
- A native video player for Saga can come later. If it borrows Jellyfin code it must be a separate
  GPL-2.0 component, not part of this app. Check the state of the .NET bindings for Android's media
  library before planning it.

**To prove before the design depends on it:** that the Android web view enforces the pinned key for
every request a screen makes, including video segments and WebSockets.

## 11. Security

Each row needs a test.

| Threat | Mitigation |
|---|---|
| Another account on the host acts as the admin | The admin socket accepts one operating-system account. Host-only actions need the system password prompt |
| A device on the Wi-Fi uses the home | Every route except server info and pairing needs a device token. LAN listener off until set up |
| An impostor home | Pinned fingerprint. The QR carries it. The 6-digit code binds it for discovered homes |
| A website drives the loopback API | Header tokens only, `Host` allowlist, no CORS |
| A lost or stolen device | Revocation, immediate. Last seen is shown to the admin |
| A tampered or fake extension release | Signature checked against a built-in key. Fixed repository list |
| A bad update | The admin confirms. The previous version is kept |
| An extension impersonates Bifröst to another | Private sockets with `0600`. Extensions never call each other directly |
| A client forges identity headers | Bifröst strips and rewrites them |
| A member reads another member's private files | Visibility comes from the folder. Every extension asks Bifröst which folders the caller may see |
| The storage disk is missing and files land on the system disk | Marker file in the home root. Without it, nothing is written |
| A download escapes its folder (`../`, symbolic links) | Sleipnir writes only to a path Bifröst resolved. One path-safety rule (§8.9) |
| A compromised online app | It can only answer a pending notice. Answers are choices. No inbound connection exists |
| A stolen online token | Stored `0600`. Refresh-token reuse revokes the family in Heimdall. Unlink revokes it |
| The browser fallback is read on the wire | Off by default. View-only. The pairing screen says it is not encrypted |
| Home key leak | Rotate, then pair again |
| Secrets in logs | Tokens, invitation secrets and tickets are never logged |

An audit log records pairing, approvals, revocations, role changes, extension installs and updates,
linking, and refused answers.

## 12. Order of work

Each step ends with working, tested software.

1. **Extract.** Move people, devices, tokens and the request pipeline from `Sleipnir.Server` into a
   new Bifröst project. Sleipnir alone keeps working throughout.
2. **Host an extension.** Bifröst runs as a system service, starts Sleipnir on a private socket and
   routes to it with identity headers. The loopback listener only. The desk companion and "ask the
   admin" on the host screen come here. **Built** (2026-11-16), and installed on a real machine. An
   extension under the service and a click on a real notification are still to be checked by hand.
3. **LAN.** Home key, HTTPS listener, pinning, pairing paths A and B, mDNS. **Mostly built**
   (2026-11-17), tested over loopback only: home key and HTTPS listener (off until `lan.json`),
   pairing A (invitation link) and B (compared 8-digit code, commit then reveal), the view-only browser
   fallback (plain HTTP, own port, off), `bifrost identity rotate` behind the polkit prompt, and mDNS
   announcement through Avahi (checked on a real network). **Left:** browsing from a client (Android has
   its own); the QR image (a screen draws it); the audit log for path B; a real device on a real
   network; the pinning client, which is step 4.
4. **Client app, first cut.** Pair, pin, show Sleipnir's screens. Run the web view pinning test here.
   **Started** (2026-11-17): the shared .NET client library `Bifrost.Client` (parse the invitation link,
   pairing by link and by compared code, an HTTP client that pins the key, `BifrostClient` for who-am-I, the extension list and an extension's address), tested against a real host
   over loopback. **Left:** the Android app, and the pinning test on each web view engine; both need an
   Android SDK and real devices.
5. **Install and update.** Signed releases, rollback, install from file.
6. **Folders and events.** Per-person folders, visibility, publish and subscribe. Saga, when it
   exists, is the second extension and the test of this interface.
7. **Online link.** Link the home, send a "download finished" notice. Then notices with answers.

Steps 1 to 3 are mostly Sleipnir's existing Phase 2 plan with a different home for the code.

## 13. Left to the builder

- Ports, the manifest format, the signature scheme, and the mDNS library.
- The construction of the 6-digit code, within §7.2's requirement.
- SQL schema and migrations.
- How Sleipnir detects which mode it is in.
- Polling or a held connection for answers.

## 14. Open questions

1. **Answered (2026-11-14).** The split in §5, with one "ask the admin" facility (S12, §5.1).
2. **Answered (2026-11-14).** Bifröst owns the folders, each person has one folder with `private`
   and `public` subfolders, and the admin sees everything (S11, §8.9).
3. **Answered (2026-11-14).** A system service under its own account (S12, §3).
4. **Answered (2026-11-14).** The command becomes `sleipnir`, with no transition period. The data
   folder is renamed with it. `dnm://` and `_dnm._tcp` go away: Bifröst owns pairing and discovery.
5. **Answered (2026-11-14).** .NET for Android, a thin shell first, the engine later (S13, §10).
6. **Answered (2026-11-14).** Bragi is left alone for now. The link formats match, so pairing Bragi
   through Bifröst can be proposed to its owner later.
7. **Answered (2026-11-14).** Offline, signed by hand by the owner, with a backup key (S14, §8.3).

## 15. Calls made by the designer

Extension list fixed to the owner's repositories · MIT suggested · LAN listener off until first-run
setup · the home root is tested by doing, not by file system name · the root is a folder, with the rest of the
disk hidden from the service · Windows file-name rules on NTFS · a marker file
to detect an unmounted disk · Bifröst's own data on the system disk · Bifröst does not move files when the root changes · the admin socket checked by peer credentials ·
the operating system's password prompt for host-only actions · `bifrost://pair` with Bragi's parameter names ·
private sockets, not loopback ports, for extensions · visibility from the subfolder, not a per-file
record · private when no choice is given · a second socket for the extension API · updates
confirmed by the admin · no event replay · one public client id for all homes · members may link their
own account · built by extraction from Sleipnir.
