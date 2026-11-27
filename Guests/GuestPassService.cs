// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Guest passes' rules (README.md, "Guest passes"): who may invite, opening a link, ending a
/// pass and cleaning up. Jellyfin is reached only through <see cref="IGuestAccounts"/>, the
/// passes are kept in <see cref="GuestPassStore"/>, and the settings are read fresh each time.
/// Codes and tokens are never logged.
/// </summary>
public sealed class GuestPassService
{
    /// <summary>How long an ended pass is kept (for the administrators' list) before it's forgotten.</summary>
    public static readonly TimeSpan EndedKept = TimeSpan.FromDays(7);

    /// <summary>How long a link opened but not finished (the account failed half-way) is held before it ends.</summary>
    public static readonly TimeSpan ClaimHeld = TimeSpan.FromMinutes(5);

    private readonly GuestPassStore _store;
    private readonly IGuestAccounts _accounts;
    private readonly Func<PluginConfiguration> _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<GuestPassService> _logger;
    private readonly IEndNotices? _notices;

    /// <summary>Creates the service.</summary>
    /// <param name="store">Where the passes are kept.</param>
    /// <param name="accounts">Jellyfin's side.</param>
    /// <param name="settings">The current settings.</param>
    /// <param name="clock">The time.</param>
    /// <param name="logger">Where what happens is logged (never a code or a token).</param>
    /// <param name="notices">Tells the rooms server when a guest's pass ends; none sends nothing.</param>
    public GuestPassService(
        GuestPassStore store,
        IGuestAccounts accounts,
        Func<PluginConfiguration> settings,
        TimeProvider clock,
        ILogger<GuestPassService> logger,
        IEndNotices? notices = null)
    {
        _store = store;
        _accounts = accounts;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _notices = notices;
        Current = this;
    }

    /// <summary>Gets the service Jellyfin made, for the plugin's uninstall (which can't be given services).</summary>
    public static GuestPassService? Current { get; private set; }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>What user <paramref name="callerId"/> may do with guest passes.</summary>
    /// <param name="callerId">The user asking.</param>
    /// <returns>Whether passes are on, whether they may invite, and the choices they have.</returns>
    public GuestStatus Status(Guid callerId)
    {
        var settings = _settings();
        var caller = _accounts.Describe(callerId);
        var canInvite = MayInvite(settings, caller);
        var libraries = canInvite
            ? settings.GuestLibraryIds
                .Where(id => caller!.AllLibraries || caller.Libraries.Contains(id))
                .Select(id => (Id: id, Name: _accounts.LibraryName(id)))
                .Where(l => l.Name is not null)
                .Select(l => new LibraryChoice(l.Id, l.Name!))
                .ToList()
            : [];
        var now = Now;
        var live = _store.Read(passes => passes.Count(p => p.InviterId == callerId && IsLive(p, now)));
        return new GuestStatus(
            settings.GuestPassesEnabled,
            canInvite,
            libraries,
            live,
            settings.MaxLivePassesPerInviter,
            settings.LinkMinutes,
            settings.PassHoursMin,
            settings.PassHoursMax);
    }

