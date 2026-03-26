using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Janitorfin.Plugin.Configuration;

public enum SonarrUnmonitorScope
{
    Episode,
    Season,
    Series,
}

public sealed class CleanupRuleConfiguration
{
    public const int Inherit = -2;

    public CleanupRuleConfiguration()
    {
        DeleteAfterWatchDays = Inherit;
        DeleteNeverWatchedAfterDays = Inherit;
    }

    public int DeleteAfterWatchDays { get; set; }

    public int DeleteNeverWatchedAfterDays { get; set; }
}

public sealed class LibraryRuleConfiguration
{
    public string LibraryName { get; set; } = string.Empty;

    public string LibraryPathPrefix { get; set; } = string.Empty;

    public CleanupRuleConfiguration DefaultRules { get; set; } = new();

    public CleanupRuleConfiguration MovieRules { get; set; } = new();

    public CleanupRuleConfiguration EpisodeRules { get; set; } = new();

    public CleanupRuleConfiguration VideoRules { get; set; } = new();
}

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        DeleteAfterWatchDays = 30;
        DeleteNeverWatchedAfterDays = 180;
        ProtectedTag = "janitorfin_keep";
        KeepFavorites = true;
        EnablePendingDeletion = true;
        PendingDeletionGraceDays = 30;
        EnableHomeScreenSectionsIntegration = true;
        DryRun = true;
        UnmonitorRadarrOnDelete = true;
        UnmonitorSonarrOnDelete = true;
        MovieRules = new CleanupRuleConfiguration();
        EpisodeRules = new CleanupRuleConfiguration();
        VideoRules = new CleanupRuleConfiguration();
        LibraryRules = [];
        SonarrUnmonitorScope = SonarrUnmonitorScope.Episode;
    }

    public int DeleteAfterWatchDays { get; set; }

    public int DeleteNeverWatchedAfterDays { get; set; }

    public CleanupRuleConfiguration MovieRules { get; set; }

    public CleanupRuleConfiguration EpisodeRules { get; set; }

    public CleanupRuleConfiguration VideoRules { get; set; }

    public List<LibraryRuleConfiguration> LibraryRules { get; set; }

    public string ProtectedTag { get; set; }

    public bool KeepFavorites { get; set; }

    public bool EnablePendingDeletion { get; set; }

    public int PendingDeletionGraceDays { get; set; }

    public bool EnableHomeScreenSectionsIntegration { get; set; }

    public bool DryRun { get; set; }

    public bool EnableRadarrIntegration { get; set; }

    public string RadarrServerUrl { get; set; } = string.Empty;

    public string RadarrApiKey { get; set; } = string.Empty;

    public bool UnmonitorRadarrOnDelete { get; set; }

    public bool EnableSonarrIntegration { get; set; }

    public string SonarrServerUrl { get; set; } = string.Empty;

    public string SonarrApiKey { get; set; } = string.Empty;

    public bool UnmonitorSonarrOnDelete { get; set; }

    public SonarrUnmonitorScope SonarrUnmonitorScope { get; set; }
}