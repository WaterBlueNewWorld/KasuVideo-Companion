// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.KasuVideoCompanion.Api;
using Jellyfin.Plugin.KasuVideoCompanion.Configuration;
using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

/// <summary>A test's HTTP server: answers each request with <see cref="Answer"/>, and keeps what came.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public Func<int, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

    public List<(HttpMethod Method, Uri Uri, byte[] Body, string? Signature)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var signature = request.Headers.TryGetValues(EndNotices.SignatureHeader, out var values) ? values.Single() : null;
        int index;
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!, body, signature));
            index = Requests.Count;
        }

        return await Answer(index, cancellationToken);
    }
}

/// <summary>A logger that keeps the warnings it gets.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            lock (Warnings)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

public class NoticeKeyTests : IDisposable
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
    public void AKeyIsMadeOnceAndKept()
    {
        using var first = new NoticeKey(_folder);
        Assert.True(File.Exists(Path.Combine(_folder, NoticeKey.FileName)));
        using var again = new NoticeKey(_folder);
        Assert.Equal(first.PublicKey, again.PublicKey);
    }

    [Fact]
    public void TheKeyIsNeverInThePluginConfiguration()
    {
        Assert.DoesNotContain(typeof(PluginConfiguration).GetProperties(), p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ItsPublicHalfVerifiesWhatItSigns()
    {
        using var key = new NoticeKey(_folder);
        var data = Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"at\":1}");
        var signature = Convert.FromBase64String(key.Sign(data));
        // r then s, as WebCrypto verifies.
        Assert.Equal(64, signature.Length);
        using var check = ECDsa.Create();
        check.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
        Assert.Equal(256, check.KeySize);
        Assert.True(check.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        data[^2] = (byte)'2';
        Assert.False(check.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void AnUnreadableKeyIsReplaced()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, NoticeKey.FileName), "not a key");
        using var replaced = new NoticeKey(_folder);
        Assert.NotEmpty(replaced.PublicKey);
        using var after = new NoticeKey(_folder);
        Assert.Equal(replaced.PublicKey, after.PublicKey);
    }
}

public class EndNoticesTests : IDisposable
{
    private static readonly Guid Guest = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Pass = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private const string PassGroup = "0123456789abcdef0123456789abcdef";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasuvideo-companion-tests", Guid.NewGuid().ToString("N"));
    private readonly NoticeKey _key;
    private readonly GroupTracker _groups = new();
    private readonly PluginConfiguration _settings = new() { RoomsServerUrl = "https://rooms.test/rooms" };
    private readonly StubHandler _server = new();
    private readonly FakeClock _clock = new();
    private readonly ListLogger<EndNotices> _log = new();
    private readonly EndNotices _notices;

