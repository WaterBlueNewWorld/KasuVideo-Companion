// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// What the companion knows of Watch together groups, learnt from the SyncPlay requests the
/// guard (<see cref="GuestGuard"/>) sees, everyone's: which group each session is in (making,
/// joining, leaving a group; a session's end), and what each group has queued. Jellyfin's own
/// SyncPlay manager tells a plugin neither. Kept in memory: groups end with the server, and so
/// does this.
/// </summary>
public sealed class GroupTracker
{
    /// <summary>The most items remembered per group (the oldest go first).</summary>
    public const int MaxItemsPerGroup = 2000;

    private readonly object _lock = new();
    private readonly Dictionary<(Guid User, string Device), string> _sessions = [];
    private readonly Dictionary<string, LinkedList<Guid>> _queued = new(StringComparer.Ordinal);

    /// <summary>Session (<paramref name="userId"/>, <paramref name="deviceId"/>) made or joined group <paramref name="groupId"/>.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">Their device.</param>
    /// <param name="groupId">The group, in any spelling.</param>
    public void Joined(Guid userId, string? deviceId, string groupId)
    {
        if (GuestPassService.NormalizeGroupId(groupId) is not { } group)
        {
            return;
        }

        lock (_lock)
        {
            _sessions[(userId, deviceId ?? string.Empty)] = group;
        }
    }

    /// <summary>The session left its group (or ended).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">Their device.</param>
    public void Left(Guid userId, string? deviceId)
    {
        lock (_lock)
        {
            _sessions.Remove((userId, deviceId ?? string.Empty));
        }
    }

    /// <summary>The group the session is in, as far as the companion saw, or null.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">Their device.</param>
    /// <returns>The group, 32 lower-case hex digits.</returns>
    public string? GroupOf(Guid userId, string? deviceId)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault((userId, deviceId ?? string.Empty));
        }
    }

    /// <summary>Every group any session of <paramref name="userId"/> is in.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The groups.</returns>
    public IReadOnlySet<string> GroupsOf(Guid userId)
    {
        lock (_lock)
        {
            return _sessions.Where(p => p.Key.User == userId).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Items were queued in <paramref name="groupId"/> (with their seasons and series, already added).</summary>
    /// <param name="groupId">The group.</param>
    /// <param name="itemIds">The items.</param>
    public void Queued(string groupId, IEnumerable<Guid> itemIds)
    {
        if (GuestPassService.NormalizeGroupId(groupId) is not { } group)
        {
            return;
        }

        lock (_lock)
        {
            if (!_queued.TryGetValue(group, out var items))
            {
                items = new LinkedList<Guid>();
                _queued[group] = items;
            }

            foreach (var id in itemIds.Where(id => id != Guid.Empty))
            {
                items.Remove(id);
                items.AddLast(id);
            }

            while (items.Count > MaxItemsPerGroup)
            {
                items.RemoveFirst();
            }
        }
    }

    /// <summary>Whether <paramref name="itemId"/> was queued in any of <paramref name="groups"/>.</summary>
    /// <param name="groups">The groups.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>True when one of them queued it.</returns>
    public bool Holds(IReadOnlySet<string> groups, Guid itemId)
    {
        lock (_lock)
        {
            return groups.Any(g => _queued.TryGetValue(g, out var items) && items.Contains(itemId));
        }
    }
}
