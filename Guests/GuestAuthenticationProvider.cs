// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// The sign-in provider of every guest account. It refuses every password, so nobody can sign
/// in as a guest by name, even knowing it: a guest's session comes only from opening their link
/// (<see cref="GuestPassService.RedeemAsync"/>, through Jellyfin's AuthenticateDirect, as Quick
/// Connect does). It also marks a user as a guest: <c>Policy.AuthenticationProviderId</c> in
/// <c>/Users/Me</c> is <see cref="ProviderId"/>, which only an administrator can change. Jellyfin
/// finds it in the plugin's assembly by itself.
/// </summary>
public class GuestAuthenticationProvider : IAuthenticationProvider
{
    /// <summary>The provider's id, as Jellyfin stores it on the user: the class's full name.</summary>
    public static readonly string ProviderId = typeof(GuestAuthenticationProvider).FullName!;

    /// <inheritdoc />
    public string Name => "KasuVideo guest pass";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password) =>
        throw new AuthenticationException("A guest pass has no password.");

    /// <inheritdoc />
    public bool HasPassword(User user) => false;

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword) =>
        throw new NotSupportedException("A guest pass has no password.");
}
