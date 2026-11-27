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
/// and what each group queued (<see cref="GroupRecorder"/>, only what Jellyfin really did), since
/// Jellyfin doesn't tell a plugin.</item>
/// <item>For a guest: lets through only what <see cref="GuestAccessRules"/> allows, within their
/// room (their pass's group and the groups their inviter is in, and what those queued, as far as
/// they may see it), from the device their pass was opened on, and trims the group list to
/// theirs. Everything else answers <c>403 {"Error": …}</c>, whichever app holds the token. A guest
/// is anyone the companion has a pass for, or any account with the guest provider (one whose pass
/// is gone is refused everything but signing out). When a guest joins their room's group,
/// SyncPlay is told never to wait for them.</item>
/// </list>
/// Raw endpoints and the server's WebSocket aren't controller actions and don't pass here.
/// </summary>
public sealed class GuestGuard : IAsyncActionFilter
{
    // The claims Jellyfin's authentication puts on a request (Jellyfin.Api.Constants.InternalClaimTypes).
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string DeviceIdClaim = "Jellyfin-DeviceId";

    // What the rules read from a request's body (Jellyfin's DTOs name them so).
    private static readonly string[] BodyProperties = ["ItemId", "MediaSourceId", "PlaySessionId", "LiveStreamId", "IgnoreWait"];

    private readonly GuestPassService _passes;
    private readonly GroupTracker _groups;
    private readonly GroupRecorder _recorder;
    private readonly GuestStalls _stalls;
    private readonly IGuestAccounts _accounts;
    private readonly IAuthorizationContext _auth;
    private readonly ILogger<GuestGuard> _logger;

    /// <summary>Creates the guard (Jellyfin does, for each request).</summary>
    /// <param name="passes">Guest passes.</param>
    /// <param name="groups">What the companion knows of Watch together groups.</param>
    /// <param name="recorder">What notes the SyncPlay requests in it.</param>
    /// <param name="stalls">How often each guest stalled their room.</param>
    /// <param name="accounts">Jellyfin's side (items, sessions, transcodes).</param>
    /// <param name="auth">Who is asking, when the request's claims don't say.</param>
    /// <param name="logger">Where refusals are logged (never a token).</param>
    public GuestGuard(
        GuestPassService passes,
        GroupTracker groups,
        GroupRecorder recorder,
        GuestStalls stalls,
        IGuestAccounts accounts,
        IAuthorizationContext auth,
        ILogger<GuestGuard> logger)
    {
        _passes = passes;
        _groups = groups;
        _recorder = recorder;
        _stalls = stalls;
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
        if (user is { } guestId && (_passes.IsGuest(guestId) || _accounts.IsGuestAccount(guestId)))
        {
            var pass = _passes.LivePassOf(guestId);
            if (pass is null)
            {
                // A guest whose pass has ended (or the companion has no pass for) may only sign out.
                verdict = name == "ReportSessionEnded" ? GuestVerdict.Allow : GuestVerdict.Deny(GuestAccessRules.NotAllowed);
            }
            else
            {
                var groups = new HashSet<string>(_groups.GroupsOf(pass.InviterId), StringComparer.Ordinal) { pass.GroupId };
                scope = new GuestScope(guestId, groups, item => _groups.Holds(groups, item) && _accounts.CanSee(guestId, item))
                {
                    DeviceId = pass.DeviceId,
                    SessionId = _accounts.SessionIdOf(guestId, device),
                    OwnJob = playSession => _accounts.JobDevice(playSession) is not { } job || string.Equals(job, device, StringComparison.OrdinalIgnoreCase),
                    SourceOf = (item, source) => _accounts.MediaSourcesOf(item).Any(s => SameSource(s, source)),
                    MayStall = () => _stalls.Allow(guestId),
                };
                verdict = GuestAccessRules.Decide(Describe(context, action, device), scope);
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
            Track(name, user.Value, device, context.ActionArguments, executed.Result, guest: verdict is not null);
        }

        if (verdict?.Kind == GuestVerdictKind.TrimGroups && scope is not null)
        {
            if (executed.Result is ObjectResult { Value: IEnumerable<GroupInfoDto> listed } result)
            {
                result.Value = listed.Where(g => scope.Groups.Contains(g.GroupId.ToString("N"))).ToList();
            }
            else
            {
                // An answer of another shape could list anyone's groups: none, then.
                executed.Result = new ObjectResult(new { Error = GuestAccessRules.NotAllowed }) { StatusCode = StatusCodes.Status403Forbidden };
            }
        }
    }

    /// <summary>Notes what a SyncPlay request did about groups (after it ran).</summary>
    private void Track(string action, Guid user, string? device, IDictionary<string, object?> arguments, IActionResult? result, bool guest)
    {
        switch (action)
        {
            case "SyncPlayCreateGroup":
                if (Value(result) is GroupInfoDto created)
                {
                    _recorder.Created(user, device, created.GroupId);
                }

                break;
            case "SyncPlayJoinGroup":
                if (BodyGuid(arguments, "GroupId") is { } joined && _recorder.Joined(user, device, joined) && guest)
                {
                    // The room never waits for a guest (their stalls can't hold it).
                    _accounts.IgnoreWait(user, device);
                }

                break;
            case "SyncPlayLeaveGroup":
                _recorder.Left(user, device);
                break;
            case "SyncPlaySetNewQueue":
                _recorder.Queued(user, device, Ids(BodyList(arguments, "PlayingQueue")));
                break;
            case "SyncPlayQueue":
                _recorder.Queued(user, device, Ids(BodyList(arguments, "ItemIds")));
                break;
        }
    }

    /// <summary>The request as <see cref="GuestAccessRules"/> reads it.</summary>
    private static GuestRequest Describe(ActionExecutingContext context, ControllerActionDescriptor action, string? device)
    {
        var route = context.RouteData.Values.ToDictionary(p => p.Key, p => p.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        var ids = context.HttpContext.Request.Query.TryGetValue("ids", out var values) ? values.ToString() : null;
        var arguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.ActionArguments)
        {
            if (Simple(value) is { } text)
            {
                arguments[key] = text;
            }
        }

        var body = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in BodyProperties)
        {
            if (Simple(BodyValue(context.ActionArguments, property)) is { } text)
            {
                body[property] = text;
            }
        }

        return new GuestRequest(
            action.ControllerTypeInfo.Namespace ?? string.Empty,
            action.ControllerName,
            action.ActionName,
            route,
            ids,
            BodyGuid(context.ActionArguments, "GroupId"))
        {
            Arguments = arguments,
            Body = body,
            Device = device,
        };
    }

    /// <summary>A plain value as text (an id, a number, a flag); null for anything else.</summary>
    private static string? Simple(object? value) => value switch
    {
        null => null,
        string s => s,
        Guid g => g.ToString(),
        bool b => b ? bool.TrueString : bool.FalseString,
        IFormattable f when value.GetType().IsPrimitive || value is decimal => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        _ => null,
    };

    /// <summary>Two media source ids naming the same source (Jellyfin writes them with and without dashes).</summary>
    private static bool SameSource(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;

    private static IEnumerable<Guid> Ids(IEnumerable? items) => items?.OfType<Guid>() ?? [];

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
            if (argument is null || argument is string || argument.GetType().IsPrimitive)
            {
                continue;
            }

            if (argument.GetType().GetProperty(property) is { } found)
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
