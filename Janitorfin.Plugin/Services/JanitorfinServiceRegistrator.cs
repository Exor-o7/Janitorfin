using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Janitorfin.Plugin.Services;

public sealed class JanitorfinServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PendingDeletionQueueService>();
        serviceCollection.AddSingleton<CleanupEvaluationService>();
        serviceCollection.AddSingleton<CleanupExecutionService>();
        serviceCollection.AddSingleton<IRadarrClient, RadarrClient>();
        serviceCollection.AddSingleton<ISonarrClient, SonarrClient>();
    }
}