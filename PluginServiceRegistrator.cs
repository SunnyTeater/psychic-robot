using Jellyfin.Plugin.NamingStandardizer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NamingStandardizer;

/// <summary>
/// Registriert die Dienste des Plugins.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LiveLog>();
        serviceCollection.AddSingleton<Planner>();
        serviceCollection.AddSingleton<Renamer>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<Renamer>());
    }
}
