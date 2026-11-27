// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Guests;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

public class GuestPassServiceTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public void StatusSaysWhetherTheCallerMayInviteAndWithWhat()
    {
        var owner = _s.Service.Status(Setup.Owner);
        Assert.True(owner.Enabled);
        Assert.True(owner.CanInvite);
        Assert.Equal(["Movies", "Shows"], owner.Libraries.Select(l => l.Name));
        Assert.Equal((3, 10, 3, 6), (owner.MaxLivePasses, owner.LinkMinutes, owner.PassHoursMin, owner.PassHoursMax));

        var other = _s.Service.Status(Setup.Other);
        Assert.False(other.CanInvite);
        Assert.Empty(other.Libraries);

        _s.Settings.GuestPassesEnabled = false;
        Assert.False(_s.Service.Status(Setup.Owner).CanInvite);
    }

    [Fact]
    public void AnInviterOnlyGivesLibrariesTheyCanSeeAndGuestsMayGet()
    {
        _s.Settings.InviterIds = [Setup.Owner, Setup.Other];
        Assert.Equal(["Movies"], _s.Service.Status(Setup.Other).Libraries.Select(l => l.Name));
        var shows = _s.Service.CreateInvite(Setup.Other, null, Setup.Group.ToString(), [Setup.Shows], null);
        Assert.Equal("bad-libraries", shows.Error);
        _s.Settings.GuestLibraryIds = [Setup.Shows];
        var movies = _s.Service.CreateInvite(Setup.Owner, null, Setup.Group.ToString(), [Setup.Movies], null);
        Assert.Equal("bad-libraries", movies.Error);
        Assert.Equal("bad-libraries", _s.Service.CreateInvite(Setup.Owner, null, Setup.Group.ToString(), [], null).Error);
    }

    [Fact]
    public void AnInviteNeedsPassesOnAnInviterTheirGroupAndHoursInRange()
    {
        var group = Setup.Group.ToString();
        Assert.Equal("not-inviter", _s.Service.CreateInvite(Setup.Admin, null, group, [Setup.Movies], null).Error);
        Assert.Equal("bad-group", _s.Service.CreateInvite(Setup.Owner, null, "not-a-group", [Setup.Movies], null).Error);
        Assert.Equal("bad-hours", _s.Service.CreateInvite(Setup.Owner, null, group, [Setup.Movies], 2).Error);
        Assert.Equal("bad-hours", _s.Service.CreateInvite(Setup.Owner, null, group, [Setup.Movies], 7).Error);
        _s.Accounts.InGroup.Clear();
        Assert.Equal("not-in-group", _s.Service.CreateInvite(Setup.Owner, null, group, [Setup.Movies], null).Error);
        _s.Settings.GuestPassesEnabled = false;
        Assert.Equal("off", _s.Service.CreateInvite(Setup.Owner, null, group, [Setup.Movies], null).Error);
    }

    [Fact]
    public void AGuestCantInvite()
    {
        var pass = _s.Redeem(_s.Invite());
        _s.Settings.InviterIds = [Setup.Owner, pass.UserId];
        Assert.Equal("guest", _s.Service.CreateInvite(pass.UserId, null, Setup.Group.ToString(), [Setup.Movies], null).Error);
    }

    [Fact]
    public void AnInviteKeepsOnlyItsCodesHashAndLastsTheShortestByDefault()
    {
        var invite = _s.Invite();
        Assert.True(GuestCodes.LooksLikeCode(invite.Code));
        Assert.Equal(3, invite.Hours);
        Assert.Equal(_s.Clock.Now.AddMinutes(10), invite.RedeemBy);
        var stored = File.ReadAllText(Path.Combine(_s.Folder, GuestPassStore.FileName));
        Assert.DoesNotContain(invite.Code, stored, StringComparison.Ordinal);
        Assert.Contains(GuestCodes.Hash(invite.Code), stored, StringComparison.Ordinal);
        // The group is kept as the rooms Worker spells it.
        Assert.Equal(Setup.Group.ToString("N"), _s.Service.InvitesOf(Setup.Owner).Single().GroupId);
        Assert.Equal(6, _s.Invite(hours: 6).Hours);
    }

    [Fact]
    public void AnInviterHasAtMostTheirCapOfOpenPasses()
    {
        _s.Invite();
        _s.Invite();
        _s.Redeem(_s.Invite());
        Assert.Equal("too-many", _s.Service.CreateInvite(Setup.Owner, null, Setup.Group.ToString(), [Setup.Movies], null).Error);
        // A link that wasn't opened in time no longer counts.
        _s.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(_s.Service.CreateInvite(Setup.Owner, null, Setup.Group.ToString(), [Setup.Movies], null).Ok);
    }

    [Fact]
    public void APreviewSaysWhoInvitedOnlyForALiveUnopenedLink()
    {
        var invite = _s.Invite();
        var preview = _s.Service.Preview(invite.Code);
        Assert.Equal("kasutest", preview!.InviterName);
        Assert.Equal(3, preview.Hours);
        Assert.Null(_s.Service.Preview("not a code"));
        Assert.Null(_s.Service.Preview(GuestCodes.New()));
        _s.Redeem(invite);
        Assert.Null(_s.Service.Preview(invite.Code));
        var late = _s.Invite();
        _s.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(_s.Service.Preview(late.Code));
    }

    [Fact]
    public async Task OpeningALinkMakesTheGuestOnceAndStartsTheirClock()
    {
        var invite = _s.Invite(hours: 4);
        var pass = _s.Redeem(invite);
        Assert.Equal("Mika-G", pass.UserName);
        Assert.Equal(Setup.Group.ToString("N"), pass.GroupId);
        Assert.Equal(_s.Clock.Now.AddHours(4), pass.EndsAt);
        var made = _s.Accounts.Created.Single();
        Assert.Equal([Setup.Movies], made.LibraryIds);
        Assert.True(made.Transcoding);
        Assert.Equal(8, made.BitrateMbps);
        Assert.Equal("visitor-device", made.DeviceId);

        var again = await _s.Service.RedeemAsync(invite.Code, "Someone", "another-device", null, null, null);
        Assert.Equal("invalid", again.Error);
        Assert.Single(_s.Accounts.Created);
    }

    [Fact]
    public async Task ALinkOpenedLateABadNicknameOrDeviceIsRefused()
    {
        var invite = _s.Invite();
        Assert.Equal("bad-name", (await _s.Service.RedeemAsync(invite.Code, " !!! ", "d", null, null, null)).Error);
        Assert.Equal("bad-device", (await _s.Service.RedeemAsync(invite.Code, "Mika", "a b", null, null, null)).Error);
        Assert.Equal("invalid", (await _s.Service.RedeemAsync("short", "Mika", "d", null, null, null)).Error);
        _s.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("invalid", (await _s.Service.RedeemAsync(invite.Code, "Mika", "d", null, null, null)).Error);
        Assert.Empty(_s.Accounts.Created);
    }

    [Fact]
    public void ATakenNameGetsANumberBeforeTheSuffix()
    {
        _s.Settings.MaxLivePassesPerInviter = 5;
        Assert.Equal("Mika-G", _s.Redeem(_s.Invite()).UserName);
        Assert.Equal("Mika2-G", _s.Redeem(_s.Invite()).UserName);
        Assert.Equal("Mika3-G", _s.Redeem(_s.Invite()).UserName);
    }

    [Fact]
    public async Task AnInviterNoLongerAllowedMakesTheirLinkInvalidAndEndsIt()
    {
        var invite = _s.Invite();
        _s.Settings.InviterIds = [];
        Assert.Equal("invalid", (await _s.Service.RedeemAsync(invite.Code, "Mika", "d", null, null, null)).Error);
        Assert.Equal(EndReasons.InviterRemoved, _s.Service.All().Single().EndReason);
    }

    [Fact]
    public async Task AFailedAccountLeavesTheLinkUsableWithinItsTime()
    {
        var invite = _s.Invite();
        _s.Accounts.CreateFails = new InvalidOperationException("Jellyfin said no");
        Assert.Equal("failed", (await _s.Service.RedeemAsync(invite.Code, "Mika", "d", null, null, null)).Error);
        _s.Accounts.CreateFails = null;
        Assert.Equal("Mika-G", _s.Redeem(invite).UserName);
    }

    [Fact]
    public async Task MeAnswersAGuestTheirPassAndNobodyElse()
    {
        var pass = _s.Redeem(_s.Invite());
        var me = _s.Service.Me(pass.UserId);
        Assert.Equal(Setup.Group.ToString("N"), me!.GroupId);
        Assert.Equal(Setup.Owner, me.InviterId);
        Assert.Equal("kasutest", me.InviterName);
        Assert.Null(_s.Service.Me(Setup.Owner));
        await _s.Service.EndAsync(me.PassId, Setup.Owner);
        Assert.Null(_s.Service.Me(pass.UserId));
    }

    [Fact]
    public async Task OnlyTheInviterOrAnAdministratorEndsAPass()
    {
        var pass = _s.Redeem(_s.Invite());
        var id = _s.Service.InvitesOf(Setup.Owner).Single().Id;
        // Someone else's pass answers as one that doesn't exist (G8: nobody learns it does).
        Assert.Equal("not-found", await _s.Service.EndAsync(id, Setup.Other));
        Assert.Equal("not-found", await _s.Service.EndAsync(Guid.NewGuid(), Setup.Owner));
        Assert.Empty(_s.Accounts.Ended);
        Assert.Null(await _s.Service.EndAsync(id, Setup.Admin));
        Assert.Equal([pass.UserId], _s.Accounts.Ended);
        var ended = _s.Service.All().Single();
        Assert.Equal(("ended", EndReasons.Admin), (ended.State, ended.EndReason));
        Assert.Empty(_s.Service.InvitesOf(Setup.Owner));
    }

    [Fact]
    public async Task CleanupEndsWhatIsDue()
    {
        _s.Settings.MaxLivePassesPerInviter = 5;
        var unopened = _s.Invite();
        var shortPass = _s.Redeem(_s.Invite(hours: 3));
        var idle = _s.Redeem(_s.Invite(hours: 6));
        var busy = _s.Redeem(_s.Invite(hours: 6));

        // 20 minutes on: the link wasn't opened, one guest did nothing, the others were active.
        _s.Clock.Advance(TimeSpan.FromMinutes(20));
        _s.Accounts.Activity[busy.UserId] = _s.Clock.Now.AddMinutes(-1);
        _s.Accounts.Activity[shortPass.UserId] = _s.Clock.Now.AddMinutes(-1);
        var report = await _s.Service.CleanupAsync(CancellationToken.None);
        Assert.Equal(2, report.Ended);
        Assert.Equal([idle.UserId], _s.Accounts.Ended);
        var reasons = _s.Service.All().Where(p => p.EndReason is not null).ToDictionary(p => p.GuestName ?? "link", p => p.EndReason);
        Assert.Equal(EndReasons.LinkExpired, reasons["link"]);
        Assert.Equal(EndReasons.Idle, reasons[idle.UserName]);
        _ = unopened;

        // Three hours in: the three-hour pass is over.
        _s.Clock.Advance(TimeSpan.FromHours(3));
        _s.Accounts.Activity[shortPass.UserId] = _s.Clock.Now;
        _s.Accounts.Activity[busy.UserId] = _s.Clock.Now;
        await _s.Service.CleanupAsync(CancellationToken.None);
        Assert.Contains(shortPass.UserId, _s.Accounts.Ended);
        Assert.DoesNotContain(busy.UserId, _s.Accounts.Ended);
    }

    [Fact]
    public async Task CleanupEndsPassesWhoseInviterWasRemovedOrWhenPassesAreOff()
    {
        var pass = _s.Redeem(_s.Invite());
        _s.Settings.InviterIds = [];
        await _s.Service.CleanupAsync(CancellationToken.None);
        Assert.Equal([pass.UserId], _s.Accounts.Ended);

        _s.Settings.InviterIds = [Setup.Owner];
        var second = _s.Redeem(_s.Invite());
        _s.Settings.GuestPassesEnabled = false;
        await _s.Service.CleanupAsync(CancellationToken.None);
        Assert.Contains(second.UserId, _s.Accounts.Ended);
        Assert.Equal(EndReasons.TurnedOff, _s.Service.All().Single(p => p.GuestUserId == second.UserId).EndReason);
    }

    [Fact]
    public async Task CleanupDeletesLeftoverGuestAccountsAndForgetsOldPasses()
    {
        var leftover = Guid.NewGuid();
        _s.Accounts.Users[leftover] = new UserFacts(leftover, "Lost-G", false, true, false, []);
        var live = _s.Redeem(_s.Invite());
        var report = await _s.Service.CleanupAsync(CancellationToken.None);
        Assert.Equal(1, report.Orphans);
        Assert.Equal([leftover], _s.Accounts.Ended);
        Assert.NotNull(_s.Service.Me(live.UserId));

        await _s.Service.EndAllAsync(EndReasons.Uninstalled);
        Assert.Contains(live.UserId, _s.Accounts.Ended);
        _s.Clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(1, (await _s.Service.CleanupAsync(CancellationToken.None)).Forgotten);
        Assert.Empty(_s.Service.All());
    }

    [Fact]
    public void ThePassesSurviveARestart()
    {
        var pass = _s.Redeem(_s.Invite());
        var reopened = new GuestPassService(
            new GuestPassStore(_s.Folder),
            _s.Accounts,
            () => _s.Settings,
            _s.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuestPassService>.Instance);
        Assert.Equal(pass.GroupId, reopened.Me(pass.UserId)!.GroupId);
    }

    [Fact]
    public void RoomSettingsComeFromTheSettings()
    {
        _s.Settings.RoomMaxMembers = 8;
        _s.Settings.RoomMaxGuests = 2;
        Assert.Equal(new RoomSettings(8, 2), _s.Service.Rooms());
    }
}
