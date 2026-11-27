// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Ends every guest pass when the server stops: a restart, an update, a shutdown, or the
/// companion disabled (which takes effect at the restart). Without the companion nothing confines
/// a guest's token: the penetration test (G8) saw one, after the companion was disabled, search
/// the libraries, stream what was never queued and approve Quick Connect. Ending a pass signs out
/// its devices and deletes the account, so the guest's token is dead before the server starts
/// again, whatever loads then. The owner's choice: a restart ends every visit and every unused
/// link. A crash skips this (seen in G8: a stop that died mid-shutdown): the passes then carry on
/// when the server is back, still confined by the guard. Only a crash after the companion was
/// disabled leaves guests unguarded, a known limit.
/// </summary>
public sealed class GuestPassesOnStop : IHostedService
{
    private readonly GuestPassService _passes;
    private readonly ILogger<GuestPassesOnStop> _logger;

    /// <summary>Creates it (Jellyfin does).</summary>
    /// <param name="passes">Guest passes.</param>
    /// <param name="logger">Where it says how many ended.</param>
    public GuestPassesOnStop(GuestPassService passes, ILogger<GuestPassesOnStop> logger)
    {
        _passes = passes;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var ended = await _passes.EndAllAsync(EndReasons.ServerStopped).ConfigureAwait(false);
        if (ended > 0)
        {
            _logger.LogInformation("The server is stopping: {Count} guest passes ended", ended);
        }
    }
}
