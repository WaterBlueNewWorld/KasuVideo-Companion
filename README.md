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
A small Jellyfin server plugin made for KasuVideo, a web frontend for Jellyfin. It answers two
questions Jellyfin itself doesn't answer for KasuVideo:

1. **What do the server's plugins add to KasuVideo?** Plugins describe their additions (entries
   in KasuVideo's item menus, pages in its Dashboard) in a manifest: a plugin page named
   `*.kasuvideo.json`. Jellyfin lists plugin pages to administrators only, so without the
   companion only an administrator's KasuVideo finds them. The companion hands them to every
   signed-in user.
2. **What does each plugin implement?** Jellyfin finds a plugin's metadata providers, image
   providers, scheduled tasks, services and so on through its interfaces, and never tells a
   client. The companion reads them from the loaded assemblies, for administrators.

It has no settings and no pages, and changes nothing on the server. KasuVideo works without it:
plugins' additions then show for administrators only, and the "what it does for the server"
panel isn't there.

The manifest format is KasuVideo's, documented in its repository (`docs/PLUGIN_MANIFEST.md`);
the companion only finds the manifests and hands them over, whatever they contain.

## What it answers

All under the server's base URL (for example `/jellyfin`, if the server has that Base URL).

| Route | Who | What |
| --- | --- | --- |
| `GET /KasuVideo/Companion/Info` | Any signed-in user | `{ "Name", "Version", "Contract": 1, "Source" }`. KasuVideo asks it first: a 404 means no companion. `Source` is where this version's source code is (see [License](#license)). |
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

## Files

| File | What it is |
| --- | --- |
| `Plugin.cs`, `Api/CompanionController.cs` | The plugin and its three routes. |
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

## Security

Everything needs a signed-in user. Manifests are written for clients to read, so every signed-in
user may read them; the manifest's own `requiresAdmin` says which additions KasuVideo shows only
to administrators. What a plugin implements is for administrators only. Never log a token or a
manifest's content. Anything a regular user may call needs `[Authorize]`; anything about the
plugins themselves, `[Authorize(Policy = "RequiresElevation")]`.

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
