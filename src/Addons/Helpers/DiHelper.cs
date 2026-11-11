using Addons.Providers;
using Core.Client.Interfaces;
using Games.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Addons.Helpers;

/// <summary>
///     Registers addon-related services with the dependency injection container.
/// </summary>
public static class DiHelper
{
    /// <summary>
    ///     Adds dependencies to work with addons.
    /// </summary>
    public static IServiceCollection WithAddons(this IServiceCollection container)
    {
        _ = container.AddSingleton<InstalledAddonsProviderFactory>();
        _ = container.AddSingleton<DownloadableAddonsProviderFactory>();
        _ = container.AddSingleton<OriginalCampaignsProvider>();
        _ = container.AddSingleton<MetadataProvider>();
        _ = container.AddSingleton(static sp => new AddonImportFolderScanner(
            sp.GetRequiredService<InstalledGamesProvider>(),
            sp.GetRequiredService<IConfigProvider>(),
            sp.GetRequiredService<ILogger<AddonImportFolderScanner>>()));

        return container.AddTransient<IAddonDropHelper, AddonDropHelper>();
    }
}
