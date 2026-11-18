# ADR 005: Files, sharing between devices, and Saga

- Date: 2026-11-19
- Status: Proposed. Nothing here is built. It rests on step 6 (home root, per-person folders, local events).
- Affects: design §8.9 (folders), §8.7 (events), §10 (client app); steps 6 and later

## Context

Today a person can send a download to the home instead of their own device (Sleipnir, built), for example because the
phone is short of space. Three things follow that nothing yet covers:

1. A person getting at their own downloaded files again.
2. Sending a file from one person's device to another's.
3. Saga (video), which also reads media at the home. Files must not become a second way to stream video.

The README already lists Files as an extension. Suite decision S5 says "file sharing (if it is split from Sleipnir)", so
the split is still open.

## Decision

**Order.** Step 6 first (it also unblocks three Sleipnir items). Then a Files extension. Then sharing, on top of Files.
[owner]

**Files: browse and download a copy.** A person sees the files in their own `private/` and `public/` folders and
downloads a copy to the device they are holding. [owner]

- **Files never streams or previews.** It serves a file as an attachment only, with no inline view and no Range
  playback. Watching a video from the home is Saga's job; a copy on the device is the person's own. [my call: this keeps
  Saga's role and is easy to state.]

**Sharing between devices is a relay through the home, not a direct transfer.** [owner]

- The sender offers a file. It is stored at the home in the receiver's name, in an inbox in the receiver's folder
  kept apart from `private/` and `public/`. [my call on the inbox.]
- The receiver's device is told when it next connects, and the file stays until it is removed.
- **Nothing is pushed.** The file reaches the receiver's device only when the receiver opens it. This is the
  difference from Quick Share: both people need not be in the app at the same moment, and nobody has a file forced on them.
- Direct transfer was rejected: both people would have to have the feature open at once, and a receiver who is away gets nothing.
- Reusing "Asks the admin" for the notice is possible; whether it fits is for the builder to check.

**Saga's library is a cache, never the truth.** [my call; kept from §8.9]

- Who may see a file stays decided by where it is (`<home root>/<person>/private|public/`), not by a record about the file.
- Saga may build its own index by scanning (titles, thumbnails, watch progress). Owner and visibility come from the path
  during the scan, and the index can be rebuilt from disk at any time.
- A manifest that said "owner" and "public" by itself would drift when a file is moved or deleted outside Bifröst,
  would have no entry for a folder of media the admin adds, and would leave two answers to "who may see this".

## Open

- **Limits on shared files:** a size limit, a space limit per person, an expiry. "Until removed" is the starting point;
  the rest is decided later. [owner]
- **Who may remove a shared file:** proposed as the receiver, the sender until it has been opened, and the admin.
- **A record that a path cannot say**, such as "this video is shared with these two people". If that is wanted, the
  rule that visibility comes from the folder has to change on purpose, and this note updated.
- **Whether Files and sharing are one extension or two,** and how S9 (Huginn as a courier between devices) overlaps.
  S9 was not read for this note.
- **Where Saga's media lives:** in the same per-person folders or in areas of its own. Only needed if its library must be
  kept apart from personal files.

## Consequences

- Step 6 gets two requirements: an inbox in each person's folder, and a way to tell a device about something waiting.
- The Files extension has a stated rule (attachment only) that tests can check.
- Saga needs a scan that reads ownership from the path, and no manifest of its own for access.
