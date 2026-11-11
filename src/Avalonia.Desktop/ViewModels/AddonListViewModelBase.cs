using Core.All;
using System.Collections.Immutable;
using Addons.Addons;
using Addons.Helpers;
using Addons.Providers;
using Avalonia.Desktop.Helpers;
using Avalonia.Desktop.Misc;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.All.Enums;
using Core.Client.Helpers;
using Core.Client.Interfaces;
using Core.Client.Providers;
using Games.Games;
using Games.Providers;
using Microsoft.Extensions.Logging;
using Ports.Ports;

namespace Avalonia.Desktop.ViewModels;

/// <summary>
///     Base class for ViewModels that display and manage a list of installed addons
///     (campaigns, maps, or mods) for a specific game.
/// </summary>
public abstract partial class AddonListViewModelBase : RightPanelViewModel, IPortsButtonControl, IDisposable
{
    /// <summary>
    ///     The provider of installed addons for the current game.
    /// </summary>
    protected readonly InstalledAddonsProvider _installedAddonsProvider;

    /// <summary>
    ///     The logger.
    /// </summary>
    protected readonly ILogger _logger;

    /// <summary>
    ///     The port starter used to launch addons.
    /// </summary>
    protected readonly PortStarter _portStarter;

    /// <summary>
    ///     The configuration provider.
    /// </summary>
    protected readonly IConfigProvider _config;

    private readonly IAddonDropHelper _addonInstaller;
    private readonly InstalledGamesProvider _gamesProvider;
    private readonly MetadataProvider _metadataProvider;
    private readonly IFolderOpener _folderOpener;
    private readonly IUserNotifier _userNotifier;

