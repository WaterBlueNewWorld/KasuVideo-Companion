// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.KasuVideoCompanion.Configuration;
using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

public class GuestCodesTests
{
    [Fact]
    public void ACodeIs43Base64UrlCharactersAndEveryOneDiffers()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => GuestCodes.New()).ToList();
        Assert.All(codes, c => Assert.True(GuestCodes.LooksLikeCode(c), c));
        Assert.Equal(50, codes.Distinct().Count());
    }

    [Fact]
    public void ItsHashIsStableHex()
    {
        var code = GuestCodes.New();
        Assert.Equal(GuestCodes.Hash(code), GuestCodes.Hash(code));
        Assert.Matches("^[0-9a-f]{64}$", GuestCodes.Hash(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("0123456789012345678901234567890123456789+/=")]
    public void AnythingElseIsntACode(string? text) => Assert.False(GuestCodes.LooksLikeCode(text));
}

public class GuestNamesTests
{
    [Theory]
    [InlineData("Mika", "Mika")]
    [InlineData("  Mika   Chan  ", "Mika Chan")]
    [InlineData("Mi<ka>!", "Mika")]
    [InlineData("O'Neil_ok-1.", "O'Neil_ok-1.")]
    [InlineData("かすみん", "かすみん")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrst")]
    public void ANicknameKeepsWhatJellyfinAllows(string raw, string expected) => Assert.Equal(expected, GuestNames.Clean(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void NothingUsableIsNoNickname(string? raw) => Assert.Null(GuestNames.Clean(raw));

    [Fact]
    public void EveryCandidateEndsInTheSuffix()
    {
        var names = GuestNames.Candidates("Mika").ToList();
        Assert.Equal(["Mika-G", "Mika2-G", "Mika3-G"], names.Take(3));
        Assert.All(names, n => Assert.EndsWith("-G", n, StringComparison.Ordinal));
        Assert.Equal(99, names.Count);
    }
}

public class GuestPolicyTests
{
    [Fact]
    public void AGuestOnlyWatchesWhatTheirLibrariesHold()
    {
        // What Jellyfin gives a new user: almost everything.
        var policy = new UserPolicy { AuthenticationProviderId = "Default", PasswordResetProviderId = "DefaultReset" };
        var movies = Guid.NewGuid();
        GuestPolicy.Apply(policy, [movies], transcoding: true, bitrateMbps: 8);

        Assert.Equal(GuestAuthenticationProvider.ProviderId, policy.AuthenticationProviderId);
        Assert.Equal("DefaultReset", policy.PasswordResetProviderId);
        Assert.True(policy.IsHidden);
        Assert.False(policy.IsAdministrator);
        Assert.False(policy.EnableAllFolders);
        Assert.Equal([movies], policy.EnabledFolders);
        Assert.Equal(SyncPlayUserAccessType.JoinGroups, policy.SyncPlayAccess);
        Assert.Equal(1, policy.MaxActiveSessions);
        Assert.Equal(8_000_000, policy.RemoteClientBitrateLimit);
        Assert.True(policy.EnableVideoPlaybackTranscoding);
        Assert.False(policy.EnableContentDownloading);
        Assert.False(policy.EnablePublicSharing);
        Assert.False(policy.EnableSharedDeviceControl);
        Assert.False(policy.EnableRemoteControlOfOtherUsers);
        Assert.False(policy.EnableUserPreferenceAccess);
        Assert.False(policy.EnableLiveTvAccess);
        Assert.False(policy.EnableLiveTvManagement);
        Assert.False(policy.EnableContentDeletion);
        Assert.False(policy.EnableAllChannels);
    }

    [Fact]
    public void TranscodingFollowsTheSetting()
    {
        var policy = new UserPolicy { AuthenticationProviderId = "Default", PasswordResetProviderId = "DefaultReset" };
        GuestPolicy.Apply(policy, [], transcoding: false, bitrateMbps: 4);
        Assert.False(policy.EnableVideoPlaybackTranscoding);
        Assert.False(policy.EnableAudioPlaybackTranscoding);
        Assert.Equal(4_000_000, policy.RemoteClientBitrateLimit);
    }

    [Fact]
    public async Task TheProviderRefusesEveryPassword()
    {
        var provider = new GuestAuthenticationProvider();
        await Assert.ThrowsAsync<MediaBrowser.Controller.Authentication.AuthenticationException>(() => provider.Authenticate("Mika-G", string.Empty));
        Assert.Equal(typeof(GuestAuthenticationProvider).FullName, GuestAuthenticationProvider.ProviderId);
    }
}

public class SettingsBoundsTests
{
    [Fact]
    public void TheDefaultsAreTheOwnersAndHaveNoProblems()
    {
        var settings = new PluginConfiguration();
        Assert.Empty(SettingsBounds.Problems(settings));
        Assert.False(settings.GuestPassesEnabled);
        Assert.Equal((10, 3, 6, 20), (settings.LinkMinutes, settings.PassHoursMin, settings.PassHoursMax, settings.IdleMinutes));
        Assert.Equal((true, 8), (settings.GuestTranscoding, settings.GuestBitrateMbps));
        Assert.Equal((16, 4), (settings.RoomMaxMembers, settings.RoomMaxGuests));
    }

    [Fact]
    public void ANumberOutOfItsRangeIsAProblem()
    {
        var problems = SettingsBounds.Problems(new PluginConfiguration { LinkMinutes = 1, RoomMaxMembers = 51 });
        Assert.Equal(["LinkMinutes", "RoomMaxMembers"], problems.Keys.Order());
    }

    [Fact]
    public void TheLongestPassCantBeShorterThanTheShortest()
    {
        var problems = SettingsBounds.Problems(new PluginConfiguration { PassHoursMin = 6, PassHoursMax = 3 });
        Assert.Equal(["PassHoursMax"], problems.Keys);
    }

    [Fact]
    public void GuestsLeaveAPlaceForTheOwner()
    {
        Assert.Equal(["RoomMaxGuests"], SettingsBounds.Problems(new PluginConfiguration { RoomMaxMembers = 4, RoomMaxGuests = 4 }).Keys);
        Assert.Empty(SettingsBounds.Problems(new PluginConfiguration { RoomMaxMembers = 4, RoomMaxGuests = 3 }));
    }

    [Fact]
    public void NobodyOrNoLibraryTwice()
    {
        var someone = Guid.NewGuid();
        var problems = SettingsBounds.Problems(new PluginConfiguration { InviterIds = [someone, someone], GuestLibraryIds = [someone, someone] });
        Assert.Equal(["GuestLibraryIds", "InviterIds"], problems.Keys.Order());
    }
}

public class VisitorLimiterTests
{
    [Fact]
    public void TenAMinuteFromAnAddressSixtyInAll()
    {
        var clock = new FakeClock();
        var limiter = new VisitorLimiter(clock);
        for (var i = 0; i < VisitorLimiter.PerAddress; i++)
        {
            Assert.True(limiter.Allow("10.0.0.1"));
        }

        Assert.False(limiter.Allow("10.0.0.1"));
        Assert.True(limiter.Allow("10.0.0.2"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.Allow("10.0.0.1"));

        for (var i = 0; i < 60; i++)
        {
            limiter.Allow($"10.1.{i}.1");
        }

        Assert.False(limiter.Allow("10.9.9.9"));
    }
}
