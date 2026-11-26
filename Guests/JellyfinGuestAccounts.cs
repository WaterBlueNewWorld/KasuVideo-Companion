// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// <see cref="IGuestAccounts"/> on Jellyfin's own services. Built against Jellyfin 10.11; it
/// avoids what 12.x removed (<c>IUserManager.Users</c>, the user-taking <c>ChangePassword</c>),
/// listing users by reflection instead, so the same build runs on both.
/// </summary>
public sealed class JellyfinGuestAccounts : IGuestAccounts
{
    private readonly IUserManager _users;
    private readonly ISessionManager _sessions;
    private readonly IDeviceManager _devices;
    private readonly ISyncPlayManager _syncPlay;
    private readonly ILibraryManager _libraries;

    /// <summary>Creates it (Jellyfin does, with its services).</summary>
    /// <param name="users">Users.</param>
    /// <param name="sessions">Sessions and sign-ins.</param>
    /// <param name="devices">Devices (each holds an access token).</param>
    /// <param name="syncPlay">Watch together groups.</param>
    /// <param name="libraries">Libraries, for their names.</param>
    public JellyfinGuestAccounts(
        IUserManager users,
        ISessionManager sessions,
        IDeviceManager devices,
        ISyncPlayManager syncPlay,
        ILibraryManager libraries)
    {
        _users = users;
        _sessions = sessions;
        _devices = devices;
        _syncPlay = syncPlay;
        _libraries = libraries;
    }

    /// <inheritdoc />
    public UserFacts? Describe(Guid userId)
    {
        if (userId == Guid.Empty || _users.GetUserById(userId) is not { } user)
        {
            return null;
        }

        var policy = _users.GetUserDto(user, null).Policy;
        return new UserFacts(
            user.Id,
            user.Username,
            policy.IsAdministrator,
            policy.AuthenticationProviderId == GuestAuthenticationProvider.ProviderId,
            policy.EnableAllFolders,
            policy.EnabledFolders ?? []);
    }

    /// <inheritdoc />
    public bool IsInGroup(Guid userId, string? deviceId, Guid groupId)
    {
        if (userId == Guid.Empty || _users.GetUserById(userId) is not { } user)
        {
            return false;
        }

        var theirs = _sessions.Sessions.Where(s => s.UserId == userId).ToList();
        var session = theirs.FirstOrDefault(s => s.DeviceId == deviceId) ?? theirs.FirstOrDefault();
        if (session is null)
        {
            return false;
        }

        // Jellyfin lists a group's participants by user name.
        return _syncPlay.GetGroup(session, groupId)?.Participants?.Contains(user.Username) == true;
    }

    /// <inheritdoc />
    public string? LibraryName(Guid libraryId) =>
        libraryId == Guid.Empty ? null : _libraries.GetItemById(libraryId)?.Name;

    /// <inheritdoc />
    public bool NameTaken(string name) => _users.GetUserByName(name) is not null;

    /// <inheritdoc />
    public async Task<CreatedGuest> CreateAsync(GuestAccountRequest request)
    {
        if (NameTaken(request.UserName))
        {
            throw new GuestNameTakenException(request.UserName);
        }

        User user;
        try
        {
            user = await _users.CreateUserAsync(request.UserName).ConfigureAwait(false);
        }
        catch (ArgumentException) when (NameTaken(request.UserName))
        {
            throw new GuestNameTakenException(request.UserName);
        }

        try
        {
            var policy = _users.GetUserDto(user, request.RemoteEndPoint).Policy;
            GuestPolicy.Apply(policy, request.LibraryIds, request.Transcoding, request.BitrateMbps);
            await _users.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            var signedIn = await _sessions.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = user.Id,
                Username = user.Username,
                App = "KasuVideo",
                AppVersion = request.AppVersion,
                DeviceId = request.DeviceId,
                DeviceName = request.DeviceName,
                RemoteEndPoint = request.RemoteEndPoint,
            }).ConfigureAwait(false);
            return new CreatedGuest(user.Id, user.Username, signedIn.AccessToken, signedIn.ServerId);
        }
        catch
        {
            await EndAsync(user.Id).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task EndAsync(Guid guestUserId)
    {
        // Jellyfin's DeleteUserAsync leaves the user's devices (their tokens): sign them out first.
        foreach (var device in _devices.GetDevices(new DeviceQuery { UserId = guestUserId }).Items)
        {
            await _sessions.Logout(device).ConfigureAwait(false);
        }

        if (_users.GetUserById(guestUserId) is not null)
        {
            await _users.DeleteUserAsync(guestUserId).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public DateTime? LastActivity(Guid guestUserId)
    {
        DateTime? last = null;
        foreach (var device in _devices.GetDevices(new DeviceQuery { UserId = guestUserId }).Items)
        {
            last = Later(last, device.DateLastActivity);
        }

        foreach (var session in _sessions.Sessions.Where(s => s.UserId == guestUserId))
        {
            last = Later(last, session.LastActivityDate);
        }

        return last;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GuestUserIds() =>
        AllUsers().Where(u => u.AuthenticationProviderId == GuestAuthenticationProvider.ProviderId).Select(u => u.Id).ToList();

    /// <summary>
    /// Every user: <c>IUserManager.Users</c> on 10.11, <c>GetUsers()</c> on 12.4 and later. Asked by
    /// reflection, so this build doesn't fail to load on a server that has only the other.
    /// </summary>
    private IEnumerable<User> AllUsers()
    {
        var type = _users.GetType();
        if (type.GetProperty("Users")?.GetValue(_users) is IEnumerable<User> users)
        {
            return users;
        }

        if (type.GetMethod("GetUsers", Type.EmptyTypes)?.Invoke(_users, null) is IEnumerable<User> listed)
        {
            return listed;
        }

        return [];
    }

    private static DateTime? Later(DateTime? a, DateTime? b) =>
        b is null || b.Value == default ? a : a is null || b.Value > a.Value ? b : a;
}
