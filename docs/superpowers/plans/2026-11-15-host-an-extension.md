# Host an Extension Implementation Plan (Bifröst step 2b)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bifröst starts each configured extension as a child process on a private socket, keeps it
running, and routes `/x/<name>/…` to it with the caller's identity attached.

**Architecture:** An `ExtensionSupervisor` per extension owns the child process and its restarts. A
proxy endpoint on the loopback listener authenticates the device as today, rewrites the headers and
forwards over the extension's Unix socket with YARP's `IHttpForwarder`. A second Unix socket per
extension serves the small extension API (§8.6); in this step it answers person lookups only.

**Tech Stack:** .NET 10, ASP.NET Core, `Yarp.ReverseProxy` 2.3.0 (forwarder only), xUnit, `FakeTimeProvider`.

**Spec:** `Bifrost/docs/design.md` §8.1, §8.4, §8.5, §8.6, §11. The extension contract (command,
environment variables, identity headers) is fixed in `Sleipnir/docs/superpowers/plans/2026-11-15-two-modes.md`.

## Global Constraints

- Everything in `docs/superpowers/plans/2026-11-15-extract.md` Global Constraints still holds.
- The extension list is built in [design §8.1]: `sleipnir`, `saga`, `files`, `norns`. Only names on it start.
- Until install from releases (step 5), each extension's program path comes from
  `<Settings>/extensions.json`: `{ "sleipnir": { "program": "/opt/sleipnir/sleipnir" } }` [my call].
  The path must be absolute. When Bifröst runs as the service (systemd sets `INVOCATION_ID`), a path
  under `/home` is refused too, because the unit hides `/home` (plan 2c); a developer's run allows it.
- Per extension, under Bifröst's runtime folder: `x/<name>/` (mode `0700`) holding `extension.sock`
  (the extension listens) and `bifrost.sock` (Bifröst listens, `0600`). Data folder: `<Data>/x/<name>/` (`0700`).
- The child runs under Bifröst's own account, with only the contract's variables plus `PATH`, `HOME`
  (its data folder) and `DOTNET_*`/`LANG` passed through. It inherits nothing else.
- Identity headers Bifröst sends: `X-Bifrost-Person-Id`, `X-Bifrost-Role`, `X-Bifrost-Device-Id`,
  `X-Bifrost-Device-Kind`. It removes every incoming `X-Bifrost-*` header, `Authorization`, `Cookie`,
  `Forwarded` and `X-Forwarded-*`, then adds its own and `X-Forwarded-Prefix: /x/<name>`.
- Restarts: delays 1, 2, 4, 8, 16, 32, 60 s (capped). Five crashes within 10 minutes → `failed`, no more
  restarts until Bifröst restarts or the admin starts it again. A run longer than 10 minutes resets the count.
- Stop: SIGTERM, then SIGKILL after 10 s.

## Review Focus

1. **A client sends its own `X-Bifrost-Role: admin`** (or mixed case `x-bifrost-role`): the extension
   sees only Bifröst's value. Test in Task 3.
2. **An extension that never opens its socket, or exits at once**: Bifröst keeps answering every other
   route, the extension shows `starting` then `crashed`/`failed`, and `/x/<name>/…` answers `503
   extension_unavailable` quickly, not after a long hang. Test in Task 2.
3. **Path tricks** like `/x/sleipnir/../people`, `/x/SLEIPNIR/…`, `/x/sleipnir%2f..%2fpeople`: never
   reach Bifröst's own routes with the extension's rights, never reach a different extension. Test in Task 3.
4. **A large or slow streamed response** (a 50 MB body): forwarded without buffering it all in memory,
   and a client that disconnects cancels the upstream request. Test in Task 3.
5. **A broken `extensions.json`** (bad JSON, a relative path, an unknown name): Bifröst starts, logs
   one line saying what is wrong, and runs the valid entries. Test in Task 2.

---

### Task 1: A fake extension for tests

