using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
namespace Jellyfin.Plugin.FirstAdded;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<ILedgerStore, LedgerStore>();
        services.AddSingleton<FirstAddedService>();
        services.AddHostedService<LibraryEvents>();
    }
}
