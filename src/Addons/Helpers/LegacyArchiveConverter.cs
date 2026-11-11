using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Core.All;
using Core.All.Enums;
using Core.All.Helpers;
using Core.All.Serializable.Addon;
using Core.Client.Helpers;
using Games.Games;
using SharpCompress.Archives;
using BclZipArchive = System.IO.Compression.ZipArchive;
using BclZipArchiveMode = System.IO.Compression.ZipArchiveMode;
using BclCompressionLevel = System.IO.Compression.CompressionLevel;

namespace Addons.Helpers;

/// <summary>
///     Describes a package produced by <see cref="LegacyArchiveConverter" />.
/// </summary>
/// <param name="AddonType">
///     The addon type the package was installed as.
/// </param>
/// <param name="PathToPackage">
///     The path to the generated package inside the game's addon folders.
/// </param>
/// <param name="Titles">
///     The titles of the addons declared by the generated manifests.
/// </param>
public sealed record ConvertedArchive(AddonTypeEnum AddonType, string PathToPackage, IReadOnlyList<string> Titles);


/// <summary>
///     Converts community archives that have no BuildLauncher manifest into installable map or campaign packages.
/// </summary>
/// <remarks>
///     The source archive is never modified. Its files are copied into a new ZIP with a generated
///     <c>addon.json</c> manifest (one manifest per map for map packs), so the package goes through the
///     regular installed-addon pipeline and is launched with the port's group-file and map switches.
/// </remarks>
public static partial class LegacyArchiveConverter
{
    private const int MaxReadmeBytes = 64 * 1024;

    private const string ImportedIdPrefix = "imported-";

    private const string ImportedVersion = "1.0";

