using Jellyfin.Plugin.AniSkip.AniSkip;
using Jellyfin.Plugin.AniSkip.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AniSkip;

/// <summary>
/// Registers the AniSkip media segment provider and its helpers with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ArmIdMapper>();
        serviceCollection.AddSingleton<AniSkipApiClient>();
        serviceCollection.AddSingleton<FribbMappingService>();
        serviceCollection.AddSingleton<IMediaSegmentProvider, AniSkipSegmentProvider>();
    }
}
