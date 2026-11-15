# System Service Implementation Plan (Bifröst step 2c)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bifröst runs as a sandboxed systemd system service under its own account, and the host
admin reaches it through an admin socket that the operating system vouches for, not a token file.

**Architecture:** A Unix socket in a second runtime folder that other accounts may enter. Each
connection's peer credentials (`SO_PEERCRED`) say which account is calling. Only the recorded admin
account is accepted, and it acts as the "This computer" device. `bifrost service install`, run once
with `sudo`, creates the account, copies the binary to `/opt/bifrost`, records the admin and writes the unit.

**Tech Stack:** .NET 10, Kestrel `ListenUnixSocket`, `Socket.GetRawSocketOption`, systemd.

**Spec:** `Bifrost/docs/design.md` §3 (system service, SELinux), §5 (the host, admin model), §11
(row "another account on the host acts as the admin").

## Global Constraints

- Everything in the step 1 plan's Global Constraints still holds.
- Account and group: `bifrost`, a system account with no login shell and no home (`useradd --system
  --user-group --no-create-home --shell /usr/sbin/nologin bifrost`).
- Binary: `/opt/bifrost/bifrost` (copied from the published single file), owned by root, `0755`.
- Unit: `/etc/systemd/system/bifrost.service`. Runtime folders `bifrost` (`0700`, Bifröst's own) and
  `bifrost-admin` (`0755`, holds `admin.sock`). The admin socket is `0666`; the peer check is the gate.
- Admin record: `<Settings>/admin.json` = `{ "uid": 1000, "name": "parth" }`, written by the installer
  from `SUDO_UID`/`SUDO_USER`, or from `--admin <user>`. Root is refused as the admin.
- Without `admin.json` (a developer running `bifrost daemon` by hand), the admin is the account the
  daemon runs as.
- The `admin.token` file is removed. The "This computer" device gets a fresh random token at each start,
  kept only in memory, never written [my call].
- The host's polkit prompt for host-only actions is **not** built here: no host-only action is exposed
  through the API in step 2 (extensions are set in `extensions.json`, which needs root). It comes with
  the first one [my call, recorded in ADR 002].
- The installer never runs anything it was not asked to: it prints each command before running it.

## Review Focus

1. **Another account on the host connects to `admin.sock`**: `403 not_the_admin`, the attempt is
   logged with the uid, and nothing else changes. Test in Task 1.
2. **The admin account uses the loopback TCP port instead, with no token**: still 401. The admin socket
   is the only token-free way in. Test in Task 1.
3. **`bifrost service install` run without `sudo`, run from `dotnet bifrost.dll`, or run twice**:
   refused with one clear line, or (twice) the same result as once, with the existing data kept.
   Test in Task 2.
4. **`bifrost service uninstall`**: stops and removes the unit, keeps `/var/lib/bifrost` and the
   account unless `--purge`, and says so. Test in Task 2.
5. **An extension program under `/home`**: with `ProtectHome=yes` it cannot start; Bifröst's log says
   why at start-up, not a bare exit code (plan 2b settings already refuse it). Checked in Task 3.

---

### Task 1: The admin socket

**Files:**
- Create: `src/Bifrost.Server/Security/PeerCredentials.cs`, `src/Bifrost.Server/Hosting/AdminSettings.cs`
- Modify: `src/Bifrost.Server/Hosting/AppPaths.cs` (add `AdminRuntime`), `Hosting/BifrostHost.cs`,
  `src/Bifrost.App/DaemonCommand.cs`, `src/Bifrost.Server/People/PeopleService.cs` (`EnsureLocalAdminAsync`
  takes the admin's name; the token is made inside)
- Delete: `src/Bifrost.Server/Security/LocalAdminToken.cs` and its tests (keep `SecretFile`)
- Test: `tests/Bifrost.Tests/Server/AdminSocketTests.cs`, `AppPathsTests.cs`, `DaemonProcessTests.cs`
  (talk to the daemon through the admin socket)

**Interfaces:**
- Produces:
  - `AppPaths(string Settings, string Data, string Runtime, string AdminRuntime)`: `RUNTIME_DIRECTORY`'s
    second `:`-separated entry, else `<parent of Runtime>/bifrost-admin`.
  - `static class PeerCredentials { static (int Pid, uint Uid, uint Gid)? Of(Socket socket); }` using
    `GetRawSocketOption(1 /*SOL_SOCKET*/, 17 /*SO_PEERCRED*/, buffer[12])`.
  - `record AdminSettings(uint Uid, string Name)` with `static AdminSettings Load(string settingsDir)` —
    falls back to the current process's uid and `Environment.UserName`.
  - `BifrostHost` gains a listener on `<AdminRuntime>/admin.sock`. For requests on that listener, the
    authentication middleware reads the peer uid from `IConnectionSocketFeature.Socket`; the admin uid →
    the local principal; any other → `403 not_the_admin`. Bearer tokens are ignored there.
  - `PeopleService.EnsureLocalAdminAsync(string adminName) -> Principal` (the token is internal now).
  - `HostFixture.AdminHttp`: an `HttpClient` over the admin socket.

- [ ] **Step 1: Write the failing tests:**
  - `The_admin_account_on_the_admin_socket_is_the_host_admin`: `GET /api/v1/me` over `AdminHttp` →
    `This computer`, `admin`.
  - `Another_account_is_refused` (Review Focus 1): a host whose `AdminSettings.Uid` is `uid+1` → 403
    `not_the_admin`, and a log line naming the caller's uid.
  - `A_bearer_token_on_the_admin_socket_changes_nothing`: a member's token over the admin socket is
    still the host admin (the peer decides).
  - `The_loopback_port_still_needs_a_token` (Review Focus 2).
  - `The_admin_socket_is_0666_in_a_0755_folder_and_the_runtime_folder_stays_0700`.
  - `AppPathsTests`: two-entry `RUNTIME_DIRECTORY` → `AdminRuntime` is the second; one entry → sibling `bifrost-admin`.
  - `DaemonProcessTests`: rewrite `Starts_answers_and_stops_on_SIGTERM` and the token-file tests to use the
    admin socket; delete the token-file cases; add `No_admin_token_file_is_written`.
- [ ] **Step 2: Run** `dotnet test`. Expected: build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(security)!: the host admin is the OS account on the admin socket, not a token file`

### Task 2: `bifrost service install | uninstall | status`

**Files:**
- Create: `src/Bifrost.App/Service/ServiceCommand.cs`, `Service/SystemUnit.cs`, `Service/ICommandRunner.cs`
  (as Sleipnir's: `Task<(int Exit, string Output)> RunAsync(string file, params string[] args)`)
- Modify: `src/Bifrost.App/Program.cs`
- Test: `tests/Bifrost.Tests/ServiceCommandTests.cs`

**Interfaces:**
- Produces:
  - `SystemUnit.Render(string binary) -> string`. The unit, exactly these lines in `[Service]`:
    `Type=simple`, `User=bifrost`, `Group=bifrost`, `ExecStart=/opt/bifrost/bifrost daemon`,
    `Restart=on-failure`, `RestartSec=5`, `StateDirectory=bifrost`, `StateDirectoryMode=0700`,
    `ConfigurationDirectory=bifrost`, `RuntimeDirectory=bifrost bifrost-admin`, `RuntimeDirectoryMode=0755`,
    `UMask=0077`, `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`,
    `PrivateDevices=yes`, `ProtectKernelTunables=yes`, `ProtectKernelModules=yes`, `ProtectControlGroups=yes`,
    `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6`, `RestrictNamespaces=yes`, `LockPersonality=yes`,
    `SystemCallArchitectures=native`, `TimeoutStopSec=20`; `[Unit]` `After=network.target`;
    `[Install]` `WantedBy=multi-user.target`. (Bifröst sets its own runtime folder to `0700` at start.)
  - `ServiceCommand(ICommandRunner runner, string processPath, Func<string,string?> env, bool isRoot, TextWriter output, IFileSystem fs)`
    with `RunAsync(string[] args) -> int`. `install [--admin <user>]`: refuses unless root (exit 1,
    "Run it with sudo: sudo bifrost service install"), refuses when started through `dotnet` (exit 1),
    refuses root as admin. Then, printing each: `id -u bifrost` (useradd only if missing), copy the
    binary, write `admin.json` into `/etc/bifrost/` (owner `bifrost`, `0600`), write the unit,
    `systemctl daemon-reload`, `systemctl enable --now bifrost`. `uninstall [--purge]`: `systemctl
    disable --now bifrost`, remove the unit, `daemon-reload`; with `--purge` also `/opt/bifrost`,
    `/var/lib/bifrost`, `/etc/bifrost` and `userdel bifrost`; without it, says what was kept.
    `status`: `systemctl status bifrost --no-pager`.
  - `IFileSystem` is a small seam (write file with mode and owner, copy, delete) so tests touch no real `/etc`.

- [ ] **Step 1: Write the failing tests** (fake runner and file system, as Sleipnir's `ServiceCommandTests`):
  - `The_unit_is_sandboxed`: `Render` contains each line listed above.
  - `Install_refuses_without_root` and `Install_refuses_from_the_dotnet_host` (Review Focus 3).
  - `Install_creates_the_account_only_when_missing`.
  - `Install_records_the_sudo_user_as_admin` and `Install_takes_admin_from_the_option`; `Root_cannot_be_the_admin`.
  - `Install_twice_gives_the_same_files_and_commands` (Review Focus 3).
  - `Uninstall_keeps_data_and_says_so` and `Uninstall_purge_removes_everything` (Review Focus 4).
- [ ] **Step 2: Run** `dotnet test`. Expected: build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(app): bifrost service install, uninstall and status`

### Task 3: A real install, and the docs

- [ ] **Step 1:** Publish (`dotnet publish src/Bifrost.App -c Release -r linux-x64 --self-contained
  -p:PublishSingleFile=true -o <scratch>/bifrost`). Ask the owner to run
  `! sudo <scratch>/bifrost/bifrost service install` (it needs root; Claude does not run sudo). Then,
  without root: `systemctl status bifrost`, `curl --unix-socket /run/bifrost-admin/admin.sock
  http://localhost/api/v1/me` → the owner as admin; `ls -l /run/bifrost` → `0700 bifrost`.
  Check `journalctl -u bifrost` for SELinux denials; if any, record them in ADR 002 with the fix.
  Note the results in the ledger.
- [ ] **Step 2:** With Sleipnir published to `/opt/sleipnir/` and named in `/etc/bifrost/extensions.json`
  (the owner copies it with sudo), restart and check `/x/sleipnir/api/v1/server` over the admin socket
  → `mode: "extension"`. A program under `/home` instead → one clear log line (Review Focus 5).
- [ ] **Step 3: Docs:** ADR 002 (system service: the account, the sandbox, the admin socket in place of a
  token, polkit deferred), threat model rows for B-new (other account → `not_the_admin`; sandbox),
  DEVELOPMENT (install, uninstall, how to reach the admin socket with curl), README, CHANGELOG.
- [ ] **Step 4: Run** `dotnet build -c Release && dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `docs: the system service and the admin socket`
