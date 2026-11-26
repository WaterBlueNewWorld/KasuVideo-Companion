// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>A refusal, with the code the routes answer in <c>{"Error": …}</c>.</summary>
/// <param name="Code">The code (<see cref="GuestErrors"/>).</param>
public sealed record GuestError(string Code);

/// <summary>The refusals guest passes give.</summary>
public static class GuestErrors
{
    /// <summary>Guest passes are off.</summary>
    public static readonly GuestError Off = new("off");

    /// <summary>The caller isn't listed as an inviter.</summary>
    public static readonly GuestError NotInviter = new("not-inviter");

    /// <summary>A guest can't invite.</summary>
    public static readonly GuestError Guest = new("guest");

    /// <summary>The group id isn't one.</summary>
    public static readonly GuestError BadGroup = new("bad-group");

    /// <summary>Jellyfin doesn't list the caller in that group.</summary>
    public static readonly GuestError NotInGroup = new("not-in-group");

    /// <summary>No libraries, or one the caller can't see or guests may not get.</summary>
    public static readonly GuestError BadLibraries = new("bad-libraries");

    /// <summary>The pass's length is outside the settings' range.</summary>
    public static readonly GuestError BadHours = new("bad-hours");

    /// <summary>The caller already has as many open passes as they may.</summary>
    public static readonly GuestError TooMany = new("too-many");

    /// <summary>The link isn't (or is no longer) valid. Never says why.</summary>
    public static readonly GuestError Invalid = new("invalid");

    /// <summary>The nickname has nothing usable in it.</summary>
    public static readonly GuestError BadName = new("bad-name");

    /// <summary>Every user name for that nickname is taken.</summary>
    public static readonly GuestError NameTaken = new("name-taken");

    /// <summary>The device id isn't one KasuVideo would send.</summary>
    public static readonly GuestError BadDevice = new("bad-device");

    /// <summary>Making the guest's account failed; the link may be tried again.</summary>
    public static readonly GuestError Failed = new("failed");

    /// <summary>No such pass.</summary>
    public const string NotFound = "not-found";

    /// <summary>Only the pass's inviter or an administrator may end it.</summary>
    public const string Forbidden = "forbidden";
}

/// <summary>A value, or why there isn't one.</summary>
/// <typeparam name="T">The value.</typeparam>
/// <param name="Value">The value, when it worked.</param>
/// <param name="Error">The refusal's code, when it didn't.</param>
public readonly record struct GuestResult<T>(T? Value, string? Error)
    where T : class
{
    /// <summary>Gets a value indicating whether it worked.</summary>
    public bool Ok => Error is null;

    /// <summary>It worked.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator GuestResult<T>(T value) => new(value, null);

    /// <summary>It didn't.</summary>
    /// <param name="error">Why.</param>
    public static implicit operator GuestResult<T>(GuestError error) => new(null, error.Code);
}

/// <summary>Thrown by <see cref="IGuestAccounts.CreateAsync"/> when the user name was taken meanwhile.</summary>
public sealed class GuestNameTakenException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="name">The name.</param>
    public GuestNameTakenException(string name)
        : base($"The user name {name} is taken.")
    {
    }
}

/// <summary>What <c>GET /KasuVideo/Guests/Status</c> answers.</summary>
/// <param name="Enabled">Whether guest passes are on.</param>
/// <param name="CanInvite">Whether the caller may invite.</param>
/// <param name="Libraries">The libraries the caller may give a guest.</param>
/// <param name="LivePasses">How many open passes the caller has.</param>
/// <param name="MaxLivePasses">How many they may have.</param>
/// <param name="LinkMinutes">How long a link waits to be opened.</param>
/// <param name="PassHoursMin">The shortest pass (and the default).</param>
/// <param name="PassHoursMax">The longest pass.</param>
public sealed record GuestStatus(
    bool Enabled,
    bool CanInvite,
    IReadOnlyList<LibraryChoice> Libraries,
    int LivePasses,
    int MaxLivePasses,
    int LinkMinutes,
    int PassHoursMin,
    int PassHoursMax);

