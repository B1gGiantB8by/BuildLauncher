using Core.All;
using Core.All.Enums;
using Core.Client.Helpers;
using Core.Client.Interfaces;
using Games.Games;
using Games.Providers;
using Microsoft.Extensions.Logging;

namespace Addons.Helpers;

/// <summary>
///     The outcome of importing one file from a game's import folder.
/// </summary>
/// <param name="Game">
///     The game whose import folder contained the file.
/// </param>
/// <param name="FileName">
///     The name of the imported file.
/// </param>
/// <param name="IsSuccess">
///     Whether the file was installed.
/// </param>
/// <param name="Message">
///     A description of the installed addon or of the failure.
/// </param>
public sealed record ImportFolderResult(GameEnum Game, string FileName, bool IsSuccess, string Message);


/// <summary>
///     Installs addon archives and loose maps placed in each installed game's import folder.
/// </summary>
/// <remarks>
///     Run this before the installed addon caches are built: imported packages are only placed in the game's
///     maps or campaigns folder and are discovered by the next cache scan. Processed source files are moved
///     to the <c>Imported</c> or <c>Failed</c> subfolder so they are not processed again.
/// </remarks>
public sealed class AddonImportFolderScanner
{
    /// <summary>
    ///     Name of the subfolder that receives successfully imported source files.
    /// </summary>
    public const string ImportedFolderName = "Imported";

    /// <summary>
    ///     Name of the subfolder that receives source files that could not be imported.
    /// </summary>
    public const string FailedFolderName = "Failed";

    private readonly InstalledGamesProvider _gamesProvider;

