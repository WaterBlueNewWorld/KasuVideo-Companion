// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Collections;
using System.Security.Claims;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Every API call goes through this filter (added to Jellyfin's MVC options by
/// <see cref="PluginServiceRegistrator"/>). It does two things:
/// <list type="bullet">
/// <item>For everyone: notes the SyncPlay requests that say who is in which Watch together group
/// and what each group queued (<see cref="GroupTracker"/>), since Jellyfin doesn't tell a plugin.</item>
/// <item>For a guest: lets through only what <see cref="GuestAccessRules"/> allows, within their
/// room (their pass's group and the groups their inviter is in, and what those queued), and
/// trims the group list to theirs. Everything else answers <c>403 {"Error": …}</c>, whichever app
/// holds the token.</item>
/// </list>
/// Raw endpoints and the server's WebSocket aren't controller actions and don't pass here.
/// </summary>
public sealed class GuestGuard : IAsyncActionFilter
{
    // The claims Jellyfin's authentication puts on a request (Jellyfin.Api.Constants.InternalClaimTypes).
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string DeviceIdClaim = "Jellyfin-DeviceId";

    private readonly GuestPassService _passes;
    private readonly GroupTracker _groups;
    private readonly IGuestAccounts _accounts;
    private readonly IAuthorizationContext _auth;
    private readonly ILogger<GuestGuard> _logger;

    /// <summary>Creates the guard (Jellyfin does, for each request).</summary>
    /// <param name="passes">Guest passes.</param>
    /// <param name="groups">What the companion knows of Watch together groups.</param>
    /// <param name="accounts">Jellyfin's side (an item's season and series).</param>
    /// <param name="auth">Who is asking, when the request's claims don't say.</param>
    /// <param name="logger">Where refusals are logged (never a token).</param>
    public GuestGuard(GuestPassService passes, GroupTracker groups, IGuestAccounts accounts, IAuthorizationContext auth, ILogger<GuestGuard> logger)
    {
        _passes = passes;
        _groups = groups;
        _accounts = accounts;
        _auth = auth;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var (user, device) = await CallerAsync(context.HttpContext).ConfigureAwait(false);
        var name = GuestAccessRules.Plain(action.ActionName);
        var isSyncPlay = action.ControllerTypeInfo.Namespace == GuestAccessRules.JellyfinControllers && action.ControllerName == "SyncPlay";

        GuestVerdict? verdict = null;
        GuestScope? scope = null;
        if (user is { } guestId && _passes.IsGuest(guestId))
        {
            var pass = _passes.LivePassOf(guestId);
            if (pass is null)
            {
                // A guest whose pass has ended may only sign out.
                verdict = name == "ReportSessionEnded" ? GuestVerdict.Allow : GuestVerdict.Deny(GuestAccessRules.NotAllowed);
            }
            else
            {
                var groups = new HashSet<string>(_groups.GroupsOf(pass.InviterId), StringComparer.Ordinal) { pass.GroupId };
                scope = new GuestScope(guestId, groups, item => _groups.Holds(groups, item));
                verdict = GuestAccessRules.Decide(Describe(context, action), scope);
            }

            if (verdict.Kind == GuestVerdictKind.Deny)
            {
                _logger.LogDebug("Guest {Guest} refused {Controller}.{Action} ({Error})", guestId, action.ControllerName, action.ActionName, verdict.Error);
                context.Result = new ObjectResult(new { Error = verdict.Error }) { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }
        }

        var executed = await next().ConfigureAwait(false);
        if (user is null || executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        if (isSyncPlay)
        {
            Track(name, user.Value, device, context.ActionArguments, executed.Result);
        }

        if (verdict?.Kind == GuestVerdictKind.TrimGroups && scope is not null
            && executed.Result is ObjectResult { Value: IEnumerable<GroupInfoDto> listed } result)
        {
            result.Value = listed.Where(g => scope.Groups.Contains(g.GroupId.ToString("N"))).ToList();
        }
    }

    /// <summary>Notes what a SyncPlay request says about groups (after it ran).</summary>
    private void Track(string action, Guid user, string? device, IDictionary<string, object?> arguments, IActionResult? result)
    {
        switch (action)
        {
            case "SyncPlayCreateGroup":
                if (Value(result) is GroupInfoDto created)
                {
                    _groups.Joined(user, device, created.GroupId.ToString("N"));
                }

                break;
            case "SyncPlayJoinGroup":
                if (BodyGuid(arguments, "GroupId") is { } joined)
                {
                    _groups.Joined(user, device, joined.ToString("N"));
                }

                break;
            case "SyncPlayLeaveGroup":
                _groups.Left(user, device);
                break;
            case "SyncPlaySetNewQueue":
                Queue(user, device, BodyList(arguments, "PlayingQueue"));
                break;
            case "SyncPlayQueue":
                Queue(user, device, BodyList(arguments, "ItemIds"));
                break;
        }
    }

    private void Queue(Guid user, string? device, IEnumerable? items)
    {
        if (items is null || _groups.GroupOf(user, device) is not { } group)
        {
            return;
        }

        var family = items.OfType<Guid>().SelectMany(_accounts.Family).ToList();
        _groups.Queued(group, family);
    }

    /// <summary>The request as <see cref="GuestAccessRules"/> reads it.</summary>
    private static GuestRequest Describe(ActionExecutingContext context, ControllerActionDescriptor action)
    {
        var route = context.RouteData.Values.ToDictionary(p => p.Key, p => p.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        var ids = context.HttpContext.Request.Query.TryGetValue("ids", out var values) ? values.ToString() : null;
        return new GuestRequest(
            action.ControllerTypeInfo.Namespace ?? string.Empty,
            action.ControllerName,
            action.ActionName,
            route,
            ids,
            BodyGuid(context.ActionArguments, "GroupId"));
    }

    /// <summary>Property <paramref name="property"/> of the first argument that has it (a request's body), as an id; null if none.</summary>
    private static Guid? BodyGuid(IDictionary<string, object?> arguments, string property) =>
        BodyValue(arguments, property) is Guid id && id != Guid.Empty ? id : null;

    /// <summary>Property <paramref name="property"/> of the first argument that has it, as a list; null if none.</summary>
    private static IEnumerable? BodyList(IDictionary<string, object?> arguments, string property) =>
        BodyValue(arguments, property) as IEnumerable;

    private static object? BodyValue(IDictionary<string, object?> arguments, string property)
    {
        foreach (var argument in arguments.Values)
        {
            if (argument?.GetType().GetProperty(property) is { } found)
            {
                return found.GetValue(argument);
            }
        }

        return null;
    }

    private static object? Value(IActionResult? result) => result switch
    {
        ObjectResult o => o.Value,
        _ => null,
    };

    /// <summary>Who is asking: from the claims Jellyfin's authentication set, or asked of it; null when nobody is signed in.</summary>
    private async Task<(Guid? User, string? Device)> CallerAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return (null, null);
        }

        if (Guid.TryParse(http.User.FindFirstValue(UserIdClaim), out var claimed) && claimed != Guid.Empty)
        {
            return (claimed, http.User.FindFirstValue(DeviceIdClaim));
        }

        var info = await _auth.GetAuthorizationInfo(http).ConfigureAwait(false);
        return info.UserId == Guid.Empty ? (null, null) : (info.UserId, info.DeviceId);
    }
}