/// <summary>A library an inviter may give.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record LibraryChoice(Guid Id, string Name);

/// <summary>An invite just made: the only time its code is given.</summary>
/// <param name="Id">The pass's id.</param>
/// <param name="Code">The code for the link (<c>/guest#code</c>).</param>
/// <param name="RedeemBy">Until when the link may be opened (UTC).</param>
/// <param name="Hours">How long the pass lasts once opened.</param>
public sealed record CreatedInvite(Guid Id, string Code, DateTime RedeemBy, int Hours);

/// <summary>A pass as inviters and administrators see it (never its code).</summary>
/// <param name="Id">The pass.</param>
/// <param name="InviterId">Who made it.</param>
/// <param name="InviterName">Their name (administrators' list only).</param>
/// <param name="GroupId">The group it was made in.</param>
/// <param name="State"><c>pending</c>, <c>active</c>, <c>expired</c> (due, not yet cleaned up) or <c>ended</c>.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="RedeemBy">Until when its link may be opened.</param>
/// <param name="Hours">Its length once opened.</param>
/// <param name="EndsAt">When it ends at the latest, once opened.</param>
/// <param name="GuestUserId">The guest's user, once opened.</param>
/// <param name="GuestName">The guest's name, once opened.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="EndReason">Why it ended.</param>
public sealed record PassView(
    Guid Id,
    Guid InviterId,
    string? InviterName,
    string GroupId,
    string State,
    DateTime CreatedAt,
    DateTime RedeemBy,
    int Hours,
    DateTime? EndsAt,
    Guid? GuestUserId,
    string? GuestName,
    DateTime? EndedAt,
    string? EndReason);

/// <summary>What the invite page shows before the visitor opens the link.</summary>
/// <param name="InviterName">Who invited them.</param>
/// <param name="RedeemBy">Until when the link works.</param>
/// <param name="Hours">How long the pass will last.</param>
public sealed record InvitePreview(string InviterName, DateTime RedeemBy, int Hours);

/// <summary>A guest's new session, from an opened link.</summary>
/// <param name="AccessToken">Their access token (never logged).</param>
/// <param name="UserId">Their user.</param>
/// <param name="UserName">Their user name.</param>
/// <param name="ServerId">The server's id.</param>
/// <param name="GroupId">The Watch together group their room plays in.</param>
/// <param name="EndsAt">When the pass ends at the latest (UTC).</param>
public sealed record RedeemedPass(string AccessToken, Guid UserId, string UserName, string ServerId, string GroupId, DateTime EndsAt);

/// <summary>What <c>GET /KasuVideo/Guests/Me</c> answers a guest (the rooms Worker asks it too).</summary>
/// <param name="PassId">Their pass.</param>
/// <param name="GroupId">The group the invite was made in.</param>
/// <param name="InviterId">Who invited them.</param>
/// <param name="InviterName">Their name.</param>
/// <param name="EndsAt">When the pass ends at the latest (UTC).</param>
public sealed record GuestMe(Guid PassId, string GroupId, Guid InviterId, string? InviterName, DateTime EndsAt);

/// <summary>What a cleanup did.</summary>
/// <param name="Ended">Passes ended.</param>
/// <param name="Orphans">Leftover guest accounts deleted.</param>
/// <param name="Forgotten">Ended passes forgotten.</param>
public sealed record CleanupReport(int Ended, int Orphans, int Forgotten);

/// <summary>What <c>GET /KasuVideo/Companion/RoomSettings</c> answers.</summary>
/// <param name="MaxMembers">The most people one room takes, its owner included.</param>
/// <param name="MaxGuests">The most guests one room takes at once.</param>
public sealed record RoomSettings(int MaxMembers, int MaxGuests);
