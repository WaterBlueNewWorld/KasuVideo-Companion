# Changelog

The KasuVideo Companion's releases (tags `v<version>`). The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The section for a version becomes its
GitHub release notes; Jellyfin's plugin catalog shows `changelog` in `build.yaml`.

## [Unreleased]

### Added
- **Settings** for KasuVideo's Watch together rooms and guest passes, changed by administrators
  from KasuVideo's own page for the companion: `GET`/`POST /KasuVideo/Companion/Settings`, with
  each number's range. `GET /KasuVideo/Companion/RoomSettings` tells any signed-in user (and
  KasuVideo's rooms Worker) how many people and guests a room takes.
- **Guest passes**, off by default: a room's owner (one an administrator listed) invites someone
  with no account on the server; opening the link makes them a hidden account named after their
  nickname with `-G`, which can't sign in with a password and only watches the invite's
  libraries, joining Watch together groups without starting them. The pass ends at its owner's or
  an administrator's word, at its end, after its guest is away too long, or when passes are
  turned off, and the account goes with it. Routes under `/KasuVideo/Guests`; a "Guest passes"
  scheduled task; uninstalling ends every pass.
- **A guest's token is confined on the server**, whatever app holds it: every API call passes
  a guard, and a guest only reaches their own session, their room's Watch together group (join,
  ready, leave; never control it or start one), and what that room has queued (with an
  episode's season and series). Browsing, search, other users and groups, writes and other
  plugins answer 403. The pass follows the room when its owner starts a new group.
- **A guest can leave:** `DELETE /KasuVideo/Guests/Me` ends their own pass at once and deletes
  their account (only a guest whose pass lives, and only their own).
- **A "Leftover guest accounts" scheduled task**, every hour and at startup, deletes guest
  accounts without a live pass (a crash, a failed delete, a pass that ran out), besides the
  five-minute "Guest passes" task. An account is never reused for another pass, and neither task
  sweeps while a link is being opened.
- `Info` answers `Capabilities`, what this version can do.
- Tests (xUnit, `tests/`), run by CI.

### Security
From KasuVideo's penetration test of guest passes (its `docs/GUEST_PASSES.md`, "Penetration test
(G8)"):
- The guard checks every argument that names an item, a media source, a session, a device or a
  play session, not only the route's item: trickplay through a queued item no longer shows
  another's frames, and a guest can't touch another member's session or end their transcode.
- A guest's token works only from the device its pass was opened on; a device id another user
  holds is refused at opening.
- Every pass ends when the server stops (a restart, an update, the companion disabled), so a
  guest is never left with a token and no guard.
- A guest is known by their provider too, not only by their pass; an unreadable pass file is set
  aside instead of stopping every request on the server.
- The group tracker notes only what Jellyfin did (it answers a refused join or queue 204 too),
  and only items the requester may see; it remembers 1,000 groups at most. A guest's group list
  goes out trimmed or not at all: an answer of another shape is refused.
- A guest never holds their room: SyncPlay's "ignore wait" is set for them when they join, and
  their stalls are limited to 12 a minute.
- Only wrong codes are rate-limited, and a refusal says when to try again (`Retry-After`): a
  flood no longer locks real links out. A wrong code no longer writes the pass file.
- An inviter taken off the list ends their guests' passes at once. A preview names only an
  inviter who may still invite. Device names and versions are cleaned and cut to 64
  characters. Ending someone else's pass answers as for an unknown one (`404`).
- A guest may no longer list an item's images (server paths) or ask for a bitrate test over
  2 MiB; `/System/Ping` is allowed (the allow-list named its routes, not its action).
- A new guest account is made under a random name and named after the nickname once its policy
  is the guest's.

## [0.1.1] - 2026-11-25

### Changed
- The README discloses the AI tools used to make the companion, as Jellyfin's LLM/AI
  Development Policy asks of projects around Jellyfin.
- The plugin itself is unchanged.

## [0.1.0] - 2026-11-25

### Added
- `GET /KasuVideo/Companion/Info` (any signed-in user): the companion's name, version, contract
  (1) and where its source code is.
- `GET /KasuVideo/Companion/Manifests` (any signed-in user): the KasuVideo manifest of every
  active plugin (its `*.kasuvideo.json` page), read from the plugin's own resources.
- `GET /KasuVideo/Companion/Implementations` (administrators): for every plugin, the Jellyfin
  interfaces its classes implement and the API routes its controllers add. In an assembly
  several plugins share (Jellyfin's bundled ones), only the classes under the plugin's own
  namespace count.
- Built for Jellyfin 10.11 (.NET 9); checked on 10.11.11 and 12.1.0.
- Licensed under the GNU Affero General Public License v3.0 only.
