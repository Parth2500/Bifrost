# Ask the Admin Implementation Plan (Bifröst step 2d)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bifröst or an extension can ask the admin a question. It reaches the host screen, through a
desk companion, and the admin's paired devices, at once. The first answer wins. Sleipnir uses it for
pending downloads.

**Architecture:** An in-memory `QuestionService` in Bifröst holds open questions, with a change
sequence that askers and admins long-poll. Extensions ask through their API socket; admins answer
through the admin socket (the desk companion, on the host) or the loopback API (paired admin
devices). The desk companion is `bifrost desk`, a user service in the admin's desktop session that
shows each question as a desktop notification with action buttons. Sleipnir creates a member's
download as `Pending` and asks.

**Tech Stack:** .NET 10, ASP.NET Core, `notify-send` (libnotify ≥ 0.8, for `--action`) and `gdbus`
for the desktop, xUnit, `FakeTimeProvider`.

**Spec:** `Bifrost/docs/design.md` §5.1, §8.6, §11 (audit of approvals);
`Sleipnir/docs/superpowers/specs/2026-11-13-sleipnir-service-design.md` §7.4.

## Global Constraints

- Questions live in memory [my call]: they do not survive a Bifröst restart. Extensions restart with
  Bifröst, and Sleipnir asks again for each download still pending when it starts.
- A question: `title` ≤ 80 chars, `text` ≤ 500, 2 to 4 choices (`id` ≤ 32 chars of `[a-z0-9_-]`, `label`
  ≤ 40), `expiresAt` between 1 minute and 7 days ahead, and `online` (may go to Huginn; unused until step 7).
  No secrets and no file paths in the text (design §9.2 rule, applied now).
- States: `open`, `answered` (with `choice` and who answered), `expired`, `withdrawn`. Only `open` moves.
- One answer wins. A later answer gets `409 already_closed` with the question's final state.
- Only admins answer. Every answer is audited: kind `question.answered`, actor, subject = the question
  id, detail = `<asker>:<choice>`.
- Long polls wait at most 30 s and return early on any change.
- Pending downloads (Sleipnir): who needs approval is Sleipnir's setting `approval` in
  `<BIFROST_DATA_DIR>/settings.json`: `"members"` (default) [my call], `"everyone"` or `"none"`.
  Alone, nothing is ever pending. Choices sent: `approve` / `reject`. Expiry 24 h.

## Review Focus

1. **The admin answers on the desk and on a phone within the same moment**: exactly one answer is
   recorded and audited, the other gets `already_closed`, and the asker sees one result. Test in Task 1.
2. **The asker withdraws (a member cancels the pending download) while the notification is up**: the
   notification goes away and a late click does nothing harmful. Test in Tasks 1 and 3.
3. **Bifröst restarts with downloads pending**: after the restart each still-pending download has
   exactly one open question again. Test in Task 4.
4. **A question that expires**: the asker gets `expired`; the download stays pending, says so, and an
   admin can still approve it directly. Test in Task 4.
5. **The desk companion started before Bifröst, or Bifröst restarting under it**: it keeps retrying
   with a growing delay and picks the open questions up, without duplicate notifications. Test in Task 3.

---

### Task 1: `QuestionService`

**Files:**
- Create: `src/Bifrost.Server/Questions/QuestionService.cs`, `Questions/Question.cs`, `src/Bifrost.Core/Api/QuestionDto.cs`
- Test: `tests/Bifrost.Tests/Questions/QuestionServiceTests.cs`

**Interfaces:**
- Produces:
  - `record Choice(string Id, string Label)`; `enum QuestionState { Open, Answered, Expired, Withdrawn }`
  - `record QuestionDto(Guid Id, string Asker, string Title, string Text, Choice[] Choices, DateTime ExpiresAt, bool Online, QuestionState State, string? Choice, Guid? AnsweredBy, long Seq)`
  - `QuestionService(PeopleService people, TimeProvider time)`:
    `Ask(string asker, string title, string text, IReadOnlyList<Choice> choices, DateTime expiresAt, bool online) -> QuestionDto` (throws `QuestionException("invalid_question")`),
    `Task<QuestionDto> AnswerAsync(Guid id, string choice, Principal admin)` (`question_not_found`, `already_closed`, `invalid_choice`, `forbidden` for a member),
    `Withdraw(Guid id, string asker) -> bool`,
    `Get(Guid id) -> QuestionDto?`, `ListOpen() -> IReadOnlyList<QuestionDto>`,
    `Task<long> WaitForChangeAsync(long afterSeq, TimeSpan max, CancellationToken ct)` — returns the current sequence.
  - Expiry runs on a `time.CreateTimer` per question.

