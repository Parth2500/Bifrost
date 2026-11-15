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
curl -H "Authorization: Bearer $(cat ~/.local/state/bifrost/admin.token)" http://127.0.0.1:7780/api/v1/me
```

Without systemd's variables the daemon uses `~/.config/bifrost`, `~/.local/share/bifrost` and
`$XDG_RUNTIME_DIR/bifrost` (or `~/.local/state/bifrost`). The API is described at
`http://127.0.0.1:7780/scalar`.

## Database changes

```sh
dotnet tool restore
dotnet ef migrations add <Name> -p src/Bifrost.Server -o Persistence/Migrations
```

## Layout

| Project | Holds |
|---|---|
| `Bifrost.Core` | API types and enums. Framework references only, so the client app can share it |
| `Bifrost.Server` | People, the store, security, and the host |
| `Bifrost.App` | The `bifrost` command |
| `Bifrost.Tests` | xUnit, `FakeTimeProvider`, CsCheck |
