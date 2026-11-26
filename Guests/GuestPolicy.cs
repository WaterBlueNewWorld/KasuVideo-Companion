// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// The policy a guest's account gets. A new Jellyfin user may do almost everything (every
/// library, downloads, sharing, Live TV and its management, controlling others' devices), so
/// every one of those is turned off here; a guest only watches what their room plays.
/// </summary>
public static class GuestPolicy
{
    /// <summary>
    /// Turns <paramref name="policy"/> (as Jellyfin made it for the new user) into a guest's.
    /// It is changed in place, never made fresh: its provider fields are required, and the
    /// password reset provider is kept as Jellyfin set it.
    /// </summary>
    /// <param name="policy">The new user's policy.</param>
    /// <param name="libraryIds">The libraries the guest may see.</param>
    /// <param name="transcoding">Whether they may transcode.</param>
    /// <param name="bitrateMbps">The most they may stream, in Mbit/s.</param>
    public static void Apply(UserPolicy policy, IReadOnlyList<Guid> libraryIds, bool transcoding, int bitrateMbps)
    {
        // Who they are: not an administrator, not on the login screen, signing in only by pass.
        policy.IsAdministrator = false;
        policy.IsHidden = true;
        policy.IsDisabled = false;
        policy.AuthenticationProviderId = GuestAuthenticationProvider.ProviderId;
        policy.LoginAttemptsBeforeLockout = -1;
        policy.AccessSchedules = [];

        // What they see: the invite's libraries, nothing else.
        policy.EnableAllFolders = false;
        policy.EnabledFolders = libraryIds.ToArray();
        policy.BlockedMediaFolders = [];
        policy.EnableAllChannels = false;
        policy.EnabledChannels = [];
        policy.BlockedChannels = [];
        policy.AllowedTags = [];
        policy.BlockedTags = [];

        // Watch together: join their room's group; never make one.
        policy.SyncPlayAccess = SyncPlayUserAccessType.JoinGroups;

        // Watching: one session, from anywhere, within the server's limits for guests.
        policy.MaxActiveSessions = 1;
        policy.EnableRemoteAccess = true;
        policy.EnableMediaPlayback = true;
        policy.EnableAudioPlaybackTranscoding = transcoding;
        policy.EnableVideoPlaybackTranscoding = transcoding;
        policy.EnablePlaybackRemuxing = true;
        policy.ForceRemoteSourceTranscoding = false;
        policy.RemoteClientBitrateLimit = bitrateMbps * 1_000_000;

        // Nothing else.
        policy.EnableContentDownloading = false;
        policy.EnableSyncTranscoding = false;
        policy.EnableMediaConversion = false;
        policy.EnablePublicSharing = false;
        policy.EnableSharedDeviceControl = false;
        policy.EnableRemoteControlOfOtherUsers = false;
        policy.EnableUserPreferenceAccess = false;
        policy.EnableLiveTvAccess = false;
        policy.EnableLiveTvManagement = false;
        policy.EnableContentDeletion = false;
        policy.EnableContentDeletionFromFolders = [];
        policy.EnableCollectionManagement = false;
        policy.EnableSubtitleManagement = false;
        policy.EnableLyricManagement = false;
    }
}
