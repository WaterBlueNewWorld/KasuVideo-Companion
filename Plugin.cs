// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.KasuVideoCompanion;

/// <summary>
/// The KasuVideo companion. It has no settings and no pages: it only answers KasuVideo's
/// questions about the other plugins (<see cref="Api.CompanionController"/>).
/// </summary>
public class Plugin : BasePlugin<BasePluginConfiguration>
{
    /// <summary>Stable plugin id. Never change it: Jellyfin keys installs and settings by it.</summary>
    public static readonly Guid PluginId = Guid.Parse("03029e1f-b932-4b53-b101-aa9fd84158a3");

    /// <summary>Creates the plugin (Jellyfin does, when it loads it).</summary>
    /// <param name="applicationPaths">The server's paths.</param>
    /// <param name="xmlSerializer">Reads and writes the settings file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "KasuVideo Companion";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Tells KasuVideo what other plugins add to it, and its administrators what each plugin implements.";
}
