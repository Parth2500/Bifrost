# ADR 003: The host screen, signed in by a one-time link

- Date: 2026-11-18
- Status: Accepted. Built and tested; not yet run under the installed system service.
- Affects: design §5, §6.4, §11; the host admin model of decision S12

## Context

Design §5 says the admin works on the host, in a browser on `localhost`, but left open how that browser
proves it is the admin. Only the admin socket knows (the kernel names the calling account), and a
browser cannot use a Unix socket. The loopback port wants a device token, and a token pasted into a
browser is a secret every program of the admin's can read.

## Decision

**One-time link.** `bifrost open` asks for a link on the admin socket (`POST /api/v1/host/login-link`,
admin socket only: the same call with a token is `403 host_only`). The link,
`http://127.0.0.1:<port>/host/login?t=<secret>`, works once and for two minutes, and at most five are
open at once. The secret is never a program argument (every account can read those): the command writes a launcher file only this account can read into `$XDG_RUNTIME_DIR`, gives `xdg-open` that file's path, and removes it a few seconds later. With no private folder it opens nothing. `--print` prints the link for the admin to use by hand.

**Session cookie, loopback only.** Opening the link trades the secret for a session: a `bifrost_host`
cookie that is `HttpOnly`, `SameSite=Strict`, path `/`, and has no expiry (the browser drops it when it
closes). The server keeps only a SHA-256 of each secret, in memory, so a restart ends every link and
session. A session ends after one idle hour or eight hours in all, or when the screen is closed.
Wrong secrets are throttled like other wrong guesses (20 a minute, then more wrong ones get 429), but a good link always works, so no local program can lock the admin out by spamming guesses.

**Where the cookie counts.** Only on a request that is loopback at both ends, not the plain-HTTP
fallback, and whose `Host` is `localhost`, `127.0.0.1` or `[::1]`. Then the session is the admin's own
"This computer" device on `/api/*` only. It is **not** honoured on `/x/*`: extension pages share this origin, so a script
in one of them must never get the admin's session (they stay behind device tokens until they are served from an origin of
their own). Anywhere else the cookie is ignored. A request that
changes something must also carry an `Origin` equal to the page's own address; a missing or foreign
one is `403 cross_site`.

**This changes design §6.4**, "no cookies on the pinned listeners": the loopback listener is not pinned,
and this one cookie is the exception. The LAN and fallback listeners still never read a cookie.

**The screen** is `/host/` and three files (`app.css`, `app.js`, `mark.png`) served with
`script-src 'self'` and no inline script, `frame-ancestors 'none'` and `no-store`. It pairs devices
(choose the person first; the code is a QR drawn by the server), lists people and devices, answers
questions, starts, stops and restarts extensions, and shows the home and its key.

**Still behind the system password.** Rotating the home key, naming the home and turning the home
network on stay in a terminal (`bifrost identity rotate`, `lan.json`), because the polkit prompt is
checked by the command, not by the service. The screen says so instead of offering a button that cannot
ask. Installing an extension from a release (design §8.3) is not built, so the screen does not offer it.

## Consequences

- Any program running as the admin's account can run `bifrost open` and get a session. That is the same
  trust as the admin socket itself, which the account already holds.
- A page on another website cannot use the session (SameSite, Origin, Host checks).
- The link is in the terminal's output only with `--print`, and in the launcher file and the browser's address bar until the redirect, which happens at once.
- Start, stop and restart of an extension are host-only (`403 host_only` for a remote admin).
