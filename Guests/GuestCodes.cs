// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Invite codes: 32 random bytes in base64url (43 characters), shown once to the inviter and
/// carried in the link's fragment (<c>/guest#code</c>), so no server ever logs it. Only their
/// SHA-256 is kept.
/// </summary>
public static class GuestCodes
{
    /// <summary>A new code.</summary>
    /// <returns>43 base64url characters.</returns>
    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>What is kept of <paramref name="code"/>: its SHA-256, in lower-case hex.</summary>
    /// <param name="code">The code.</param>
    /// <returns>64 hex digits.</returns>
    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    /// <summary>Whether <paramref name="text"/> could be a code (43 base64url characters).</summary>
    /// <param name="text">What a visitor sent.</param>
    /// <returns>True when it has a code's shape.</returns>
    public static bool LooksLikeCode(string? text) =>
        text is { Length: 43 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
}