**Files:**
- Create: `tests/Bifrost.FakeExtension/Bifrost.FakeExtension.csproj` (exe, `AssemblyName fake-extension`),
  `tests/Bifrost.FakeExtension/Program.cs`; add to `Bifrost.slnx`; reference it from `Bifrost.Tests`
  with `ReferenceOutputAssembly=false` so it builds beside the tests.

**Interfaces:**
- Produces: `fake-extension extension`, which follows the contract and reads `FAKE_MODE`:
  - `echo` (default): listens on `BIFROST_SOCKET` and answers any request with JSON
    `{ "method", "path", "query", "headers": { name: [values] }, "env": { BIFROST_*: value } }`;
    `GET /big?mb=N` streams N MiB of zeros; `GET /slow` writes one byte a second until cancelled and
    then writes `cancelled` to `<BIFROST_DATA_DIR>/slow.log`.
  - `crash`: exits 3 at once. `hang`: never opens the socket. `exit-after=<s>`: echo, then exits 0.
- It is a test tool. It shares no code with Bifröst (it only reads the contract).

- [ ] **Step 1:** Write it. **Step 2:** `dotnet build`. Expected: `fake-extension.dll` next to `Bifrost.Tests.dll`.
- [ ] **Step 3: Commit** `test: a fake extension that follows the contract`

### Task 2: Extension settings and the supervisor

**Files:**
- Create: `src/Bifrost.Server/Extensions/ExtensionCatalog.cs`, `ExtensionSettings.cs`,
  `ExtensionSupervisor.cs`, `ExtensionManager.cs`, `src/Bifrost.Core/Api/ExtensionDto.cs`,
  `src/Bifrost.Server/Api/ExtensionEndpoints.cs`
- Test: `tests/Bifrost.Tests/Extensions/ExtensionSettingsTests.cs`, `SupervisorTests.cs`

**Interfaces:**
- Produces:
  - `static class ExtensionCatalog { static IReadOnlySet<string> Names; }`
  - `ExtensionSettings.Load(string path, bool asService, ILogger log) -> IReadOnlyDictionary<string, string>` (name → program);
    a missing file is an empty map; each refused entry logs one warning naming the entry and the reason.
  - `enum ExtensionState { Stopped, Starting, Running, Crashed, Failed }`
  - `ExtensionSupervisor(string name, string program, string runtimeRoot, string dataRoot, TimeProvider time, ILogger log)`:
    `StartAsync()`, `StopAsync()`, `ExtensionState State`, `int? Pid`, `int Restarts`,
    `string SocketPath`, `string ApiSocketPath`, `string DataDir`.
    Running = the socket accepts a connection (polled every 100 ms, 20 s limit, then the child is
    killed and counted as a crash).
  - `ExtensionManager(IReadOnlyDictionary<string,string> programs, …)`: `StartAllAsync`, `StopAllAsync`,
    `ExtensionSupervisor? Get(string name)`, `IReadOnlyList<ExtensionSupervisor> All`.
  - `ExtensionDto(string Name, ExtensionState State, int? Pid, int Restarts)`; enum sent as `running` etc.
  - `GET /api/v1/extensions` (admin) → `ExtensionDto[]`.

- [ ] **Step 1: Write the failing tests:**
  - `ExtensionSettingsTests`, theory: missing file → empty; bad JSON → empty plus one warning
    (Review Focus 5); relative path, an unknown name, and (with `asService`) a path under `/home` → that entry
    dropped with a warning, valid entries kept; without `asService` the `/home` path is kept.
  - `SupervisorTests` (real child processes from Task 1):
    - `Starts_the_extension_with_exactly_the_contract_environment`: echo's `env` holds the five
      `BIFROST_*` values; no other variable of the test process (set `BIFROST_TEST_LEAK=1` on the test
      process; it must be absent from the child).
    - `The_folders_and_the_socket_are_owner_only`: `x/<name>` and data dir `0700`.
    - `A_crashing_extension_is_restarted_with_growing_delays_then_given_up` (Review Focus 2):
      `FAKE_MODE=crash`, drive the `FakeTimeProvider` through 1, 2, 4, 8 s → five starts, then `Failed`,
      no sixth start after advancing an hour.
    - `An_extension_that_never_opens_its_socket_is_killed_after_20_seconds_and_counted_as_a_crash`.
    - `A_long_run_resets_the_crash_count`.
    - `Stop_sends_SIGTERM_then_kills_after_10_seconds`: echo stops at SIGTERM within 1 s.
    - `A_stale_socket_file_does_not_count_as_running`: a plain file at the path before start → state
      reaches `Running` only after the child really listens.