    private readonly IConfigProvider _config;

    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AddonImportFolderScanner" /> class.
    /// </summary>
    /// <param name="gamesProvider">
    ///     The installed games provider.
    /// </param>
    /// <param name="config">
    ///     The configuration provider holding custom import folders.
    /// </param>
    /// <param name="logger">
    ///     The logger.
    /// </param>
    public AddonImportFolderScanner(InstalledGamesProvider gamesProvider, IConfigProvider config, ILogger logger)
    {
        _gamesProvider = gamesProvider;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    ///     Gets the import folder of a game: the user's custom folder, or the default under the game's addons folder.
    /// </summary>
    /// <param name="game">
    ///     The game.
    /// </param>
    /// <returns>
    ///     The full path of the import folder.
    /// </returns>
    public string GetImportFolder(BaseGame game)
    {
        var custom = _config.GetImportFolder(game.GameEnum);

        return string.IsNullOrWhiteSpace(custom) ? game.ImportFolderPath : custom;
    }

    /// <summary>
    ///     Creates the import folder of every installed game and imports the files found in them.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A token that stops the scan between files.
    /// </param>
    /// <returns>
    ///     One result per processed file.
    /// </returns>
    public async Task<IReadOnlyList<ImportFolderResult>> ScanAsync(CancellationToken cancellationToken = default)
    {
        List<ImportFolderResult> results = [];

        foreach (var game in _gamesProvider.GetGames())
        {
            if (!game.IsBaseGameInstalled || game.GameEnum is GameEnum.Standalone)
            {
                continue;
            }

            results.AddRange(await ScanGameAsync(game, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    ///     Creates the import folder of one game and imports the files found in it.
    /// </summary>
    /// <param name="game">
    ///     The game whose import folder is scanned.
    /// </param>
    /// <param name="cancellationToken">
    ///     A token that stops the scan between files.
    /// </param>
    /// <returns>
    ///     One result per processed file.
    /// </returns>
    public async Task<IReadOnlyList<ImportFolderResult>> ScanGameAsync(BaseGame game, CancellationToken cancellationToken = default)
    {
        List<ImportFolderResult> results = [];
        var folder = GetImportFolder(game);

        try
        {
            Ensure.DirectoryExists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Can't create import folder {Folder}.", folder);

            return results;
        }

        var files = Directory.GetFiles(folder)
                             .Where(static f => LegacyArchiveConverter.IsSupportedArchive(f) || f.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
                             .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ImportFileAsync(file, game, cancellationToken).ConfigureAwait(false);

            if (result.ResultEnum is ResultEnum.Cancelled)
            {
                break;
            }

            var fileName = Path.GetFileName(file);

            if (result.IsSuccess)
            {
                _logger.LogInformation("Imported {File} for {Game}: {Message}", fileName, game.GameEnum, result.Message);
            }
            else
            {
                _logger.LogError("Failed to import {File} for {Game}: {Message}", fileName, game.GameEnum, result.Message);
            }

            MoveProcessedFile(file, folder, result.IsSuccess ? ImportedFolderName : FailedFolderName);

            results.Add(new(game.GameEnum, fileName, result.IsSuccess, result.Message));
        }

        return results;
    }

    /// <summary>
    ///     Places one file in the game's addon folders, converting it when it has no manifest.
    /// </summary>
    private static async Task<Result> ImportFileAsync(string pathToFile, BaseGame game, CancellationToken cancellationToken)
    {
        try
        {
            if (pathToFile.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            {
                Ensure.DirectoryExists(game.MapsFolderPath);
                File.Copy(pathToFile, Path.Combine(game.MapsFolderPath, Path.GetFileName(pathToFile)), true);

                return new(ResultEnum.Success, $"Added map {Path.GetFileName(pathToFile)}.");
            }

            if (pathToFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var manifest = await ManifestHelper.GetMainManifestAsync(pathToFile).ConfigureAwait(false);

                if (manifest.IsSuccess && manifest.ResultObject is not null)
                {
                    return CopyManifestPackage(pathToFile, game, manifest.ResultObject.SupportedGame.Game, manifest.ResultObject.AddonType, manifest.ResultObject.Title);
                }
            }

            var converted = await LegacyArchiveConverter.ConvertAsync(pathToFile, game, cancellationToken).ConfigureAwait(false);

            if (converted.IsSuccess)
            {
                var kind = converted.ResultObject.AddonType is AddonTypeEnum.TC ? "campaign" : "map";

                return new(ResultEnum.Success, $"Added {kind} {string.Join(", ", converted.ResultObject.Titles)}.");
            }

            return new(cancellationToken.IsCancellationRequested ? ResultEnum.Cancelled : ResultEnum.Error, converted.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new(ResultEnum.Error, ex.Message);
        }
    }

    /// <summary>
    ///     Copies an archive that already has a BuildLauncher manifest into the folder matching its type.
    /// </summary>
    private static Result CopyManifestPackage(string pathToFile, BaseGame game, GameEnum manifestGame, AddonTypeEnum type, string title)
    {
        if (manifestGame != game.GameEnum)
        {
            return new(ResultEnum.Error, $"Addon is for {manifestGame}, not {game.GameEnum}.");
        }

        var folder = type switch
        {
            AddonTypeEnum.TC => game.CampaignsFolderPath,
            AddonTypeEnum.Map => game.MapsFolderPath,
            AddonTypeEnum.Mod => game.ModsFolderPath,
            _ => null
        };

        if (folder is null)
        {
            return new(ResultEnum.Error, $"Unsupported addon type: {type}.");
        }

        Ensure.DirectoryExists(folder);
        File.Copy(pathToFile, Path.Combine(folder, Path.GetFileName(pathToFile)), true);

        return new(ResultEnum.Success, $"Added {type} {title}.");
    }

    /// <summary>
    ///     Moves a processed source file into a subfolder of the import folder.
    /// </summary>
    private void MoveProcessedFile(string pathToFile, string importFolder, string subfolder)
    {
        try
        {
            var destinationFolder = Path.Combine(importFolder, subfolder);
            Ensure.DirectoryExists(destinationFolder);
            File.Move(pathToFile, Path.Combine(destinationFolder, Path.GetFileName(pathToFile)), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Can't move processed import file {File}.", pathToFile);
        }
    }
}
