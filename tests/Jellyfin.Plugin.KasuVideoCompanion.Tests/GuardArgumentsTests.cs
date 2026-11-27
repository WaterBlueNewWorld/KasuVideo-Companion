// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Guests;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

/// <summary>
/// The guard reads every argument that names something, not only the route's item: what the
/// penetration test (G8) found Jellyfin's actions also read (trickplay's media source, a
/// subtitle's query item, another session, device or play session).
/// </summary>
public class GuardArgumentsTests
{
    private const string Jellyfin = GuestAccessRules.JellyfinControllers;
    private const string Room = "0123456789abcdef0123456789abcdef";
    private const string Device = "pass-device";
    private const string Session = "session-1";
    private const string Job = "their-job";
    private static readonly Guid Guest = Guid.NewGuid();
    private static readonly Guid Queued = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static readonly GuestScope Scope = new(Guest, new HashSet<string> { Room }, id => id == Queued)
    {
        DeviceId = Device,
        SessionId = Session,
        OwnJob = job => job == Job,
        SourceOf = (item, source) => item == Queued && source == Version.ToString("N"),
        MayStall = () => true,
    };

    private static GuestVerdict Ask(
        string controller,
        string action,
        Dictionary<string, string?>? route = null,
        Dictionary<string, string?>? args = null,
        Dictionary<string, string?>? body = null,
        string? device = Device,
        GuestScope? scope = null) =>
        GuestAccessRules.Decide(
            new GuestRequest(Jellyfin, controller, action, route ?? [], null, null)
            {
                Arguments = new Dictionary<string, string?>(args ?? [], StringComparer.OrdinalIgnoreCase),
                Body = body ?? [],
                Device = device,
            },
            scope ?? Scope);

    private static Dictionary<string, string?> Item(Guid id, string key = "itemId") => new() { [key] = id.ToString() };

    [Fact]
    public void TrickplayOfAnotherItemThroughAQueuedOneIsRefused()
    {
        // Jellyfin reads `mediaSourceId ?? itemId`: a queued item in the route gave any item's frames.
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Trickplay", "GetTrickplayTileImage", Item(Queued), new() { ["mediaSourceId"] = Other.ToString() }).Error);
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Trickplay", "GetTrickplayHlsPlaylist", Item(Queued), new() { ["MediaSourceId"] = Other.ToString("N") }).Error);
        Assert.Equal(GuestVerdict.Allow, Ask("Trickplay", "GetTrickplayTileImage", Item(Queued), new() { ["mediaSourceId"] = Queued.ToString("N") }));
        // One of the item's own versions is fine.
        Assert.Equal(GuestVerdict.Allow, Ask("Videos", "GetVideoStream", Item(Queued), new() { ["mediaSourceId"] = Version.ToString("N") }));
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Videos", "GetVideoStream", Item(Queued), new() { ["mediaSourceId"] = "not-a-source" }).Error);
    }

    [Fact]
    public void ASubtitlesQueryItemIsCheckedToo()
    {
        // Jellyfin's subtitle action takes the query's itemId over the route's.
        var route = new Dictionary<string, string?> { ["routeItemId"] = Queued.ToString(), ["routeMediaSourceId"] = Queued.ToString("N") };
        Assert.Equal(GuestVerdict.Allow, Ask("Subtitle", "GetSubtitle", route));
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Subtitle", "GetSubtitle", route, new() { ["itemId"] = Other.ToString() }).Error);
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Subtitle", "GetSubtitle", route, new() { ["mediaSourceId"] = Other.ToString("N") }).Error);
        Assert.Equal(
            GuestAccessRules.ItemNotAllowed,
            Ask("Subtitle", "GetSubtitle", new() { ["routeItemId"] = Queued.ToString(), ["routeMediaSourceId"] = Other.ToString("N") }).Error);
    }

