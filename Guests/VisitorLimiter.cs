// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// A limit on wrong codes at the two routes anyone may call (an invite's preview, and opening
/// it): so many a minute from one address, and so many a minute in all. Only a code that isn't a
/// live link's counts, and only those are refused: a real link always goes through, whatever a
/// flood of wrong ones did (G8: ten bogus previews from one address used to lock everyone out,
/// and behind a reverse proxy every visitor is one address). Codes are 256-bit random, so guessing
/// is hopeless anyway; this keeps a flood from costing the server. Counted in memory, with at most
/// <see cref="MaxAddresses"/> addresses: a restart forgets it, which costs nothing.
/// </summary>
public sealed class VisitorLimiter
{
    /// <summary>Wrong codes a minute from one address.</summary>
    public const int PerAddress = 10;

    /// <summary>Wrong codes a minute from everyone.</summary>
    public const int InAll = 60;

    /// <summary>The most addresses remembered; past it, a new address's wrong codes are refused until some go quiet.</summary>
    public const int MaxAddresses = 10_000;

    /// <summary>How long a refused visitor is told to wait (<c>Retry-After</c>), in seconds.</summary>
    public const int RetryAfterSeconds = 60;

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

    /// <summary>How many addresses it remembers.</summary>
    public int Addresses
    {
        get
        {
            lock (_lock)
            {
                return _byAddress.Count;
            }
        }
    }

    /// <summary>
    /// Counts a wrong code from <paramref name="address"/>; false (and not counted) when that
    /// address, or everyone together, is over the limit: the visitor is then told to wait.
    /// </summary>
    /// <param name="address">The caller's address (an empty one counts as one address).</param>
    /// <returns>True when it's answered as a wrong code; false when it's refused as too many.</returns>
    public bool Allow(string? address)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            Trim(_all, now);
            var key = address ?? string.Empty;
            if (!_byAddress.TryGetValue(key, out var mine))
            {
                if (_byAddress.Count >= 1000)
                {
                    Forget(now);
                }

                if (_byAddress.Count >= MaxAddresses)
                {
                    return false;
                }

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

    // Forgets the addresses that went quiet.
    private void Forget(DateTime now)
    {
        foreach (var quiet in _byAddress.Where(p => { Trim(p.Value, now); return p.Value.Count == 0; }).Select(p => p.Key).ToList())
        {
            _byAddress.Remove(quiet);
        }
    }
}
