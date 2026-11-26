// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.KasuVideoCompanion.Configuration;

/// <summary>
/// The companion's settings, kept by Jellyfin in the plugin's XML file. Administrators change
/// them from KasuVideo's own page for the companion (<c>/KasuVideo/Companion/Settings</c>), which
/// checks every value against <see cref="SettingsBounds"/>. The defaults are the KasuVideo
/// owner's choices (2026-11-25).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether guest passes are on. Off until an administrator turns them on.</summary>
    public bool GuestPassesEnabled { get; set; }

    /// <summary>Gets or sets the users who may invite guests (to rooms they own).</summary>
    public Guid[] InviterIds { get; set; } = [];

    /// <summary>Gets or sets the libraries a guest may ever be given; an invite picks among them.</summary>
    public Guid[] GuestLibraryIds { get; set; } = [];

    /// <summary>Gets or sets how many passes one inviter may have open at once (links not yet used, and guests).</summary>
    public int MaxLivePassesPerInviter { get; set; } = 3;

    /// <summary>Gets or sets how long an invite link may wait to be opened, in minutes.</summary>
    public int LinkMinutes { get; set; } = 10;

    /// <summary>Gets or sets the shortest pass an inviter may give, in hours (also the default).</summary>
    public int PassHoursMin { get; set; } = 3;

    /// <summary>Gets or sets the longest pass an inviter may give, in hours.</summary>
    public int PassHoursMax { get; set; } = 6;

    /// <summary>Gets or sets how long a guest may go without a session before the pass ends, in minutes.</summary>
    public int IdleMinutes { get; set; } = 20;

    /// <summary>Gets or sets a value indicating whether guests may transcode (most browsers need it for HEVC).</summary>
    public bool GuestTranscoding { get; set; } = true;

    /// <summary>Gets or sets the most a guest may stream, in Mbit/s.</summary>
    public int GuestBitrateMbps { get; set; } = 8;

    /// <summary>Gets or sets the most people one Watch together room takes, its owner included.</summary>
    public int RoomMaxMembers { get; set; } = 16;

    /// <summary>Gets or sets the most guests one room takes at once.</summary>
    public int RoomMaxGuests { get; set; } = 4;
}
