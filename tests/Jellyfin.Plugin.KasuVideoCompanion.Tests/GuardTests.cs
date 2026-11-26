// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Guests;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

public class GroupTrackerTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();
    private const string Group = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ItKnowsWhichGroupEachSessionIsIn()
    {
        var tracker = new GroupTracker();
        tracker.Joined(Owner, "laptop", "01234567-89ab-cdef-0123-456789ABCDEF");
        tracker.Joined(Owner, "phone", "ffffffffffffffffffffffffffffffff");
        Assert.Equal(Group, tracker.GroupOf(Owner, "laptop"));
        Assert.Equal([Group, "ffffffffffffffffffffffffffffffff"], tracker.GroupsOf(Owner).Order());
        tracker.Left(Owner, "phone");
        Assert.Equal([Group], tracker.GroupsOf(Owner));
        Assert.Null(tracker.GroupOf(Member, "laptop"));
        tracker.Joined(Owner, "laptop", "not a group");
        Assert.Equal(Group, tracker.GroupOf(Owner, "laptop"));
    }

    [Fact]
    public void ItKnowsWhatEachGroupQueued()
    {
        var tracker = new GroupTracker();
        var film = Guid.NewGuid();
        tracker.Queued(Group, [film]);
        Assert.True(tracker.Holds(new HashSet<string> { Group }, film));
        Assert.False(tracker.Holds(new HashSet<string> { "ffffffffffffffffffffffffffffffff" }, film));
        Assert.False(tracker.Holds(new HashSet<string> { Group }, Guid.NewGuid()));
    }

    [Fact]
    public void AGroupRemembersItsNewestItemsOnly()
    {
        var tracker = new GroupTracker();
        var first = Guid.NewGuid();
        tracker.Queued(Group, [first]);
        tracker.Queued(Group, Enumerable.Range(0, GroupTracker.MaxItemsPerGroup).Select(_ => Guid.NewGuid()));
        Assert.False(tracker.Holds(new HashSet<string> { Group }, first));
    }
}

public class GuestAccessRulesTests
{
    private const string Jellyfin = GuestAccessRules.JellyfinControllers;
    private const string Companion = "Jellyfin.Plugin.KasuVideoCompanion.Api";
    private const string Room = "0123456789abcdef0123456789abcdef";
    private static readonly Guid Guest = Guid.NewGuid();
    private static readonly Guid Queued = Guid.NewGuid();
    private static readonly GuestScope Scope = new(Guest, new HashSet<string> { Room }, id => id == Queued);

    private static GuestVerdict Ask(string controller, string action, Dictionary<string, string?>? route = null, string? ids = null, Guid? group = null, string ns = Jellyfin) =>
        GuestAccessRules.Decide(new GuestRequest(ns, controller, action, route ?? [], ids, group), Scope);

    private static Dictionary<string, string?> Item(Guid id, string key = "itemId") => new() { [key] = id.ToString() };

    [Theory]
    [InlineData("User", "GetCurrentUser")]
    [InlineData("TimeSync", "GetUtcTime")]
    [InlineData("Session", "PostFullCapabilities")]
    [InlineData("Session", "ReportSessionEnded")]
    [InlineData("Playstate", "ReportPlaybackProgress")]
    [InlineData("DisplayPreferences", "GetDisplayPreferences")]
    [InlineData("SyncPlay", "SyncPlayReady")]
    [InlineData("SyncPlay", "SyncPlayBuffering")]
    [InlineData("SyncPlay", "SyncPlayLeaveGroup")]
    [InlineData("Image", "GetItemImage")]
    public void WhatConcernsOnlyTheirSessionIsAllowed(string controller, string action) =>
        Assert.Equal(GuestVerdict.Allow, Ask(controller, action));

    [Theory]
    [InlineData("SyncPlay", "SyncPlayCreateGroup")]
    [InlineData("SyncPlay", "SyncPlayPause")]
    [InlineData("SyncPlay", "SyncPlaySeek")]
    [InlineData("SyncPlay", "SyncPlaySetNewQueue")]
    [InlineData("SyncPlay", "SyncPlayStop")]
    [InlineData("User", "GetUsers")]
    [InlineData("Session", "GetSessions")]
    [InlineData("UserViews", "GetUserViews")]
    [InlineData("TvShows", "GetNextUp")]
    [InlineData("Search", "GetSearchHints")]
    [InlineData("DisplayPreferences", "UpdateDisplayPreferences")]
    [InlineData("Library", "GetDownload")]
    [InlineData("Playstate", "MarkPlayedItem")]
    [InlineData("Plugins", "GetPlugins")]
    public void BrowsingControllingAndEverythingElseIsRefused(string controller, string action) =>
        Assert.Equal(GuestAccessRules.NotAllowed, Ask(controller, action).Error);

