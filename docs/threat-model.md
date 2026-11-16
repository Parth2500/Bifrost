# Bifröst threat model (steps 1 and 2b)

Scope: what step 1 builds (design §12): the `bifrost daemon` process, its SQLite store, the local
admin token, and the loopback HTTP API for people and devices; and what step 2b builds: extensions
as child processes, the `/x/<name>/…` route and the extension API socket. The rows of design §11 that later
steps build are listed at the end, with the step that owns each.

Each decision names the test that proves it. A decision without a test is a defect.

## Glossary

| Term | Meaning |
|---|---|
| Home | One Bifröst installation on one machine. |
| Person | Someone in the home. A person has a role and has no password. |
| Device | Something a person uses. A device has one token. |
| Device token | A random 256-bit secret, base64url, 43 characters. Bifröst stores only its SHA-256. |
| Local admin token | The token of the built-in "This computer" device, kept in `admin.token` in the runtime folder. It stands in for the host admin until step 2. |
| Runtime folder | `RUNTIME_DIRECTORY` under systemd, else `$XDG_RUNTIME_DIR/bifrost`. |
| Data folder | `STATE_DIRECTORY` under systemd, else `$XDG_DATA_HOME/bifrost`. Holds `bifrost.db` and the instance lock. |
| Extension | A separate program Bifröst starts as a child process under its own account: `sleipnir`, `saga`, `files` or `norns`. |
| Extension socket | `x/<name>/extension.sock` in the runtime folder. The extension listens; only Bifröst calls it. |
| Extension API socket | `x/<name>/bifrost.sock`. Bifröst listens; the extension asks about people and devices. |
| Identity headers | `X-Bifrost-Person-Id`, `X-Bifrost-Role`, `X-Bifrost-Device-Id`, `X-Bifrost-Device-Kind`. Bifröst sets them on every forwarded request. |
| Audit log | The `audit` table. One row per change to a person or a device. |

## Trust boundaries

| # | Boundary | Other side | Trust level |
|---|---|---|---|
| B1 | Loopback HTTP | Any process on the host, and any web page the host's browser opens | Untrusted until it shows a device token |
| B2 | Runtime and data folders | Other operating-system accounts on the host | Untrusted |
| B3 | Extension sockets | An extension, running under Bifröst's own account | Trusted to be one of the built-in extensions; not trusted to stay up, or to be the one it claims to be |

## Decisions

