// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Configuration;
using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuVideoCompanion.Tests;

/// <summary>A clock the test moves.</summary>
public sealed class FakeClock : TimeProvider
{
    public DateTime Now { get; set; } = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(Now);

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Jellyfin's side, in memory: users, who is in which group, and what was made or ended.</summary>
public sealed class FakeAccounts : IGuestAccounts
{
    public Dictionary<Guid, UserFacts> Users { get; } = [];

    public HashSet<(Guid User, Guid Group)> InGroup { get; } = [];

    public Dictionary<Guid, string> Libraries { get; } = [];

    public List<GuestAccountRequest> Created { get; } = [];

    public List<Guid> Ended { get; } = [];

    public Dictionary<Guid, DateTime> Activity { get; } = [];

    /// <summary>When set, CreateAsync throws it.</summary>
    public Exception? CreateFails { get; set; }

    /// <summary>When set, CreateAsync runs it once the account exists, before answering.</summary>
    public Func<Task>? DuringCreate { get; set; }

    public UserFacts? Describe(Guid userId) => Users.GetValueOrDefault(userId);

    public bool IsInGroup(Guid userId, string? deviceId, Guid groupId) => InGroup.Contains((userId, groupId));

    public string? LibraryName(Guid libraryId) => Libraries.GetValueOrDefault(libraryId);

    public bool NameTaken(string name) => Users.Values.Any(u => u.Name == name);

    public async Task<CreatedGuest> CreateAsync(GuestAccountRequest request)
    {
        if (CreateFails is { } e)
        {
            throw e;
        }

        if (NameTaken(request.UserName))
        {
            throw new GuestNameTakenException(request.UserName);
        }

        Created.Add(request);
        var id = Guid.NewGuid();
        Users[id] = new UserFacts(id, request.UserName, false, true, false, request.LibraryIds);
        if (DuringCreate is { } during)
        {
            await during();
        }

        return new CreatedGuest(id, request.UserName, "token-" + id.ToString("N"), "server-1");
    }

    /// <summary>Accounts whose EndAsync throws, as a failed delete.</summary>
    public HashSet<Guid> EndFails { get; } = [];

    public Task EndAsync(Guid guestUserId)
    {
        if (EndFails.Contains(guestUserId))
        {
            throw new InvalidOperationException("delete failed");
        }

        Ended.Add(guestUserId);
        Users.Remove(guestUserId);
        return Task.CompletedTask;
    }

    public DateTime? LastActivity(Guid guestUserId) => Activity.TryGetValue(guestUserId, out var at) ? at : null;

    public IReadOnlyList<Guid> GuestUserIds() => Users.Values.Where(u => u.IsGuest).Select(u => u.Id).ToList();

    /// <summary>Items' seasons and series, as the test sets them.</summary>
    public Dictionary<Guid, Guid[]> Families { get; } = [];

    public IReadOnlyList<Guid> Family(Guid itemId) =>
        [itemId, .. Families.GetValueOrDefault(itemId) ?? []];
}

/// <summary>A service over fakes, in a temporary folder, with an inviter in a group and two libraries.</summary>
public sealed class Setup : IDisposable
{
    public Setup()
    {
        Folder = Path.Combine(Path.GetTempPath(), "kasuvideo-companion-tests", Guid.NewGuid().ToString("N"));
        Accounts.Users[Owner] = new UserFacts(Owner, "kasutest", false, false, true, []);
        Accounts.Users[Admin] = new UserFacts(Admin, "kasuadmin", true, false, true, []);
        Accounts.Users[Other] = new UserFacts(Other, "kasutest2", false, false, false, [Movies]);
        Accounts.Libraries[Movies] = "Movies";
        Accounts.Libraries[Shows] = "Shows";
        Accounts.InGroup.Add((Owner, Group));
        Accounts.InGroup.Add((Other, Group));
        Settings.GuestPassesEnabled = true;
        Settings.InviterIds = [Owner];
        Settings.GuestLibraryIds = [Movies, Shows];
        Store = new GuestPassStore(Folder);
        Service = new GuestPassService(Store, Accounts, () => Settings, Clock, NullLogger<GuestPassService>.Instance);
    }

    public static Guid Owner { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static Guid Admin { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static Guid Other { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static Guid Movies { get; } = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public static Guid Shows { get; } = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static Guid Group { get; } = Guid.Parse("0123456789abcdef0123456789abcdef");

    public string Folder { get; }

    public FakeClock Clock { get; } = new();

    public FakeAccounts Accounts { get; } = new();

    public PluginConfiguration Settings { get; } = new();

    public GuestPassStore Store { get; }

    public GuestPassService Service { get; }

    /// <summary>An invite from the owner for the movies, as it should work.</summary>
    public CreatedInvite Invite(int? hours = null)
    {
        var result = Service.CreateInvite(Owner, "owner-device", Group.ToString(), [Movies], hours);
        Assert.True(result.Ok, result.Error);
        return result.Value!;
    }

    /// <summary>Opens <paramref name="invite"/> as Mika, as it should work.</summary>
    public RedeemedPass Redeem(CreatedInvite invite, string nickname = "Mika")
    {
        var result = Service.RedeemAsync(invite.Code, nickname, "visitor-device", "Firefox", "0.9.0", "10.0.0.9").GetAwaiter().GetResult();
        Assert.True(result.Ok, result.Error);
        return result.Value!;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder.
        }
    }
}