    [Theory]
    [InlineData("UserLibrary", "GetItem", "itemId")]
    [InlineData("UserLibrary", "GetItemLegacy", "itemId")]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", "itemId")]
    [InlineData("DynamicHls", "GetHlsVideoSegment", "itemId")]
    [InlineData("HlsSegment", "GetHlsVideoSegmentLegacy", "itemId")]
    [InlineData("Videos", "GetVideoStream", "itemId")]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId")]
    [InlineData("VideoAttachments", "GetAttachment", "videoId")]
    [InlineData("Trickplay", "GetTrickplayTileImage", "itemId")]
    [InlineData("MediaSegments", "GetItemSegments", "itemId")]
    public void AnItemTheirRoomQueuedPlaysAndNothingElse(string controller, string action, string key)
    {
        Assert.Equal(GuestVerdict.Allow, Ask(controller, action, Item(Queued, key)));
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask(controller, action, Item(Guid.NewGuid(), key)).Error);
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask(controller, action).Error);
    }

    [Fact]
    public void ItemsByIdOnlyWhenEveryOneWasQueued()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("Items", "GetItems", ids: Queued.ToString()));
        Assert.Equal(GuestVerdict.Allow, Ask("Items", "GetItems", ids: $"{Queued:N}"));
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Items", "GetItems", ids: $"{Queued},{Guid.NewGuid()}").Error);
        // Browsing (no ids) is refused.
        Assert.Equal(GuestAccessRules.ItemNotAllowed, Ask("Items", "GetItems").Error);
    }

    [Fact]
    public void OnlyTheirOwnUser()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("User", "GetUserById", new() { ["userId"] = Guest.ToString() }));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("User", "GetUserById", new() { ["userId"] = Guid.NewGuid().ToString() }).Error);
    }

    [Fact]
    public void OnlyTheirRoomsGroup()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("SyncPlay", "SyncPlayJoinGroup", group: Guid.ParseExact(Room, "N")));
        Assert.Equal(GuestAccessRules.GroupNotAllowed, Ask("SyncPlay", "SyncPlayJoinGroup", group: Guid.NewGuid()).Error);
        Assert.Equal(GuestAccessRules.GroupNotAllowed, Ask("SyncPlay", "SyncPlayJoinGroup").Error);
        Assert.Equal(GuestVerdict.Allow, Ask("SyncPlay", "SyncPlayGetGroup", new() { ["id"] = "01234567-89ab-cdef-0123-456789abcdef" }));
        Assert.Equal(GuestAccessRules.GroupNotAllowed, Ask("SyncPlay", "SyncPlayGetGroup", new() { ["id"] = Guid.NewGuid().ToString() }).Error);
        Assert.Equal(GuestVerdict.TrimGroups, Ask("SyncPlay", "SyncPlayGetGroups"));
    }

    [Fact]
    public void OfTheCompanionOnlyWhatAGuestNeedsAndOfOtherPluginsNothing()
    {
        Assert.Equal(GuestVerdict.Allow, Ask("Guests", "Me", ns: Companion));
        // Their own pass, to leave (the route checks it's theirs and live).
        Assert.Equal(GuestVerdict.Allow, Ask("Guests", "EndMe", ns: Companion));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Guests", "EndInvite", ns: Companion).Error);
        Assert.Equal(GuestVerdict.Allow, Ask("Companion", "Info", ns: Companion));
        Assert.Equal(GuestVerdict.Allow, Ask("Settings", "GetRoomSettings", ns: Companion));
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Guests", "CreateInvite", ns: Companion).Error);
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Guests", "Status", ns: Companion).Error);
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Settings", "GetSettings", ns: Companion).Error);
        Assert.Equal(GuestAccessRules.NotAllowed, Ask("Overview", "GetCurrentUser", ns: "Jellyfin.Plugin.Other.Api").Error);
    }
}

public class GuestLookupTests : IDisposable
{
    private readonly Setup _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task AGuestIsKnownWhileTheirPassLivesAndAfter()
    {
        var pass = _s.Redeem(_s.Invite());
        Assert.True(_s.Service.IsGuest(pass.UserId));
        Assert.False(_s.Service.IsGuest(Setup.Owner));
        Assert.Equal(Setup.Group.ToString("N"), _s.Service.LivePassOf(pass.UserId)!.GroupId);
        await _s.Service.EndAllAsync(EndReasons.Ended);
        Assert.True(_s.Service.IsGuest(pass.UserId));
        Assert.Null(_s.Service.LivePassOf(pass.UserId));
    }
}
