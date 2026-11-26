// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Configuration;
using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.KasuVideoCompanion;

/// <summary>
/// The KasuVideo companion. It answers KasuVideo's questions about the other plugins
/// (<see cref="Api.CompanionController"/>), keeps the settings for Watch together rooms and guest
/// passes (<see cref="PluginConfiguration"/>, changed from KasuVideo's own page for the
/// companion), and makes, confines and deletes guests' accounts (<see cref="GuestPassService"/>).
/// It has no page in Jellyfin's own dashboard.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>Stable plugin id. Never change it: Jellyfin keys installs and settings by it.</summary>
    public static readonly Guid PluginId = Guid.Parse("03029e1f-b932-4b53-b101-aa9fd84158a3");

    /// <summary>Creates the plugin (Jellyfin does, when it loads it).</summary>
    /// <param name="applicationPaths">The server's paths.</param>
    /// <param name="xmlSerializer">Reads and writes the settings file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the plugin Jellyfin loaded (its settings and data folder).</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "KasuVideo Companion";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "KasuVideo's side of the server: what other plugins add to KasuVideo, Watch together's settings, and guest passes.";

    /// <summary>
    /// Uninstalling ends every guest pass, which deletes every guest's account: without the
    /// companion nothing would confine their tokens.
    /// </summary>
    public override void OnUninstalling()
    {
        GuestPassService.Current?.EndAllAsync(EndReasons.Uninstalled).GetAwaiter().GetResult();
        base.OnUninstalling();
    }
}
