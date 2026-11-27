// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion;

/// <summary>
/// Adds the companion's services to Jellyfin's: guest passes (<see cref="GuestPassService"/>,
/// its store, its Jellyfin side and the visitors' rate limit), what it knows of Watch together
/// groups (<see cref="GroupTracker"/>, written by <see cref="GroupRecorder"/>), the guard every API
/// call passes through (<see cref="GuestGuard"/>, with <see cref="GuestStalls"/>), the notices that
/// tell the rooms server a pass ended (<see cref="EndNotices"/>, signed with
/// <see cref="NoticeKey"/>), and the end of every pass when the server stops
/// (<see cref="GuestPassesOnStop"/>). Jellyfin finds this class in the plugin's assembly and calls it
/// once at startup.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IGuestAccounts, JellyfinGuestAccounts>();
        serviceCollection.AddSingleton<VisitorLimiter>(_ => new VisitorLimiter(TimeProvider.System));

        // A pass's end told to the rooms server at once, signed with the companion's own key.
        serviceCollection.AddSingleton<NoticeKey>(services => new NoticeKey(
            Plugin.Instance!.DataFolderPath,
            services.GetRequiredService<ILogger<NoticeKey>>()));
        serviceCollection.AddSingleton<IEndNotices>(services => new EndNotices(
            services.GetRequiredService<NoticeKey>(),
            services.GetRequiredService<GroupTracker>(),
            () => Plugin.Instance!.Configuration,
            () => services.GetRequiredService<IHttpClientFactory>().CreateClient(NamedClient.Default),
            TimeProvider.System,
            services.GetRequiredService<ILogger<EndNotices>>()));
        serviceCollection.AddSingleton<GuestPassService>(services => new GuestPassService(
            new GuestPassStore(Plugin.Instance!.DataFolderPath),
            services.GetRequiredService<IGuestAccounts>(),
            () => Plugin.Instance!.Configuration,
            TimeProvider.System,
            services.GetRequiredService<ILogger<GuestPassService>>(),
            services.GetRequiredService<IEndNotices>()));

        // A session that ends leaves its group (Jellyfin's SyncPlay does the same).
        serviceCollection.AddSingleton<GroupTracker>(services =>
        {
            var tracker = new GroupTracker();
            services.GetRequiredService<ISessionManager>().SessionEnded +=
                (_, e) => tracker.Left(e.SessionInfo.UserId, e.SessionInfo.DeviceId);
            return tracker;
        });

        serviceCollection.AddSingleton<GroupRecorder>();
        serviceCollection.AddSingleton<GuestStalls>(_ => new GuestStalls(TimeProvider.System));
        serviceCollection.AddScoped<GuestGuard>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<GuestGuard>());

        // Every pass ends when the server stops: without the companion nothing confines a guest.
        serviceCollection.AddHostedService<GuestPassesOnStop>();
    }
}
