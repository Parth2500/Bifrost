# ADR 001: Bifröst starts as a copy of Sleipnir's identity code

- Date: 2026-11-15
- Status: Accepted
- Affects: design §12, step 1

## Context

The design says Bifröst is built by moving people, devices, tokens and the request pipeline out of
`Sleipnir.Server`, and that Sleipnir alone keeps working throughout. Sleipnir's identity code is
tested and reviewed. Sleipnir's own design keeps users and devices until it gains its "under
Bifröst" mode.

## Decision

Step 1 copies the code into Bifröst and renames it: users become people, `/users` becomes
`/people`, and a device gains a kind (`app` or `browser-fallback`). Sleipnir does not change in
step 1. Bifröst references no Sleipnir assembly: the suite shares no code (suite principle 4).

Left out on purpose: download routes, the per-user `allowedRoot` and the destination policy.
Folders come from the home root in step 6.

Added on the way: Heimdall's build rules, `TimeProvider`, an audit table, OpenAPI with Scalar, and
the systemd directory variables. A token file that others could have read is now replaced, not
reused (threat model B10).

## Consequences

For one step there are two copies of the identity code. Step 2 deletes Sleipnir's copy when
Sleipnir learns to take identity headers from Bifröst. A fix to either copy before then must be
made in both.