- [ ] **Step 2: Run** `dotnet test`. Expected: build fails.
- [ ] **Step 3: Implement.** Restart delays are scheduled with `time.CreateTimer`, so tests advance
  them. The readiness poll uses real waits (it is short) but counts its 20 s against `time`.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(extensions): start, watch and restart extensions`

### Task 3: Routing `/x/<name>/…`

**Files:**
- Create: `src/Bifrost.Server/Extensions/ExtensionProxy.cs`
- Modify: `src/Bifrost.Server/Hosting/BifrostHost.cs` (map the route; `Create` takes an `ExtensionManager`),
  `Bifrost.Server.csproj` (YARP)
- Test: `tests/Bifrost.Tests/Extensions/ProxyTests.cs`, `HostFixture.cs` (optionally starts a manager
  with the fake extension)

**Interfaces:**
- Consumes: `ExtensionManager.Get`, `ExtensionSupervisor.SocketPath/State`, `Principal` (step 1).
- Produces: route `/x/{name}/{**rest}` for every method, behind the existing authentication.
  `name` must be on the catalogue, compared exactly (lower case). Unknown or not configured → `404
  extension_not_found`. Not `Running` → `503 extension_unavailable` at once. Upstream connect failure
  → `503 extension_unavailable`. The upstream path is `/` + `rest` as received (already decoded by
  routing is not acceptable: use the raw path after the prefix, so `%2f` stays encoded).

- [ ] **Step 1: Write the failing tests** (fixture with the fake extension running as `sleipnir`):
  - `Forwards_method_path_query_and_body`: `POST /x/sleipnir/api/v1/downloads?a=1` with a JSON body →
    echo shows `POST`, `/api/v1/downloads`, `?a=1`, the body length.
  - `Adds_the_callers_identity`: member bob's token → echo headers carry bob's person id, `member`,
    his device id, `app`, and `X-Forwarded-Prefix: /x/sleipnir`.
  - `Client_identity_headers_and_credentials_never_reach_the_extension` (Review Focus 1): the client sends
    `X-Bifrost-Role: admin`, `x-bifrost-person-id: <other>`, `Cookie`, `Forwarded`, `X-Forwarded-For` →
    echo sees only Bifröst's identity values, one of each, and no `Authorization`, `Cookie`, `Forwarded`,
    `X-Forwarded-For`.
  - `The_route_needs_a_device_token`: no token → 401, and the fake sees no request.
  - `Path_tricks_never_escape_the_extension` (Review Focus 3), theory: `/x/sleipnir/../api/v1/people` →
    not 200 with people data (Kestrel normalises it to `/api/v1/people`, which for a member is 403);
    `/x/SLEIPNIR/x` → 404 `extension_not_found`; `/x/sleipnir/%2e%2e%2fapi` → echo path keeps the encoded form.
  - `An_unknown_extension_is_404_and_a_stopped_one_503` (Review Focus 2): `/x/nope/` → 404; `saga`
    configured with `FAKE_MODE=hang` → 503 within 1 s.
  - `Large_responses_stream` (Review Focus 4): `GET /x/sleipnir/big?mb=50` → 50 MiB received; the test
    process's managed heap grows by less than 20 MB during the call (`GC.GetTotalMemory(true)` before and after).
  - `A_client_that_goes_away_cancels_the_upstream_request`: start `GET /x/sleipnir/slow`, cancel after
    2 s → `slow.log` says `cancelled` within 2 s.
  - `Bifrosts_own_routes_still_work_while_an_extension_is_down`.
- [ ] **Step 2: Run** `dotnet test`. Expected: build fails.
- [ ] **Step 3: Implement** with `IHttpForwarder.SendAsync` and one `HttpMessageInvoker` per extension
  (`SocketsHttpHandler` whose `ConnectCallback` opens a `UnixDomainSocketEndPoint`, `UseProxy=false`,
  `AllowAutoRedirect=false`, `UseCookies=false`). An `HttpTransformer` subclass does the header rules.
  The 1 MiB request body limit stays.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(extensions): route /x/<name> to the extension with the caller's identity`

