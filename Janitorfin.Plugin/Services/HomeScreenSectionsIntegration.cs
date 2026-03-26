using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public sealed class JanitorfinHomeScreenSectionPayload
{
    public Guid UserId { get; set; }

    public string? AdditionalData { get; set; }
}

public sealed class JanitorfinHomeScreenSectionResultsHandler
{
    private const int DefaultResultLimit = 50;

    private readonly IDtoService _dtoService;
    private readonly ILibraryManager _libraryManager;
    private readonly PendingDeletionQueueService _pendingDeletionQueueService;
    private readonly IUserManager _userManager;
    private readonly ILogger<JanitorfinHomeScreenSectionResultsHandler> _logger;

    public JanitorfinHomeScreenSectionResultsHandler(
        IDtoService dtoService,
        ILibraryManager libraryManager,
        PendingDeletionQueueService pendingDeletionQueueService,
        IUserManager userManager,
        ILogger<JanitorfinHomeScreenSectionResultsHandler> logger)
    {
        _dtoService = dtoService;
        _libraryManager = libraryManager;
        _pendingDeletionQueueService = pendingDeletionQueueService;
        _userManager = userManager;
        _logger = logger;
    }

    public QueryResult<BaseItemDto> GetPendingReviewItems(JanitorfinHomeScreenSectionPayload payload)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null
            || !configuration.EnablePendingDeletion
            || !configuration.EnableHomeScreenSectionsIntegration)
        {
            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }

        var user = _userManager.GetUserById(payload.UserId);
        if (user is null)
        {
            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }

        var entries = _pendingDeletionQueueService.GetSummary(configuration).Entries;
        if (entries.Count == 0)
        {
            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }

        var items = new List<BaseItem>(entries.Count);
        foreach (var entry in entries)
        {
            var item = _libraryManager.GetItemById<BaseItem>(entry.ItemId, user);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        if (items.Count == 0)
        {
            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }

        var totalCount = items.Count;
        if (items.Count > DefaultResultLimit)
        {
            items = items.Take(DefaultResultLimit).ToList();
        }

        var dtoOptions = new DtoOptions
        {
            Fields = new List<ItemFields>
            {
                ItemFields.PrimaryImageAspectRatio,
                ItemFields.DateCreated,
                ItemFields.Path,
                ItemFields.Overview,
            },
            ImageTypeLimit = 1,
            ImageTypes = new List<ImageType>
            {
                ImageType.Primary,
                ImageType.Thumb,
                ImageType.Backdrop,
            },
        };

        var dtos = _dtoService.GetBaseItemDtos(items, dtoOptions, user);
        _logger.LogDebug(
            "Returned {ReturnedCount} Janitorfin staged items to Home Screen Sections for user {UserId} ({TotalCount} total).",
            dtos.Count,
            user.Id,
            totalCount);

        return new QueryResult<BaseItemDto>(0, totalCount, dtos);
    }
}

public static class HomeScreenSectionsIntegrationBootstrap
{
    private const string HomeScreenSectionsAssemblyMarker = ".HomeScreenSections";
    private const string HomeScreenSectionsPluginInterfaceTypeName = "Jellyfin.Plugin.HomeScreenSections.PluginInterface";
    private const string SectionId = "JanitorfinReview";
    private const string SectionDisplayText = "Removing Soon";

    private static readonly object SyncRoot = new();
    private static bool _initialized;

    public static void Initialize(PluginConfiguration? configuration)
    {
        lock (SyncRoot)
        {
            if (!_initialized)
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                _initialized = true;
            }
        }

        Refresh(configuration);
    }

    public static void Refresh(PluginConfiguration? configuration)
    {
        if (configuration?.EnableHomeScreenSectionsIntegration != true)
        {
            return;
        }

        TryRegisterSection();
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (!(args.LoadedAssembly.FullName?.Contains(HomeScreenSectionsAssemblyMarker, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return;
        }

        Refresh(Plugin.Instance?.Configuration);
    }

    private static void TryRegisterSection()
    {
        try
        {
            var homeScreenSectionsAssembly = AssemblyLoadContext.All
                .SelectMany(loadContext => loadContext.Assemblies)
                .FirstOrDefault(assembly => assembly.FullName?.Contains(HomeScreenSectionsAssemblyMarker, StringComparison.OrdinalIgnoreCase) ?? false);

            var pluginInterfaceType = homeScreenSectionsAssembly?.GetType(HomeScreenSectionsPluginInterfaceTypeName);
            var registerMethod = pluginInterfaceType?.GetMethod("RegisterSection", BindingFlags.Public | BindingFlags.Static);
            if (registerMethod is null)
            {
                return;
            }

            var payload = CreateRegistrationPayload();
            if (payload is null)
            {
                return;
            }

            registerMethod.Invoke(null, [payload]);
        }
        catch
        {
        }
    }

    private static object? CreateRegistrationPayload()
    {
        var newtonsoftAssembly = AssemblyLoadContext.All
            .SelectMany(loadContext => loadContext.Assemblies)
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "Newtonsoft.Json", StringComparison.Ordinal));

        var jobjectType = newtonsoftAssembly?.GetType("Newtonsoft.Json.Linq.JObject");
        var parseMethod = jobjectType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (parseMethod is null)
        {
            return null;
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            id = SectionId,
            displayText = SectionDisplayText,
            resultsAssembly = typeof(JanitorfinHomeScreenSectionResultsHandler).Assembly.FullName,
            resultsClass = typeof(JanitorfinHomeScreenSectionResultsHandler).FullName,
            resultsMethod = nameof(JanitorfinHomeScreenSectionResultsHandler.GetPendingReviewItems),
        });

        return parseMethod.Invoke(null, [payloadJson]);
    }
}