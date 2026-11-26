// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KasuVideoCompanion.Api;

/// <summary>
/// Guest passes (README.md, "Guest passes"): a Watch together room's owner invites someone with
/// no account on the server; opening the link makes them a short-lived, confined account.
/// Refusals answer <c>{"Error": code}</c> (<see cref="GuestErrors"/>).
/// </summary>
[ApiController]
[Route("KasuVideo/Guests")]
public class GuestsController : ControllerBase
{
    private readonly GuestPassService _passes;
    private readonly IAuthorizationContext _auth;
    private readonly VisitorLimiter _visitors;

    /// <summary>Creates the controller.</summary>
    /// <param name="passes">Guest passes.</param>
    /// <param name="auth">Who is asking.</param>
    /// <param name="visitors">The limit on the routes anyone may call.</param>
    public GuestsController(GuestPassService passes, IAuthorizationContext auth, VisitorLimiter visitors)
    {
        _passes = passes;
        _auth = auth;
        _visitors = visitors;
    }

    /// <summary>For any signed-in user: whether guest passes are on, whether they may invite, and their choices.</summary>
    /// <returns>Their status.</returns>
    [HttpGet("Status")]
    [Authorize]
    public async Task<ActionResult<GuestStatus>> Status()
    {
        var caller = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return _passes.Status(caller.UserId);
    }

    /// <summary>For an inviter: their open passes (links not yet opened, and guests), without codes.</summary>
    /// <returns>Their passes.</returns>
    [HttpGet("Invites")]
    [Authorize]
    public async Task<ActionResult<PassList>> Invites()
    {
        var caller = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return new PassList(_passes.InvitesOf(caller.UserId));
    }

    /// <summary>
    /// For an inviter: a new invite for a Watch together group they're in. The answer holds the
    /// code, given only this once; KasuVideo puts it in the link's fragment.
    /// </summary>
    /// <param name="input">The group, the libraries, and the hours.</param>
    /// <returns>The invite, or why not.</returns>
    [HttpPost("Invites")]
    [Authorize]
    public async Task<ActionResult<CreatedInvite>> CreateInvite([FromBody] InviteInput input)
    {
        var caller = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var result = _passes.CreateInvite(caller.UserId, caller.DeviceId, input.GroupId, input.LibraryIds, input.Hours);
        return result.Ok ? result.Value! : Refuse(result.Error!);
    }

    /// <summary>For the pass's inviter or an administrator: ends it (and the guest's account).</summary>
    /// <param name="id">The pass.</param>
    /// <returns>204, or why not.</returns>
    [HttpDelete("Invites/{id}")]
    [Authorize]
    public async Task<ActionResult> EndInvite([FromRoute] Guid id)
    {
        var caller = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var error = await _passes.EndAsync(id, caller.UserId).ConfigureAwait(false);
        return error is null ? NoContent() : Refuse(error);
    }

    /// <summary>
    /// For anyone holding a link: who invited them and until when it works. <c>404 invalid</c>
    /// for anything but a live, unopened link, without saying why. Limited per address.
    /// </summary>
    /// <param name="input">The code.</param>
    /// <returns>The preview.</returns>
    [HttpPost("Invites/Preview")]
    [AllowAnonymous]
    public ActionResult<InvitePreview> Preview([FromBody] CodeInput input)
    {
        if (!_visitors.Allow(Address))
        {
            return Refuse("too-many-requests");
        }

        var preview = _passes.Preview(input.Code);
        return preview is null ? Refuse(GuestErrors.Invalid.Code) : preview;
    }

    /// <summary>
    /// For anyone holding a link: opens it, once. Makes the guest's account and answers its
    /// session (token, user, server), the group of the room they're invited to, and when the
    /// pass ends. Limited per address.
    /// </summary>
    /// <param name="input">The code, a nickname, and the visitor's device.</param>
    /// <returns>The guest's session, or why not.</returns>
    [HttpPost("Redeem")]
    [AllowAnonymous]
    public async Task<ActionResult<RedeemedPass>> Redeem([FromBody] RedeemInput input)
    {
        if (!_visitors.Allow(Address))
        {
            return Refuse("too-many-requests");
        }

        var result = await _passes.RedeemAsync(input.Code, input.Nickname, input.DeviceId, input.DeviceName, input.AppVersion, Address)
            .ConfigureAwait(false);
        return result.Ok ? result.Value! : Refuse(result.Error!);
    }

    /// <summary>For a guest: their pass. <c>404 not-guest</c> for anyone else. The rooms Worker asks it too.</summary>
    /// <returns>The pass.</returns>
    [HttpGet("Me")]
    [Authorize]
    public async Task<ActionResult<GuestMe>> Me()
    {
        var caller = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var me = _passes.Me(caller.UserId);
        return me is null ? NotFound(new { Error = "not-guest" }) : me;
    }

    /// <summary>For administrators: every pass, live and recently ended.</summary>
    /// <returns>The passes.</returns>
    [HttpGet("Passes")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<PassList> Passes() => new PassList(_passes.All());

    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();

    private ObjectResult Refuse(string code) =>
        StatusCode(StatusOf(code), new { Error = code });

    private static int StatusOf(string code) => code switch
    {
        "off" or "not-inviter" or "guest" or "not-in-group" or GuestErrors.Forbidden => StatusCodes.Status403Forbidden,
        "bad-group" or "bad-libraries" or "bad-hours" or "bad-name" or "bad-device" => StatusCodes.Status400BadRequest,
        "too-many" or "name-taken" => StatusCodes.Status409Conflict,
        "invalid" or GuestErrors.NotFound => StatusCodes.Status404NotFound,
        "too-many-requests" => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };
}

/// <summary>A list of passes.</summary>
/// <param name="Passes">The passes.</param>
public sealed record PassList(IReadOnlyList<PassView> Passes);

/// <summary>What <c>POST /KasuVideo/Guests/Invites</c> takes.</summary>
public sealed class InviteInput
{
    /// <summary>Gets or sets the Watch together group (the room's), with or without dashes.</summary>
    public string? GroupId { get; set; }

    /// <summary>Gets or sets the libraries the guest may see.</summary>
    public Guid[]? LibraryIds { get; set; }

    /// <summary>Gets or sets how long the pass lasts once opened; omitted for the shortest.</summary>
    public int? Hours { get; set; }
}

/// <summary>What <c>POST /KasuVideo/Guests/Invites/Preview</c> takes.</summary>
public sealed class CodeInput
{
    /// <summary>Gets or sets the code from the link.</summary>
    public string? Code { get; set; }
}

/// <summary>What <c>POST /KasuVideo/Guests/Redeem</c> takes.</summary>
public sealed class RedeemInput
{
    /// <summary>Gets or sets the code from the link.</summary>
    public string? Code { get; set; }

    /// <summary>Gets or sets what the visitor wants to be called.</summary>
    public string? Nickname { get; set; }

    /// <summary>Gets or sets the visitor's device id, as KasuVideo names it.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the device's name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets KasuVideo's version.</summary>
    public string? AppVersion { get; set; }
}
