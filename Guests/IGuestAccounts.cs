// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Everything guest passes ask of Jellyfin, in one place: who a user is, whether they're in a
/// Watch together group, and making, signing in and deleting guest accounts.
/// <see cref="JellyfinGuestAccounts"/> is the real one; the tests use a fake, so the rules in
/// <see cref="GuestPassService"/> run without a server.
/// </summary>
public interface IGuestAccounts
{
    /// <summary>Who user <paramref name="userId"/> is, or null if there's no such user.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>What the rules need to know about them.</returns>
    UserFacts? Describe(Guid userId);

    /// <summary>
    /// Whether user <paramref name="userId"/>, signed in on <paramref name="deviceId"/> (any of
    /// their sessions if that one isn't found), is a participant of Watch together group
    /// <paramref name="groupId"/>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device of the request asking.</param>
    /// <param name="groupId">The group.</param>
    /// <returns>True when Jellyfin lists them in it.</returns>
    bool IsInGroup(Guid userId, string? deviceId, Guid groupId);

    /// <summary>The name of library <paramref name="libraryId"/>, or null if there's none.</summary>
    /// <param name="libraryId">A library (collection folder).</param>
    /// <returns>Its name.</returns>
    string? LibraryName(Guid libraryId);

    /// <summary>Whether a user named <paramref name="name"/> exists.</summary>
    /// <param name="name">A user name.</param>
    /// <returns>True when it's taken.</returns>
    bool NameTaken(string name);

    /// <summary>
    /// Makes a guest's account: a hidden user that can't sign in with a password, with the
    /// guest policy (<see cref="GuestPolicy"/>), then signs it in on the visitor's device.
    /// Nothing is left behind if a step fails.
    /// </summary>
    /// <param name="request">The account to make.</param>
    /// <returns>The user and its access token.</returns>
    Task<CreatedGuest> CreateAsync(GuestAccountRequest request);

    /// <summary>
    /// Ends guest <paramref name="guestUserId"/>'s account: every device of theirs signed out
    /// (which ends their sessions, and so takes them out of their Watch together group), then
    /// the user deleted. Fine if they're gone already.
    /// </summary>
    /// <param name="guestUserId">The guest.</param>
    /// <returns>When it's done.</returns>
    Task EndAsync(Guid guestUserId);

    /// <summary>The last time guest <paramref name="guestUserId"/> did anything (UTC), or null if never.</summary>
    /// <param name="guestUserId">The guest.</param>
    /// <returns>Their last activity.</returns>
    DateTime? LastActivity(Guid guestUserId);

    /// <summary>Every user who signs in through the guest provider (<see cref="GuestAuthenticationProvider"/>).</summary>
    /// <returns>Their ids.</returns>
    IReadOnlyList<Guid> GuestUserIds();

    /// <summary>
    /// Item <paramref name="itemId"/> and what a player asks for alongside it: an episode's season
    /// and series, a season's series. What a room queues is let through with these.
    /// </summary>
    /// <param name="itemId">A queued item.</param>
    /// <returns>The item, then its family.</returns>
    IReadOnlyList<Guid> Family(Guid itemId);

    /// <summary>
    /// Whether user <paramref name="userId"/> signs in through the guest provider: a guest's
    /// account, whether or not the companion still has its pass. Cheap: the guard asks it on every
    /// request of anyone it doesn't know as a guest.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>True for a guest's account.</returns>
    bool IsGuestAccount(Guid userId);

    /// <summary>Whether user <paramref name="userId"/> may see item <paramref name="itemId"/> (their libraries, ratings, tags).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>True when Jellyfin shows it to them.</returns>
    bool CanSee(Guid userId, Guid itemId);

    /// <summary>The id of user <paramref name="userId"/>'s session on <paramref name="deviceId"/>, or null.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <returns>Jellyfin's session id.</returns>
    string? SessionIdOf(Guid userId, string? deviceId);

    /// <summary>The device a transcode with play session <paramref name="playSessionId"/> runs for, or null if none runs.</summary>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Its device.</returns>
    string? JobDevice(string playSessionId);

    /// <summary>Whether any user already has a device (a token) or a session with id <paramref name="deviceId"/>.</summary>
    /// <param name="deviceId">A device id.</param>
    /// <returns>True when it's in use.</returns>
    bool DeviceInUse(string deviceId);

    /// <summary>The ids of item <paramref name="itemId"/>'s own media sources (its versions).</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The sources' ids, as Jellyfin writes them.</returns>
    IReadOnlyList<string> MediaSourcesOf(Guid itemId);

    /// <summary>
    /// Sets SyncPlay's "ignore wait" on user <paramref name="userId"/>'s session on
    /// <paramref name="deviceId"/>: the group never waits for them (a guest's stall never holds the
    /// room). Nothing if they have no session.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    void IgnoreWait(Guid userId, string? deviceId);
}

/// <summary>What the rules need to know about a user.</summary>
/// <param name="Id">Their id.</param>
/// <param name="Name">Their user name.</param>
/// <param name="IsAdmin">Whether they're an administrator.</param>
/// <param name="IsGuest">Whether they're a guest (their provider is the guest provider).</param>
/// <param name="AllLibraries">Whether they may see every library.</param>
/// <param name="Libraries">The libraries they may see, when not all.</param>
public sealed record UserFacts(Guid Id, string Name, bool IsAdmin, bool IsGuest, bool AllLibraries, IReadOnlyList<Guid> Libraries);

/// <summary>A guest account to make (<see cref="IGuestAccounts.CreateAsync"/>).</summary>
/// <param name="UserName">The user name (the nickname and <see cref="GuestNames.Suffix"/>).</param>
/// <param name="LibraryIds">The libraries the guest may see.</param>
/// <param name="Transcoding">Whether the guest may transcode.</param>
/// <param name="BitrateMbps">The most the guest may stream, in Mbit/s.</param>
/// <param name="DeviceId">The visitor's device, as KasuVideo names it.</param>
/// <param name="DeviceName">The device's name.</param>
/// <param name="AppVersion">KasuVideo's version.</param>
/// <param name="RemoteEndPoint">The visitor's address, for Jellyfin's session.</param>
public sealed record GuestAccountRequest(
    string UserName,
    IReadOnlyList<Guid> LibraryIds,
    bool Transcoding,
    int BitrateMbps,
    string DeviceId,
    string DeviceName,
    string AppVersion,
    string? RemoteEndPoint);

/// <summary>A guest account made and signed in.</summary>
/// <param name="UserId">The guest's user id.</param>
/// <param name="UserName">Their user name.</param>
/// <param name="AccessToken">Their access token (never logged).</param>
/// <param name="ServerId">The server's id.</param>
public sealed record CreatedGuest(Guid UserId, string UserName, string AccessToken, string ServerId);