    [Fact]
    public void OnlyTheirOwnSessionsCapabilitiesAndViewing()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("Session", "PostFullCapabilities"));
        Assert.Equal(GuestVerdict.Allow, Ask("Session", "PostCapabilities", args: new() { ["id"] = Session }));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Session", "PostCapabilities", args: new() { ["id"] = "someone-else" }).Error);
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Session", "PostFullCapabilities", args: new() { ["id"] = "someone-else" }).Error);
        Assert.Equal(GuestVerdict.Allow, Ask("Session", "ReportViewing", args: new() { ["itemId"] = Queued.ToString("N") }));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Session", "ReportViewing", args: new() { ["sessionId"] = "someone-else", ["itemId"] = Queued.ToString() }).Error);
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Session", "ReportViewing", args: new() { ["itemId"] = Other.ToString() }).Error);
    }

    [Fact]
    public void OnlyTheirOwnDeviceAndPlaySessions()
    {
        // Jellyfin kills a transcode by its play session alone, whoever's it is.
        Assert.Equal(GuestVerdict.Allow, Ask("HlsSegment", "StopEncodingProcess", args: new() { ["deviceId"] = Device, ["playSessionId"] = Job }));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("HlsSegment", "StopEncodingProcess", args: new() { ["deviceId"] = Device, ["playSessionId"] = "someone-elses" }).Error);
        Assert.Equal(GuestAccessRules.DeviceNotAllowed, Ask("HlsSegment", "StopEncodingProcess", args: new() { ["deviceId"] = "not-theirs", ["playSessionId"] = Job }).Error);
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Playstate", "PingPlaybackSession", args: new() { ["playSessionId"] = "someone-elses" }).Error);
        Assert.Equal(GuestVerdict.Allow, Ask("Playstate", "PingPlaybackSession", args: new() { ["playSessionId"] = Job }));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Playstate", "ReportPlaybackStopped", body: new() { ["ItemId"] = Queued.ToString(), ["PlaySessionId"] = "someone-elses" }).Error);
        // A stream asked with another device's id is refused too.
        Assert.Equal(GuestAccessRules.DeviceNotAllowed, Ask("DynamicHls", "GetMasterHlsVideoPlaylist", Item(Queued), new() { ["deviceId"] = "not-theirs" }).Error);
        Assert.Equal(GuestVerdict.Allow, Ask("DynamicHls", "GetMasterHlsVideoPlaylist", Item(Queued), new() { ["DeviceId"] = Device.ToUpperInvariant(), ["playSessionId"] = Job }));
    }

    [Fact]
    public void ReportsNameOnlyTheirRoomsItems()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("Playstate", "ReportPlaybackStart", body: new() { ["ItemId"] = Queued.ToString(), ["MediaSourceId"] = Queued.ToString("N") }));
        Assert.Equal(GuestVerdict.Allow, Ask("Playstate", "ReportPlaybackProgress", body: new() { ["ItemId"] = Queued.ToString(), ["MediaSourceId"] = Version.ToString("N") }));
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Playstate", "ReportPlaybackStart", body: new() { ["ItemId"] = Other.ToString() }).Error);
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Playstate", "ReportPlaybackStart", body: new() { ["ItemId"] = Queued.ToString(), ["MediaSourceId"] = Other.ToString("N") }).Error);
        // Nothing named: their session only (Jellyfin fills in theirs).
        Assert.Equal(GuestVerdict.Allow, Ask("Playstate", "ReportPlaybackStopped", body: new() { ["ItemId"] = Guid.Empty.ToString() }));
    }

    [Fact]
    public void LiveStreamsAreNeverAGuests()
    {
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Videos", "GetVideoStream", Item(Queued), new() { ["liveStreamId"] = "opened-for-someone" }).Error);
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("MediaInfo", "GetPostedPlaybackInfo", Item(Queued), body: new() { ["LiveStreamId"] = "opened-for-someone" }).Error);
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Playstate", "ReportPlaybackStopped", body: new() { ["LiveStreamId"] = "opened-for-someone" }).Error);
    }

    [Fact]
    public void OnlyFromTheDeviceTheirPassWasOpenedOn()
    {
        Assert.Equal(GuestAccessRules.DeviceNotAllowed, Ask("User", "GetCurrentUser", device: "not-theirs").Error);
        Assert.Equal(GuestAccessRules.DeviceNotAllowed, Ask("Videos", "GetVideoStream", Item(Queued), device: null).Error);
        Assert.Equal(GuestVerdict.Allow, Ask("User", "GetCurrentUser", device: "PASS-DEVICE"));
        // Signing out is always fine.
        Assert.Equal(GuestVerdict.Allow, Ask("Session", "ReportSessionEnded", device: "not-theirs"));
        // A pass from before G8 has no device: not checked.
        Assert.Equal(GuestVerdict.Allow, Ask("User", "GetCurrentUser", device: "anything", scope: Scope with { DeviceId = null }));
    }

    [Fact]
    public void ThePicturesButNotTheirFilePaths()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("Image", "GetItemImage", Item(Other)));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Image", "GetItemImageInfos", Item(Queued)).Error);
    }

    [Fact]
    public void ABitrateTestNoBiggerThanKasuVideosOwn()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("MediaInfo", "GetBitrateTestBytes"));
        Assert.Equal(GuestVerdict.Allow, Ask("MediaInfo", "GetBitrateTestBytes", args: new() { ["size"] = "2097152" }));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("MediaInfo", "GetBitrateTestBytes", args: new() { ["size"] = "2097153" }).Error);
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("MediaInfo", "GetBitrateTestBytes", args: new() { ["size"] = "100000000" }).Error);
    }

    [Fact]
    public void AGuestsStallsNeverHoldTheRoom()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("SyncPlay", "SyncPlaySetIgnoreWait", body: new() { ["IgnoreWait"] = "True" }));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("SyncPlay", "SyncPlaySetIgnoreWait", body: new() { ["IgnoreWait"] = "False" }).Error);
        Assert.Equal(GuestVerdict.Allow, Ask("SyncPlay", "SyncPlayBuffering"));
        Assert.Equal(GuestAccessRules.Busy, Ask("SyncPlay", "SyncPlayBuffering", scope: Scope with { MayStall = () => false }).Error);
        // Ready and the rest aren't counted.
        Assert.Equal(GuestVerdict.Allow, Ask("SyncPlay", "SyncPlayReady", scope: Scope with { MayStall = () => false }));
    }

    [Fact]
    public void PingIsTheirs()
    {
        // The action behind GET and POST /System/Ping.
        Assert.Equal(GuestVerdict.Allow, Ask("System", "PingSystem"));
    }
}