    /// <summary>
    /// Makes an invite for Watch together group <paramref name="groupId"/>. The caller must be
    /// listed as an inviter, not be a guest, be in that group (asked of Jellyfin), stay under
    /// their cap of open passes, and give libraries they can see themselves and that guests may
    /// get, for a number of hours within the settings (the shortest when none is given).
    /// </summary>
    /// <param name="callerId">Who invites.</param>
    /// <param name="deviceId">The device of their request (to find their session).</param>
    /// <param name="groupId">The group, with or without dashes.</param>
    /// <param name="libraryIds">The libraries the guest may see.</param>
    /// <param name="hours">How long the pass lasts once opened; null for the shortest.</param>
    /// <returns>The invite and its code (the only time the code is given), or why not.</returns>
    public GuestResult<CreatedInvite> CreateInvite(
        Guid callerId,
        string? deviceId,
        string? groupId,
        IReadOnlyList<Guid>? libraryIds,
        int? hours)
    {
        var settings = _settings();
        if (!settings.GuestPassesEnabled)
        {
            return GuestErrors.Off;
        }

        var caller = _accounts.Describe(callerId);
        if (caller is { IsGuest: true })
        {
            return GuestErrors.Guest;
        }

        if (!MayInvite(settings, caller))
        {
            return GuestErrors.NotInviter;
        }

        if (NormalizeGroupId(groupId) is not { } group)
        {
            return GuestErrors.BadGroup;
        }

        var length = hours ?? settings.PassHoursMin;
        if (length < settings.PassHoursMin || length > settings.PassHoursMax)
        {
            return GuestErrors.BadHours;
        }

        var libraries = (libraryIds ?? []).Distinct().ToArray();
        if (libraries.Length == 0
            || libraries.Any(id => !settings.GuestLibraryIds.Contains(id)
                || !(caller!.AllLibraries || caller.Libraries.Contains(id))))
        {
            return GuestErrors.BadLibraries;
        }

        if (!_accounts.IsInGroup(callerId, deviceId, Guid.ParseExact(group, "N")))
        {
            return GuestErrors.NotInGroup;
        }

        var code = GuestCodes.New();
        var now = Now;
        var pass = new GuestPass
        {
            Id = Guid.NewGuid(),
            CodeHash = GuestCodes.Hash(code),
            InviterId = callerId,
            GroupId = group,
            LibraryIds = libraries,
            Hours = length,
            CreatedAt = now,
            RedeemBy = now.AddMinutes(settings.LinkMinutes),
        };
        var added = _store.Update(passes =>
        {
            if (passes.Count(p => p.InviterId == callerId && IsLive(p, now)) >= settings.MaxLivePassesPerInviter)
            {
                return false;
            }

            passes.Add(pass);
            return true;
        });
        if (!added)
        {
            return GuestErrors.TooMany;
        }

        _logger.LogInformation("{Inviter} made guest pass {Pass} for group {Group}", caller!.Name, pass.Id, group);
        return new CreatedInvite(pass.Id, code, pass.RedeemBy, pass.Hours);
    }

