// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Plugin.KasuVideoCompanion.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>Who a guest-ended notice is about, and the Watch together groups their room may go by.</summary>
/// <param name="GuestUserId">The guest's account.</param>
/// <param name="Groups">The pass's group and the groups the guest was in, without dashes.</param>
public sealed record GuestEndedNotice(Guid GuestUserId, IReadOnlyList<string> Groups);

/// <summary>What <see cref="IEndNotices.CheckAsync"/> found.</summary>
public static class NoticeResults
{
    /// <summary>The rooms server took a signed ping.</summary>
    public const string Ok = "ok";

    /// <summary>No rooms server is set.</summary>
    public const string NotSet = "not-set";

    /// <summary>The rooms server couldn't be reached, or failed.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The rooms server answered, but refused the ping (its key check, or not a rooms server).</summary>
    public const string Refused = "refused";
}

/// <summary>
/// Telling the rooms server that a guest's pass ended, so the room lets them go at once instead
/// of when their ticket runs out (README.md, "Notices to the rooms server").
/// </summary>
public interface IEndNotices
{
    /// <summary>
    /// The notice for guest <paramref name="guestUserId"/>, read now: the account's sign-out
    /// empties the tracker, so this comes before the account ends.
    /// </summary>
    /// <param name="guestUserId">The guest's account.</param>
    /// <param name="passGroupId">The group the pass was made in.</param>
    /// <returns>The notice.</returns>
    GuestEndedNotice Prepare(Guid guestUserId, string passGroupId);

    /// <summary>Sends <paramref name="notice"/> in the background; never throws, never waits.</summary>
    /// <param name="passId">The pass, for the log.</param>
    /// <param name="notice">What to say.</param>
    void Send(Guid passId, GuestEndedNotice notice);

    /// <summary>Waits for the notices still on their way, <paramref name="limit"/> at most (the server's stop).</summary>
    /// <param name="limit">How long to wait in all.</param>
    /// <returns>When they're sent, or the time is up.</returns>
    Task FlushAsync(TimeSpan limit);

    /// <summary>Sends a signed ping to <paramref name="url"/> (the saved address when null), for setting it up.</summary>
    /// <param name="url">The rooms server's address to check, or null for the saved one.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>One of <see cref="NoticeResults"/>.</returns>
    Task<string> CheckAsync(string? url, CancellationToken cancellationToken);
}

/// <summary>
/// The companion's notices to the rooms Worker: <c>POST {RoomsServerUrl}/notices</c> with a small
/// JSON body (<c>{"type": "guest-ended", "userId", "groups", "at"}</c>), signed with
/// <see cref="NoticeKey"/> in <see cref="SignatureHeader"/>. The Worker checks the signature with
/// the key it reads from this server, then closes the guest's sockets, revokes their tickets and
/// takes them out of the voice call. A notice is tried <see cref="Tries"/> times; a refusal isn't
/// tried again. When it can't get through, the room still lets the guest go when their ticket
/// runs out (ten minutes at most), as before. Nothing but the pass id is logged.
/// </summary>
public sealed class EndNotices : IEndNotices
{
    /// <summary>The header with the body's signature.</summary>
    public const string SignatureHeader = "X-Kasu-Signature";

    /// <summary>The most groups one notice names.</summary>
    public const int MaxGroups = 10;

    /// <summary>The waits before each try: at once, then after 1 and 5 seconds.</summary>
    public static readonly IReadOnlyList<TimeSpan> Tries = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    /// <summary>How long one try may take.</summary>
    public static readonly TimeSpan TryTimeout = TimeSpan.FromSeconds(5);

    private readonly NoticeKey _key;
    private readonly GroupTracker _groups;
    private readonly Func<PluginConfiguration> _settings;
    private readonly Func<HttpClient> _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<EndNotices> _logger;
    private readonly IReadOnlyList<TimeSpan> _tries;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<Task, byte> _pending = new();

