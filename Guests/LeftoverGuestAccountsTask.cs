// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Deletes guest accounts without a live pass (<see cref="GuestPassService.SweepLeftoversAsync"/>),
/// every hour and when the server starts: accounts left by a crash or a delete that failed (a
/// guest's Leave over a dropped connection, say), and accounts whose pass ran out. The five-minute
/// "Guest passes" task sweeps the same way; this one keeps working if an administrator turns that
/// one off. Listed in Jellyfin's scheduled tasks as "Leftover guest accounts".
/// </summary>
public class LeftoverGuestAccountsTask : IScheduledTask
{
    private readonly GuestPassService _passes;

    /// <summary>Creates the task (Jellyfin does).</summary>
    /// <param name="passes">The guest passes.</param>
    public LeftoverGuestAccountsTask(GuestPassService passes)
    {
        _passes = passes;
    }

    /// <inheritdoc />
    public string Name => "Leftover guest accounts";

    /// <inheritdoc />
    public string Key => "KasuVideoLeftoverGuestAccounts";

    /// <inheritdoc />
    public string Description =>
        "Deletes KasuVideo guest accounts that no live guest pass owns (left by a crash or a failed delete, or whose pass ran out).";

    /// <inheritdoc />
    public string Category => "KasuVideo";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _passes.SweepLeftoversAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
    ];
}