| # | Threat | Decision | Test |
|---|---|---|---|
| B1 | A web page drives the loopback API (CSRF, DNS rebinding) | Tokens in the `Authorization` header only, never a cookie or the query string. The `Host` must be `localhost`, `127.0.0.1` or `[::1]`. No CORS headers, no `Set-Cookie` | `HostTests.A_token_in_the_query_string_or_a_cookie_is_not_accepted`, `Hosts_that_only_look_local_are_refused_even_with_a_valid_token`, `No_response_carries_CORS_or_cookie_headers` |
| B2 | Something outside the process opens another listener | The host is built from an empty builder: no configuration file and no environment variable is read | `HostTests.Ambient_Kestrel_configuration_cannot_open_extra_listeners` |
| B3 | A process on the host uses the home without a token | Every route except `GET /api/v1/server` needs a device token. Server info says only name, version and API versions | `HostTests.Server_info_is_public_and_says_nothing_else`, `A_request_without_a_token_is_refused_as_problem_json` |
| B4 | Guessing tokens | 20 wrong tokens per source per minute, then 429. A request with no token does not count. A right token always works | `HostTests.Wrong_tokens_are_throttled_but_the_right_one_still_works`, `Requests_without_credentials_do_not_use_up_the_failure_budget` |
| B5 | A lost or stolen device | Revocation takes effect on the next request. Deleting a person deletes their devices. Nothing caches a principal | `MeEndpointTests.A_revoked_device_is_refused_on_its_next_request`, `Deleting_a_person_refuses_their_devices_at_once` |
| B6 | A member acts as an admin | People, and shutdown, are admin only. A member sees and revokes only their own devices; another's device answers like a missing one | `PeopleEndpointTests.A_member_cannot_shut_down_or_manage_people`, `DeviceEndpointTests.A_device_can_be_revoked_by_its_owner_or_an_admin_but_not_by_others` |
| B7 | The home is left without an admin | The last admin cannot be demoted or deleted. One gate serializes every change, so two at once cannot both win. The admin who owns "This computer" cannot be demoted or deleted either, and that device cannot be revoked, so the host always keeps a working admin token | `PeopleServiceTests.Two_concurrent_changes_to_the_two_admins_leave_one_admin`, `The_host_admin_cannot_be_demoted_or_deleted_even_with_another_admin`, `The_local_device_cannot_be_revoked` |
| B8 | A stolen database gives tokens (D3) | Only the SHA-256 of a token is stored. No response carries a token or a hash | `PeopleServiceTests.Only_the_hash_is_stored_never_the_token`, `DeviceEndpointTests.Token_hashes_never_appear_in_responses` |
| B9 | Another account reads the token or the database (D3) | Both are created `0600`, the runtime folder `0700`. A database file with wider permissions is set back to `0600` on open | `SecretTests.CreateNew_makes_the_file_private_and_its_folder_owner_only`, `DaemonProcessTests.A_widened_database_file_is_made_owner_only_again` |
| B10 | Another account read or planted the local admin token | A token file with permissions other than `0600`, or a symbolic link, is never reused: it is replaced. The runtime folder is set to `0700` first | `SecretTests.A_token_with_loose_permissions_is_replaced_because_someone_may_have_read_it`, `A_symlinked_token_file_is_replaced_and_its_target_left_alone`, `An_open_runtime_folder_is_made_owner_only` |
| B11 | Timing reveals a token (D4) | Tokens are found by their hash. The local token check is constant-time | `SecretTests.Matches_rejects_missing_short_and_wrong_tokens` |
| B12 | Error answers tell an attacker why (D5) | One answer per failure kind: unknown, revoked and malformed tokens all get `401 unauthorized`. The reason goes to the log with the source address | `HostTests.Wrong_tokens_of_any_shape_are_refused` |
| B13 | Secrets in logs (D7) | No token, hash or query string is logged. An unhandled error logs the method and the path only | `HostTests.No_token_appears_in_the_log` |
| B14 | A change cannot be traced | Every change to a person or a device writes an audit row in the same transaction, with the acting person and device. A refused change writes none. Rows outlive the person they name | `PeopleServiceTests.Every_change_is_audited_with_its_actor`, `A_refused_change_writes_no_audit_row`, `DatabaseTests.Audit_rows_survive_deleting_the_person_they_name` |
| B16 | Two people look the same to the admin, who then revokes or promotes the wrong one | Names are unique by their compatibility-normalised, upper-cased form, so Unicode case and composed/decomposed forms collide. Invisible format characters (zero-width, bidi) are refused | `PeopleServiceTests.Names_that_differ_by_unicode_case_or_form_are_taken`, `Names_with_invisible_format_characters_are_rejected` |
| B17 | Opening the API docs makes the host's browser call out | Scalar's default CDN fonts are turned off; the page names no outside address | `HostTests.The_API_docs_page_loads_nothing_from_outside_the_home` |
| B15 | Two daemons write one database | An exclusive lock file in the data folder. The operating system releases it when the process dies | `DaemonProcessTests.A_second_daemon_exits_with_1`, `After_a_crash_a_new_daemon_starts_despite_the_leftover_files` |
| X1 | A client forges identity headers (`X-Bifrost-Role: admin`, in any case) | Every incoming `X-Bifrost-*`, `Authorization`, `Cookie`, `Forwarded` and `X-Forwarded-*` header is removed before Bifröst adds its own. The extension never sees the device token | `ProxyTests.Client_identity_headers_and_credentials_never_reach_the_extension`, `Adds_the_callers_identity` |
| X2 | Someone reaches an extension without a device | `/x/…` goes through the same token check as `/api`. The extension listens on a Unix socket only, in a `0700` folder | `ProxyTests.The_route_needs_a_device_token`, `SupervisorTests.The_folders_are_owner_only` |
| X3 | A crafted path reaches Bifröst's own routes with an extension's rights, or another extension | The name is matched exactly against the built-in list. A path that leaves `/x/<name>/` is judged as the route it resolves to, with the caller's own rights | `ProxyTests.Path_tricks_never_escape_the_extension`, `ExtensionSettingsTests.Names_are_matched_exactly` |
| X4 | An extension impersonates Bifröst or another extension | Both sockets sit in the extension's own `0700` folder, and `bifrost.sock` is `0600`. Bifröst knows who asks from the socket the request arrived on, never from what the request says | `ExtensionApiTests.The_sockets_are_owner_only`, `Each_request_knows_which_extension_asked` |
| X5 | A crashed, hung or looping extension takes the home down | Each extension is its own process. Restarts back off from 1 to 60 seconds and stop after five crashes in 10 minutes. One that never opens its socket is killed after 20 seconds. Meanwhile `/x/<name>/…` answers `503` at once and every other route works | `SupervisorTests.A_crashing_extension_is_restarted_with_growing_delays_then_given_up`, `An_extension_that_never_opens_its_socket_is_killed_after_20_seconds_and_counted_as_a_crash`, `ProxyTests.An_unknown_extension_is_404_and_one_that_is_not_running_503`, `Bifrosts_own_routes_still_work_while_an_extension_is_down` |
| X6 | An extension inherits Bifröst's secrets | The child gets only the contract's variables plus `PATH`, `HOME` (its data folder), `LANG`, `LC_ALL`, `TZ` and `DOTNET_*`. The extension API returns no token and no hash | `SupervisorTests.Starts_the_extension_with_exactly_the_contract_environment`, `ExtensionApiTests.Looks_up_a_device_by_id` |
| X7 | An extension sets a cookie, turning the browser into a credential holder (B1) | `Set-Cookie` is removed from every forwarded response | `ProxyTests.The_extension_cannot_set_cookies_on_the_client` |
| X8 | A broken or hostile `extensions.json` starts an unknown program name or stops the home | Only built-in names with an absolute program path start. Anything else is logged and skipped. The file is in the settings folder, which needs root to edit under the service | `ExtensionSettingsTests.Bad_json_means_no_extensions_and_one_warning`, `Valid_entries_are_kept_and_each_bad_one_is_dropped_with_a_warning`, `As_the_service_a_program_under_home_is_refused` |
| X9 | A large or abandoned transfer exhausts the home | Responses stream without being held in memory. A client that goes away cancels the request to the extension | `ProxyTests.Large_responses_stream`, `A_client_that_goes_away_cancels_the_upstream_request` |
| X10 | An extension outlives Bifröst and keeps serving with nobody checking tokens | The daemon stops every extension before it exits: SIGTERM, then SIGKILL after 10 seconds | `DaemonProcessTests.The_daemon_starts_configured_extensions_and_stops_them_with_itself`, `SupervisorTests.Stop_ends_a_running_extension_quickly` |

Not covered yet: if Bifröst itself is killed with SIGKILL, its extensions keep running until the
service manager cleans up. The systemd unit (step 2c) ends the whole control group.

## Later steps

| Design §11 row | Step |
|---|---|
| Another account on the host acts as the admin (admin socket, peer credentials, system password prompt) | 2 |
| A device on the Wi-Fi uses the home; an impostor home; home key leak | 3 |
| The browser fallback is read on the wire | 3 |
| A tampered or fake extension release; a bad update | 5 |
| A member reads another member's private files; the storage disk is missing; a download escapes its folder | 6 |
| A compromised online app; a stolen online token | 7 |
