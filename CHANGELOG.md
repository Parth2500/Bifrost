# Changelog

## Unreleased

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
