// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Text;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Guests' user names: the nickname they chose, then <see cref="Suffix"/> (the KasuVideo owner's
/// choice, 2026-11-25). Jellyfin user names are unique, so a taken name gets a number before the
/// suffix (<c>Mika2-G</c>), and a guest's name always ends in <c>-G</c>.
/// </summary>
public static class GuestNames
{
    /// <summary>What every guest's user name ends with.</summary>
    public const string Suffix = "-G";

    /// <summary>The longest nickname, in characters.</summary>
    public const int MaxNickname = 20;

    /// <summary>
    /// The nickname to use, or null if none is left: spaces trimmed and runs of them made one,
    /// then only letters, digits, spaces and <c>- _ . '</c> kept (a subset of what Jellyfin
    /// allows in a user name), at most <see cref="MaxNickname"/> characters.
    /// </summary>
    /// <param name="raw">What the visitor typed.</param>
    /// <returns>The nickname, or null.</returns>
    public static string? Clean(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var kept = new StringBuilder();
        foreach (var c in raw.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '\'')
            {
                kept.Append(c);
            }
            else if (char.IsWhiteSpace(c) && kept.Length > 0 && kept[^1] != ' ')
            {
                kept.Append(' ');
            }
        }

        var name = kept.ToString().Trim();
        if (name.Length > MaxNickname)
        {
            name = name[..MaxNickname].TrimEnd();
        }

        return name.Length == 0 ? null : name;
    }

    /// <summary>The user names to try for <paramref name="nickname"/>, in order: <c>Mika-G</c>, then <c>Mika2-G</c> to <c>Mika99-G</c>.</summary>
    /// <param name="nickname">A nickname from <see cref="Clean"/>.</param>
    /// <returns>The candidates.</returns>
    public static IEnumerable<string> Candidates(string nickname)
    {
        yield return nickname + Suffix;
        for (var i = 2; i < 100; i++)
        {
            yield return $"{nickname}{i}{Suffix}";
        }
    }
}
