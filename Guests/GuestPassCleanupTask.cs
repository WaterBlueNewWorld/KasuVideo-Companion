// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Ends guest passes that are due and deletes leftover guest accounts
/// (<see cref="GuestPassService.CleanupAsync"/>), every five minutes and when the server starts.
/// Listed in Jellyfin's scheduled tasks as "Guest passes", where an administrator can run it now.
/// </summary>
public class GuestPassCleanupTask : IScheduledTask
{
    private readonly GuestPassService _passes;

    /// <summary>Creates the task (Jellyfin does).</summary>
    /// <param name="passes">The guest passes.</param>
    public GuestPassCleanupTask(GuestPassService passes)
    {
        _passes = passes;
    }

    /// <inheritdoc />
    public string Name => "Guest passes";

    /// <inheritdoc />
    public string Key => "KasuVideoGuestPassCleanup";

    /// <inheritdoc />
    public string Description =>
        "Ends KasuVideo guest passes that are due (unopened links, passes past their time, guests away too long) and deletes their accounts.";

    /// <inheritdoc />
    public string Category => "KasuVideo";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _passes.CleanupAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(5).Ticks },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
    ];
}
