// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// What a guest's token may call on the server: an allow-list (README.md, "What a guest may
/// call"). Everything not on it is refused, whatever app holds the token: a guest watches their
/// room, and nothing else. Jellyfin's own API is matched by action name (a <c>…Legacy</c> route
/// counts as the action it's a legacy of); other plugins are refused, except the companion's
/// own few routes a guest needs. A pure function, so it's tested without a server
/// (<see cref="GuestGuard"/> applies it).
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

    /// <summary>Calls that only concern the guest's own session, or that anyone may make anyway.</summary>
    private static readonly HashSet<string> Always = new(StringComparer.Ordinal)
    {
        // Who they are, the server, the time (Watch together measures the server's clock).
        "GetCurrentUser", "GetPublicSystemInfo", "GetPingSystem", "PostPingSystem", "GetUtcTime",
        "GetBrandingOptions", "GetBrandingCss", "GetDisplayPreferences",
        "GetCountries", "GetCultures", "GetLocalizationOptions", "GetParentalRatings",

        // Their session: capabilities, what they're watching, signing out.
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

    /// <summary>The route values that name the item, by the names Jellyfin's routes use.</summary>
    private static readonly string[] ItemRouteKeys = ["itemId", "routeItemId", "videoId"];

    /// <summary>The companion's own routes a guest needs: is it there, room settings, their pass.</summary>
    private static readonly HashSet<string> CompanionRoutes = new(StringComparer.Ordinal)
    {
        "CompanionController.Info", "SettingsController.GetRoomSettings", "GuestsController.Me",
    };

    /// <summary>The companion's controllers' namespace.</summary>
    private static readonly string CompanionControllers = typeof(Api.GuestsController).Namespace!;

    /// <summary>What to do with a guest's request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="scope">What the guest may reach.</param>
    /// <returns>The verdict.</returns>
    public static GuestVerdict Decide(GuestRequest request, GuestScope scope)
    {
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

        var action = Plain(request.Action);
        if (Always.Contains(action))
        {
            return GuestVerdict.Allow;
        }

        // Pictures are public in Jellyfin anyway.
        if (request.Controller == "Image" && (action.StartsWith("Get", StringComparison.Ordinal) || action.StartsWith("Head", StringComparison.Ordinal)))
        {
            return GuestVerdict.Allow;
        }

        if (ForAnItem.Contains(action))
        {
            var item = ItemRouteKeys.Select(k => request.Route.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);
            return Guid.TryParse(item, out var id) && scope.ItemAllowed(id)
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
    Guid? BodyGroupId);

/// <summary>What a guest may reach.</summary>
/// <param name="GuestId">The guest's user.</param>
/// <param name="Groups">The Watch together groups: their pass's, and those their inviter is in now.</param>
/// <param name="ItemAllowed">Whether an item was queued in one of those groups.</param>
public sealed record GuestScope(Guid GuestId, IReadOnlySet<string> Groups, Func<Guid, bool> ItemAllowed);

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
