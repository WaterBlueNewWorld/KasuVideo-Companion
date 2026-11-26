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
