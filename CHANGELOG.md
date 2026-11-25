# Changelog

The KasuVideo Companion's releases (tags `v<version>`). The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The section for a version becomes its
GitHub release notes; Jellyfin's plugin catalog shows `changelog` in `build.yaml`.

## [Unreleased]

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