    public EndNoticesTests()
    {
        _key = new NoticeKey(_folder);
        _notices = new EndNotices(
            _key,
            _groups,
            () => _settings,
            () => new HttpClient(_server, disposeHandler: false),
            _clock,
            _log,
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)],
            TimeSpan.FromMilliseconds(200));
    }

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder.
        }
    }

    private bool Verifies(byte[] body, string? signature)
    {
        using var check = ECDsa.Create();
        check.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_key.PublicKey), out _);
        return signature is not null
            && check.VerifyData(body, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    [Fact]
    public void ANoticeNamesThePassGroupAndTheGroupsTheGuestWasIn()
    {
        _groups.Joined(Guest, "guest-device", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");
        _groups.Joined(Guest, "other-device", PassGroup);
        var notice = _notices.Prepare(Guest, PassGroup);
        Assert.Equal(Guest, notice.GuestUserId);
        Assert.Equal(PassGroup, notice.Groups[0]);
        Assert.Equal(2, notice.Groups.Count);
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", notice.Groups);

        // A group however it's written: without dashes, in lower case.
        var dashed = _notices.Prepare(Guid.NewGuid(), "01234567-89AB-CDEF-0123-456789ABCDEF");
        Assert.Equal(PassGroup, Assert.Single(dashed.Groups));
    }

    [Fact]
    public void ANoticeNamesTenGroupsAtMost()
    {
        for (var i = 0; i < 20; i++)
        {
            _groups.Joined(Guest, $"device-{i}", Guid.NewGuid().ToString("N"));
        }

        Assert.Equal(EndNotices.MaxGroups, _notices.Prepare(Guest, PassGroup).Groups.Count);
    }

    [Fact]
    public async Task NothingIsSentWithoutARoomsServer()
    {
        _settings.RoomsServerUrl = string.Empty;
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup]));
        await _notices.FlushAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(_server.Requests);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task ANoticeSaysWhoAndWhereAndIsSigned()
    {
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup, "ffffffffffffffffffffffffffffffff"]));
        await _notices.FlushAsync(TimeSpan.FromSeconds(5));
        var (method, uri, body, signature) = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://rooms.test/rooms/notices", uri.ToString());
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("guest-ended", root.GetProperty("type").GetString());
        Assert.Equal(Guest.ToString("N"), root.GetProperty("userId").GetString());
        Assert.Equal([PassGroup, "ffffffffffffffffffffffffffffffff"], root.GetProperty("groups").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal(new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds(), root.GetProperty("at").GetInt64());
        Assert.True(Verifies(body, signature));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task ARoomsServerAddressWithASlashStillGetsNotices()
    {
        _settings.RoomsServerUrl = "https://rooms.test/rooms/";
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup]));
        await _notices.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("https://rooms.test/rooms/notices", Assert.Single(_server.Requests).Uri.ToString());
    }

    [Fact]
    public async Task AnUnreachableServerIsTriedThreeTimesThenLogged()
    {
        _server.Answer = (_, _) => throw new HttpRequestException("down");
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup]));
        await _notices.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, _server.Requests.Count);
        var warning = Assert.Single(_log.Warnings);
        Assert.Contains(Pass.ToString(), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(Guest.ToString("N"), warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerErrorIsTriedAgain()
    {
        _server.Answer = (n, _) => Task.FromResult(new HttpResponseMessage(n < 3 ? HttpStatusCode.BadGateway : HttpStatusCode.NoContent));
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup]));
        await _notices.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, _server.Requests.Count);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task ARefusalIsNotTriedAgain()
    {
        _server.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup]));
        await _notices.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_server.Requests);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public async Task ATryThatHangsGivesUp()
    {
        _server.Answer = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        var started = DateTime.UtcNow;
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup]));
        await _notices.FlushAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, _server.Requests.Count);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public async Task TheStopWaitsForNoticesButNotForever()
    {
        var release = new TaskCompletionSource();
        _server.Answer = async (_, _) =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        _notices.Send(Pass, new GuestEndedNotice(Guest, [PassGroup]));
        var started = DateTime.UtcNow;
        await _notices.FlushAsync(TimeSpan.FromMilliseconds(100));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));

        // A notice that gets through in time is waited for.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            release.SetResult();
        });
        await _notices.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task TheCheckSaysWhatItFound()
    {
        Assert.Equal(NoticeResults.Ok, await _notices.CheckAsync(null, CancellationToken.None));
        var (_, uri, body, signature) = Assert.Single(_server.Requests);
        Assert.Equal("https://rooms.test/rooms/notices", uri.ToString());
        using (var json = JsonDocument.Parse(body))
        {
            Assert.Equal("ping", json.RootElement.GetProperty("type").GetString());
            Assert.False(json.RootElement.TryGetProperty("userId", out _));
        }

        Assert.True(Verifies(body, signature));

        // An address given is checked instead of the saved one.
        Assert.Equal(NoticeResults.Ok, await _notices.CheckAsync("https://other.test/rooms", CancellationToken.None));
        Assert.Equal("https://other.test/rooms/notices", _server.Requests[^1].Uri.ToString());

        _server.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Equal(NoticeResults.Refused, await _notices.CheckAsync(null, CancellationToken.None));
        _server.Answer = (_, _) => throw new HttpRequestException("down");
        Assert.Equal(NoticeResults.Unreachable, await _notices.CheckAsync(null, CancellationToken.None));
        _server.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(NoticeResults.Unreachable, await _notices.CheckAsync(null, CancellationToken.None));
        _settings.RoomsServerUrl = string.Empty;
        var asked = _server.Requests.Count;
        Assert.Equal(NoticeResults.NotSet, await _notices.CheckAsync(null, CancellationToken.None));
        Assert.Equal(asked, _server.Requests.Count);
    }
}

