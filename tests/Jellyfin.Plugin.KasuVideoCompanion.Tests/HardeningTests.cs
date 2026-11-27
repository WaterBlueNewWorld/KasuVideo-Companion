// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Net;
using Jellyfin.Plugin.KasuVideoCompanion.Api;
using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

/// <summary>What the penetration test (G8) found, each a test: the tracker, stalls, the visitors' limit, redeeming, the store and the ends of passes.</summary>
public class GroupRecorderTests
{
    private static readonly Guid Member = Guid.NewGuid();
    private static readonly Guid Group = Guid.Parse("0123456789abcdef0123456789abcdef");
    private const string Room = "0123456789abcdef0123456789abcdef";

    private readonly GroupTracker _tracker = new();
    private readonly FakeAccounts _accounts = new();

    private GroupRecorder Recorder => new(_tracker, _accounts);

    [Fact]
    public void AJoinJellyfinRefusedIsntNoted()
    {
        // Jellyfin answers a refused join 204 too.
        Assert.False(Recorder.Joined(Member, "laptop", Group));
        Assert.Null(_tracker.GroupOf(Member, "laptop"));
        _accounts.InGroup.Add((Member, Group));
        Assert.True(Recorder.Joined(Member, "laptop", Group));
        Assert.Equal(Room, _tracker.GroupOf(Member, "laptop"));
    }

    [Fact]
    public void AQueueCountsOnlyFromInTheGroupAndOnlyWhatTheyMaySee()
    {
        var film = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var groups = new HashSet<string> { Room };

        // Noted in the room by an earlier, real join, but Jellyfin no longer has them in it.
        _accounts.InGroup.Add((Member, Group));
        Recorder.Joined(Member, "laptop", Group);
        _accounts.InGroup.Remove((Member, Group));
        Recorder.Queued(Member, "laptop", [film]);
        Assert.False(_tracker.Holds(groups, film));

        // In it: what they may see counts, with its family; what they may not doesn't.
        _accounts.InGroup.Add((Member, Group));
        _accounts.Hidden.Add((Member, hidden));
        var season = Guid.NewGuid();
        _accounts.Families[film] = [season];
        Recorder.Queued(Member, "laptop", [film, hidden]);
        Assert.True(_tracker.Holds(groups, film));
        Assert.True(_tracker.Holds(groups, season));
        Assert.False(_tracker.Holds(groups, hidden));
    }

    [Fact]
    public void AQueueFromSomeoneInNoGroupIsntNoted()
    {
        var film = Guid.NewGuid();
        Recorder.Queued(Member, "laptop", [film]);
        Assert.False(_tracker.Holds(new HashSet<string> { Room }, film));
    }

    [Fact]
    public void TheTrackerRemembersAtMostItsGroups()
    {
        var first = Guid.NewGuid();
        _tracker.Queued(Room, [first]);
        for (var i = 0; i < GroupTracker.MaxGroups; i++)
        {
            _tracker.Queued(Guid.NewGuid().ToString("N"), [Guid.NewGuid()]);
        }

        Assert.False(_tracker.Holds(new HashSet<string> { Room }, first));
    }
}

public class GuestStallsTests
{
    [Fact]
    public void TwelveStallsAMinute()
    {
        var clock = new FakeClock();
        var stalls = new GuestStalls(clock);
        var guest = Guid.NewGuid();
        for (var i = 0; i < GuestStalls.PerMinute; i++)
        {
            Assert.True(stalls.Allow(guest));
        }

        Assert.False(stalls.Allow(guest));
        Assert.True(stalls.Allow(Guid.NewGuid()));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(stalls.Allow(guest));
    }
}

