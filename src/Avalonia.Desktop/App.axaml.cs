using System.Collections.Immutable;
using Addons.Helpers;
using Addons.Providers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Desktop.Helpers;
using Avalonia.Desktop.Misc;
using Avalonia.Desktop.ViewModels;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Core.All.Enums;
using Core.Client.Enums;
using Core.Client.Helpers;
using Core.Client.Interfaces;
using Database.Client;
using Games;
using Games.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ports.Helpers;
using Ports.Providers;
using S3;
using Tools;

namespace Avalonia.Desktop;

/// <summary>
///     Application entry point and service configuration.
/// </summary>
public sealed class App : Application
{
    /// <summary>
    ///     The singleton application instance.
    /// </summary>
    private static App _app = null!;

    /// <summary>
    ///     The application service provider.
    /// </summary>
    private static ServiceProvider _services;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        #if DEBUG
        this.AttachDeveloperTools();
        #endif

        _app = this;
    }

    /// <summary>
    ///     Runs the application with the specified AppBuilder.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="smokeTest">Whether to verify the published executable in an isolated test directory.</param>
    /// <param name="restart">Whether to verify settings from the first smoke-test launch.</param>
    /// <returns>The application exit code.</returns>
    public static int Run(AppBuilder builder, bool smokeTest = false, bool restart = false)
    {
        int code;

        using ClassicDesktopStyleApplicationLifetime lifetime = new()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        _ = builder.SetupWithLifetime(lifetime);

        LoadBindings();

        var logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
        using (var dbContext = _services.GetRequiredService<IDbContextFactory<DatabaseContext>>().CreateDbContext())
        {
            try
            {
                var recoveredLocks = DatabaseInitializer.InitializeAsync(dbContext).GetAwaiter().GetResult();
                if (recoveredLocks > 0)
                {
                    logger.LogWarning("Recovered an abandoned SQLite migration lock. SQLite recovery files were preserved.");
                }
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Could not initialize the application database.");
                throw;
            }
        }

        var config = _services.GetRequiredService<IConfigProvider>();
        var viewLocator = _services.GetRequiredService<ViewLocator>();
        var vmFactory = _services.GetRequiredService<IViewModelsFactory>();
        var installedGamesProvider = _services.GetRequiredService<InstalledGamesProvider>();

        SetTheme(config.Theme);

        var bitmapsCache = _services.GetRequiredService<BitmapsCache>();
        bitmapsCache.InitializeCache();

        _app.DataTemplates.Add(viewLocator);
        _app.Resources[nameof(CachedHashToBitmapConverter)] = new CachedHashToBitmapConverter(bitmapsCache);

        var importResults = ScanImportFolders(config, logger);

        using MainWindow mainWindow = new(installedGamesProvider, config, _services.GetRequiredService<ILogger<MainWindow>>());

        if (importResults.Count > 0)
        {
            mainWindow.Opened += (_, _) => ShowImportResults(importResults, _services.GetRequiredService<IUserNotifier>());
        }

        lifetime.MainWindow = mainWindow;
        lifetime.MainWindow.DataContext = vmFactory.GetMainWindowViewModel();

        //initialize
        _ = NotificationsHelper.NotificationManager;

        if (ClientProperties.IsDeveloperMode)
        {
            logger.LogInformation("Starting in developer mode");
        }

        if (ClientProperties.IsOfflineMode)
        {
            logger.LogInformation("Starting in offline mode");
        }

        logger.LogInformation($"BuildLauncher version: {ClientProperties.CurrentVersion}");
        logger.LogInformation($"Operating system: {Environment.OSVersion}");
        logger.LogInformation($"Working folder is {ClientProperties.WorkingFolder}");

        var portsProvider = _services.GetRequiredService<PortsProvider>();
        RenameSaveFolder(portsProvider);

        var metadataProvider = _services.GetRequiredService<MetadataProvider>();
        var metadataInitialization = metadataProvider.InitializeAsync();

        if (smokeTest)
        {
            mainWindow.Opened += async (_, _) => await PublishedSmokeTest.RunAsync(
                mainWindow, lifetime, config, _services.GetRequiredService<IApiInterface>(), metadataInitialization, restart).ConfigureAwait(true);
        }

        try
        {
            code = lifetime.Start();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "== Critical error while running app ==");

            try
            {
                lifetime.Shutdown();
            }
            catch (Exception ex2)
            {
                logger.LogCritical(ex2, "== Critical error while shutting down app ==");
            }

            throw;
        }

        return code;
    }


    /// <summary>
    ///     Renames legacy save folders to their correct names.
    /// </summary>
    /// <param name="portsProvider">The ports provider.</param>
    /// <summary>
    ///     Installs archives found in each game's import folder before the addon lists are built.
    /// </summary>
    /// <param name="config">The configuration provider.</param>
    /// <param name="logger">The application logger.</param>
    /// <returns>The per-file import results.</returns>
    private static IReadOnlyList<ImportFolderResult> ScanImportFolders(IConfigProvider config, ILogger logger)
    {
        if (Design.IsDesignMode || !config.ScanImportFolders)
        {
            return [];
        }

        try
        {
            return _services.GetRequiredService<AddonImportFolderScanner>().ScanAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import folder scan failed.");

            return [];
        }
    }

    /// <summary>
    ///     Shows a summary notification for the startup import.
    /// </summary>
    /// <param name="results">The per-file import results.</param>
    /// <param name="notifier">The user notifier.</param>
    private static void ShowImportResults(IReadOnlyList<ImportFolderResult> results, IUserNotifier notifier)
    {
        var succeeded = results.Where(static r => r.IsSuccess).ToList();

        if (succeeded.Count > 0)
        {
            notifier.Show(
                succeeded.Count == 1
                    ? $"Imported {succeeded[0].FileName} ({succeeded[0].Game}). {succeeded[0].Message}"
                    : $"Imported {succeeded.Count} files from import folders.",
                NotificationSeverity.Success);
        }

        foreach (var failure in results.Where(static r => !r.IsSuccess))
        {
            notifier.Show($"Could not import {failure.FileName} ({failure.Game}): {failure.Message}", NotificationSeverity.Error);
        }
    }

    [Obsolete("Remove some time later")]
    private static void RenameSaveFolder(PortsProvider portsProvider)
    {
        ImmutableArray<string> paths =
        [
            portsProvider.GetPort(PortEnum.BuildGDX).PortSavedGamesFolderPath,
            portsProvider.GetPort(PortEnum.VoidSW).PortSavedGamesFolderPath,
            portsProvider.GetPort(PortEnum.PCExhumed).PortSavedGamesFolderPath,
            portsProvider.GetPort(PortEnum.Raze).PortSavedGamesFolderPath
        ];

        foreach (var path in paths)
        {
            var swPath = Path.Combine(path, "Wang", "shadowwarrior");
            var wangPath = Path.Combine(path, "Wang", "wang");

            var exPath = Path.Combine(path, "Slave", "exhumed");
            var slavePath = Path.Combine(path, "Slave", "slave");

            if (Directory.Exists(swPath))
            {
                Directory.Move(swPath, wangPath);
            }

            if (Directory.Exists(exPath))
            {
                Directory.Move(exPath, slavePath);
            }
        }
    }

    /// <summary>
    ///     Loads and configures all dependency injection bindings.
    /// </summary>
    private static void LoadBindings()
    {
        ServiceCollection services = new();

        _ = services.WithDebugLogging();

        if (Design.IsDesignMode)
        {
            _ = services.WithFakeConfig();
        }
        else
        {
            _ = services.WithConfig();
            _ = services.WithFileLogging();
            _ = services.WithDatabase();
        }

        if (ClientProperties.IsOfflineMode)
        {
            _ = services.WithOfflineApi();
            _ = services.WithFakeHttpClients();
        }
        else
        {
            _ = services.WithGitHubApi();
            _ = services.WithHttpClients();
        }

        _ = services.WithClient();
        _ = services.WithMVVM();
        _ = services.WithBitmapsCache();
        _ = services.WithS3FilesUploader();

        _ = services.WithGames();
        _ = services.WithPorts();
        _ = services.WithTools();
        _ = services.WithAddons();

        _services?.Dispose();

        _services = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            }
            );
    }

    /// <summary>
    ///     Sets the application theme.
    /// </summary>
    /// <param name="theme">The theme to apply.</param>
    private static void SetTheme(ThemeEnum theme)
    {
        var themeEnum = theme switch
        {
            ThemeEnum.System => ThemeVariant.Default,
            ThemeEnum.Light => ThemeVariant.Light,
            ThemeEnum.Dark => ThemeVariant.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, $"Unsupported theme value: {theme}.")
        };

        _app.RequestedThemeVariant = themeEnum;
    }
}
