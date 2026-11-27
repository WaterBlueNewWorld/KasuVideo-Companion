// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Configuration;
using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion.Api;

/// <summary>
/// The companion's settings (README.md, "Settings"), changed from KasuVideo's own page for the
/// companion, and the room settings the rooms Worker and KasuVideo read.
/// </summary>
[ApiController]
[Route("KasuVideo/Companion")]
public class SettingsController : ControllerBase
{
    private readonly GuestPassService _passes;
    private readonly IEndNotices _notices;
    private readonly ILogger<SettingsController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="passes">Guest passes (turning them off ends them all).</param>
    /// <param name="notices">The notices to the rooms server (for the check).</param>
    /// <param name="logger">Where changes are logged.</param>
    public SettingsController(GuestPassService passes, IEndNotices notices, ILogger<SettingsController> logger)
    {
        _passes = passes;
        _notices = notices;
        _logger = logger;
    }

    /// <summary>For administrators: every setting, with the range each number may take.</summary>
    /// <returns>The settings.</returns>
    [HttpGet("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<CompanionSettings> GetSettings() => CompanionSettings.Of(Plugin.Instance!.Configuration);

    /// <summary>
    /// For administrators: saves every setting at once. Refused with <c>400 {"Problems": {name:
    /// message}}</c> when a value is out of its range. Turning guest passes off ends every pass.
    /// </summary>
    /// <param name="input">The settings.</param>
    /// <returns>The settings as saved.</returns>
    [HttpPost("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<CompanionSettings>> SaveSettings([FromBody] SettingsInput input)
    {
        var next = input.ToConfiguration(Plugin.Instance!.Configuration);
        var problems = SettingsBounds.Problems(next);
        if (problems.Count > 0)
        {
            return BadRequest(new { Problems = problems });
        }

        var plugin = Plugin.Instance!;
        var wasOn = plugin.Configuration.GuestPassesEnabled;
        plugin.UpdateConfiguration(next);
        _logger.LogInformation("KasuVideo Companion settings saved");
        if (wasOn && !next.GuestPassesEnabled)
        {
            await _passes.EndAllAsync(EndReasons.TurnedOff).ConfigureAwait(false);
        }
        else
        {
            // Someone taken off the inviters' list: their guests' passes end now (G8).
            await _passes.EndUnallowedAsync().ConfigureAwait(false);
        }

        return CompanionSettings.Of(plugin.Configuration);
    }

    /// <summary>
    /// For any signed-in user: how many people and guests a Watch together room takes. The rooms
    /// Worker asks it (with the joining user's own header) instead of its own MAX_MEMBERS.
    /// </summary>
    /// <returns>The room settings.</returns>
    [HttpGet("RoomSettings")]
    [Authorize]
    public ActionResult<RoomSettings> GetRoomSettings() => _passes.Rooms();

    /// <summary>
    /// For administrators: sends the rooms server a signed ping, to see that notices get through
    /// (README.md, "Notices to the rooms server"). Checks <c>Url</c> when given (before it's
    /// saved), else the saved address.
    /// </summary>
    /// <param name="input">The address to check, or nothing.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns><c>{"Result": "ok" | "not-set" | "unreachable" | "refused"}</c>, or <c>400</c> for an address that can't be one.</returns>
    [HttpPost("RoomsServer/Check")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<RoomsServerCheck>> CheckRoomsServer([FromBody] RoomsServerCheckInput? input, CancellationToken cancellationToken)
    {
        var url = input?.Url;
        if (!SettingsBounds.ValidRoomsServerUrl(url))
        {
            return BadRequest(new { Problems = new Dictionary<string, string> { [nameof(PluginConfiguration.RoomsServerUrl)] = "must be an http or https address" } });
        }

        var result = await _notices.CheckAsync(string.IsNullOrEmpty(url) ? null : url, cancellationToken).ConfigureAwait(false);
        return new RoomsServerCheck(result);
    }
}

/// <summary>What <c>POST /KasuVideo/Companion/RoomsServer/Check</c> takes.</summary>
/// <param name="Url">The address to check; null or empty for the saved one.</param>
public sealed record RoomsServerCheckInput(string? Url);

/// <summary>What <c>POST /KasuVideo/Companion/RoomsServer/Check</c> answers.</summary>
/// <param name="Result">One of <see cref="NoticeResults"/>.</param>
public sealed record RoomsServerCheck(string Result);

/// <summary>What <c>GET /KasuVideo/Companion/Settings</c> answers.</summary>
/// <param name="GuestPassesEnabled">Whether guest passes are on.</param>
/// <param name="InviterIds">Who may invite.</param>
/// <param name="GuestLibraryIds">The libraries guests may ever get.</param>
/// <param name="MaxLivePassesPerInviter">Open passes per inviter.</param>
/// <param name="LinkMinutes">How long a link waits to be opened.</param>
/// <param name="PassHoursMin">The shortest pass.</param>
/// <param name="PassHoursMax">The longest pass.</param>
/// <param name="IdleMinutes">How long a guest may be away before the pass ends.</param>
/// <param name="GuestTranscoding">Whether guests may transcode.</param>
/// <param name="GuestBitrateMbps">The most a guest may stream.</param>
/// <param name="RoomMaxMembers">The most people a room takes.</param>
/// <param name="RoomMaxGuests">The most guests a room takes.</param>
/// <param name="RoomsServerUrl">Where the companion says a guest's pass ended; empty for nowhere.</param>
/// <param name="Bounds">The range each number may take.</param>
public sealed record CompanionSettings(
    bool GuestPassesEnabled,
    Guid[] InviterIds,
    Guid[] GuestLibraryIds,
    int MaxLivePassesPerInviter,
    int LinkMinutes,
    int PassHoursMin,
    int PassHoursMax,
    int IdleMinutes,
    bool GuestTranscoding,
    int GuestBitrateMbps,
    int RoomMaxMembers,
    int RoomMaxGuests,
    string RoomsServerUrl,
    Dictionary<string, Bound> Bounds)
{
    /// <summary>The answer for <paramref name="c"/>.</summary>
    /// <param name="c">The settings.</param>
    /// <returns>The answer.</returns>
    public static CompanionSettings Of(PluginConfiguration c) => new(
        c.GuestPassesEnabled,
        c.InviterIds,
        c.GuestLibraryIds,
        c.MaxLivePassesPerInviter,
        c.LinkMinutes,
        c.PassHoursMin,
        c.PassHoursMax,
        c.IdleMinutes,
        c.GuestTranscoding,
        c.GuestBitrateMbps,
        c.RoomMaxMembers,
        c.RoomMaxGuests,
        c.RoomsServerUrl,
        SettingsBounds.Numbers.ToDictionary(p => p.Key, p => new Bound(p.Value.Min, p.Value.Max), StringComparer.Ordinal));
}

/// <summary>A number setting's range.</summary>
/// <param name="Min">The lowest value.</param>
/// <param name="Max">The highest value.</param>
public sealed record Bound(int Min, int Max);

/// <summary>What <c>POST /KasuVideo/Companion/Settings</c> takes: every setting.</summary>
public sealed class SettingsInput
{
    /// <summary>Gets or sets a value indicating whether guest passes are on.</summary>
    public bool GuestPassesEnabled { get; set; }

    /// <summary>Gets or sets who may invite.</summary>
    public Guid[] InviterIds { get; set; } = [];

    /// <summary>Gets or sets the libraries guests may ever get.</summary>
    public Guid[] GuestLibraryIds { get; set; } = [];

    /// <summary>Gets or sets open passes per inviter.</summary>
    public int MaxLivePassesPerInviter { get; set; }

    /// <summary>Gets or sets how long a link waits to be opened, in minutes.</summary>
    public int LinkMinutes { get; set; }

    /// <summary>Gets or sets the shortest pass, in hours.</summary>
    public int PassHoursMin { get; set; }

    /// <summary>Gets or sets the longest pass, in hours.</summary>
    public int PassHoursMax { get; set; }

    /// <summary>Gets or sets how long a guest may be away, in minutes.</summary>
    public int IdleMinutes { get; set; }

    /// <summary>Gets or sets a value indicating whether guests may transcode.</summary>
    public bool GuestTranscoding { get; set; }

    /// <summary>Gets or sets the most a guest may stream, in Mbit/s.</summary>
    public int GuestBitrateMbps { get; set; }

    /// <summary>Gets or sets the most people a room takes.</summary>
    public int RoomMaxMembers { get; set; }

    /// <summary>Gets or sets the most guests a room takes.</summary>
    public int RoomMaxGuests { get; set; }

    /// <summary>
    /// Gets or sets the rooms server's address; null (left out, as by a KasuVideo that doesn't know
    /// it) keeps the saved one, empty clears it.
    /// </summary>
    public string? RoomsServerUrl { get; set; }

    /// <summary>The settings to save.</summary>
    /// <param name="current">The settings saved now, for what this leaves out.</param>
    /// <returns>A new configuration with these values.</returns>
    public PluginConfiguration ToConfiguration(PluginConfiguration current) => new()
    {
        GuestPassesEnabled = GuestPassesEnabled,
        InviterIds = InviterIds ?? [],
        GuestLibraryIds = GuestLibraryIds ?? [],
        MaxLivePassesPerInviter = MaxLivePassesPerInviter,
        LinkMinutes = LinkMinutes,
        PassHoursMin = PassHoursMin,
        PassHoursMax = PassHoursMax,
        IdleMinutes = IdleMinutes,
        GuestTranscoding = GuestTranscoding,
        GuestBitrateMbps = GuestBitrateMbps,
        RoomMaxMembers = RoomMaxMembers,
        RoomMaxGuests = RoomMaxGuests,
        RoomsServerUrl = (RoomsServerUrl ?? current.RoomsServerUrl).Trim(),
    };
}
