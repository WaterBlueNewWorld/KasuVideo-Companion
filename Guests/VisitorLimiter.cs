// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// A limit on the two routes anyone may call (an invite's preview, and opening it): so many a
/// minute from one address, and so many a minute in all. Codes are 256-bit random, so guessing
/// is hopeless anyway; this keeps a flood from costing the server. Counted in memory: a restart
/// forgets it, which costs nothing.
/// </summary>
public sealed class VisitorLimiter
{
    /// <summary>Requests a minute from one address.</summary>
    public const int PerAddress = 10;

    /// <summary>Requests a minute from everyone.</summary>
    public const int InAll = 60;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTime>> _byAddress = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _all = new();

    /// <summary>Creates the limiter.</summary>
    /// <param name="clock">The time.</param>
    public VisitorLimiter(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Counts a request from <paramref name="address"/>; false when it's over a limit (and not counted).</summary>
    /// <param name="address">The caller's address (an empty one counts as one address).</param>
    /// <returns>True when it may go ahead.</returns>
    public bool Allow(string? address)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            Trim(_all, now);
            var key = address ?? string.Empty;
            if (!_byAddress.TryGetValue(key, out var mine))
            {
                mine = new Queue<DateTime>();
                _byAddress[key] = mine;
            }

            Trim(mine, now);
            if (mine.Count >= PerAddress || _all.Count >= InAll)
            {
                return false;
            }

            mine.Enqueue(now);
            _all.Enqueue(now);

            // Forget addresses that went quiet.
            if (_byAddress.Count > 1000)
            {
                foreach (var quiet in _byAddress.Where(p => { Trim(p.Value, now); return p.Value.Count == 0; }).Select(p => p.Key).ToList())
                {
                    _byAddress.Remove(quiet);
                }
            }

            return true;
        }
    }

    private static void Trim(Queue<DateTime> times, DateTime now)
    {
        while (times.Count > 0 && now - times.Peek() >= Window)
        {
            times.Dequeue();
        }
    }
}
