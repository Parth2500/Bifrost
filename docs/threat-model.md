# Bifröst threat model (step 1)

Scope: what step 1 builds (design §12): the `bifrost daemon` process, its SQLite store, the local
admin token, and the loopback HTTP API for people and devices. The rows of design §11 that later
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
| Audit log | The `audit` table. One row per change to a person or a device. |

## Trust boundaries

| # | Boundary | Other side | Trust level |
|---|---|---|---|
| B1 | Loopback HTTP | Any process on the host, and any web page the host's browser opens | Untrusted until it shows a device token |
| B2 | Runtime and data folders | Other operating-system accounts on the host | Untrusted |

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

## Later steps

| Design §11 row | Step |
|---|---|
| Another account on the host acts as the admin (admin socket, peer credentials, system password prompt) | 2 |
| An extension impersonates Bifröst to another; a client forges identity headers | 2 |
| A device on the Wi-Fi uses the home; an impostor home; home key leak | 3 |
| The browser fallback is read on the wire | 3 |
| A tampered or fake extension release; a bad update | 5 |
| A member reads another member's private files; the storage disk is missing; a download escapes its folder | 6 |
| A compromised online app; a stolen online token | 7 |
