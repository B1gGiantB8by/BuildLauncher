using Addons.Providers;
using Core.All.Enums;
using Core.Client.Helpers;
using Core.Client.Interfaces;
using Games.Games;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;

namespace Addons.Helpers;

/// <summary>
///     Adds dropped addon files to a game and installs them.
/// </summary>
public interface IAddonDropHelper
{
    /// <summary>
    ///     Adds the addons from the specified file paths for the given game.
    /// </summary>
    /// <param name="filePaths">
    ///     The paths of the dropped addon files.
    /// </param>
    /// <param name="game">
    ///     The game the addons belong to.
    /// </param>
    /// <returns>
    ///     The file names that failed to install, or
    ///     <c>
    ///         null
    ///     </c>
    ///     when all succeeded.
    /// </returns>
    Task<IReadOnlyList<string>?> AddAddonsAsync(IReadOnlyList<string> filePaths, BaseGame game);
}


/// <summary>
///     Default implementation of <see cref="IAddonDropHelper" />.
/// </summary>
public sealed class AddonDropHelper : IAddonDropHelper
{
    private readonly InstalledAddonsProviderFactory _installedAddonsProvider;
    private readonly IUserNotifier _notifier;
    private readonly ILogger<AddonDropHelper> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AddonDropHelper" /> class.
    /// </summary>
    /// <param name="installedAddonsProvider">
    ///     The installed addons provider factory.
    /// </param>
    /// <param name="notifier">
    ///     The user notifier.
    /// </param>
    /// <param name="logger">
    ///     The logger.
    /// </param>
    public AddonDropHelper(
        InstalledAddonsProviderFactory installedAddonsProvider,
        IUserNotifier notifier,
        ILogger<AddonDropHelper> logger
        )
    {
        _installedAddonsProvider = installedAddonsProvider;
        _notifier = notifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> AddAddonsAsync(IReadOnlyList<string> filePaths, BaseGame game)
    {
        if (filePaths.Count == 0)
        {
            return null;
        }

        List<string>? failedInstalls = null;

        foreach (var file in filePaths)
        {
            var addedAddon = await AddAddonAsync(file, game).ConfigureAwait(false);

            if (addedAddon is not null)
            {
                _notifier.Show($"Added {addedAddon.Value.Type} for {addedAddon.Value.Game}.", NotificationSeverity.Success);

                continue;
            }

            failedInstalls ??= [];
            failedInstalls.Add(Path.GetFileName(file));
        }

        return failedInstalls;
    }

    private async Task<(GameEnum Game, AddonTypeEnum Type)?> AddAddonAsync(string pathToFile, BaseGame game)
    {
        if (!LegacyArchiveConverter.IsSupportedArchive(pathToFile) &&
            !pathToFile.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("File is not an archive or .map.");
            _notifier.Show($"{pathToFile} is not .zip, .7z, .rar or .map.", NotificationSeverity.Error);

            return null;
        }

        var addon = pathToFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || pathToFile.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
            ? await GetGameAndTypeFromFileAsync(pathToFile, game.GameEnum).ConfigureAwait(false)
            : null;

        if (addon is null && LegacyArchiveConverter.IsSupportedArchive(pathToFile))
        {
            return await ImportLegacyArchiveAsync(pathToFile, game).ConfigureAwait(false);
        }

        if (addon is null)
        {
            _logger.LogError("Can't get addon from the file.");
            _notifier.Show($"Can't get addon from {pathToFile}.", NotificationSeverity.Error);

            return null;
        }

        if (addon.Value.Game != game.GameEnum)
        {
            _logger.LogError("Addon is for the wrong game: {Game}.", addon.Value.Game);
            _notifier.Show($"Addon {pathToFile} is for the wrong game: {addon.Value.Game}.", NotificationSeverity.Error);

            return null;
        }

        string folderToPutFile;

        if (addon.Value.AddonType is AddonTypeEnum.TC)
        {
            folderToPutFile = game.CampaignsFolderPath;
        }
        else if (addon.Value.AddonType is AddonTypeEnum.Map)
        {
            folderToPutFile = game.MapsFolderPath;
        }
        else if (addon.Value.AddonType is AddonTypeEnum.Mod)
        {
            folderToPutFile = game.ModsFolderPath;
        }
        else
        {
            _logger.LogError("Unknown addon type: {AddonType}.", addon.Value.AddonType);
            _notifier.Show($"Unknown addon type: {addon.Value.AddonType}.", NotificationSeverity.Error);

            return null;
        }

        Ensure.DirectoryExists(folderToPutFile);

        var newPathToFile = Path.Combine(folderToPutFile, Path.GetFileName(pathToFile));

        File.Copy(pathToFile, newPathToFile, true);

        var provider = _installedAddonsProvider.Get(game);
        await provider.AddAddonAsync(newPathToFile).ConfigureAwait(false);

        return addon;
    }

    /// <summary>
    ///     Converts an archive without a BuildLauncher manifest and adds the generated package.
    /// </summary>
    private async Task<(GameEnum Game, AddonTypeEnum Type)?> ImportLegacyArchiveAsync(string pathToFile, BaseGame game)
    {
        var converted = await LegacyArchiveConverter.ConvertAsync(pathToFile, game).ConfigureAwait(false);

        if (!converted.IsSuccess)
        {
            _logger.LogError("Can't import archive {Path}: {Message}", pathToFile, converted.Message);
            _notifier.Show(converted.Message, NotificationSeverity.Error);

            return null;
        }

        _logger.LogInformation("Imported {Path} as {Type} package {Package}.", pathToFile, converted.ResultObject.AddonType, converted.ResultObject.PathToPackage);

        var provider = _installedAddonsProvider.Get(game);
        await provider.AddAddonAsync(converted.ResultObject.PathToPackage).ConfigureAwait(false);

        return (game.GameEnum, converted.ResultObject.AddonType);
    }

    private async Task<(GameEnum Game, AddonTypeEnum AddonType)?> GetGameAndTypeFromFileAsync(string pathToFile, GameEnum gameEnum)
    {
        if (ArchiveFactory.IsArchive(pathToFile, out _))
        {
            var manifest = await ManifestHelper.GetMainManifestAsync(pathToFile).ConfigureAwait(false);

            if (!manifest.IsSuccess)
            {
                return null;
            }

            var supportedGame = manifest.ResultObject.SupportedGame.Game;
            var type = manifest.ResultObject.AddonType;

            return new(supportedGame, type);
        }

        if (pathToFile.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            return new(gameEnum, AddonTypeEnum.Map);
        }

        return null;
    }
}
