// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Globalization;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// What a guest's token may call on the server: an allow-list (README.md, "What a guest may
/// call"). Everything not on it is refused, whatever app holds the token: a guest watches their
/// room, and nothing else. Jellyfin's own API is matched by action name (a <c>…Legacy</c> route
/// counts as the action it's a legacy of); other plugins are refused, except the companion's
/// own few routes a guest needs. A pure function, so it's tested without a server
/// (<see cref="GuestGuard"/> applies it).
/// <para>
/// Every argument that names something is checked, not only the route's item: Jellyfin's
/// actions also read an item from the query (trickplay's <c>mediaSourceId</c>, a subtitle's
/// <c>itemId</c>), and some take another session, device or play session (G8, the penetration
/// test). A guest's request only ever names their room's items and their own session.
/// </para>
/// </summary>
public static class GuestAccessRules
{
    /// <summary>The namespace of Jellyfin's own controllers.</summary>
    public const string JellyfinControllers = "Jellyfin.Api.Controllers";

    /// <summary>Refused: not something a guest may call.</summary>
    public const string NotAllowed = "guest-not-allowed";

    /// <summary>Refused: an item their room hasn't queued.</summary>
    public const string ItemNotAllowed = "guest-item";

    /// <summary>Refused: a Watch together group that isn't their room's.</summary>
    public const string GroupNotAllowed = "guest-group";

    /// <summary>Refused: a device that isn't the one their pass was opened on.</summary>
    public const string DeviceNotAllowed = "guest-device";

    /// <summary>Refused: too many stalls (SyncPlay Buffering) in a minute.</summary>
    public const string Busy = "guest-busy";

    /// <summary>The largest bitrate test a guest may ask for: KasuVideo's own is 256 KiB, then 2 MiB.</summary>
    public const int MaxBitrateTestBytes = 2 * 1024 * 1024;

    /// <summary>Calls that only concern the guest's own session, or that anyone may make anyway.</summary>
    private static readonly HashSet<string> Always = new(StringComparer.Ordinal)
    {
        // Who they are, the server, the time (Watch together measures the server's clock).
        // /System/Ping's action is PingSystem (GetPingSystem and PostPingSystem are its routes' names).
        "GetCurrentUser", "GetPublicSystemInfo", "PingSystem", "GetUtcTime",
        "GetBrandingOptions", "GetBrandingCss", "GetDisplayPreferences",
        "GetCountries", "GetCultures", "GetLocalizationOptions", "GetParentalRatings",

        // Their session: capabilities, what they're watching, signing out (each checked below).
        "PostCapabilities", "PostFullCapabilities", "ReportSessionEnded", "ReportViewing",
        "ReportPlaybackStart", "ReportPlaybackProgress", "ReportPlaybackStopped", "PingPlaybackSession",
        "GetBitrateTestBytes", "StopEncodingProcess", "GetFallbackFontList", "GetFallbackFont",

        // Being in their room's group: never controlling it.
        "SyncPlayLeaveGroup", "SyncPlayPing", "SyncPlayBuffering", "SyncPlayReady", "SyncPlaySetIgnoreWait",
    };

    /// <summary>Calls about one item: allowed for the items their room queued (and their seasons and series).</summary>
    private static readonly HashSet<string> ForAnItem = new(StringComparer.Ordinal)
    {
        "GetItem", "GetPlaybackInfo", "GetPostedPlaybackInfo",
        "GetVideoStream", "GetVideoStreamByContainer", "HeadVideoStream", "HeadVideoStreamByContainer",
        "GetMasterHlsVideoPlaylist", "HeadMasterHlsVideoPlaylist", "GetVariantHlsVideoPlaylist", "GetHlsVideoSegment",

        // GetHlsPlaylistLegacy (GetHlsVideoSegmentLegacy is GetHlsVideoSegment's).
        "GetHlsPlaylist",
        "GetSubtitle", "GetSubtitleWithTicks", "GetSubtitlePlaylist", "GetAttachment",
        "GetTrickplayHlsPlaylist", "GetTrickplayTileImage", "GetItemSegments",
        "OnPlaybackStart", "OnPlaybackProgress", "OnPlaybackStopped",
    };

    /// <summary>Picture actions that are about more than a picture: the image list shows the server's file paths.</summary>
    private static readonly HashSet<string> NotPictures = new(StringComparer.Ordinal) { "GetItemImageInfos" };

