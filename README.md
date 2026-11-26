# KasuVideo Companion
# THIS WAS DONE USING AI - Thanks for your attention! :p

> This package and its parent project (KasuVideo yet to be made public, probably next week idk), was done using tools such as "Claude code" with the guidance of a human (Me, WaterBlueNewWorld) which makes plugin/repository this qualify as "AI Slop", you are free to install this plugin (Despite it would do nothing on your current Jellyfin server); you are free to contribute, copy or modify this plugin however you want. I write up this and any issue which may come to me regarding the usage of AI assumes that you already read this disclaimer. 
>
> I am open to any suggestions for this plugin c:
>
> BTW the logo was not done using AI (What a surprise! right?), it was done by one of my closest friends @BosefoSefo (On Twitter/X).

> As for the reason on why use AI for this?
> * Simple, I was bored and I am lazy.

## Your AI Slop plugin, sir.
A Jellyfin server plugin made for KasuVideo, a web frontend for Jellyfin. It does three things
Jellyfin itself doesn't do for KasuVideo:

1. **What do the server's plugins add to KasuVideo?** Plugins describe their additions (entries
   in KasuVideo's item menus, pages in its Dashboard) in a manifest: a plugin page named
   `*.kasuvideo.json`. Jellyfin lists plugin pages to administrators only, so without the
   companion only an administrator's KasuVideo finds them. The companion hands them to every
   signed-in user.
2. **What does each plugin implement?** Jellyfin finds a plugin's metadata providers, image
   providers, scheduled tasks, services and so on through its interfaces, and never tells a
   client. The companion reads them from the loaded assemblies, for administrators.