    private static readonly FrozenSet<string> _archiveExtensions = new[] { ".zip", ".7z", ".rar" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _previewExtensions = new[] { ".png", ".jpg", ".jpeg" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<GameEnum> _conGames = new[]
    {
        GameEnum.Duke3D, GameEnum.Duke64, GameEnum.Fury, GameEnum.Redneck, GameEnum.RidesAgain, GameEnum.NAM, GameEnum.WW2GI
    }.ToFrozenSet();

    /// <summary>
    ///     Gets a value indicating whether the file has an archive extension this converter can read.
    /// </summary>
    /// <param name="pathToFile">
    ///     The path to check.
    /// </param>
    /// <returns>
    ///     <see langword="true" /> for .zip, .7z and .rar files.
    /// </returns>
    public static bool IsSupportedArchive(string pathToFile) => _archiveExtensions.Contains(Path.GetExtension(pathToFile));

    /// <summary>
    ///     Converts an archive without a BuildLauncher manifest and places the result in the game's maps or campaigns folder.
    /// </summary>
    /// <param name="pathToArchive">
    ///     The path to the source archive. It is only read.
    /// </param>
    /// <param name="game">
    ///     The game the archive is imported for.
    /// </param>
    /// <param name="cancellationToken">
    ///     A token that cancels the conversion.
    /// </param>
    /// <returns>
    ///     The generated package, or an error describing why the archive cannot be imported.
    /// </returns>
    public static async Task<Result<ConvertedArchive>> ConvertAsync(string pathToArchive, BaseGame game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        var archiveName = Path.GetFileName(pathToArchive);

        if (!File.Exists(pathToArchive) || !IsSupportedArchive(pathToArchive))
        {
            return Fail($"{archiveName} is not a .zip, .7z or .rar archive.");
        }

        string? tempPath = null;

        try
        {
            using var archive = ArchiveFactory.Open(pathToArchive);

            var entries = archive.Entries
                                 .Where(static e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
                                 .Select(static e => (Entry: e, Path: NormalizePath(e.Key!)))
                                 .Where(static e => e.Path.Length > 0 && !IsJunk(e.Path))
                                 .ToList();

            var rootPrefix = GetCommonRootFolder(entries.Select(static e => e.Path));

            var files = entries.Select(e => (e.Entry, Path: e.Path[rootPrefix.Length..])).ToList();

            if (files.Any(static f => IsRootFile(f.Path) && f.Path.StartsWith("addon", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                return Fail($"{archiveName} contains a BuildLauncher manifest that could not be used. Check that it is valid and made for this game.");
            }

            var maps = files.Where(static f => f.Path.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
                            .Select(static f => f.Path)
                            .Order(StringComparer.OrdinalIgnoreCase)
                            .ToList();

            if (maps.Count == 0)
            {
                return Fail($"No .map files were found in {archiveName}.");
            }

            var readme = await ReadReadmeAsync(files, maps, cancellationToken).ConfigureAwait(false);
            var packTitle = GetValue(readme, TitleRegex()) ?? Humanize(Path.GetFileNameWithoutExtension(pathToArchive));
            var author = GetValue(readme, AuthorRegex());
            var description = readme is null ? null : $"```{Environment.NewLine}{readme.Trim()}{Environment.NewLine}```";
            var baseId = ImportedIdPrefix + Slugify(Path.GetFileNameWithoutExtension(pathToArchive));

            var rootFiles = files.Where(static f => IsRootFile(f.Path)).Select(static f => f.Path).ToList();
            var manifests = BuildManifests(game.GameEnum, maps, rootFiles, baseId, packTitle, author, description);

            foreach (var manifest in manifests)
            {
                var validation = AddonManifestValidator.Validate(manifest, []);

                if (!validation.IsSuccess)
                {
                    return Fail($"Could not create a manifest for {archiveName}: {validation.Message}");
                }
            }

            var addonType = manifests[0].AddonType;
            var destinationFolder = addonType is AddonTypeEnum.TC ? game.CampaignsFolderPath : game.MapsFolderPath;

            Ensure.DirectoryExists(destinationFolder);

            var destination = Path.Combine(destinationFolder, Path.GetFileNameWithoutExtension(pathToArchive) + ".zip");
            tempPath = destination + ".tmp";

            await WritePackageAsync(tempPath, files, manifests, cancellationToken).ConfigureAwait(false);

            File.Move(tempPath, destination, true);
            tempPath = null;

            return new(ResultEnum.Success, new ConvertedArchive(addonType, destination, [.. manifests.Select(static m => m.Title)]), string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new(ResultEnum.Cancelled, null, $"Import of {archiveName} was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail($"Could not import {archiveName}: {ex.Message}");
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    //nothing to do, a stale temporary file is overwritten on the next import
                }
            }
        }
    }

    /// <summary>
    ///     Builds one campaign manifest, or one map manifest per map file.
    /// </summary>
    private static List<AddonManifestJsonModel> BuildManifests(
        GameEnum game,
        IReadOnlyList<string> maps,
        IReadOnlyList<string> rootFiles,
        string baseId,
        string packTitle,
        string? author,
        string? description
        )
    {
        var rootCons = rootFiles.Where(static f => f.EndsWith(".con", StringComparison.OrdinalIgnoreCase)).ToList();
        var rootInis = rootFiles.Where(static f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).ToList();

        var isBloodEpisode = game is GameEnum.Blood && rootInis.Count > 0;
        var isConEpisode = _conGames.Contains(game) && maps.Count > 1 && rootCons.Count > 0;

        if (isBloodEpisode || isConEpisode)
        {
            string? mainCon = null;

            if (isConEpisode &&
                !rootCons.Any(static c => c.Equals("GAME.CON", StringComparison.OrdinalIgnoreCase)) &&
                rootCons.Count == 1)
            {
                mainCon = rootCons[0];
            }

            return
            [
                new AddonManifestJsonModel
                {
                    Id = baseId,
                    AddonType = AddonTypeEnum.TC,
                    SupportedGame = new() { Game = game },
                    Title = packTitle,
                    Version = ImportedVersion,
                    Author = author,
                    Description = description,
                    MainCon = mainCon,
                    Ini = isBloodEpisode ? rootInis[0] : null
                }
            ];
        }

        List<AddonManifestJsonModel> result = new(maps.Count);

        foreach (var map in maps)
        {
            var mapName = Path.GetFileNameWithoutExtension(map);

            result.Add(new AddonManifestJsonModel
            {
                Id = maps.Count == 1 ? baseId : $"{baseId}-{Slugify(mapName)}",
                AddonType = AddonTypeEnum.Map,
                SupportedGame = new() { Game = game },
                Title = maps.Count == 1 ? packTitle : $"{packTitle}: {mapName}",
                Version = ImportedVersion,
                Author = author,
                Description = description,
                StartMap = new MapFileJsonModel { File = map }
            });
        }

        return result;
    }

    /// <summary>
    ///     Copies the archive contents and the generated manifests into a new ZIP package.
    /// </summary>
    private static async Task WritePackageAsync(
        string pathToPackage,
        IReadOnlyList<(IArchiveEntry Entry, string Path)> files,
        IReadOnlyList<AddonManifestJsonModel> manifests,
        CancellationToken cancellationToken
        )
    {
        var output = File.Create(pathToPackage);
        await using var outputScope = output.ConfigureAwait(false);
        using var zip = new BclZipArchive(output, BclZipArchiveMode.Create);

        var hasPreview = files.Any(static f => IsRootFile(f.Path) && f.Path.StartsWith("preview.", StringComparison.OrdinalIgnoreCase));
        HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (entry, path) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!written.Add(path))
            {
                continue;
            }

            var isPreviewCandidate = !hasPreview && _previewExtensions.Contains(Path.GetExtension(path));

            var source = await entry.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var sourceScope = source.ConfigureAwait(false);

            if (!isPreviewCandidate)
            {
                await CopyToEntryAsync(zip, path, source, cancellationToken).ConfigureAwait(false);

                continue;
            }

            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            buffer.Position = 0;
            await CopyToEntryAsync(zip, path, buffer, cancellationToken).ConfigureAwait(false);

            buffer.Position = 0;
            await CopyToEntryAsync(zip, "preview" + Path.GetExtension(path).ToLowerInvariant(), buffer, cancellationToken).ConfigureAwait(false);

            hasPreview = true;
        }

        for (var i = 0; i < manifests.Count; i++)
        {
            var name = i == 0 ? "addon.json" : $"addon_{i + 1}.json";
            var json = JsonSerializer.SerializeToUtf8Bytes(manifests[i], AddonManifestJsonContext.Default.AddonManifestJsonModel);

            using var jsonStream = new MemoryStream(json);
            await CopyToEntryAsync(zip, name, jsonStream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Writes a stream into a new archive entry.
    /// </summary>
    private static async Task CopyToEntryAsync(BclZipArchive zip, string path, Stream source, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(path, BclCompressionLevel.Optimal);
        var target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var targetScope = target.ConfigureAwait(false);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the most relevant text file of the archive, preferring one named after a map, then a readme.
    /// </summary>
    private static async Task<string?> ReadReadmeAsync(
        IReadOnlyList<(IArchiveEntry Entry, string Path)> files,
        IReadOnlyList<string> maps,
        CancellationToken cancellationToken
        )
    {
        var texts = files.Where(static f => f.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();

        if (texts.Count == 0)
        {
            return null;
        }

        var mapNames = maps.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var chosen = texts.FirstOrDefault(t => mapNames.Contains(Path.GetFileNameWithoutExtension(t.Path)));

        if (chosen.Entry is null)
        {
            chosen = texts.FirstOrDefault(static t => Path.GetFileName(t.Path).StartsWith("readme", StringComparison.OrdinalIgnoreCase));
        }

        if (chosen.Entry is null)
        {
            chosen = texts.OrderByDescending(static t => t.Entry.Size).First();
        }

        var stream = await chosen.Entry.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var streamScope = stream.ConfigureAwait(false);

        var buffer = new byte[MaxReadmeBytes];
        var total = 0;
        int read;

        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        var bytes = buffer.AsSpan(0, total);

        string text;

        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        text = text.Replace("```", "'''", StringComparison.Ordinal).Replace("\0", string.Empty, StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    ///     Gets the first captured value of a "Key : Value" line in a readme.
    /// </summary>
    private static string? GetValue(string? readme, Regex regex)
    {
        if (readme is null)
        {
            return null;
        }

        var match = regex.Match(readme);

        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value.Trim().Trim('"', '\'', '.').Trim();

        return value.Length is 0 or > 100 ? null : value;
    }

    /// <summary>
    ///     Converts a slash-separated archive key to a relative forward-slash path.
    /// </summary>
    private static string NormalizePath(string key) => key.Replace('\\', '/').TrimStart('.', '/');

    /// <summary>
    ///     Gets a value indicating whether an archive entry is operating-system metadata rather than content.
    /// </summary>
    private static bool IsJunk(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets a value indicating whether the path is at the package root.
    /// </summary>
    private static bool IsRootFile(string path) => !path.Contains('/', StringComparison.Ordinal);

    /// <summary>
    ///     Gets the single top-level folder that wraps every entry, including its trailing slash, or an empty string.
    /// </summary>
    private static string GetCommonRootFolder(IEnumerable<string> paths)
    {
        string? prefix = null;

        foreach (var path in paths)
        {
            var slash = path.IndexOf('/', StringComparison.Ordinal);

            if (slash < 0)
            {
                return string.Empty;
            }

            var folder = path[..(slash + 1)];

            if (prefix is null)
            {
                prefix = folder;
            }
            else if (!prefix.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        return prefix ?? string.Empty;
    }

    /// <summary>
    ///     Converts a file name into a readable title.
    /// </summary>
    private static string Humanize(string name)
    {
        var words = name.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length == 0
            ? name
            : string.Join(' ', words.Select(static w => w.Length > 1 && w.All(char.IsLower) ? char.ToUpperInvariant(w[0]) + w[1..] : w));
    }

    /// <summary>
    ///     Converts a name into a lowercase identifier containing only letters, digits and dashes.
    /// </summary>
    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);

        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                _ = sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                _ = sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');

        return slug.Length == 0 ? "addon" : slug;
    }

    /// <summary>
    ///     Creates a failed conversion result.
    /// </summary>
    private static Result<ConvertedArchive> Fail(string message) => new(ResultEnum.Error, null, message);

    /// <summary>
    ///     Matches a readme title line such as "Title : My Map".
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(?:map[ \t]*)?(?:title|name)[ \t]*(?:of[ \t]+map[ \t]*)?[:=][ \t]*(?<value>\S.*?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TitleRegex();

    /// <summary>
    ///     Matches a readme author line such as "Author : Someone".
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(?:author(?:\(s\)|s)?|created[ \t]+by|made[ \t]+by|designer|mapper)[ \t]*[:=][ \t]*(?<value>\S.*?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AuthorRegex();
}