public class VisitorLimitTests
{
    [Fact]
    public void ItRemembersAtMostItsAddressesAndForgetsQuietOnes()
    {
        var clock = new FakeClock();
        var limiter = new VisitorLimiter(clock);
        for (var i = 0; i < 1000; i++)
        {
            limiter.Allow($"10.0.{i / 250}.{i % 250}");
            if (i % 50 == 49)
            {
                // Under the limit for everyone.
                clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.Allow("192.0.2.1"));
        Assert.True(limiter.Addresses < 1000);
    }
}

public class LabelTests
{
    [Theory]
    [InlineData("Firefox on Windows", "Firefox on Windows")]
    [InlineData("  Chrome\t\n 157 ", "Chrome 157")]
    [InlineData("evil‮name​\u0007", "evilname")]
    [InlineData(null, "fallback")]
    [InlineData("\u0007​", "fallback")]
    public void ALabelIsCleaned(string? raw, string expected) => Assert.Equal(expected, GuestNames.Label(raw, "fallback"));

    [Fact]
    public void ALabelIsShort() => Assert.Equal(GuestNames.MaxLabel, GuestNames.Label(new string('D', 10240), "x").Length);
}

public class RedeemHardeningTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public void APreviewNamesOnlyAnInviterWhoMayStillInvite()
    {
        var invite = _s.Invite();
        Assert.NotNull(_s.Service.Preview(invite.Code));
        _s.Settings.InviterIds = [];
        Assert.Null(_s.Service.Preview(invite.Code));
    }

    [Fact]
    public void AnOpenCodeIsALiveUnopenedLinks()
    {
        var invite = _s.Invite();
        Assert.True(_s.Service.IsOpenCode(invite.Code));
        Assert.False(_s.Service.IsOpenCode(GuestCodes.New()));
        Assert.False(_s.Service.IsOpenCode("short"));
        _s.Redeem(invite);
        Assert.False(_s.Service.IsOpenCode(invite.Code));
    }

    [Fact]
    public async Task ADeviceAnotherUserHoldsIsRefusedAndTheLinkStaysUsable()
    {
        var invite = _s.Invite();
        _s.Accounts.DevicesInUse.Add("members-laptop");
        var refused = await _s.Service.RedeemAsync(invite.Code, "Mika", "members-laptop", "Firefox", "0.9.0", null);
        Assert.Equal(GuestErrors.BadDevice.Code, refused.Error);
        Assert.Empty(_s.Accounts.Created);
        Assert.True(_s.Service.IsOpenCode(invite.Code));
    }

    [Fact]
    public void ThePassKeepsItsDeviceAndTheLabelsAreCleaned()
    {
        var invite = _s.Invite();
        var result = _s.Service.RedeemAsync(invite.Code, "Mika", "visitor-device", new string('D', 10240), "0.9.0\u0007", null).GetAwaiter().GetResult();
        Assert.True(result.Ok);
        Assert.Equal("visitor-device", _s.Service.LivePassOf(result.Value!.UserId)!.DeviceId);
        var made = _s.Accounts.Created.Single();
        Assert.Equal(GuestNames.MaxLabel, made.DeviceName.Length);
        Assert.Equal("0.9.0", made.AppVersion);
    }

    [Fact]
    public async Task AWrongCodeWritesNothing()
    {
        _s.Invite();
        var file = Path.Combine(_s.Folder, GuestPassStore.FileName);
        var before = File.GetLastWriteTimeUtc(file);
        await Task.Delay(30);
        await _s.Service.RedeemAsync(GuestCodes.New(), "Mika", "visitor-device", "Firefox", "0.9.0", null);
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
    }
}

