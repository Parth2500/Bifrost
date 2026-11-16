# Development

## Requirements

- .NET SDK 10.0.402 (pinned in `global.json`).
- Linux. Bifröst is Linux first, and the tests check Unix file modes.

## Build and test

```sh
dotnet build -c Release   # warnings are errors
dotnet test
```

The process tests (`DaemonProcessTests`) run the real `bifrost` binary. They set
`STATE_DIRECTORY`, `RUNTIME_DIRECTORY` and `CONFIGURATION_DIRECTORY` to a temporary folder, the way
systemd sets them for the service, so they never touch your own folders.

## Run it

```sh
dotnet run --project src/Bifrost.App -- daemon
curl --unix-socket "$XDG_RUNTIME_DIR/bifrost-admin/admin.sock" http://localhost/api/v1/me
```

The second line is the host admin's way in: the admin socket. It needs no token, because the
operating system tells Bifröst which account is calling, and run by hand the admin is you. The
loopback port (7780) needs a device token, and devices come with pairing (step 3).

Without systemd's variables the daemon uses `~/.config/bifrost`, `~/.local/share/bifrost` and
`$XDG_RUNTIME_DIR/bifrost` (or `~/.local/state/bifrost`), with the admin socket in `bifrost-admin`
beside the runtime folder. The API is described at `http://127.0.0.1:7780/scalar`.

## Install it as the system service

```sh
dotnet publish src/Bifrost.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
sudo out/bifrost service install        # the admin is whoever ran sudo; or: --admin <account>
curl --unix-socket /run/bifrost-admin/admin.sock http://localhost/api/v1/me
bifrost service status
sudo /opt/bifrost/bifrost service uninstall            # keeps the data
sudo /opt/bifrost/bifrost service uninstall --purge    # removes data, settings and the account
```

The installer prints each command before running it. It creates the `bifrost` account, copies the
program to `/opt/bifrost`, writes `/etc/bifrost/admin.json` and the unit, and starts it. After
installing a newer version, run `sudo systemctl restart bifrost`. The reasons are in
[ADR 002](decisions/002-system-service-and-admin-socket.md).

## Asking the admin

An extension asks on its API socket (`POST /v1/questions`), waits with `GET /v1/questions/<id>?wait=30`
and can take the question back with `DELETE`. Admins see open questions at `GET /api/v1/questions`
(long-poll with `?after=<seq>&wait=30`) and answer with `POST /api/v1/questions/<id>/answer`, on the
loopback API or the admin socket. Questions live in memory and are gone after a restart.

```sh
bifrost desk            # show questions as desktop notifications, until Ctrl+C
bifrost desk install    # run it in your desktop session from now on (no sudo)
```

The desk companion needs `notify-send` (libnotify 0.8 or later) and `gdbus`. It looks for the
service's socket, `/run/bifrost-admin/admin.sock`, then for a daemon you run by hand. Set
`BIFROST_ADMIN_SOCKET` to point it somewhere else. To try it without an extension:

```sh
S="$XDG_RUNTIME_DIR/bifrost-admin/admin.sock"
curl --unix-socket "$S" http://localhost/api/v1/questions
curl --unix-socket "$S" -H 'Content-Type: application/json' -d '{"choice":"approve"}' \
     http://localhost/api/v1/questions/<id>/answer
```

## Extensions

Until extensions install from releases (step 5), `extensions.json` in the settings folder says which
program each one runs:

```json
{ "sleipnir": { "program": "/opt/sleipnir/sleipnir" } }
```

The path must be absolute. The name must be one of `sleipnir`, `saga`, `files`, `norns`. Under the
service a program under `/home` is refused, because the unit hides `/home`; a run by hand allows it.
Each extension gets `x/<name>/` in the runtime folder (`extension.sock`, where it listens, and
`bifrost.sock`, where it asks Bifröst) and `x/<name>/` in the data folder. Requests to
`/x/<name>/…` reach it; `GET /api/v1/extensions` shows its state.

The tests use `tests/Bifrost.FakeExtension`, a small program that follows the extension contract.
`FAKE_MODE` picks how it behaves: `echo`, `crash`, `hang` or `exit-after`. It is built next to the
tests and found by `FakeExtension.Program`.

## Database changes

```sh
dotnet tool restore
dotnet ef migrations add <Name> -p src/Bifrost.Server -o Persistence/Migrations
```

## Layout

| Project | Holds |
|---|---|
| `Bifrost.Core` | API types and enums. Framework references only, so the client app can share it |
| `Bifrost.Server` | People, the store, security, the host, the extension supervisor and proxy, and questions |
| `Bifrost.App` | The `bifrost` command: `daemon`, `service` and `desk` |
| `Bifrost.Tests` | xUnit, `FakeTimeProvider`, CsCheck |
| `Bifrost.FakeExtension` | The extension the tests run |