3. **Watch together's settings and guest passes** (from 0.2.0). It keeps the settings of
   KasuVideo's Watch together rooms, and lets a room's owner invite someone who has no account on
   the server: opening the invite link makes them a short-lived, confined account, deleted when
   the pass ends ([Guest passes](#guest-passes)). Administrators change the settings from
   KasuVideo's own page for the companion; it has no page in Jellyfin's dashboard.

The first two change nothing on the server. Guest passes are off until an administrator turns
them on; then they make and delete users. KasuVideo works without the companion: plugins'
additions show for administrators only, the "what it does for the server" panel isn't there,
and there are no guest passes.

The manifest format is KasuVideo's, documented in its repository (`docs/PLUGIN_MANIFEST.md`);
the companion only finds the manifests and hands them over, whatever they contain.

## What it answers

All under the server's base URL (for example `/jellyfin`, if the server has that Base URL).

| Route | Who | What |
| --- | --- | --- |
| `GET /KasuVideo/Companion/Info` | Any signed-in user | `{ "Name", "Version", "Contract": 1, "Source", "Capabilities" }`. KasuVideo asks it first: a 404 means no companion. `Source` is where this version's source code is (see [License](#license)). `Capabilities` (from 0.2.0) names what this version can do: `manifests`, `implementations`, `settings`, `roomSettings`, `guestPasses`. |
| `GET /KasuVideo/Companion/Manifests` | Any signed-in user | `{ "Manifests": [{ "PluginId", "PluginName", "PluginVersion", "Page", "Manifest" }] }`: the manifest of every **active** plugin, read from the plugin's own resources. A page that isn't JSON is left out and logged (without its content). |
| `GET /KasuVideo/Companion/Implementations` | Administrators (`RequiresElevation`) | `{ "Plugins": [{ "Id", "Name", "Version", "Status", "Interfaces": [{ "Interface", "Name", "Types" }], "Routes" }] }`: for every plugin, the Jellyfin interfaces its public types implement and the API routes its controllers add. |

How `Implementations` decides what belongs to a plugin:

- **Its types** are the public, non-abstract classes of the DLLs Jellyfin loaded for it. Jellyfin's
  bundled plugins (TMDb, OMDb, MusicBrainz, AudioDB, Studio Images) share one assembly with the
  server's own providers; in an assembly several plugins share, a plugin's types are those under
  the namespace of its Plugin class.
- **The interfaces** are those in `MediaBrowser.*` and `Jellyfin.*` namespaces, and
  `IHostedService`, minus the three every plugin has (`IPlugin`, `IHasPluginConfiguration`,
  `IPluginAssembly`). `Interface` is the full name of the generic definition
  (`MediaBrowser.Controller.Providers.IRemoteMetadataProvider\`2`), to group by; `Name` is how
  C# writes it; `Types` lists the plugin's classes, with the type arguments when they differ
  (`TmdbMovieProvider (IRemoteMetadataProvider<Movie, MovieInfo>)`).
- **The routes** come from controllers' `[Route]` and `[Http…]` attributes, with ASP.NET's
  `[controller]` token filled in.
- A plugin that isn't loaded (disabled, failed) lists none.

From 0.2.0, the settings and guest passes:

| Route | Who | What |
| --- | --- | --- |
| `GET /KasuVideo/Companion/Settings` | Administrators | Every setting ([Settings](#settings)), with `Bounds`: the range each number may take. |
| `POST /KasuVideo/Companion/Settings` | Administrators | Saves every setting at once; `400 { "Problems": { name: message } }` when one is out of range. Turning guest passes off ends every pass. |
| `GET /KasuVideo/Companion/RoomSettings` | Any signed-in user | `{ "MaxMembers", "MaxGuests" }`: how many people and guests a Watch together room takes. KasuVideo's rooms Worker reads it with the joining user's own header. |
| `GET /KasuVideo/Guests/Status` | Any signed-in user | Whether guest passes are on, whether the caller may invite, the libraries they may give, their open passes and the allowed lengths. |
| `GET /KasuVideo/Guests/Invites` | An inviter | Their open passes (links not yet opened, and guests), never with a code. |
| `POST /KasuVideo/Guests/Invites` | An inviter | `{ "GroupId", "LibraryIds", "Hours" }`: a new invite for a Watch together group they're in. Answers the code, the only time it's given. |
| `DELETE /KasuVideo/Guests/Invites/{id}` | The pass's inviter, administrators | Ends the pass, and the guest's account. |
| `POST /KasuVideo/Guests/Invites/Preview` | Anyone (limited per address) | `{ "Code" }`: who invited them and until when the link works; `404 invalid` otherwise, never saying why. |
| `POST /KasuVideo/Guests/Redeem` | Anyone (limited per address) | `{ "Code", "Nickname", "DeviceId", "DeviceName", "AppVersion" }`: opens the link, once; makes the guest's account and answers its session (`AccessToken`, `UserId`, `UserName`, `ServerId`), the room's `GroupId` and `EndsAt`. |
| `GET /KasuVideo/Guests/Me` | A guest | Their pass: `PassId`, `GroupId`, `InviterId`, `InviterName`, `EndsAt`; `404 not-guest` for anyone else. |
| `GET /KasuVideo/Guests/Passes` | Administrators | Every pass, live and ended in the last week, with why each ended. |

Refusals answer `{ "Error": code }`: `off`, `not-inviter`, `guest`, `not-in-group` (403);
`bad-group`, `bad-libraries`, `bad-hours`, `bad-name`, `bad-device` (400); `too-many`,
`name-taken` (409); `invalid`, `not-found` (404); `too-many-requests` (429); `failed` (500).

## Settings

Kept by Jellyfin in the plugin's settings file, changed from KasuVideo's page for the companion
(administrators). The defaults are the KasuVideo owner's choices.

| Setting | Default | Range | What |
| --- | --- | --- | --- |
| `GuestPassesEnabled` | off | | Whether guest passes work at all. Turning them off ends every pass. |
| `InviterIds` | nobody | | Who may invite (to rooms they own). Administrators too only if listed. |
| `GuestLibraryIds` | none | | The libraries a guest may ever be given; each invite picks among them, and only those the inviter can see. |
| `MaxLivePassesPerInviter` | 3 | 1–20 | Open passes (unopened links and guests) per inviter. |
| `LinkMinutes` | 10 | 5–60 | How long a link waits to be opened. |
| `PassHoursMin`, `PassHoursMax` | 3, 6 | 1–24 | The pass lengths an inviter may pick (the shortest by default). |
| `IdleMinutes` | 20 | 5–120 | How long a guest may go without a session before the pass ends. |
| `GuestTranscoding` | on | | Whether guests may transcode (most browsers need it for HEVC). |
| `GuestBitrateMbps` | 8 | 1–200 | The most a guest may stream. |
| `RoomMaxMembers` | 16 | 2–50 | The most people a Watch together room takes, its owner included. |
| `RoomMaxGuests` | 4 | 0–49 | The most guests a room takes at once; at least one place is left for the owner. |

## Guest passes

A Watch together room's owner (one an administrator listed) invites someone who has no account
on the server. The design is in KasuVideo's repository (`docs/GUEST_PASSES.md`).

1. **The invite.** The owner, in a Watch together group, asks for an invite with the libraries the
   guest may see and the pass's length. The companion checks with Jellyfin that they're in the
   group, keeps only the code's SHA-256, and answers the code once. KasuVideo puts it in the
   link's fragment (`/guest#code`), so no server logs it.
2. **Opening it.** Within `LinkMinutes`, once: the companion makes a hidden user named after the
   visitor's nickname with `-G` at the end (`Mika-G`; `Mika2-G` when taken), signs it in with
   Jellyfin's `AuthenticateDirect` (as Quick Connect does), and the pass's clock starts.
3. **The guest's account** can't sign in with a password: its authentication provider (`KasuVideo
   guest pass`) refuses every one. That provider is also how anything tells a guest: `/Users/Me`
   says `Policy.AuthenticationProviderId` is
   `Jellyfin.Plugin.KasuVideoCompanion.Guests.GuestAuthenticationProvider`, which only an
   administrator can change. Its policy: hidden, the invite's libraries only, Watch together
   join-only, one session, the guest bitrate and transcoding settings, and nothing else (no
   downloads, sharing, Live TV, preferences, control of others' devices or any management).
4. **The end.** The inviter or an administrator ends it, or it reaches its length, or the guest
   goes `IdleMinutes` without a session, or its inviter is no longer listed, or guest passes are
   turned off. Ending signs out every device of the guest (their token stops working, and they
   leave their Watch together group), then deletes the user. A scheduled task ("Guest passes",
   under KasuVideo) does this every five minutes and at startup, and also deletes any guest
   account no live pass owns (left by a crash). Uninstalling the companion ends every pass.

The passes are kept in `guest-passes.json` in the plugin's folder
(`<config>/plugins/Jellyfin.Plugin.KasuVideoCompanion/`); ended ones are forgotten after a week.

**Prior art:** [ShareLinks](https://github.com/Franciskid/jellyfin-plugin-sharelinks) by
Franciskid (GPL-3.0) makes temporary guest users from links too. The ideas of a hidden user per
visitor with a provider that refuses passwords, `AuthenticateDirect`, and hashed codes come
from it; none of its code is used. Guest passes differ by being bound to a KasuVideo room.

Coming next (KasuVideo's plan): confining what a guest's token can call on the server, to their
room's group and what it plays.

## Installing

### From its plugin repository

The companion is published as a Jellyfin plugin repository, like the official one. On the
server: Dashboard › Plugins › Repositories (Jellyfin's or KasuVideo's) › add

```
https://waterbluenewworld.github.io/KasuVideo-Companion/manifest.json
```

then install "KasuVideo Companion" from the Catalog and restart the server. Servers with it
installed are offered each new version. It needs Jellyfin 10.11 or newer.

### By hand

Build it (below), copy the DLL into a folder of its own under the server's plugin folder,
`<config>/plugins/KasuVideo Companion_0.1.1.0/`, then restart the server. For a server in
Docker (a container named `jellyfin`, config at `/config`):

```bash
# Under Git Bash on Windows, stop it rewriting paths first: export MSYS_NO_PATHCONV=1
docker cp bin/Release/net9.0/Jellyfin.Plugin.KasuVideoCompanion.dll jellyfin:/tmp/c.dll
docker exec jellyfin sh -c 'mkdir -p "/config/plugins/KasuVideo Companion_0.1.1.0" && mv /tmp/c.dll "/config/plugins/KasuVideo Companion_0.1.1.0/Jellyfin.Plugin.KasuVideoCompanion.dll"'
docker restart jellyfin
```

It then shows in Dashboard › Plugins (Jellyfin's or KasuVideo's) as "KasuVideo Companion", and
can be uninstalled there like any plugin. (Jellyfin 10.11 deletes the folder of a hand-copied
plugin that is disabled when the server restarts; copy it again to get it back.)

## Building

The .NET 9 SDK, then, at the repository's root:

```bash
dotnet build -c Release
```

The plugin is `bin/Release/net9.0/Jellyfin.Plugin.KasuVideoCompanion.dll`. It compiles against
the Jellyfin 10.11 packages (`-p:JellyfinVersion=…` for others) and doesn't ship them: the server
provides them.

## Testing

```bash
dotnet test tests/Jellyfin.Plugin.KasuVideoCompanion.Tests
```

The tests (xUnit) run without a Jellyfin server: guest passes' rules, store, codes, names and
policy, the settings' ranges, and the visitors' rate limit. A fake stands in for Jellyfin
(`IGuestAccounts`, which `JellyfinGuestAccounts` implements for real), so what touches Jellyfin
itself is checked on a server: see [Where it was checked](#where-it-was-checked). The test
project lives in `tests/` and is never part of the plugin (the plugin's project leaves `tests/`
out). CI runs it on every push to `main`.

## Files

| File | What it is |
| --- | --- |
| `Plugin.cs`, `PluginServiceRegistrator.cs` | The plugin, and the services it adds to Jellyfin's. |
| `Api/CompanionController.cs` | Info, Manifests and Implementations. |
| `Api/SettingsController.cs` | Settings and RoomSettings. |
| `Api/GuestsController.cs` | Guest passes' routes. |
| `Configuration/` | The settings (`PluginConfiguration`) and their ranges (`SettingsBounds`). |
| `Guests/` | Guest passes: the rules (`GuestPassService`), the store, codes, names, the guest policy, the provider that refuses passwords, Jellyfin's side (`JellyfinGuestAccounts`), the cleanup task and the visitors' rate limit. |
| `tests/` | The tests ([Testing](#testing)). |
| `Jellyfin.Plugin.KasuVideoCompanion.csproj` | The project; its `<Version>` is the plugin's version. |
| `build.yaml` | What jprm (Jellyfin's plugin repository tool) needs to package it: name, id, version, `targetAbi`, the catalog's texts and this version's changelog. |
| `image.png` | Its picture in the catalog and the Installed list (960×540): KasuVideo's hat logo and name, in Nunito, on KasuVideo's cream background. |
| `CHANGELOG.md` | Its releases; a version's section becomes its release notes. |
| `scripts/check-version.sh` | Checks the version is the same in every place that names it, and the release tag. |
| `.github/workflows/ci.yml` | On every push to `main`: the version check and the build. |
| `.github/workflows/release.yml` | On a `v*` tag: CI, the zip, the GitHub release and the plugin repository. |
| `LICENSE` | The GNU Affero General Public License, version 3. |

## Where it was checked

- **Jellyfin 10.11.11** (KasuVideo's dev server): Info and Manifests answer the administrator and
  the test user; Implementations answers the administrator and refuses the test user (403);
  signed out, everything is 401.
- **Jellyfin 12.1.0** (a throwaway local container): the same DLL, built against 10.11, loads and
  answers all three, and reports 12.x's new interfaces too.
- **From a plugin repository** (2026-11-25), built with jprm 1.1.0 exactly as the release workflow
  does and served locally: on 10.11.11 through KasuVideo (Repositories › add, the Catalog shows
  it with its texts and changelog, Install, Restart, Active with its picture, and it answers);
  on 12.1.0 through the API (the catalog offers it, it installs, Active with its picture after a
  restart, and it answers).
- **Settings and guest passes** (2026-11-25), on 10.11.11 and on a throwaway 12.1.0, 43 checks
  each: the settings (administrators only, the defaults, a range refused, saved), room settings
  for any user; invites refused outside the group, for a library guests may not get, and from
  someone not listed; the preview; opening the link once (Mika-G, signed in, in the group); the
  guest's policy as above, no password sign-in, not on the login screen, can't start a group
  but can join the room's; the inviter's list without codes; ending a pass (the token stops
  working, the account is gone, they're out of the group); turning passes off ending them; the
  administrators' list; the scheduled task; the visitors' rate limit. On 12.1.0 also a guest
  account whose record was lost, deleted by the cleanup at the next startup.

## Security

Everything needs a signed-in user, except an invite's preview and opening it, which anyone
holding a link may call: those two are limited to 10 a minute per address and 60 a minute in
all, and answer the same `invalid` for every kind of bad link. Manifests are written for clients
to read, so every signed-in user may read them; the manifest's own `requiresAdmin` says which
additions KasuVideo shows only to administrators. What a plugin implements, the settings and the
list of every pass are for administrators only. Never log a token, an invite code or a manifest's
content; only a code's SHA-256 is kept. Anything a regular user may call needs `[Authorize]`;
anything about the plugins themselves or the settings, `[Authorize(Policy =
Policies.RequiresElevation)]`; the two anonymous routes, `[AllowAnonymous]`.

## History

The companion was written inside KasuVideo's repository (`services/jellyfin-companion/`) and
moved here, without that history, before its first release.

## License

Copyright (C) 2026 WaterBlueNewWorld.

This program is free software: you can redistribute it and/or modify it under the terms of the
GNU Affero General Public License as published by the Free Software Foundation, version 3 of the
License only (`AGPL-3.0-only`). It is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the full text. Everything in this repository is
under it, `image.png` included.

It is built against Jellyfin's packages (`GPL-3.0-only`); section 13 of both licenses allows the
combination.

The AGPL asks that everyone who uses a modified version over a network can get its source. The
companion answers on the network, so `Info` says where its source is (`Source`, from
`CompanionController.SourceUrl`): if you run a modified version, point it at your modified
source.
