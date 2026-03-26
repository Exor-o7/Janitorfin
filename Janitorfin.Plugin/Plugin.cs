using System;
using System.Collections.Generic;
using Janitorfin.Plugin.Configuration;
using Janitorfin.Plugin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Janitorfin.Plugin;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        HomeScreenSectionsIntegrationBootstrap.Initialize(Configuration);
    }

    public override string Name => "Janitorfin";

    public override Guid Id => Guid.Parse("8eab83a3-4377-4036-8b37-68bc4767bc9e");

    public static Plugin? Instance { get; private set; }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);

        if (configuration is PluginConfiguration pluginConfiguration)
        {
            HomeScreenSectionsIntegrationBootstrap.Refresh(pluginConfiguration);
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        ];
    }
}