// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// What the guard writes into <see cref="GroupTracker"/> after a SyncPlay request ran, and only
/// what Jellyfin really did: Jellyfin answers a join or a queue 204 even when it refused it (no
/// such group, no access to its queue, not in it). The penetration test (G8) saw a user outside
/// the room, who couldn't even see the film, "queue" it into the room that way, and the room's
/// guest then open it.
/// <list type="bullet">
/// <item>A join counts once Jellyfin lists the session in the group.</item>
/// <item>A queue counts for a session Jellyfin has in the group it's filed under, and only the
/// items that session's user may see.</item>
/// </list>
/// </summary>
public sealed class GroupRecorder
{
    private readonly GroupTracker _tracker;
    private readonly IGuestAccounts _accounts;

    /// <summary>Creates the recorder.</summary>
    /// <param name="tracker">What the companion knows of groups.</param>
    /// <param name="accounts">Jellyfin's side.</param>
    public GroupRecorder(GroupTracker tracker, IGuestAccounts accounts)
    {
        _tracker = tracker;
        _accounts = accounts;
    }

    /// <summary>A session made group <paramref name="groupId"/> (Jellyfin's answer says so).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">Their device.</param>
    /// <param name="groupId">The group made.</param>
    public void Created(Guid userId, string? deviceId, Guid groupId) => _tracker.Joined(userId, deviceId, groupId.ToString("N"));

    /// <summary>A session asked to join <paramref name="groupId"/>; counts only if Jellyfin has them in it.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">Their device.</param>
    /// <param name="groupId">The group.</param>
    /// <returns>True when they're in it.</returns>
    public bool Joined(Guid userId, string? deviceId, Guid groupId)
    {
        if (!_accounts.IsInGroup(userId, deviceId, groupId))
        {
            return false;
        }

        _tracker.Joined(userId, deviceId, groupId.ToString("N"));
        return true;
    }

    /// <summary>The session left its group.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">Their device.</param>
    public void Left(Guid userId, string? deviceId) => _tracker.Left(userId, deviceId);

    /// <summary>A session queued <paramref name="items"/>: what they may see is noted under their group, if Jellyfin has them in it.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">Their device.</param>
    /// <param name="items">The items asked for.</param>
    public void Queued(Guid userId, string? deviceId, IEnumerable<Guid> items)
    {
        if (_tracker.GroupOf(userId, deviceId) is not { } group || !_accounts.IsInGroup(userId, deviceId, Guid.ParseExact(group, "N")))
        {
            return;
        }

        var family = items.Where(item => item != Guid.Empty && _accounts.CanSee(userId, item)).SelectMany(_accounts.Family).ToList();
        if (family.Count > 0)
        {
            _tracker.Queued(group, family);
        }
    }
}