- [ ] **Step 1: Write the failing tests:** `Ask_validates_every_limit` (theory over each limit above);
  `The_first_answer_wins_and_is_audited` and `Two_answers_at_once_record_one` (Review Focus 1: two
  `Task.Run` behind a `Barrier`, 50 rounds, one wins, one `already_closed`, one audit row);
  `A_member_cannot_answer`; `An_unknown_choice_is_refused`; `Expiry_closes_the_question` (advance the
  fake time); `Withdraw_closes_it_and_only_for_its_asker` (Review Focus 2); `Waiters_wake_on_any_change_and_time_out_after_the_limit`.
- [ ] **Step 2: Run, Step 3: implement, Step 4: run** `dotnet test`. Expected: fail, then pass.
- [ ] **Step 5: Commit** `feat(questions): ask the admin, first answer wins`

### Task 2: Question routes for extensions and admins

**Files:**
- Create: `src/Bifrost.Server/Api/QuestionEndpoints.cs`
- Modify: `Extensions/ExtensionApiHost.cs`, `Hosting/BifrostHost.cs`
- Test: `tests/Bifrost.Tests/Questions/QuestionRouteTests.cs`

**Interfaces:**
- Produces:
  - Extension API socket: `POST /v1/questions` (body: title, text, choices, expiresAt, online) → 201
    `QuestionDto`, `asker` = the calling extension; `GET /v1/questions/{id}?wait=<s>` → the question,
    returning when it is no longer `open` or after `wait` (≤ 30) seconds; `DELETE /v1/questions/{id}` →
    withdraw (204; 404 for another extension's question).
  - Loopback API and admin socket, admin only: `GET /api/v1/questions` → `{ seq, items: QuestionDto[] }`
    (open ones); `GET /api/v1/questions?after=<seq>&wait=<s>` long-polls; `POST /api/v1/questions/{id}/answer`
    body `{ "choice": "approve" }` → the closed question, or 409 `already_closed`.

- [ ] **Step 1: Write the failing tests:** an extension asks, the admin answers over the loopback API, the
  extension's long poll returns `answered` with the choice; the desk side (admin socket) sees the
  question appear through `after=<seq>`; a member gets 403; one extension cannot read or withdraw
  another's question.
- [ ] **Step 2–4:** run, implement, run.
- [ ] **Step 5: Commit** `feat(questions): routes for extensions and admins`

### Task 3: The desk companion

**Files:**
- Create: `src/Bifrost.App/Desk/DeskCommand.cs`, `Desk/INotifier.cs`, `Desk/NotifySendNotifier.cs`,
  `Desk/DeskLoop.cs`, `Desk/DeskUnit.cs`
- Modify: `src/Bifrost.App/Program.cs`
- Test: `tests/Bifrost.Tests/Desk/DeskLoopTests.cs`

**Interfaces:**
- Produces:
  - `interface INotifier { Task<string?> ShowAsync(Guid questionId, string title, string text, IReadOnlyList<Choice> choices, CancellationToken closeWhen); }`
    — returns the chosen choice id, or null if dismissed or closed.
  - `NotifySendNotifier`: `notify-send --app-name=Bifröst --print-id --wait --action=<id>=<label>… <title> <text>`;
    reads the id from the first line and the action from the last; on `closeWhen`, calls
    `gdbus call --session --dest org.freedesktop.Notifications --object-path /org/freedesktop/Notifications
    --method org.freedesktop.Notifications.CloseNotification <id>`. Arguments go through `ArgumentList`,
    never a shell.
  - `DeskLoop(HttpClient adminSocket, INotifier notifier, TimeProvider time)`: `RunAsync(ct)`. Long-polls
    `/api/v1/questions`; shows each new open question once; closes notifications whose question closed;
    posts the chosen answer; on connection failure retries after 1, 2, 4 … 30 s.
  - `bifrost desk` runs the loop. `bifrost desk install` writes `~/.config/systemd/user/bifrost-desk.service`
    (`ExecStart=/opt/bifrost/bifrost desk`, `Restart=always`, `WantedBy=graphical-session.target`) and
    runs `systemctl --user enable --now bifrost-desk`.

- [ ] **Step 1: Write the failing tests** (fake notifier, `HostFixture` admin socket):
  `A_new_question_is_shown_once_and_the_click_answers_it`; `A_question_answered_elsewhere_closes_its_notification`
  (Review Focus 2); `A_click_after_the_question_closed_does_no_harm` (the 409 is swallowed and logged);
  `Without_Bifrost_the_loop_retries_and_then_shows_the_open_questions_once` (Review Focus 5: start the
  loop first, start the host later, advance the fake time); `The_desk_unit_starts_with_the_graphical_session`.
- [ ] **Step 2–4:** run, implement, run.
- [ ] **Step 5:** By hand on the owner's desktop: `bifrost desk` against a dev daemon, ask a question
  with a small script on an extension socket, click a button, see the answer. Ledger it.
- [ ] **Step 6: Commit** `feat(desk): the desk companion shows questions as notifications`

### Task 4: Pending downloads in Sleipnir (Sleipnir repo, branch `step2-under-bifrost`)

**Files:**
- Modify: `src/Sleipnir.Core/Models/DownloadStatus.cs` (add `Pending`, at the end, so stored numbers keep
  their meaning), `DownloadTask.cs` (`ApprovalState? Approval`: `waiting`, `expired`, `rejected`),
  `src/Sleipnir.Engine/Services/DownloadService.cs` (`AddDownloadAsync(..., bool pending = false)`,
  `ApproveAsync(Guid)`, `RejectAsync(Guid)`), `Api/DownloadEndpoints.cs`, `Api/DownloadDto.cs`
- Create: `src/Sleipnir.Server/Bifrost/BifrostApiClient.cs`, `Bifrost/ApprovalCoordinator.cs`,
  `Hosting/ExtensionSettings.cs`
- Test: `tests/Sleipnir.Tests/Server/PendingTests.cs`, `DownloadServiceTests.cs`

**Interfaces:**
- Consumes: the extension API routes of Task 2 (by HTTP over `BIFROST_API_SOCKET`; no Bifröst code).
- Produces:
  - `BifrostApiClient(string socketPath)`: `AskAsync(...) -> Guid`, `WaitAsync(Guid id, ct) -> (string State, string? Choice)`, `WithdrawAsync(Guid id)`.
  - `ApprovalCoordinator(IDownloadService, BifrostApiClient, ExtensionSettings, ILogger)`: when a pending
    download is added, asks ("Download for <person>?", text = the host name and file name only, no
    path); waits; `approve` → `ApproveAsync` (queued); `reject` → `RejectAsync` (cancelled, approval
    `rejected`); `expired` → approval `expired`, stays pending. On start, asks again for every pending
    download (Review Focus 3). Cancelling a pending download withdraws its question (Review Focus 2).
  - `POST /api/v1/downloads/{id}/approve` and `/reject` (admin only) for an expired one, or from any admin client.
  - Who is pending: `approval` setting × the caller's role. The name in the question comes from Bifröst's
    `GET /v1/people/{id}`.

- [ ] **Step 1: Write the failing tests** with a fake Bifröst API on a temp socket (a tiny Kestrel app in
  the test): `A_members_download_waits_for_approval_and_starts_when_approved`; `Rejected_is_cancelled_and_says_rejected`;
  `Expired_stays_pending_says_expired_and_an_admin_can_still_approve` (Review Focus 4);
  `An_admins_download_starts_at_once_with_the_default_setting`; `Setting_everyone_and_none`;
  `Cancelling_a_pending_download_withdraws_the_question`; `After_a_restart_each_pending_download_is_asked_again_once` (Review Focus 3);
  `Alone_nothing_is_ever_pending`; `The_question_text_has_no_path`.
- [ ] **Step 2–4:** run, implement, run (`dotnet test` in Sleipnir).
- [ ] **Step 5: Commit** (Sleipnir) `feat: pending downloads ask the admin through Bifröst`

### Task 5: End to end and docs

- [ ] **Step 1:** By hand: dev Bifröst with the real Sleipnir and the desk companion; a member adds a
  download through `/x/sleipnir/…`; the notification appears; Approve; the download runs. Repeat with
  Reject, and with the answer from the admin's device token instead. Ledger the results.
- [ ] **Step 2: Docs** in both repos: threat model (answers audited, only admins answer, questions carry
  no paths), DEVELOPMENT (`bifrost desk`, the approval setting), CHANGELOGs, README status (step 2 done),
  design §12 step 2 marked built.
- [ ] **Step 3: Run** both suites. Expected: all pass.
- [ ] **Step 4: Commit** in each repo `docs: ask the admin`