    /// <summary>The route values that name the item, by the names Jellyfin's routes use.</summary>
    private static readonly string[] ItemRouteKeys = ["itemId", "routeItemId", "videoId"];

    /// <summary>
    /// Every argument that names an item, in the route or the query: trickplay reads
    /// <c>mediaSourceId</c> as an item, a subtitle's query <c>itemId</c> wins over its route's.
    /// </summary>
    private static readonly string[] ItemArguments = ["itemId", "routeItemId", "videoId"];

    /// <summary>Arguments that name a media source: the item's own, or one of its versions.</summary>
    private static readonly string[] SourceArguments = ["mediaSourceId", "routeMediaSourceId"];

    /// <summary>
    /// The companion's own routes a guest needs: is it there, room settings, their pass, and
    /// leaving (which ends only their own pass, <see cref="GuestPassService.EndMineAsync"/>).
    /// </summary>
    private static readonly HashSet<string> CompanionRoutes = new(StringComparer.Ordinal)
    {
        "CompanionController.Info", "SettingsController.GetRoomSettings", "GuestsController.Me", "GuestsController.EndMe",
    };

    /// <summary>The companion's controllers' namespace.</summary>
    private static readonly string CompanionControllers = typeof(Api.GuestsController).Namespace!;

    /// <summary>What to do with a guest's request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="scope">What the guest may reach.</param>
    /// <returns>The verdict.</returns>
    public static GuestVerdict Decide(GuestRequest request, GuestScope scope)
    {
        var action = Plain(request.Action);

        // Only from the device their pass was opened on (signing out is always fine).
        if (scope.DeviceId is { Length: > 0 } pinned && action != "ReportSessionEnded"
            && !string.Equals(request.Device, pinned, StringComparison.OrdinalIgnoreCase))
        {
            return GuestVerdict.Deny(DeviceNotAllowed);
        }

        if (request.Namespace == CompanionControllers)
        {
            return CompanionRoutes.Contains($"{request.Controller}Controller.{request.Action}")
                ? GuestVerdict.Allow
                : GuestVerdict.Deny(NotAllowed);
        }

        if (request.Namespace != JellyfinControllers)
        {
            // Another plugin.
            return GuestVerdict.Deny(NotAllowed);
        }

        // Nothing they send may name another session's play session, a live stream, or a device
        // that isn't theirs, whatever the action.
        if (Theirs(request, scope) is { } refused)
        {
            return refused;
        }

        if (Always.Contains(action))
        {
            return Own(action, request, scope);
        }

        // Pictures are public in Jellyfin anyway.
        if (request.Controller == "Image" && !NotPictures.Contains(action)
            && (action.StartsWith("Get", StringComparison.Ordinal) || action.StartsWith("Head", StringComparison.Ordinal)))
        {
            return GuestVerdict.Allow;
        }

        if (ForAnItem.Contains(action))
        {
            var item = ItemRouteKeys.Select(k => request.Route.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);
            return Guid.TryParse(item, out var id) && scope.ItemAllowed(id) && ArgumentsAllowed(request, scope, id)
                ? GuestVerdict.Allow
                : GuestVerdict.Deny(ItemNotAllowed);
        }

        switch (action)
        {
            case "GetUserById":
                return Guid.TryParse(request.Route.GetValueOrDefault("userId"), out var user) && user == scope.GuestId
                    ? GuestVerdict.Allow
                    : GuestVerdict.Deny(NotAllowed);

            case "GetItems":
                // Only items by id (a room's playlist), all of them queued in the room.
                var ids = (request.Ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return ids.Length > 0 && ids.All(i => Guid.TryParse(i, out var id) && scope.ItemAllowed(id))
                    ? GuestVerdict.Allow
                    : GuestVerdict.Deny(ItemNotAllowed);

            case "SyncPlayJoinGroup":
                return InScope(request.BodyGroupId?.ToString(), scope) ? GuestVerdict.Allow : GuestVerdict.Deny(GroupNotAllowed);

            case "SyncPlayGetGroup":
                return InScope(request.Route.GetValueOrDefault("id"), scope) ? GuestVerdict.Allow : GuestVerdict.Deny(GroupNotAllowed);

            case "SyncPlayGetGroups":
                return GuestVerdict.TrimGroups;

            default:
                return GuestVerdict.Deny(NotAllowed);
        }
    }

    /// <summary>An action's name without a trailing <c>Legacy</c> (its legacy route).</summary>
    /// <param name="action">The action's name.</param>
    /// <returns>The name.</returns>
    public static string Plain(string action) =>
        action.EndsWith("Legacy", StringComparison.Ordinal) ? action[..^"Legacy".Length] : action;

    /// <summary>Refuses a request naming someone else's device or play session, or a live stream.</summary>
    private static GuestVerdict? Theirs(GuestRequest request, GuestScope scope)
    {
        if (Value(request, "deviceId") is { Length: > 0 } device && !string.Equals(device, request.Device, StringComparison.OrdinalIgnoreCase))
        {
            return GuestVerdict.Deny(DeviceNotAllowed);
        }

        foreach (var playSession in new[] { Value(request, "playSessionId"), request.Body.GetValueOrDefault("PlaySessionId") })
        {
            if (playSession is { Length: > 0 } && !scope.OwnJob(playSession))
            {
                return GuestVerdict.Deny(NotAllowed);
            }
        }

        // Live streams (TV, a stream a server opened for someone) are never a guest's.
        return Value(request, "liveStreamId") is { Length: > 0 } || request.Body.GetValueOrDefault("LiveStreamId") is { Length: > 0 }
            ? GuestVerdict.Deny(ItemNotAllowed)
            : null;
    }

    /// <summary>The calls on their own session: each may name only their session and their room's items.</summary>
    private static GuestVerdict Own(string action, GuestRequest request, GuestScope scope)
    {
        switch (action)
        {
            case "PostCapabilities" or "PostFullCapabilities":
                return OwnSession(Value(request, "id"), scope) ? GuestVerdict.Allow : GuestVerdict.Deny(NotAllowed);

            case "ReportViewing":
                return OwnSession(Value(request, "sessionId"), scope) && ItemOrNone(Value(request, "itemId"), scope)
                    ? GuestVerdict.Allow
                    : GuestVerdict.Deny(NotAllowed);

            case "ReportPlaybackStart" or "ReportPlaybackProgress" or "ReportPlaybackStopped":
                return ItemOrNone(request.Body.GetValueOrDefault("ItemId"), scope)
                    && SourceOrNone(request.Body.GetValueOrDefault("ItemId"), request.Body.GetValueOrDefault("MediaSourceId"), scope)
                    ? GuestVerdict.Allow
                    : GuestVerdict.Deny(ItemNotAllowed);

            case "StopEncodingProcess":
                // Theirs: the device is checked above; its play session too.
                return GuestVerdict.Allow;

            case "GetBitrateTestBytes":
                return Value(request, "size") is not { } size
                    || int.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes <= MaxBitrateTestBytes
                    ? GuestVerdict.Allow
                    : GuestVerdict.Deny(NotAllowed);

            case "SyncPlaySetIgnoreWait":
                // A guest's stalls never hold the room (the companion sets it when they join).
                return string.Equals(request.Body.GetValueOrDefault("IgnoreWait"), bool.FalseString, StringComparison.OrdinalIgnoreCase)
                    ? GuestVerdict.Deny(NotAllowed)
                    : GuestVerdict.Allow;

            case "SyncPlayBuffering":
                return scope.MayStall() ? GuestVerdict.Allow : GuestVerdict.Deny(Busy);

            default:
                return GuestVerdict.Allow;
        }
    }

    /// <summary>Whether every item and media source the request names, beyond the route's, is allowed.</summary>
    private static bool ArgumentsAllowed(GuestRequest request, GuestScope scope, Guid routeItem)
    {
        foreach (var name in ItemArguments)
        {
            if (Value(request, name) is { Length: > 0 } value && !(Guid.TryParse(value, out var id) && (id == routeItem || scope.ItemAllowed(id))))
            {
                return false;
            }
        }

        foreach (var value in SourceArguments.Select(name => Value(request, name)).Append(request.Body.GetValueOrDefault("MediaSourceId")))
        {
            if (value is { Length: > 0 } && !SourceAllowed(routeItem, value, scope))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A media source of <paramref name="item"/>: the item itself, another allowed item, or one of its versions.</summary>
    private static bool SourceAllowed(Guid item, string source, GuestScope scope) =>
        Guid.TryParse(source, out var id) && (id == item || scope.ItemAllowed(id)) || scope.SourceOf(item, source);

    private static bool SourceOrNone(string? itemId, string? source, GuestScope scope) =>
        string.IsNullOrEmpty(source) || Guid.TryParse(itemId, out var item) && SourceAllowed(item, source, scope);

    private static bool OwnSession(string? sessionId, GuestScope scope) =>
        string.IsNullOrEmpty(sessionId) || string.Equals(sessionId, scope.SessionId, StringComparison.OrdinalIgnoreCase);

    private static bool ItemOrNone(string? itemId, GuestScope scope) =>
        string.IsNullOrEmpty(itemId) || Guid.TryParse(itemId, out var id) && (id == Guid.Empty || scope.ItemAllowed(id));

    private static string? Value(GuestRequest request, string name) =>
        request.Arguments.TryGetValue(name, out var value) ? value : request.Route.GetValueOrDefault(name);

    private static bool InScope(string? groupId, GuestScope scope) =>
        GuestPassService.NormalizeGroupId(groupId) is { } group && scope.Groups.Contains(group);
}

/// <summary>A guest's request, as the guard sees it.</summary>
/// <param name="Namespace">The controller's namespace.</param>
/// <param name="Controller">The controller's name (without <c>Controller</c>).</param>
/// <param name="Action">The action's name.</param>
/// <param name="Route">The route's values.</param>
/// <param name="Ids">The <c>ids</c> query, if any.</param>
/// <param name="BodyGroupId">The <c>GroupId</c> in the request's body, if any.</param>
public sealed record GuestRequest(
    string Namespace,
    string Controller,
    string Action,
    IReadOnlyDictionary<string, string?> Route,
    string? Ids,
    Guid? BodyGroupId)
{
    private static readonly IReadOnlyDictionary<string, string?> None = new Dictionary<string, string?>();

    /// <summary>Gets the action's simple arguments (route and query), by name, any case.</summary>
    public IReadOnlyDictionary<string, string?> Arguments { get; init; } = None;

    /// <summary>Gets what the request's body says that the rules read: <c>ItemId</c>, <c>MediaSourceId</c>, <c>PlaySessionId</c>, <c>LiveStreamId</c>, <c>IgnoreWait</c>.</summary>
    public IReadOnlyDictionary<string, string?> Body { get; init; } = None;

    /// <summary>Gets the device the request comes from (Jellyfin's claim).</summary>
    public string? Device { get; init; }
}

/// <summary>What a guest may reach.</summary>
/// <param name="GuestId">The guest's user.</param>
/// <param name="Groups">The Watch together groups: their pass's, and those their inviter is in now.</param>
/// <param name="ItemAllowed">Whether an item was queued in one of those groups (and the guest may see it).</param>
public sealed record GuestScope(Guid GuestId, IReadOnlySet<string> Groups, Func<Guid, bool> ItemAllowed)
{
    /// <summary>Gets the device their pass was opened on (null for a pass from before G8: not checked).</summary>
    public string? DeviceId { get; init; }

    /// <summary>Gets the guest's own session on this device, if Jellyfin has one.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets whether a play session is the guest's own (or none runs under it).</summary>
    public Func<string, bool> OwnJob { get; init; } = _ => false;

    /// <summary>Gets whether a media source (by id) is one of an item's own sources (its versions).</summary>
    public Func<Guid, string, bool> SourceOf { get; init; } = (_, _) => false;

    /// <summary>Gets whether the guest may stall the room once more now (counts it when so).</summary>
    public Func<bool> MayStall { get; init; } = () => true;
}

/// <summary>What to do with a guest's request.</summary>
/// <param name="Kind">Allow, refuse, or allow and trim the group list.</param>
/// <param name="Error">The refusal's code.</param>
public sealed record GuestVerdict(GuestVerdictKind Kind, string? Error)
{
    /// <summary>Gets the verdict that lets the request through.</summary>
    public static GuestVerdict Allow { get; } = new(GuestVerdictKind.Allow, null);

    /// <summary>Gets the verdict that lets it through and trims the groups listed to the guest's.</summary>
    public static GuestVerdict TrimGroups { get; } = new(GuestVerdictKind.TrimGroups, null);

    /// <summary>The verdict that refuses it.</summary>
    /// <param name="error">Why.</param>
    /// <returns>The verdict.</returns>
    public static GuestVerdict Deny(string error) => new(GuestVerdictKind.Deny, error);
}

/// <summary>The kinds of verdict.</summary>
public enum GuestVerdictKind
{
    /// <summary>Let it through.</summary>
    Allow,

    /// <summary>Let it through, and trim the groups listed to the guest's.</summary>
    TrimGroups,

    /// <summary>Refuse it (403).</summary>
    Deny,
}
