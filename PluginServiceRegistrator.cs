// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using Jellyfin.Plugin.KasuVideoCompanion.Guests;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion;

/// <summary>
/// Adds the companion's services to Jellyfin's: guest passes (<see cref="GuestPassService"/>,
/// its store, its Jellyfin side and the visitors' rate limit). Jellyfin finds this class in the
/// plugin's assembly and calls it once at startup.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IGuestAccounts, JellyfinGuestAccounts>();
        serviceCollection.AddSingleton<VisitorLimiter>(_ => new VisitorLimiter(TimeProvider.System));
        serviceCollection.AddSingleton<GuestPassService>(services => new GuestPassService(
            new GuestPassStore(Plugin.Instance!.DataFolderPath),
            services.GetRequiredService<IGuestAccounts>(),
            () => Plugin.Instance!.Configuration,
            TimeProvider.System,
            services.GetRequiredService<ILogger<GuestPassService>>()));
    }
}
