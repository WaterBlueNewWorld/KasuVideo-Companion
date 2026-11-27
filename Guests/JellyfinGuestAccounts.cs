// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;

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
    private readonly ITranscodeManager _transcodes;
    private readonly IMediaSourceManager _sources;

    // Accounts known to be guests' (a guest's account stays one until it's deleted).
    private readonly ConcurrentDictionary<Guid, bool> _guestAccounts = new();

    /// <summary>Creates it (Jellyfin does, with its services).</summary>
    /// <param name="users">Users.</param>
    /// <param name="sessions">Sessions and sign-ins.</param>
    /// <param name="devices">Devices (each holds an access token).</param>
    /// <param name="syncPlay">Watch together groups.</param>
    /// <param name="libraries">Libraries, for their names.</param>
    /// <param name="transcodes">Running transcodes, for whose they are.</param>
    /// <param name="sources">Items' media sources (their versions).</param>
    public JellyfinGuestAccounts(
        IUserManager users,
        ISessionManager sessions,
        IDeviceManager devices,
        ISyncPlayManager syncPlay,
        ILibraryManager libraries,
        ITranscodeManager transcodes,
        IMediaSourceManager sources)
    {
        _users = users;
        _sessions = sessions;
        _devices = devices;
        _syncPlay = syncPlay;
        _libraries = libraries;
        _transcodes = transcodes;
        _sources = sources;
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

        // Made under a random name nobody knows, and named after the nickname only once its policy
        // is the guest's: a new user starts with Jellyfin's default policy and provider, which take
        // an empty password, and the visitor knows the name they asked for (G8).
        var hidden = "kvguest-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var user = await _users.CreateUserAsync(hidden).ConfigureAwait(false);
        try
        {
            var policy = _users.GetUserDto(user, request.RemoteEndPoint).Policy;
            GuestPolicy.Apply(policy, request.LibraryIds, request.Transcoding, request.BitrateMbps);
            await _users.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            try
            {
                await RenameAsync(user, hidden, request.UserName).ConfigureAwait(false);
            }
            catch (ArgumentException) when (NameTaken(request.UserName))
            {
                throw new GuestNameTakenException(request.UserName);
            }

            var named = _users.GetUserById(user.Id) ?? user;
            var signedIn = await _sessions.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = named.Id,
                Username = named.Username,
                App = "KasuVideo",
                AppVersion = request.AppVersion,
                DeviceId = request.DeviceId,
                DeviceName = request.DeviceName,
                RemoteEndPoint = request.RemoteEndPoint,
            }).ConfigureAwait(false);
            _guestAccounts[named.Id] = true;
            return new CreatedGuest(named.Id, named.Username, signedIn.AccessToken, signedIn.ServerId);
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

        _guestAccounts.TryRemove(guestUserId, out _);
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
    public IReadOnlyList<Guid> Family(Guid itemId)
    {
        var family = new List<Guid> { itemId };
        switch (itemId == Guid.Empty ? null : _libraries.GetItemById(itemId))
        {
            case Episode episode:
                family.Add(episode.SeasonId);
                family.Add(episode.SeriesId);
                break;
            case Season season:
                family.Add(season.SeriesId);
                break;
        }

        return family.Where(id => id != Guid.Empty).Distinct().ToList();
    }

    /// <inheritdoc />
    public bool IsGuestAccount(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        if (_guestAccounts.ContainsKey(userId))
        {
            return true;
        }

        // Only a yes is kept: an account being made isn't a guest's yet, and then becomes one.
        var guest = _users.GetUserById(userId)?.AuthenticationProviderId == GuestAuthenticationProvider.ProviderId;
        if (guest)
        {
            _guestAccounts[userId] = true;
        }

        return guest;
    }

    /// <inheritdoc />
    public bool CanSee(Guid userId, Guid itemId) =>
        userId != Guid.Empty && itemId != Guid.Empty && _libraries.GetItemById<BaseItem>(itemId, userId) is not null;

    /// <inheritdoc />
    public string? SessionIdOf(Guid userId, string? deviceId) =>
        _sessions.Sessions.FirstOrDefault(s => s.UserId == userId && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <inheritdoc />
    public string? JobDevice(string playSessionId) => _transcodes.GetTranscodingJob(playSessionId)?.DeviceId;

    /// <inheritdoc />
    public bool DeviceInUse(string deviceId) =>
        _devices.GetDevices(new DeviceQuery { DeviceId = deviceId }).Items.Count > 0
        || _sessions.Sessions.Any(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<string> MediaSourcesOf(Guid itemId) =>
        itemId == Guid.Empty || _libraries.GetItemById(itemId) is not { } item
            ? []
            : _sources.GetStaticMediaSources(item, false).Select(s => s.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

    /// <inheritdoc />
    public void IgnoreWait(Guid userId, string? deviceId)
    {
        var session = _sessions.Sessions.FirstOrDefault(s => s.UserId == userId && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (session is not null)
        {
            _syncPlay.HandleRequest(session, new IgnoreWaitGroupRequest(true), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GuestUserIds() =>
        AllUsers().Where(u => u.AuthenticationProviderId == GuestAuthenticationProvider.ProviderId).Select(u => u.Id).ToList();

    /// <summary>
    /// Renames <paramref name="user"/>: <c>RenameUser(Guid, string, string)</c> on 10.11.11 and
    /// 12.x, <c>RenameUser(User, string)</c> on 10.11.0 (which this build compiles against). Asked
    /// by reflection, so the same build runs on each.
    /// </summary>
    private async Task RenameAsync(User user, string oldName, string newName)
    {
        var type = _users.GetType();
        var byId = type.GetMethod("RenameUser", [typeof(Guid), typeof(string), typeof(string)]);
        var byUser = byId is null ? type.GetMethod("RenameUser", [typeof(User), typeof(string)]) : null;
        if (byId is null && byUser is null)
        {
            throw new MissingMethodException(type.FullName, "RenameUser");
        }

        try
        {
            var call = byId is not null ? byId.Invoke(_users, [user.Id, oldName, newName]) : byUser!.Invoke(_users, [user, newName]);
            await ((Task)call!).ConfigureAwait(false);
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
    }

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
