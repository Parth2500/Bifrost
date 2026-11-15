# Changelog

## Unreleased

### Added: people, devices and the loopback API (step 1)

- **`bifrost daemon`** runs the home as one instance per machine. It listens on `127.0.0.1` and
  `::1`, port 7780 or the next free one, recorded in `daemon.json`. It stops cleanly on SIGTERM,
  SIGINT, SIGHUP or `POST /api/v1/server/shutdown` (admin only).
- **People and devices** live in a private SQLite file, `bifrost.db`. The first start creates an
  admin, named after the operating-system account, and the "This computer" device, whose token is
  `admin.token`. Each device has its own token. Only its hash is stored. Revoking works on the next
  request.
- **Roles:** `admin` and `member`. Admins manage people (`/people`). Everyone sees and revokes their
  own devices (`/devices`) and sees who they are (`/me`). The last admin cannot be demoted or deleted.
- **Audit log:** every change to a person or a device, with who made it.
- **Folders:** under systemd, the `STATE_DIRECTORY`, `RUNTIME_DIRECTORY` and
  `CONFIGURATION_DIRECTORY` that the unit sets. Otherwise per-user XDG folders.
- **API docs:** OpenAPI at `/openapi/v1.json`, browsable at `/scalar`.