    private ImmutableList<BaseAddon>? _addonsList;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AddonListViewModelBase" /> class.
    /// </summary>
    /// <param name="game">
    ///     The game associated with this panel.
    /// </param>
    /// <param name="gamesProvider">
    ///     The installed games provider.
    /// </param>
    /// <param name="playtimeProvider">
    ///     The playtime provider.
    /// </param>
    /// <param name="ratingProvider">
    ///     The rating provider.
    /// </param>
    /// <param name="metadataProvider">
    ///     The metadata provider.
    /// </param>
    /// <param name="installedAddonsProviderFactory">
    ///     Factory to create the installed addons provider for the given game.
    /// </param>
    /// <param name="portStarter">
    ///     The port starter for launching addons, or null if not supported.
    /// </param>
    /// <param name="bitmapsCache">
    ///     The bitmaps cache.
    /// </param>
    /// <param name="addonInstaller">
    ///     The addon drop helper for installing dropped files.
    /// </param>
    /// <param name="folderOpener">
    ///     Service to open OS file explorer folders.
    /// </param>
    /// <param name="userNotifier">
    ///     Service to show user notifications.
    /// </param>
    /// <param name="config">
    ///     The configuration provider.
    /// </param>
    /// <param name="logger">
    ///     The logger instance.
    /// </param>
    protected AddonListViewModelBase(
        BaseGame game,
        InstalledGamesProvider gamesProvider,
        PlaytimeProvider playtimeProvider,
        RatingProvider ratingProvider,
        MetadataProvider metadataProvider,
        InstalledAddonsProviderFactory installedAddonsProviderFactory,
        PortStarter portStarter,
        BitmapsCache bitmapsCache,
        IAddonDropHelper addonInstaller,
        IFolderOpener folderOpener,
        IUserNotifier userNotifier,
        IConfigProvider config,
        ILogger logger
        ) : base(playtimeProvider, ratingProvider, metadataProvider, bitmapsCache, config)
    {
        Game = game;
        _gamesProvider = gamesProvider;
        _installedAddonsProvider = installedAddonsProviderFactory.Get(game);
        _metadataProvider = metadataProvider;
        _addonInstaller = addonInstaller;
        _folderOpener = folderOpener;
        _userNotifier = userNotifier;
        _portStarter = portStarter;
        _config = config;
        _logger = logger;

        _gamesProvider.GameChangedEvent += OnGameChanged;
        _installedAddonsProvider.AddonsChangedEvent += OnAddonChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gamesProvider.GameChangedEvent -= OnGameChanged;
        _installedAddonsProvider.AddonsChangedEvent -= OnAddonChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Gets the game associated with this panel.
    /// </summary>
    public BaseGame Game { get; }

    /// <summary>
    ///     Gets the addon type that this panel manages.
    /// </summary>
    protected abstract AddonTypeEnum AddonType { get; }

    /// <summary>
    ///     Gets the file system path to the folder containing the managed addons.
    /// </summary>
    protected abstract string BaseFolderPath { get; }

    /// <summary>
    ///     Initializes the ViewModel by refreshing the addon cache.
    /// </summary>
    /// <returns>
    ///     A task representing the asynchronous operation.
    /// </returns>
    public Task InitializeAsync() => UpdateAsync(false);

    private async Task UpdateAsync(bool createNew)
    {
        IsInProgress = true;
        await _installedAddonsProvider.CreateCacheAsync(createNew, AddonType).ConfigureAwait(true);
        IsInProgress = false;
    }

    private void OnGameChanged(GameEnum parameterName)
    {
        if (parameterName == Game.GameEnum)
        {
            RefreshAddonsList();
        }
    }

    private void OnAddonChanged(GameEnum gameEnum, AddonTypeEnum? addonType)
    {
        if (gameEnum == Game.GameEnum && addonType == AddonType)
        {
            RefreshAddonsList();
        }
    }


    #region Binding Properties

    /// <summary>
    ///     Gets the list of installed addons for the managed <see cref="AddonType" />.
    /// </summary>
    public ImmutableList<BaseAddon> AddonsList
    {
        get
        {
            if (_addonsList is null)
            {
                _addonsList = BuildAddonsList();
            }

            return _addonsList;
        }
    }

    /// <summary>
    ///     Builds the list of installed addons for the managed <see cref="AddonType" />.
    ///     Override to apply additional sorting or filtering.
    /// </summary>
    protected virtual ImmutableList<BaseAddon> BuildAddonsList() => [.. _installedAddonsProvider.GetInstalledAddonsByType(AddonType)];

    /// <summary>
    /// Refreshes the list of addons by clearing the cached addon list and notifying
    /// that the <see cref="AddonsList" /> property has changed.
    /// </summary>
    protected void RefreshAddonsList()
    {
        _addonsList = null;

        OnPropertyChanged(nameof(AddonsList));
    }

    /// <summary>
    ///     Gets or sets the currently selected addon.
    /// </summary>
    public override BaseAddon? SelectedAddon
    {
        get;
        set
        {
            field = value;

            OnPropertyChanged(nameof(SelectedAddonDescription));
            OnPropertyChanged(nameof(SelectedAddonPreview));
            OnPropertyChanged(nameof(IsPreviewVisible));
            OnPropertyChanged(nameof(SelectedAddonRating));
            OnPropertyChanged(nameof(IsMetadataUpdateAvailable));

            OnSelectedAddonChanged();
        }
    }

    /// <summary>
    ///     Called when <see cref="SelectedAddon" /> changes. Override to add additional property change
    ///     notifications or command invalidation.
    /// </summary>
    protected virtual void OnSelectedAddonChanged() { }

    /// <summary>
    ///     Gets or sets the search box filter text.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSearchBoxCommand))]
    public partial string SearchBoxText { get; set; } = string.Empty;

    partial void OnSearchBoxTextChanged(string value) => RefreshAddonsList();

    /// <summary>
    ///     Gets or sets whether a background operation is in progress.
    /// </summary>
    [ObservableProperty]
    public partial bool IsInProgress { get; set; }

    /// <summary>
    ///     Gets whether the ports buttons panel should be visible.
    /// </summary>
    public abstract bool IsPortsButtonsVisible { get; }

    #endregion


    #region Relay Commands

    /// <summary>
    ///     Starts the selected addon with the specified port command.
    /// </summary>
    /// <param name="command">
    ///     The port or custom port to use for starting.
    /// </param>
    [RelayCommand]
    private async Task StartAddonAsync(object? command)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(SelectedAddon);

            var result = await StartAddonCoreAsync(command).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _userNotifier.Show(result.Message, NotificationSeverity.Error);
            }