public class PassEndsTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task AnInviterTakenOffTheListEndsTheirPassesAtOnce()
    {
        var pass = _s.Redeem(_s.Invite());
        Assert.Equal(0, await _s.Service.EndUnallowedAsync());
        _s.Settings.InviterIds = [];
        Assert.Equal(1, await _s.Service.EndUnallowedAsync());
        Assert.Equal([pass.UserId], _s.Accounts.Ended);
        Assert.Equal(EndReasons.InviterRemoved, _s.Service.All().Single().EndReason);
    }

    [Fact]
    public async Task EveryPassEndsWhenTheServerStops()
    {
        var pass = _s.Redeem(_s.Invite());
        _s.Invite();
        var onStop = new GuestPassesOnStop(_s.Service, _s.Notices, NullLogger<GuestPassesOnStop>.Instance);
        await onStop.StartAsync(CancellationToken.None);
        Assert.Empty(_s.Accounts.Ended);
        await onStop.StopAsync(CancellationToken.None);
        Assert.Equal([pass.UserId], _s.Accounts.Ended);
        Assert.All(_s.Service.All(), p => Assert.Equal((EndReasons.ServerStopped, "ended"), (p.EndReason, p.State)));
    }

    [Fact]
    public async Task AnEndedPassIsForgottenOnlyOnceItsAccountIsGone()
    {
        var pass = _s.Redeem(_s.Invite());
        _s.Accounts.EndFails.Add(pass.UserId);
        await _s.Service.EndAllAsync(EndReasons.Ended);
        _s.Clock.Advance(GuestPassService.EndedKept + TimeSpan.FromDays(1));
        await _s.Service.CleanupAsync(CancellationToken.None);
        // Still kept: its delete keeps failing, and the guard knows them by it.
        Assert.True(_s.Service.IsGuest(pass.UserId));
        _s.Accounts.EndFails.Clear();
        await _s.Service.CleanupAsync(CancellationToken.None);
        Assert.False(_s.Service.IsGuest(pass.UserId));
    }
}

public class StoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasuvideo-companion-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder.
        }
    }

    [Fact]
    public void AnUnreadableFileIsSetAsideAndTheStoreStartsEmpty()
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, GuestPassStore.FileName);
        File.WriteAllText(file, "{ not json");
        var store = new GuestPassStore(_folder);
        Assert.Equal(0, store.Read(p => p.Count));
        Assert.NotNull(store.SetAside);
        Assert.Equal("{ not json", File.ReadAllText(store.SetAside!));
        Assert.False(File.Exists(file));
        store.Update(p => { p.Add(new GuestPass { Id = Guid.NewGuid() }); return 0; });
        Assert.Equal(1, new GuestPassStore(_folder).Read(p => p.Count));
    }
}

public class VisitorRoutesTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    private GuestsController Controller(VisitorLimiter limiter, string address = "10.0.0.7")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return new GuestsController(_s.Service, new NobodyAuth(), limiter) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static int? Status(ActionResult? r) => (r as ObjectResult)?.StatusCode;

    [Fact]
    public async Task AFloodOfWrongCodesNeverBlocksARealLink()
    {
        var limiter = new VisitorLimiter(_s.Clock);
        var routes = Controller(limiter);
        for (var i = 0; i < VisitorLimiter.PerAddress; i++)
        {
            Assert.Equal(404, Status(routes.Preview(new CodeInput { Code = GuestCodes.New() }).Result));
        }

        var refused = routes.Preview(new CodeInput { Code = GuestCodes.New() }).Result;
        Assert.Equal(429, Status(refused));
        Assert.Equal("60", routes.Response.Headers.RetryAfter.ToString());

        // The same address, a real link: through, both ways in.
        var invite = _s.Invite();
        Assert.NotNull(routes.Preview(new CodeInput { Code = invite.Code }).Value);
        var redeemed = await routes.Redeem(new RedeemInput { Code = invite.Code, Nickname = "Mika", DeviceId = "visitor-device" });
        Assert.NotNull(redeemed.Value);
        Assert.Equal(429, Status((await routes.Redeem(new RedeemInput { Code = GuestCodes.New(), Nickname = "Mika", DeviceId = "visitor-device" })).Result));
    }

    private sealed class NobodyAuth : IAuthorizationContext
    {
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext) => Task.FromResult(new AuthorizationInfo());

        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext) => Task.FromResult(new AuthorizationInfo());
    }
}
