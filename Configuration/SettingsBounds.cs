// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Configuration;

/// <summary>
/// The range each number setting may take. KasuVideo's page for the companion reads them from
/// <c>GET /KasuVideo/Companion/Settings</c>, so the limits live in one place.
/// </summary>
public static class SettingsBounds
{
    /// <summary>The settings that are numbers, with their lowest and highest values.</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Numbers =
        new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal)
        {
            [nameof(PluginConfiguration.MaxLivePassesPerInviter)] = (1, 20),
            [nameof(PluginConfiguration.LinkMinutes)] = (5, 60),
            [nameof(PluginConfiguration.PassHoursMin)] = (1, 24),
            [nameof(PluginConfiguration.PassHoursMax)] = (1, 24),
            [nameof(PluginConfiguration.IdleMinutes)] = (5, 120),
            [nameof(PluginConfiguration.GuestBitrateMbps)] = (1, 200),
            [nameof(PluginConfiguration.RoomMaxMembers)] = (2, 50),
            [nameof(PluginConfiguration.RoomMaxGuests)] = (0, 49),
        };

    /// <summary>
    /// What's wrong with <paramref name="settings"/>, by setting name (empty when nothing is): a
    /// number out of its range, the longest pass shorter than the shortest, more guests than a
    /// room has places for besides its owner, or an inviter or library listed twice.
    /// </summary>
    /// <param name="settings">The settings to check.</param>
    /// <returns>A message for each setting that's wrong.</returns>
    public static Dictionary<string, string> Problems(PluginConfiguration settings)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, (min, max)) in Numbers)
        {
            var value = (int)typeof(PluginConfiguration).GetProperty(name)!.GetValue(settings)!;
            if (value < min || value > max)
            {
                problems[name] = $"must be from {min} to {max}";
            }
        }

        if (!problems.ContainsKey(nameof(PluginConfiguration.PassHoursMax))
            && settings.PassHoursMax < settings.PassHoursMin)
        {
            problems[nameof(PluginConfiguration.PassHoursMax)] = "must be at least PassHoursMin";
        }

        if (!problems.ContainsKey(nameof(PluginConfiguration.RoomMaxGuests))
            && settings.RoomMaxGuests > settings.RoomMaxMembers - 1)
        {
            problems[nameof(PluginConfiguration.RoomMaxGuests)] = "must leave a place for the room's owner";
        }

        if (settings.InviterIds.Distinct().Count() != settings.InviterIds.Length)
        {
            problems[nameof(PluginConfiguration.InviterIds)] = "lists someone twice";
        }

        if (settings.GuestLibraryIds.Distinct().Count() != settings.GuestLibraryIds.Length)
        {
            problems[nameof(PluginConfiguration.GuestLibraryIds)] = "lists a library twice";
        }

        return problems;
    }
}