            OnPropertyChanged(nameof(SelectedAddonPlaytime));
        }
        catch (Exception ex)
        {
            _userNotifier.Show("Critical error! Exception is written to the log.", NotificationSeverity.Error);
            _logger.LogCritical(ex, $"=== Error while starting addon {SelectedAddon?.Title} ===");
        }
    }

    /// <summary>
    ///     Called by <see cref="StartAddonAsync" /> to execute the type-specific start logic.
    ///     Override to handle starting for the specific addon type.
    /// </summary>
    /// <param name="command">
    ///     The port or custom port to use for starting.
    /// </param>
    /// <returns>
    ///     A task representing the asynchronous operation.
    /// </returns>
    protected virtual Task<Result> StartAddonCoreAsync(object? command)
    {
        throw new NotSupportedException("This addon type does not support starting.");
    }

    /// <summary>
    ///     Opens the addon folder in the OS file explorer.
    /// </summary>
    [RelayCommand]
    private void OpenFolder() => _folderOpener.OpenFolder(BaseFolderPath);

    /// <summary>
    ///     Refreshes the addon list by recreating the cache.
    /// </summary>
    /// <returns>
    ///     A task representing the asynchronous operation.
    /// </returns>
    [RelayCommand]
    private Task RefreshListAsync() => UpdateAsync(true);

    /// <summary>
    ///     Deletes the currently selected addon.
    /// </summary>
    [RelayCommand]
    private void DeleteAddon()
    {
        ArgumentNullException.ThrowIfNull(SelectedAddon);

        _installedAddonsProvider.DeleteAddon(SelectedAddon);
    }

    /// <summary>
    ///     Clears the search box text.
    /// </summary>
    [RelayCommand(CanExecute = nameof(ClearSearchBoxCanExecute))]
    private void ClearSearchBox() => SearchBoxText = string.Empty;

    private bool ClearSearchBoxCanExecute() => !string.IsNullOrEmpty(SearchBoxText);

    /// <summary>
    ///     Processes dropped files by installing them as addons.
    /// </summary>
    /// <param name="filePaths">
    ///     The list of file paths to install.
    /// </param>
    /// <returns>
    ///     A task representing the asynchronous operation.
    /// </returns>
    [RelayCommand]
    private Task ProcessDroppedFilesAsync(List<string> filePaths) => _addonInstaller.AddAddonsAsync(filePaths, Game);

    /// <summary>
    ///     Lets the user pick archives or maps and imports them; archives without a manifest are converted.
    /// </summary>
    [RelayCommand]
    private async Task ImportAddonsAsync()
    {
        try
        {
            var files = await AvaloniaProperties.TopLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = $"Import addons for {Game.FullName}",
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Addon archives and maps") { Patterns = ["*.zip", "*.7z", "*.rar", "*.map"] },
                        FilePickerFileTypes.All
                    ]
                }
                ).ConfigureAwait(true);

            var paths = files.Select(static f => f.TryGetLocalPath()).OfType<string>().ToList();

            if (paths.Count == 0)
            {
                return;
            }

            _ = await _addonInstaller.AddAddonsAsync(paths, Game).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _userNotifier.Show("Import failed. Details are written to the log.", NotificationSeverity.Error);
            _logger.LogError(ex, "Error while importing addons for {Game}.", Game.GameEnum);
        }
    }

    /// <inheritdoc />
    public string ImportFolderPath => CreateImportScanner().GetImportFolder(Game);

    /// <summary>
    ///     Opens the game's import folder in the OS file explorer, creating it when needed.
    /// </summary>
    [RelayCommand]
    private void OpenImportFolder()
    {
        try
        {
            Ensure.DirectoryExists(ImportFolderPath);
            _folderOpener.OpenFolder(ImportFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _userNotifier.Show($"Can't open import folder {ImportFolderPath}: {ex.Message}", NotificationSeverity.Error);
        }
    }

    /// <summary>
    ///     Imports the files in the game's import folder and refreshes every addon list of the game.
    /// </summary>
    [RelayCommand]
    private async Task ScanImportFolderAsync()
    {
        try
        {
            IsInProgress = true;

            var results = await CreateImportScanner().ScanGameAsync(Game).ConfigureAwait(true);

            if (results.Count == 0)
            {
                _userNotifier.Show($"No new .zip, .7z, .rar or .map files in {ImportFolderPath}.", NotificationSeverity.Success);

                return;
            }

            foreach (var type in new[] { AddonTypeEnum.TC, AddonTypeEnum.Map, AddonTypeEnum.Mod })
            {
                await _installedAddonsProvider.CreateCacheAsync(true, type).ConfigureAwait(true);
            }

            var succeeded = results.Count(static r => r.IsSuccess);

            if (succeeded > 0)
            {
                _userNotifier.Show($"Imported {succeeded} file(s) from the import folder.", NotificationSeverity.Success);
            }

            foreach (var failure in results.Where(static r => !r.IsSuccess))
            {
                _userNotifier.Show($"Could not import {failure.FileName}: {failure.Message}", NotificationSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            _userNotifier.Show("Import failed. Details are written to the log.", NotificationSeverity.Error);
            _logger.LogError(ex, "Error while scanning the import folder for {Game}.", Game.GameEnum);
        }
        finally
        {
            IsInProgress = false;
        }
    }

    /// <summary>
    ///     Lets the user choose a custom import folder for the game.
    /// </summary>
    [RelayCommand]
    private async Task ChangeImportFolderAsync()
    {
        try
        {
            var folders = await AvaloniaProperties.TopLevel.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = $"Choose import folder for {Game.FullName}",
                    AllowMultiple = false
                }
                ).ConfigureAwait(true);

            var path = folders.Count == 0 ? null : folders[0].TryGetLocalPath();

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _config.SetImportFolder(Game.GameEnum, path);
            OnPropertyChanged(nameof(ImportFolderPath));
            _userNotifier.Show($"{Game.FullName} import folder: {path}", NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            _userNotifier.Show("Could not change the import folder. Details are written to the log.", NotificationSeverity.Error);
            _logger.LogError(ex, "Error while choosing the import folder for {Game}.", Game.GameEnum);
        }
    }

    /// <summary>
    ///     Switches the game back to its default import folder.
    /// </summary>
    [RelayCommand]
    private void ResetImportFolder()
    {
        _config.SetImportFolder(Game.GameEnum, null);
        OnPropertyChanged(nameof(ImportFolderPath));
        _userNotifier.Show($"{Game.FullName} import folder: {ImportFolderPath}", NotificationSeverity.Success);
    }

    /// <summary>
    ///     Creates an import folder scanner for this view model's game.
    /// </summary>
    private AddonImportFolderScanner CreateImportScanner() => new(_gamesProvider, _config, _logger);

    /// <summary>
    ///     Updates metadata for the specified addon, or for the selected addon if null is passed.
    /// </summary>
    /// <param name="value">
    ///     The addon to update, or null to use the selected addon.
    /// </param>
    /// <returns>
    ///     A task representing the asynchronous operation.
    /// </returns>
    public override async Task UpdateMetadataAsync(object? value)
    {
        BaseAddon addon;

        if (value is BaseAddon v)
        {
            addon = v;
        }
        else if (value is null && SelectedAddon is not null)
        {
            addon = SelectedAddon;
        }
        else
        {
            throw new ArgumentException(
                $"Cannot update metadata. Unexpected type: {(value is null ? "null" : value.GetType().Name)}.",
                nameof(value)
                );
        }

        if (addon.FileInfo is null)
        {
            throw new InvalidOperationException("Cannot update metadata because file info is missing.");
        }

        IsInProgress = true;

        var result = await _metadataProvider.UpdateMetadataAsync(addon.FileInfo.Value).ConfigureAwait(true);

        IsInProgress = false;

        if (result.IsSuccess)
        {
            _userNotifier.Show("Metadata updated.", NotificationSeverity.Success);
        }
        else
        {
            _userNotifier.Show("Error while updating metadata.", NotificationSeverity.Error);
        }
    }

    /// <summary>
    ///     Adds the specified campaign to favorites.
    /// </summary>
    [RelayCommand]
    public void AddToFavorite(object? value)
    {
        if (value is not BaseAddon addon)
        {
            throw new ArgumentException($"Expected {nameof(BaseAddon)} but received {value?.GetType().Name}.", nameof(value));
        }

        _config.ChangeFavoriteState(addon.AddonId, true);
        addon.IsFavorite = true;

        RefreshAddonsList();
    }

    /// <summary>
    ///     Removes the specified campaign from favorites.
    /// </summary>
    [RelayCommand]
    public void RemoveFromFavorite(object? value)
    {
        if (value is not BaseAddon addon)
        {
            throw new ArgumentException($"Expected {nameof(BaseAddon)} but received {value?.GetType().Name}.", nameof(value));
        }

        _config.ChangeFavoriteState(addon.AddonId, false);
        addon.IsFavorite = false;

        RefreshAddonsList();
    }

    #endregion
}
