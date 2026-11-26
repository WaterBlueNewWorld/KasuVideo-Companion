// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

/// <summary>A guest leaving (<c>DELETE Guests/Me</c>), and the sweeps of leftover guest accounts.</summary>
public class LeavingAndLeftoversTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task AGuestEndsTheirOwnPassAndTheirAccountGoes()
    {
        var pass = _s.Redeem(_s.Invite());
        Assert.Null(await _s.Service.EndMineAsync(pass.UserId));
        Assert.Equal([pass.UserId], _s.Accounts.Ended);
        var view = Assert.Single(_s.Service.All());
        Assert.Equal(("ended", EndReasons.Left), (view.State, view.EndReason));
        // The inviter's slot is free again.
        Assert.Equal(0, _s.Service.Status(Setup.Owner).LivePasses);
    }

    [Fact]
    public async Task SomeoneWhoIsntAGuestEndsNothing()
    {
        var pass = _s.Redeem(_s.Invite());
        Assert.Equal(GuestErrors.NotGuest, await _s.Service.EndMineAsync(Setup.Owner));
        Assert.Equal(GuestErrors.NotGuest, await _s.Service.EndMineAsync(Setup.Admin));
        Assert.Equal(GuestErrors.NotGuest, await _s.Service.EndMineAsync(Guid.NewGuid()));
        Assert.Empty(_s.Accounts.Ended);
        Assert.NotNull(_s.Service.Me(pass.UserId));
    }

    [Fact]
    public async Task AGuestWhosePassNoLongerLivesIsRefused()
    {
        var pass = _s.Redeem(_s.Invite());
        _s.Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1));
        Assert.Equal(GuestAccessRules.NotAllowed, await _s.Service.EndMineAsync(pass.UserId));
        Assert.Empty(_s.Accounts.Ended);

        var second = _s.Redeem(_s.Invite(), "Yu");
        await _s.Service.EndAllAsync(EndReasons.Ended);
        // Its account lingers (as after a failed delete): still refused.
        _s.Accounts.Users[second.UserId] = new UserFacts(second.UserId, second.UserName, false, true, false, []);
        _s.Accounts.Ended.Clear();
        Assert.Equal(GuestAccessRules.NotAllowed, await _s.Service.EndMineAsync(second.UserId));
        Assert.Empty(_s.Accounts.Ended);
    }

    [Fact]
    public async Task TheSweepDeletesEveryGuestAccountWithoutALivePass()
    {
        var lost = Guid.NewGuid();
        _s.Accounts.Users[lost] = new UserFacts(lost, "Lost-G", false, true, false, []);
        var late = _s.Redeem(_s.Invite(), "Late");
        var live = _s.Redeem(_s.Invite(5), "Live");
        // Past the first pass's three hours; the five-minute task (which would end it) is off.
        _s.Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));

        Assert.Equal(2, await _s.Service.SweepLeftoversAsync(CancellationToken.None));
        Assert.Equal(2, _s.Accounts.Ended.Count);
        Assert.Contains(lost, _s.Accounts.Ended);
        Assert.Contains(late.UserId, _s.Accounts.Ended);
        Assert.DoesNotContain(live.UserId, _s.Accounts.Ended);
        Assert.DoesNotContain(Setup.Owner, _s.Accounts.Ended);
        Assert.DoesNotContain(Setup.Admin, _s.Accounts.Ended);
        // The pass that ran out is ended too, not left open.
        var ended = _s.Service.All().Single(p => p.GuestName == late.UserName);
        Assert.Equal(("ended", EndReasons.Expired), (ended.State, ended.EndReason));
        Assert.NotNull(_s.Service.Me(live.UserId));
    }

    [Fact]
    public async Task ASweepDuringARedeemLeavesTheNewAccountAlone()
    {
        var invite = _s.Invite();
        _s.Accounts.DuringCreate = async () => await _s.Service.SweepLeftoversAsync(CancellationToken.None);
        var pass = _s.Redeem(invite);
        Assert.Empty(_s.Accounts.Ended);
        Assert.NotNull(_s.Service.Me(pass.UserId));
    }

    [Fact]
    public async Task AClaimLeftByACrashDoesntStopTheSweepForEver()
    {
        _s.Invite();
        var lost = Guid.NewGuid();
        _s.Accounts.Users[lost] = new UserFacts(lost, "Lost-G", false, true, false, []);
        // A redeem that never finished: claimed, no account recorded.
        _s.Store.Update(passes =>
        {
            passes.Single().RedeemedAt = _s.Clock.Now;
            return 0;
        });
        Assert.Equal(0, await _s.Service.SweepLeftoversAsync(CancellationToken.None));
        _s.Clock.Advance(GuestPassService.ClaimHeld + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await _s.Service.SweepLeftoversAsync(CancellationToken.None));
        Assert.Equal([lost], _s.Accounts.Ended);
    }

    [Fact]
    public async Task ALingeringAccountIsNeverReused()
    {
        var lingering = Guid.NewGuid();
        _s.Accounts.Users[lingering] = new UserFacts(lingering, "Mika-G", false, true, false, []);
        var pass = _s.Redeem(_s.Invite(), "Mika");
        Assert.NotEqual(lingering, pass.UserId);
        Assert.Equal("Mika2-G", pass.UserName);
        Assert.Null(_s.Service.LivePassOf(lingering));
        Assert.Null(_s.Service.Me(lingering));
        Assert.Equal(GuestAccessRules.NotAllowed, await _s.Service.EndMineAsync(lingering));
    }

    [Fact]
    public async Task TheHourlyTaskSweeps()
    {
        var task = new LeftoverGuestAccountsTask(_s.Service);
        Assert.Equal(("KasuVideoLeftoverGuestAccounts", "KasuVideo"), (task.Key, task.Category));
        var triggers = task.GetDefaultTriggers().ToList();
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromHours(1).Ticks);
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);

        var lost = Guid.NewGuid();
        _s.Accounts.Users[lost] = new UserFacts(lost, "Lost-G", false, true, false, []);
        var live = _s.Redeem(_s.Invite());
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal([lost], _s.Accounts.Ended);
        Assert.NotNull(_s.Service.Me(live.UserId));
    }

    [Fact]
    public async Task OneFailedDeleteDoesntStopTheSweep()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _s.Accounts.Users[first] = new UserFacts(first, "A-G", false, true, false, []);
        _s.Accounts.Users[second] = new UserFacts(second, "B-G", false, true, false, []);
        _s.Accounts.EndFails.Add(first);
        Assert.Equal(1, await _s.Service.SweepLeftoversAsync(CancellationToken.None));
        Assert.Equal([second], _s.Accounts.Ended);
    }
}