### Task 4: The extension API socket

**Files:**
- Create: `src/Bifrost.Server/Extensions/ExtensionApiHost.cs`, `src/Bifrost.Core/Api/ExtensionPersonDto.cs`
- Test: `tests/Bifrost.Tests/Extensions/ExtensionApiTests.cs`

**Interfaces:**
- Consumes: `PeopleService.GetPersonAsync/GetDeviceAsync` (step 1), `ExtensionSupervisor.ApiSocketPath`.
- Produces: one Kestrel app listening on every extension's `bifrost.sock` (`0600`). The calling
  extension is known from the socket the request came in on (`HttpContext.Features.Get<IConnectionEndPointFeature>().LocalEndPoint`
  → path → name), exposed as `HttpContext.Extension()`. Routes (no tokens: only Bifröst's account can
  reach the socket):
  - `GET /v1/people/{id}` → `ExtensionPersonDto(Guid Id, string DisplayName, PersonRole Role)`, or 404 `person_not_found`.
  - `GET /v1/devices/{id}` → `{ id, personId, name, kind }` or 404 `device_not_found`.
  - Plan 2d adds questions here.

- [ ] **Step 1: Write the failing tests:** `Looks_up_a_person_by_id`, `Unknown_people_are_404`,
  `The_socket_is_owner_only`, `Each_request_knows_which_extension_asked` (two sockets, a test-only
  route registered by the test returns `HttpContext.Extension()`).
- [ ] **Step 2: Run** `dotnet test`. Expected: build fails.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(extensions): the extension API socket, with person lookups`

### Task 5: Wire it into the daemon, try it with the real Sleipnir, document it

**Files:**
- Modify: `src/Bifrost.App/DaemonCommand.cs`, `docs/threat-model.md`, `docs/DEVELOPMENT.md`, `CHANGELOG.md`, `README.md`
- Test: `tests/Bifrost.Tests/DaemonProcessTests.cs`

- [ ] **Step 1: Write the failing test** `The_daemon_starts_configured_extensions_and_stops_them_with_itself`:
  write `extensions.json` naming the fake extension as `sleipnir`, start the daemon, `GET /x/sleipnir/hello`
  with the admin token → echo; SIGTERM → the daemon and the child have both exited.
- [ ] **Step 2: Run, implement, run.** The daemon starts extensions after its own listener, and stops
  them before it exits.
- [ ] **Step 3: By hand, with the real Sleipnir** (built from the `step2-under-bifrost` branch with
  `dotnet publish src/Sleipnir.App -c Release -o <scratch>/sleipnir`): point `extensions.json` at it, run
  the daemon against scratch folders, create a member through the API, issue the member a device with
  a small test script, `POST /x/sleipnir/api/v1/downloads` as each, and check the member sees only
  their own. Write what was run and seen into the ledger.
- [ ] **Step 4: Docs.** Threat model rows: forged identity headers (Task 3 test), extension impersonates
  Bifröst or another extension (`0600` sockets, extension known by socket), crashed extension does not
  stop Bifröst (Task 2/3 tests). DEVELOPMENT: `extensions.json`, the fake extension. CHANGELOG.
- [ ] **Step 5: Commit** `feat(app): the daemon runs its extensions`