    /// <summary>The live passes user <paramref name="callerId"/> made (links not yet opened, and guests).</summary>
    /// <param name="callerId">The inviter.</param>
    /// <returns>Their passes, newest first.</returns>
    public IReadOnlyList<PassView> InvitesOf(Guid callerId)
    {
        var now = Now;
        return _store.Read(passes => passes
            .Where(p => p.InviterId == callerId && IsLive(p, now))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => View(p, null))
            .ToList());
    }

    /// <summary>Every pass, live and recently ended, for administrators.</summary>
    /// <returns>The passes, newest first, with their inviters' names.</returns>
    public IReadOnlyList<PassView> All() =>
        _store.Read(passes => passes
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => View(p, _accounts.Describe(p.InviterId)?.Name))
            .ToList());

    /// <summary>
    /// What the invite page shows before the visitor picks a nickname: who invited them and
    /// until when the link works. Null for anything but a live, unopened link (the answer never
    /// says why).
    /// </summary>
    /// <param name="code">The code from the link.</param>
    /// <returns>The preview, or null.</returns>
    public InvitePreview? Preview(string? code)
    {
        if (!_settings().GuestPassesEnabled || !GuestCodes.LooksLikeCode(code))
        {
            return null;
        }

        var hash = GuestCodes.Hash(code!);
        var now = Now;
        var pass = _store.Read(passes => passes.FirstOrDefault(p => p.CodeHash == hash));
        if (pass is null || pass.State != GuestPassState.Pending || now >= pass.RedeemBy)
        {
            return null;
        }

        var inviter = _accounts.Describe(pass.InviterId);
        return MayInvite(_settings(), inviter) ? new InvitePreview(inviter!.Name, pass.RedeemBy, pass.Hours) : null;
    }

    /// <summary>
    /// Whether <paramref name="code"/> is a live, unopened link's (what opening it needs). The
    /// visitors' limit counts only the codes that aren't (<see cref="VisitorLimiter"/>): a real
    /// link always goes through, whatever a flood of wrong ones did.
    /// </summary>
    /// <param name="code">The code from the link.</param>
    /// <returns>True for a link that can be opened now.</returns>
    public bool IsOpenCode(string? code)
    {
        if (!_settings().GuestPassesEnabled || !GuestCodes.LooksLikeCode(code))
        {
            return false;
        }

        var hash = GuestCodes.Hash(code!);
        var now = Now;
        return _store.Read(passes => passes.Any(p => p.CodeHash == hash && p.State == GuestPassState.Pending && now < p.RedeemBy));
    }

    /// <summary>
    /// Opens an invite link: once only, within its time, while its inviter may still invite.
    /// Makes the guest's account (named after <paramref name="nickname"/>), signs it in on the
    /// visitor's device and starts the pass's clock.
    /// </summary>
    /// <param name="code">The code from the link.</param>
    /// <param name="nickname">What the visitor wants to be called.</param>
    /// <param name="deviceId">Their device, as KasuVideo names it.</param>
    /// <param name="deviceName">Its name.</param>
    /// <param name="appVersion">KasuVideo's version.</param>
    /// <param name="remoteEndPoint">Their address.</param>
    /// <returns>The guest's session, or why not.</returns>
    public async Task<GuestResult<RedeemedPass>> RedeemAsync(
        string? code,
        string? nickname,
        string? deviceId,
        string? deviceName,
        string? appVersion,
        string? remoteEndPoint)
    {
        var settings = _settings();
        if (!settings.GuestPassesEnabled || !GuestCodes.LooksLikeCode(code))
        {
            return GuestErrors.Invalid;
        }

        if (GuestNames.Clean(nickname) is not { } name)
        {
            return GuestErrors.BadName;
        }

        // A device another user holds is refused: Jellyfin keys sessions by device, so a guest on
        // it would take over that user's session, and their pass's end would end it (G8).
        if (!ValidDeviceId(deviceId) || _accounts.DeviceInUse(deviceId!))
        {
            return GuestErrors.BadDevice;
        }

        // Claim the link: only one visitor gets past here.
        var hash = GuestCodes.Hash(code!);
        var now = Now;
        var pass = _store.UpdateIfFound(passes =>
        {
            var found = passes.FirstOrDefault(p => p.CodeHash == hash);
            if (found is null || found.State != GuestPassState.Pending || now >= found.RedeemBy)
            {
                return null;
            }

            found.RedeemedAt = now;
            return Copy(found);
        });
        if (pass is null)
        {
            return GuestErrors.Invalid;
        }

        if (!MayInvite(settings, _accounts.Describe(pass.InviterId)))
        {
            await EndPassAsync(pass.Id, EndReasons.InviterRemoved).ConfigureAwait(false);
            return GuestErrors.Invalid;
        }

        CreatedGuest? created = null;
        try
        {
            foreach (var candidate in GuestNames.Candidates(name))
            {
                if (_accounts.NameTaken(candidate))
                {
                    continue;
                }

                try
                {
                    created = await _accounts.CreateAsync(new GuestAccountRequest(
                        candidate,
                        pass.LibraryIds,
                        settings.GuestTranscoding,
                        settings.GuestBitrateMbps,
                        deviceId!,
                        GuestNames.Label(deviceName, "KasuVideo guest"),
                        GuestNames.Label(appVersion, "0"),
                        remoteEndPoint)).ConfigureAwait(false);
                    break;
                }
                catch (GuestNameTakenException)
                {
                    // Taken between the check and the making: the next one.
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Making the guest account for pass {Pass} failed", pass.Id);
        }

        if (created is null)
        {
            // Not used up: the visitor may try again (another nickname) within the link's time.
            _store.Update(passes =>
            {
                var found = passes.FirstOrDefault(p => p.Id == pass.Id);
                if (found is { GuestUserId: null, EndedAt: null })
                {
                    found.RedeemedAt = null;
                }

                return 0;
            });
            return GuestNames.Candidates(name).All(_accounts.NameTaken) ? GuestErrors.NameTaken : GuestErrors.Failed;
        }

        var endsAt = now.AddHours(pass.Hours);
        _store.Update(passes =>
        {
            var found = passes.First(p => p.Id == pass.Id);
            found.GuestUserId = created.UserId;
            found.GuestName = created.UserName;
            found.DeviceId = deviceId;
            found.EndsAt = endsAt;
            return 0;
        });
        _logger.LogInformation("Guest pass {Pass} opened by {Guest}", pass.Id, created.UserName);
        return new RedeemedPass(created.AccessToken, created.UserId, created.UserName, created.ServerId, pass.GroupId, endsAt);
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is a guest of any pass the companion knows, live or
    /// ended (the guard confines them all; an ended one's account is gone anyway).
    /// </summary>
    /// <param name="userId">A user.</param>
    /// <returns>True for a guest.</returns>
    public bool IsGuest(Guid userId) => _store.Read(passes => passes.Any(p => p.GuestUserId == userId));

    /// <summary>The live pass of guest <paramref name="userId"/>, or null.</summary>
    /// <param name="userId">A guest.</param>
    /// <returns>A copy of their pass.</returns>
    public GuestPass? LivePassOf(Guid userId)
    {
        var now = Now;
        return _store.Read(passes => passes.Where(p => p.GuestUserId == userId && IsLive(p, now)).Select(Copy).FirstOrDefault());
    }

    /// <summary>The pass of guest <paramref name="userId"/>, or null if they aren't a guest with a live pass.</summary>
    /// <param name="userId">The user asking.</param>
    /// <returns>Their pass: its room's group, who invited them, when it ends.</returns>
    public GuestMe? Me(Guid userId)
    {
        var now = Now;
        var pass = _store.Read(passes => passes.FirstOrDefault(p => p.GuestUserId == userId && IsLive(p, now)));
        if (pass is null)
        {
            return null;
        }

        var inviter = _accounts.Describe(pass.InviterId);
        return new GuestMe(pass.Id, pass.GroupId, pass.InviterId, inviter?.Name, pass.EndsAt!.Value);
    }

    /// <summary>Ends pass <paramref name="passId"/> at its inviter's or an administrator's word.</summary>
    /// <param name="passId">The pass.</param>
    /// <param name="callerId">Who asks.</param>
    /// <returns>Null when it's ended (or was already), otherwise why not.</returns>
    public async Task<string?> EndAsync(Guid passId, Guid callerId)
    {
        var pass = _store.Read(passes => passes.FirstOrDefault(p => p.Id == passId));
        if (pass is null)
        {
            return GuestErrors.NotFound;
        }

        var caller = _accounts.Describe(callerId);
        var isInviter = pass.InviterId == callerId;
        if (!isInviter && caller is not { IsAdmin: true })
        {
            // As for a pass that doesn't exist: nobody learns that someone else's does (G8).
            return GuestErrors.NotFound;
        }

        await EndPassAsync(passId, isInviter ? EndReasons.Ended : EndReasons.Admin).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// For a guest: ends their own pass (KasuVideo's Leave, <c>DELETE Guests/Me</c>). Only a guest
    /// whose pass is live, and only their own: the account is always the caller's, never one
    /// named in the request. Their devices are signed out and the account deleted; a delete that
    /// fails is left for the sweeps (<see cref="SweepLeftoversAsync"/>).
    /// </summary>
    /// <param name="callerId">The user asking.</param>
    /// <returns>Null when it's ended, otherwise why not: <c>not-guest</c>, or <c>guest-not-allowed</c> when their pass no longer lives.</returns>
    public async Task<string?> EndMineAsync(Guid callerId)
    {
        if (_accounts.Describe(callerId) is not { IsGuest: true })
        {
            return GuestErrors.NotGuest;
        }

        var pass = LivePassOf(callerId);
        if (pass is null)
        {
            return GuestAccessRules.NotAllowed;
        }

        await EndPassAsync(pass.Id, EndReasons.Left).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Deletes every guest account without a live pass: one whose pass has ended or run out (that
    /// pass is ended too), or one no pass owns (a crash, a delete that failed). An account is never
    /// reused: every pass makes a new one. Runs in the five-minute cleanup and on its own every
    /// hour (<see cref="LeftoverGuestAccountsTask"/>). While a link is being opened its new account
    /// isn't on its pass yet, so nothing is swept then (for <see cref="ClaimHeld"/> at most).
    /// </summary>
    /// <param name="cancellationToken">Stops between accounts.</param>
    /// <returns>How many accounts went.</returns>
    public async Task<int> SweepLeftoversAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var opening = _store.Read(passes => passes.Any(p =>
            p.EndedAt is null && p.GuestUserId is null && p.RedeemedAt is { } at && now - at < ClaimHeld));
        if (opening)
        {
            return 0;
        }

        var live = _store.Read(passes => passes
            .Where(p => p.GuestUserId is not null && IsLive(p, now))
            .Select(p => p.GuestUserId!.Value)
            .ToHashSet());
        var swept = 0;
        foreach (var user in _accounts.GuestUserIds().Where(id => !live.Contains(id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var open = _store.Read(passes => passes
                .Where(p => p.GuestUserId == user && p.EndedAt is null)
                .Select(p => p.Id)
                .ToList());
            try
            {
                if (open.Count == 0)
                {
                    await _accounts.EndAsync(user).ConfigureAwait(false);
                }
                else
                {
                    // Ran out without the five-minute task ending it: ending it deletes the account.
                    foreach (var id in open)
                    {
                        await EndPassAsync(id, EndReasons.Expired).ConfigureAwait(false);
                    }
                }

                swept++;
            }
            catch (Exception e)
            {
                // The next sweep tries again.
                _logger.LogError(e, "Deleting leftover guest account {User} failed", user);
            }
        }

        return swept;
    }

    /// <summary>
    /// Ends every live pass whose inviter may no longer invite (taken off the list, made a guest,
    /// gone): at once when the settings are saved, rather than at the next cleanup.
    /// </summary>
    /// <returns>How many ended.</returns>
    public async Task<int> EndUnallowedAsync()
    {
        var settings = _settings();
        var ids = _store.Read(passes => passes.Where(p => p.EndedAt is null).Select(p => (p.Id, p.InviterId)).ToList())
            .Where(p => !MayInvite(settings, _accounts.Describe(p.InviterId)))
            .Select(p => p.Id)
            .ToList();
        foreach (var id in ids)
        {
            await EndPassAsync(id, EndReasons.InviterRemoved).ConfigureAwait(false);
        }

        return ids.Count;
    }

    /// <summary>Ends every live pass, for <paramref name="reason"/> (turned off, uninstalled).</summary>
    /// <param name="reason">Why (<see cref="EndReasons"/>).</param>
    /// <returns>How many ended.</returns>
    public async Task<int> EndAllAsync(string reason)
    {
        var ids = _store.Read(passes => passes.Where(p => p.EndedAt is null).Select(p => p.Id).ToList());
        foreach (var id in ids)
        {
            await EndPassAsync(id, reason).ConfigureAwait(false);
        }

        return ids.Count;
    }

    /// <summary>
    /// What the scheduled task does every few minutes (and at startup): ends the passes that are
    /// due (links unopened in time, passes past their end, guests without a session for too long,
    /// inviters no longer allowed, everything when passes are off, links half-opened by a
    /// failure), deletes guest accounts no live pass owns (left by a crash), and forgets ended
    /// passes after <see cref="EndedKept"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops between passes.</param>
    /// <returns>What it did.</returns>
    public async Task<CleanupReport> CleanupAsync(CancellationToken cancellationToken)
    {
        var settings = _settings();
        var now = Now;
        var open = _store.Read(passes => passes.Where(p => p.EndedAt is null).Select(Copy).ToList());
        var ended = 0;
        foreach (var pass in open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = DueReason(pass, settings, now);
            if (reason is not null)
            {
                await EndPassAsync(pass.Id, reason).ConfigureAwait(false);
                ended++;
            }
        }

        var orphans = await SweepLeftoversAsync(cancellationToken).ConfigureAwait(false);

        // An ended pass is forgotten only once its account is gone: the guard knows a guest by
        // their pass too, so one whose delete keeps failing stays confined until it goes.
        var accounts = _accounts.GuestUserIds().ToHashSet();
        var forgotten = _store.Update(passes => passes.RemoveAll(p =>
            p.EndedAt is { } at && now - at > EndedKept && (p.GuestUserId is not { } guest || !accounts.Contains(guest))));
        if (ended + orphans > 0)
        {
            _logger.LogInformation("Guest passes: {Ended} ended, {Orphans} leftover guest accounts deleted", ended, orphans);
        }

        return new CleanupReport(ended, orphans, forgotten);
    }

    /// <summary>The room settings the rooms Worker and KasuVideo read (<c>GET /KasuVideo/Companion/RoomSettings</c>).</summary>
    /// <returns>How many people and guests a room takes.</returns>
    public RoomSettings Rooms()
    {
        var settings = _settings();
        return new RoomSettings(settings.RoomMaxMembers, settings.RoomMaxGuests);
    }

    /// <summary>Whether a pass is live at <paramref name="now"/>: not ended, and within its link's time or its own.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="now">The time (UTC).</param>
    /// <returns>True while it counts.</returns>
    public static bool IsLive(GuestPass pass, DateTime now) => pass.State switch
    {
        GuestPassState.Pending => now < pass.RedeemBy,
        GuestPassState.Active => pass.EndsAt is null || now < pass.EndsAt,
        _ => false,
    };

    /// <summary>A Watch together group id as 32 lower-case hex digits, or null if it isn't one.</summary>
    /// <param name="groupId">With or without dashes, any case.</param>
    /// <returns>The normalized id.</returns>
    public static string? NormalizeGroupId(string? groupId) =>
        Guid.TryParse(groupId, out var id) ? id.ToString("N") : null;

    private string? DueReason(GuestPass pass, PluginConfiguration settings, DateTime now)
    {
        if (!settings.GuestPassesEnabled)
        {
            return EndReasons.TurnedOff;
        }

        if (!MayInvite(settings, _accounts.Describe(pass.InviterId)))
        {
            return EndReasons.InviterRemoved;
        }

        if (pass.RedeemedAt is null)
        {
            return now >= pass.RedeemBy ? EndReasons.LinkExpired : null;
        }

        if (pass.GuestUserId is null)
        {
            return now - pass.RedeemedAt.Value >= ClaimHeld ? EndReasons.Failed : null;
        }

        if (pass.EndsAt is { } end && now >= end)
        {
            return EndReasons.Expired;
        }

        var last = _accounts.LastActivity(pass.GuestUserId.Value) is { } seen && seen > pass.RedeemedAt.Value
            ? seen
            : pass.RedeemedAt.Value;
        return now - last >= TimeSpan.FromMinutes(settings.IdleMinutes) ? EndReasons.Idle : null;
    }

    private async Task EndPassAsync(Guid passId, string reason)
    {
        var pass = _store.Update(passes =>
        {
            var found = passes.FirstOrDefault(p => p.Id == passId);
            if (found is null || found.EndedAt is not null)
            {
                return null;
            }

            found.EndedAt = Now;
            found.EndReason = reason;
            return Copy(found);
        });
        if (pass?.GuestUserId is { } user)
        {
            // Read before the account ends: its sign-out empties the tracker.
            var notice = _notices?.Prepare(user, pass.GroupId);
            try
            {
                await _accounts.EndAsync(user).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The account is left for the next cleanup's leftover sweep.
                _logger.LogError(e, "Ending guest pass {Pass}'s account failed", passId);
            }

            // The room lets them go now, not when their ticket runs out.
            if (notice is not null)
            {
                _notices!.Send(passId, notice);
            }
        }

        if (pass is not null)
        {
            _logger.LogInformation("Guest pass {Pass} ended ({Reason})", passId, reason);
        }
    }

    private static bool MayInvite(PluginConfiguration settings, UserFacts? user) =>
        settings.GuestPassesEnabled && user is { IsGuest: false } && settings.InviterIds.Contains(user.Id);

    private static bool ValidDeviceId(string? deviceId) =>
        deviceId is { Length: > 0 and <= 128 }
        && deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private PassView View(GuestPass pass, string? inviterName)
    {
        var now = Now;
        var state = pass.State switch
        {
            GuestPassState.Pending when !IsLive(pass, now) => "expired",
            GuestPassState.Pending => "pending",
            GuestPassState.Active when !IsLive(pass, now) => "expired",
            GuestPassState.Active => "active",
            _ => "ended",
        };
        return new PassView(
            pass.Id,
            pass.InviterId,
            inviterName,
            pass.GroupId,
            state,
            pass.CreatedAt,
            pass.RedeemBy,
            pass.Hours,
            pass.EndsAt,
            pass.GuestUserId,
            pass.GuestName,
            pass.EndedAt,
            pass.EndReason);
    }

    private static GuestPass Copy(GuestPass p) => new()
    {
        Id = p.Id,
        CodeHash = p.CodeHash,
        InviterId = p.InviterId,
        GroupId = p.GroupId,
        LibraryIds = p.LibraryIds,
        Hours = p.Hours,
        CreatedAt = p.CreatedAt,
        RedeemBy = p.RedeemBy,
        RedeemedAt = p.RedeemedAt,
        EndsAt = p.EndsAt,
        GuestUserId = p.GuestUserId,
        GuestName = p.GuestName,
        DeviceId = p.DeviceId,
        EndedAt = p.EndedAt,
        EndReason = p.EndReason,
    };
}