public class PassEndNoticeTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    private void ToldOnce(RedeemedPass pass)
    {
        var (passId, notice) = Assert.Single(_s.Notices.Sent);
        Assert.Equal(pass.UserId, notice.GuestUserId);
        Assert.Equal(_s.Service.All().Single(p => p.GuestUserId == pass.UserId).Id, passId);
        // Read while the account still was (its sign-out empties the tracker).
        var prepared = Assert.Single(_s.Notices.Prepared);
        Assert.Equal((pass.UserId, Setup.Group.ToString("N"), false), prepared);
    }

    [Fact]
    public async Task TheInvitersEndTellsTheRoomsServer()
    {
        var pass = _s.Redeem(_s.Invite());
        await _s.Service.EndAsync(_s.Service.Me(pass.UserId)!.PassId, Setup.Owner);
        ToldOnce(pass);
    }

    [Fact]
    public async Task AGuestLeavingTellsTheRoomsServer()
    {
        var pass = _s.Redeem(_s.Invite());
        Assert.Null(await _s.Service.EndMineAsync(pass.UserId));
        ToldOnce(pass);
    }

    [Fact]
    public async Task PassesTurnedOffTellTheRoomsServer()
    {
        var pass = _s.Redeem(_s.Invite());
        await _s.Service.EndAllAsync(EndReasons.TurnedOff);
        ToldOnce(pass);
    }

    [Fact]
    public async Task AnInviterTakenOffTheListTellsTheRoomsServer()
    {
        var pass = _s.Redeem(_s.Invite());
        _s.Settings.InviterIds = [];
        await _s.Service.EndUnallowedAsync();
        ToldOnce(pass);
    }

    [Fact]
    public async Task AnIdleGuestTellsTheRoomsServer()
    {
        var pass = _s.Redeem(_s.Invite());
        _s.Clock.Advance(TimeSpan.FromMinutes(_s.Settings.IdleMinutes + 1));
        await _s.Service.CleanupAsync(CancellationToken.None);
        ToldOnce(pass);
    }

    [Fact]
    public async Task APassThatRanOutTellsTheRoomsServer()
    {
        var pass = _s.Redeem(_s.Invite(hours: 3));
        _s.Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        _s.Accounts.Activity[pass.UserId] = _s.Clock.Now;
        await _s.Service.CleanupAsync(CancellationToken.None);
        ToldOnce(pass);
    }

    [Fact]
    public async Task ALinkNeverOpenedTellsNobody()
    {
        _s.Invite();
        await _s.Service.EndAllAsync(EndReasons.Ended);
        Assert.Empty(_s.Notices.Prepared);
        Assert.Empty(_s.Notices.Sent);
    }

    [Fact]
    public async Task AnEndedPassIsntToldTwice()
    {
        var pass = _s.Redeem(_s.Invite());
        await _s.Service.EndMineAsync(pass.UserId);
        await _s.Service.EndAllAsync(EndReasons.Ended);
        ToldOnce(pass);
    }

    [Fact]
    public async Task TheServersStopWaitsForTheNotices()
    {
        _s.Redeem(_s.Invite());
        var onStop = new GuestPassesOnStop(_s.Service, _s.Notices, NullLogger<GuestPassesOnStop>.Instance);
        await onStop.StopAsync(CancellationToken.None);
        Assert.Single(_s.Notices.Sent);
        Assert.Equal([GuestPassesOnStop.NoticesWait], _s.Notices.Flushed);
        Assert.Equal(TimeSpan.FromSeconds(5), GuestPassesOnStop.NoticesWait);
    }
}

public class RoomsServerSettingTests
{
    [Fact]
    public void ASaveWithoutTheAddressKeepsIt()
    {
        var current = new PluginConfiguration { RoomsServerUrl = "https://rooms.test/rooms" };
        Assert.Equal("https://rooms.test/rooms", new SettingsInput { RoomsServerUrl = null }.ToConfiguration(current).RoomsServerUrl);
        Assert.Equal(string.Empty, new SettingsInput { RoomsServerUrl = string.Empty }.ToConfiguration(current).RoomsServerUrl);
        Assert.Equal("https://new.test/rooms", new SettingsInput { RoomsServerUrl = "  https://new.test/rooms " }.ToConfiguration(current).RoomsServerUrl);
    }

    [Fact]
    public void AnOlderKasuVideosSaveKeepsTheAddress()
    {
        // KasuVideo before the field sends every other setting, and not this one.
        var input = JsonSerializer.Deserialize<SettingsInput>("{\"GuestPassesEnabled\":true,\"RoomMaxMembers\":16,\"RoomMaxGuests\":4}")!;
        var current = new PluginConfiguration { RoomsServerUrl = "https://rooms.test/rooms" };
        Assert.Equal("https://rooms.test/rooms", input.ToConfiguration(current).RoomsServerUrl);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://alpha.example.org/rooms", true)]
    [InlineData("http://host.docker.internal:9150/rooms", true)]
    [InlineData("ftp://example.org/rooms", false)]
    [InlineData("/rooms", false)]
    [InlineData("https://example.org/rooms?x=1", false)]
    [InlineData("https://example.org/rooms#x", false)]
    [InlineData("https://user:secret@example.org/rooms", false)]
    [InlineData("not a url", false)]
    public void TheAddressIsHttpOrHttps(string url, bool valid)
    {
        Assert.Equal(valid, SettingsBounds.ValidRoomsServerUrl(url));
        var problems = SettingsBounds.Problems(new PluginConfiguration { RoomsServerUrl = url });
        Assert.Equal(!valid, problems.ContainsKey(nameof(PluginConfiguration.RoomsServerUrl)));
    }

    [Fact]
    public void AVeryLongAddressIsRefused()
    {
        Assert.False(SettingsBounds.ValidRoomsServerUrl("https://example.org/" + new string('a', SettingsBounds.MaxUrlLength)));
    }

    [Fact]
    public void TheSettingsSayTheAddress()
    {
        var settings = CompanionSettings.Of(new PluginConfiguration { RoomsServerUrl = "https://rooms.test/rooms" });
        Assert.Equal("https://rooms.test/rooms", settings.RoomsServerUrl);
    }

    [Fact]
    public void TheKeyIsForAnyoneAndTheCheckForAdministrators()
    {
        var key = typeof(CompanionController).GetMethod(nameof(CompanionController.GetNoticeKey))!;
        Assert.NotNull(key.GetCustomAttribute<AllowAnonymousAttribute>());
        var check = typeof(SettingsController).GetMethod(nameof(SettingsController.CheckRoomsServer))!;
        Assert.Null(check.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(Policies.RequiresElevation, check.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void TheCompanionSaysItSendsNotices()
    {
        Assert.Contains("endNotices", CompanionController.Capabilities);
    }
}
