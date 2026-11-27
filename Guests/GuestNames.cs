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

    /// <summary>The most a device's name or an app's version keeps (G8: 10 KB of each was stored).</summary>
    public const int MaxLabel = 64;

    /// <summary>
    /// A label a visitor sends (their device's name, KasuVideo's version): control and invisible
    /// characters dropped, spaces collapsed, at most <see cref="MaxLabel"/> characters, or
    /// <paramref name="fallback"/> when nothing is left. Jellyfin stores it and shows it in its
    /// dashboard.
    /// </summary>
    /// <param name="raw">What was sent.</param>
    /// <param name="fallback">What to use when nothing is left.</param>
    /// <returns>The label.</returns>
    public static string Label(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var kept = new System.Text.StringBuilder();
        foreach (var c in raw.Normalize(System.Text.NormalizationForm.FormC))
        {
            var category = char.GetUnicodeCategory(c);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            kept.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }

        var label = string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (label.Length > MaxLabel)
        {
            label = label[..MaxLabel].TrimEnd();
        }

        return label.Length == 0 ? fallback : label;
    }
}
