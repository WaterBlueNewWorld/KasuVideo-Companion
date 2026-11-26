// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// One guest pass, from the invite link a room's owner made to the end of the guest's account.
/// Kept in <see cref="GuestPassStore"/>. The link's code itself is never kept, only its hash.
/// </summary>
public sealed class GuestPass
{
    /// <summary>Gets or sets the pass's id (also the invite's).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the SHA-256 of the invite code, in lower-case hex (<see cref="GuestCodes.Hash"/>).</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Gets or sets who made the invite.</summary>
    public Guid InviterId { get; set; }

    /// <summary>Gets or sets the Watch together group the invite was made in: 32 lower-case hex digits.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Gets or sets the libraries the guest may see (what SyncPlay's queue check needs).</summary>
    public Guid[] LibraryIds { get; set; } = [];

    /// <summary>Gets or sets how long the pass lasts once the link is opened, in hours.</summary>
    public int Hours { get; set; }

    /// <summary>Gets or sets when the invite was made (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets until when the link may be opened (UTC).</summary>
    public DateTime RedeemBy { get; set; }

    /// <summary>Gets or sets when the link was opened (UTC); null while it hasn't been.</summary>
    public DateTime? RedeemedAt { get; set; }

    /// <summary>Gets or sets when the pass ends at the latest (UTC): <see cref="RedeemedAt"/> plus <see cref="Hours"/>.</summary>
    public DateTime? EndsAt { get; set; }

    /// <summary>Gets or sets the guest's Jellyfin user, once made.</summary>
    public Guid? GuestUserId { get; set; }

    /// <summary>Gets or sets the guest's user name (their nickname and <see cref="GuestNames.Suffix"/>).</summary>
    public string? GuestName { get; set; }

    /// <summary>Gets or sets when the pass ended (UTC); null while it's live.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Gets or sets why it ended (<see cref="EndReasons"/>).</summary>
    public string? EndReason { get; set; }

    /// <summary>Gets where the pass stands, regardless of the time: pending, active or ended.</summary>
    [JsonIgnore]
    public GuestPassState State => EndedAt is not null
        ? GuestPassState.Ended
        : RedeemedAt is null ? GuestPassState.Pending : GuestPassState.Active;
}

/// <summary>Where a pass stands.</summary>
public enum GuestPassState
{
    /// <summary>The link hasn't been opened yet.</summary>
    Pending,

    /// <summary>The guest's account exists.</summary>
    Active,

    /// <summary>The pass is over; the guest's account, if any, is gone.</summary>
    Ended,
}

/// <summary>Why a pass ended, as kept in <see cref="GuestPass.EndReason"/> and answered to clients.</summary>
public static class EndReasons
{
    /// <summary>The link wasn't opened in time.</summary>
    public const string LinkExpired = "link-expired";

    /// <summary>The pass reached its end time.</summary>
    public const string Expired = "expired";

    /// <summary>The guest had no session for too long.</summary>
    public const string Idle = "idle";

    /// <summary>The inviter (or the room's owner, through KasuVideo) ended it.</summary>
    public const string Ended = "ended";

    /// <summary>An administrator ended it.</summary>
    public const string Admin = "admin";

    /// <summary>The inviter may no longer invite (removed from the list, or gone).</summary>
    public const string InviterRemoved = "inviter-removed";

    /// <summary>An administrator turned guest passes off.</summary>
    public const string TurnedOff = "turned-off";

    /// <summary>The companion was uninstalled.</summary>
    public const string Uninstalled = "uninstalled";

    /// <summary>Making the guest's account failed.</summary>
    public const string Failed = "failed";
}