    /// <summary>Creates the notices.</summary>
    /// <param name="key">What they're signed with.</param>
    /// <param name="groups">Which groups each guest was in.</param>
    /// <param name="settings">The current settings (the rooms server's address).</param>
    /// <param name="http">An HTTP client (Jellyfin's).</param>
    /// <param name="clock">The time.</param>
    /// <param name="logger">Where a notice that didn't get through is noted.</param>
    /// <param name="tries">The waits before each try (tests shorten them).</param>
    /// <param name="timeout">How long one try may take (tests shorten it).</param>
    public EndNotices(
        NoticeKey key,
        GroupTracker groups,
        Func<PluginConfiguration> settings,
        Func<HttpClient> http,
        TimeProvider clock,
        ILogger<EndNotices> logger,
        IReadOnlyList<TimeSpan>? tries = null,
        TimeSpan? timeout = null)
    {
        _key = key;
        _groups = groups;
        _settings = settings;
        _http = http;
        _clock = clock;
        _logger = logger;
        _tries = tries ?? Tries;
        _timeout = timeout ?? TryTimeout;
    }

    /// <inheritdoc />
    public GuestEndedNotice Prepare(Guid guestUserId, string passGroupId)
    {
        var groups = new List<string>();
        foreach (var group in _groups.GroupsOf(guestUserId).Prepend(passGroupId))
        {
            var id = group.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (id.Length > 0 && !groups.Contains(id) && groups.Count < MaxGroups)
            {
                groups.Add(id);
            }
        }

        return new GuestEndedNotice(guestUserId, groups);
    }

    /// <inheritdoc />
    public void Send(Guid passId, GuestEndedNotice notice)
    {
        var url = _settings().RoomsServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var body = Body("guest-ended", notice);
        var task = Task.Run(() => DeliverAsync(url, body, passId));
        _pending[task] = 0;
        task.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <inheritdoc />
    public async Task FlushAsync(TimeSpan limit)
    {
        var all = Task.WhenAll(_pending.Keys);
        await Task.WhenAny(all, Task.Delay(limit, _clock)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> CheckAsync(string? url, CancellationToken cancellationToken)
    {
        url ??= _settings().RoomsServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return NoticeResults.NotSet;
        }

        return await PostAsync(url, Body("ping", null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The URL notices go to under the rooms server's address.</summary>
    /// <param name="roomsServerUrl">The address (<c>https://example.org/rooms</c>).</param>
    /// <returns>Its <c>/notices</c>.</returns>
    public static Uri NoticesUri(string roomsServerUrl) => new(roomsServerUrl.TrimEnd('/') + "/notices");

    private async Task DeliverAsync(string url, byte[] body, Guid passId)
    {
        var result = NoticeResults.Unreachable;
        foreach (var wait in _tries)
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _clock).ConfigureAwait(false);
            }

            result = await PostAsync(url, body, CancellationToken.None).ConfigureAwait(false);
            if (result != NoticeResults.Unreachable)
            {
                break;
            }
        }

        if (result != NoticeResults.Ok)
        {
            _logger.LogWarning(
                "The rooms server didn't take guest pass {Pass}'s end ({Result}); the room lets the guest go when their ticket runs out",
                passId,
                result);
        }
    }

    private async Task<string> PostAsync(string url, byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, NoticesUri(url));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add(SignatureHeader, _key.Sign(body));
            using var answer = await _http().SendAsync(request, timeout.Token).ConfigureAwait(false);
            var status = (int)answer.StatusCode;
            return status switch
            {
                >= 200 and < 300 => NoticeResults.Ok,
                >= 400 and < 500 => NoticeResults.Refused,
                _ => NoticeResults.Unreachable,
            };
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or UriFormatException or InvalidOperationException)
        {
            return NoticeResults.Unreachable;
        }
    }

    private byte[] Body(string type, GuestEndedNotice? notice)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("type", type);
            if (notice is not null)
            {
                json.WriteString("userId", notice.GuestUserId.ToString("N"));
                json.WriteStartArray("groups");
                foreach (var group in notice.Groups)
                {
                    json.WriteStringValue(group);
                }

                json.WriteEndArray();
            }

            json.WriteNumber("at", _clock.GetUtcNow().ToUnixTimeMilliseconds());
            json.WriteEndObject();
        }

        return stream.ToArray();
    }
}
