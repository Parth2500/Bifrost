# ADR 002: A system service, and an admin socket in place of the token file

- Date: 2026-11-16
- Status: Accepted. The real install (plan 2c, Task 3) has not been run yet; its findings go here.
- Affects: design §3, §5, §11; steps 2b and 2c

## Context

Step 1 let the host admin in with `admin.token`, a secret file in the runtime folder. That works
only while Bifröst runs as the admin's own account. As a system service it runs as `bifrost`, and
the admin can read nothing in its folders. A secret file the admin could read would also be a
secret every program of theirs can read and copy.

## Decision

**The account and the sandbox.** `bifrost service install` creates a system account `bifrost` with
no shell and no home, copies the program to `/opt/bifrost`, and writes a unit with
`ProtectSystem=strict`, `ProtectHome=yes`, `NoNewPrivileges=yes`, private `/tmp` and devices, and
only the Unix and IP address families. It prints each command before it runs it.

**The admin socket.** Bifröst listens on `/run/bifrost-admin/admin.sock`, mode `0666` in a `0755`
folder, so any account can connect. The kernel says which account is on the other end
(`SO_PEERCRED`). Only the account recorded in `/etc/bifrost/admin.json` is accepted, and it acts as
the "This computer" device. Everyone else gets `403 not_the_admin` on every route, and the attempt
is logged with their uid. A bearer token on this socket is ignored.

**No token file.** `admin.token` is gone, and one left by step 1 is deleted at start. The "This
computer" device still has a token, because a device must have one, but it is random at each start
and kept nowhere.

**Who the admin is.** The installer records whoever ran `sudo`, or `--admin <account>`. Root is
refused: root can already do anything, and the admin should be a person. Without `admin.json`, as
when a developer runs `bifrost daemon` by hand, the admin is the account the daemon runs as. A
broken `admin.json` stops the start: guessing an admin is worse than having none.

**Is this the service?** Bifröst treats itself as the service when `SYSTEMD_EXEC_PID` is its own
process id. `INVOCATION_ID` is not enough: a desktop session passes its own to every terminal. So
the unit must start `bifrost` directly, not through a wrapper script.

**Deferred: the polkit prompt.** Design §5 gives host-only actions a system password prompt. Step 2
exposes no host-only action through the API: extensions are set in `extensions.json`, which needs
root to edit. The prompt comes with the first such action.

## Consequences

- Any program running as the admin account is the admin. That was already true with a token file
  the admin could read. The polkit prompt is what will raise the bar for host-only actions.
- The loopback port still needs a device token, so the admin account has no token-free way in over TCP.
- The installer copies the program and the `*.so` files beside it (SQLite's native library is not
  inside a single-file publish). Publishing in another shape needs the installer to change.
- After installing a newer version the service must be restarted by hand; the installer says so.
- Not yet checked on a real machine: SELinux on Fedora may refuse to start a program from `/opt`
  until it is labelled. Task 3 checks `journalctl -u bifrost` and records the fix here.
