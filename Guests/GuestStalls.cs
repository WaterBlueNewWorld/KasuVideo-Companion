// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// How often a guest may stall their room (SyncPlay Buffering): <see cref="PerMinute"/> a minute,
/// the owner's choice (G8). A real slow connection stalls a few times; a guest sending it again
/// and again is refused (<c>guest-busy</c>). Together with SyncPlay's "ignore wait", which the
/// companion sets on every guest when they join (<see cref="IGuestAccounts.IgnoreWait"/>), a
/// guest never holds the room: the live test saw one Buffering with no Ready after it keep
/// everyone paused. Counted in memory.
/// </summary>
public sealed class GuestStalls
{
    /// <summary>Stalls a minute per guest.</summary>
    public const int PerMinute = 12;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Queue<DateTime>> _byGuest = [];

    /// <summary>Creates the counter.</summary>
    /// <param name="clock">The time.</param>
    public GuestStalls(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Counts a stall from <paramref name="guestId"/>; false (and not counted) when it's over the limit.</summary>
    /// <param name="guestId">The guest.</param>
    /// <returns>True when it may go ahead.</returns>
    public bool Allow(Guid guestId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (!_byGuest.TryGetValue(guestId, out var times))
            {
                times = new Queue<DateTime>();
                _byGuest[guestId] = times;
            }

            while (times.Count > 0 && now - times.Peek() >= Window)
            {
                times.Dequeue();
            }

            if (times.Count >= PerMinute)
            {
                return false;
            }

            times.Enqueue(now);

            // Forget guests who went quiet.
            if (_byGuest.Count > 1000)
            {
                foreach (var quiet in _byGuest.Where(p => p.Value.Count == 0 || now - p.Value.Last() >= Window).Select(p => p.Key).ToList())
                {
                    _byGuest.Remove(quiet);
                }
            }

            return true;
        }
    }
}
