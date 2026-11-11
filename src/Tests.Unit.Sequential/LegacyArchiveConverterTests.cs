using System.IO.Compression;
using System.Text.Json;
using Addons.Addons;
using Addons.Helpers;
using Addons.Providers;
using Core.All;
using Core.All.Enums;
using Core.All.Serializable.Addon;
using Core.Client.Api;
using Core.Client.Cache;
using Core.Client.Config;
using Core.Client.Helpers;
using Core.Client.Interfaces;
using Games.Games;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ports.Ports.EDuke32;

namespace Tests.Unit.Sequential;

/// <summary>
///     Tests for <see cref="LegacyArchiveConverter" /> and importing archives without a manifest.
/// </summary>
public sealed class LegacyArchiveConverterTests : IDisposable
{
    private readonly DukeGame _game = new() { Duke64RomPath = null, DukeZHRomPath = null, DukeWTInstallPath = null };

    private readonly string _sourceFolder = Path.Combine(Path.GetTempPath(), "BL_LegacyImport_" + Guid.NewGuid().ToString("N"));

    /// <summary>
    ///     Initializes a new instance of the <see cref="LegacyArchiveConverterTests" /> class.
    /// </summary>
    public LegacyArchiveConverterTests()
    {
        _ = Directory.CreateDirectory(_sourceFolder);
        _ = Directory.CreateDirectory(_game.MapsFolderPath);
        _ = Directory.CreateDirectory(_game.CampaignsFolderPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_sourceFolder))
        {
            Directory.Delete(_sourceFolder, true);
        }

        if (Directory.Exists(ClientProperties.AddonsFolderPath))
        {
            Directory.Delete(ClientProperties.AddonsFolderPath, true);
        }
    }

    /// <summary>
    ///     A single map with a readme becomes one map package that takes its title and author from the readme.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_SingleMap_CreatesMapPackageWithReadmeMetadata()
    {
        var source = CreateZip("duke_test_map.zip", new()
        {
            ["testmap.map"] = "MAPDATA",
            ["music.mid"] = "MIDI",
            ["testmap.txt"] = "==========\r\nTitle                   : Test Station\r\nAuthor                  : Some Mapper\r\nDescription             : A test.\r\n"
        });

        var result = await LegacyArchiveConverter.ConvertAsync(source, _game, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(AddonTypeEnum.Map, result.ResultObject.AddonType);
        Assert.Equal(Path.Combine(_game.MapsFolderPath, "duke_test_map.zip"), result.ResultObject.PathToPackage);
        Assert.True(File.Exists(source), "The source archive must be preserved.");

        var manifests = ReadManifests(result.ResultObject.PathToPackage);
        var manifest = Assert.Single(manifests);

        Assert.Equal("imported-duke-test-map", manifest.Id);
        Assert.Equal("Test Station", manifest.Title);
        Assert.Equal("Some Mapper", manifest.Author);
        Assert.Equal(GameEnum.Duke3D, manifest.SupportedGame.Game);
        Assert.Equal("testmap.map", Assert.IsType<MapFileJsonModel>(manifest.StartMap).File);
        Assert.Contains("Test Station", manifest.Description, StringComparison.Ordinal);

        var entries = ReadEntryNames(result.ResultObject.PathToPackage);
        Assert.Contains("testmap.map", entries);
        Assert.Contains("music.mid", entries);
    }

    /// <summary>
    ///     A single wrapping folder is removed so map and resource paths match the original release layout.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_WrappedInFolder_StripsCommonRoot()
    {
        var source = CreateZip("wrapped.zip", new()
        {
            ["wrapped/level.map"] = "MAPDATA",
            ["wrapped/tiles020.art"] = "ART"
        });

        var result = await LegacyArchiveConverter.ConvertAsync(source, _game, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var manifest = Assert.Single(ReadManifests(result.ResultObject.PathToPackage));
        Assert.Equal("level.map", Assert.IsType<MapFileJsonModel>(manifest.StartMap).File);
        Assert.Equal("Wrapped", manifest.Title);
        Assert.Contains("tiles020.art", ReadEntryNames(result.ResultObject.PathToPackage));
    }

    /// <summary>
    ///     A pack of maps without scripts gets one map manifest per map.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_MultipleMapsWithoutCon_CreatesOneManifestPerMap()
    {
        var source = CreateZip("pack.zip", new()
        {
            ["alpha.map"] = "A",
            ["beta.map"] = "B"
        });

        var result = await LegacyArchiveConverter.ConvertAsync(source, _game, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(AddonTypeEnum.Map, result.ResultObject.AddonType);

        var manifests = ReadManifests(result.ResultObject.PathToPackage);
        Assert.Equal(2, manifests.Count);
        Assert.Equal(["imported-pack-alpha", "imported-pack-beta"], manifests.Select(static m => m.Id).Order());
        Assert.Equal(2, manifests.Select(static m => m.Id).Distinct().Count());
    }

    /// <summary>
    ///     Several maps with CON scripts are treated as a campaign and placed in the campaigns folder.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_MultipleMapsWithCon_CreatesCampaign()
    {
        var source = CreateZip("episode.zip", new()
        {
            ["e1l1.map"] = "A",
            ["e1l2.map"] = "B",
            ["episode.con"] = "include game.con"
        });

        var result = await LegacyArchiveConverter.ConvertAsync(source, _game, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(AddonTypeEnum.TC, result.ResultObject.AddonType);
        Assert.StartsWith(_game.CampaignsFolderPath, result.ResultObject.PathToPackage, StringComparison.OrdinalIgnoreCase);

        var manifest = Assert.Single(ReadManifests(result.ResultObject.PathToPackage));
        Assert.Equal("episode.con", manifest.MainCon);
    }

    /// <summary>
    ///     An archive without maps is rejected and nothing is written.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_NoMaps_FailsWithoutOutput()
    {
        var source = CreateZip("nomaps.zip", new() { ["readme.txt"] = "hello" });

        var result = await LegacyArchiveConverter.ConvertAsync(source, _game, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("No .map files", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_game.MapsFolderPath, "nomaps.zip")));
        Assert.False(File.Exists(Path.Combine(_game.CampaignsFolderPath, "nomaps.zip")));
    }

    /// <summary>
    ///     A screenshot in the archive is reused as the addon preview image.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_WithScreenshot_AddsPreviewImage()
    {
        var source = CreateZip("shots.zip", new()
        {
            ["shots.map"] = "A",
            ["screen1.png"] = "PNG"
        });

        var result = await LegacyArchiveConverter.ConvertAsync(source, _game, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var entries = ReadEntryNames(result.ResultObject.PathToPackage);
        Assert.Contains("screen1.png", entries);
        Assert.Contains("preview.png", entries);
    }

    /// <summary>
    ///     Dropping a community zip installs it, the installed addon scan loads it as a map, and EDuke32
    ///     receives the package as a group file together with the start map.
    /// </summary>
    [Fact]
    public async Task AddAddonsAsync_ArchiveWithoutManifest_IsImportedAndLaunchable()
    {
        var source = CreateZip("dropped_map.zip", new()
        {
            ["dropped.map"] = "MAPDATA",
            ["dropped.txt"] = "Title: Dropped Map"
        });

        using var factory = CreateProviderFactory();
        var helper = new AddonDropHelper(factory, new Mock<IUserNotifier>().Object, NullLogger<AddonDropHelper>.Instance);

        var failed = await helper.AddAddonsAsync([source], _game);

        Assert.Null(failed);

        var provider = factory.Get(_game);
        await provider.CreateCacheAsync(true, AddonTypeEnum.Map);

        var map = Assert.Single(provider.GetInstalledAddonsByType(AddonTypeEnum.Map), static a => a.Title == "Dropped Map");
        Assert.IsNotType<LooseMap>(map);
        Assert.Equal("dropped.map", Assert.IsType<MapFileJsonModel>(map.StartMap).File);

        var args = new EDuke32().GetStartGameArgs(_game, map, [], [], false, true);

        Assert.Contains($@"-g ""{Path.Combine(_game.MapsFolderPath, "dropped_map.zip")}""", args, StringComparison.Ordinal);
        Assert.Contains(@"-map ""dropped.map""", args, StringComparison.Ordinal);
        Assert.Contains("-nosetup", args, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Files placed in a game's import folder are installed and moved aside so they are not imported twice.
    /// </summary>
    [Fact]
    public async Task ImportFolderScanner_ImportsAndMovesProcessedFiles()
    {
        var gamesProvider = new Mock<Games.Providers.InstalledGamesProvider>(new Mock<IConfigProvider>().Object);

        var installFolder = Path.Combine(_sourceFolder, "duke");
        _ = Directory.CreateDirectory(installFolder);
        await File.WriteAllTextAsync(Path.Combine(installFolder, "DUKE3D.GRP"), "GRP", TestContext.Current.CancellationToken);
        var installed = new DukeGame { GameInstallFolder = installFolder, Duke64RomPath = null, DukeZHRomPath = null, DukeWTInstallPath = null };

        _ = Directory.CreateDirectory(_game.ImportFolderPath);

        var good = Path.Combine(_game.ImportFolderPath, "good.zip");
        var bad = Path.Combine(_game.ImportFolderPath, "bad.zip");
        File.Move(CreateZip("good.zip", new() { ["good.map"] = "A" }), good);
        File.Move(CreateZip("bad.zip", new() { ["notes.txt"] = "no maps" }), bad);

        _ = gamesProvider.Setup(static x => x.GetGames()).Returns([installed]);

        var scanner = new AddonImportFolderScanner(gamesProvider.Object, new ConfigProviderFake(), NullLogger<AddonImportFolderScanner>.Instance);
        var results = await scanner.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(static r => r.FileName == "good.zip").IsSuccess);
        Assert.False(results.Single(static r => r.FileName == "bad.zip").IsSuccess);

        Assert.True(File.Exists(Path.Combine(_game.MapsFolderPath, "good.zip")));
        Assert.True(File.Exists(Path.Combine(_game.ImportFolderPath, AddonImportFolderScanner.ImportedFolderName, "good.zip")));
        Assert.True(File.Exists(Path.Combine(_game.ImportFolderPath, AddonImportFolderScanner.FailedFolderName, "bad.zip")));
        Assert.False(File.Exists(good));
        Assert.False(File.Exists(bad));

        var second = await scanner.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Empty(second);
    }

    /// <summary>
    ///     A custom import folder chosen by the user is scanned instead of the default one.
    /// </summary>
    [Fact]
    public async Task ImportFolderScanner_UsesCustomFolder()
    {
        var customFolder = Path.Combine(_sourceFolder, "MyDukeMaps");
        _ = Directory.CreateDirectory(customFolder);
        File.Move(CreateZip("custom.zip", new() { ["custom.map"] = "A" }), Path.Combine(customFolder, "custom.zip"));

        var config = new ConfigProviderFake();
        config.SetImportFolder(GameEnum.Duke3D, customFolder);

        var scanner = new AddonImportFolderScanner(new Mock<Games.Providers.InstalledGamesProvider>(config).Object, config, NullLogger<AddonImportFolderScanner>.Instance);

        Assert.Equal(customFolder, scanner.GetImportFolder(_game));

        var result = Assert.Single(await scanner.ScanGameAsync(_game, TestContext.Current.CancellationToken));

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(File.Exists(Path.Combine(_game.MapsFolderPath, "custom.zip")));
        Assert.True(File.Exists(Path.Combine(customFolder, AddonImportFolderScanner.ImportedFolderName, "custom.zip")));

        config.SetImportFolder(GameEnum.Duke3D, null);
        Assert.Equal(_game.ImportFolderPath, scanner.GetImportFolder(_game));
    }

    /// <summary>
    ///     Creates a ZIP file in the temporary source folder.
    /// </summary>
    private string CreateZip(string name, Dictionary<string, string> files)
    {
        var path = Path.Combine(_sourceFolder, name);

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryName, content) in files)
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }

    /// <summary>
    ///     Reads every root manifest of a package.
    /// </summary>
    private static List<AddonManifestJsonModel> ReadManifests(string pathToPackage)
    {
        using var zip = ZipFile.OpenRead(pathToPackage);

        return
        [
            .. zip.Entries
                  .Where(static e => e.FullName.StartsWith("addon", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                  .Select(static e =>
                  {
                      using var s = e.Open();
                      return JsonSerializer.Deserialize(s, AddonManifestJsonContext.Default.AddonManifestJsonModel)!;
                  })
        ];
    }

    /// <summary>
    ///     Reads the entry names of a package.
    /// </summary>
    private static HashSet<string> ReadEntryNames(string pathToPackage)
    {
        using var zip = ZipFile.OpenRead(pathToPackage);

        return zip.Entries.Select(static e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Creates an installed addons provider factory backed by offline metadata.
    /// </summary>
    private static InstalledAddonsProviderFactory CreateProviderFactory()
    {
        Mock<IConfigProvider> config = new();
        _ = config.Setup(static x => x.DisabledAutoloadMods).Returns(new HashSet<string>());
        _ = config.Setup(static x => x.FavoriteAddons).Returns(new HashSet<AddonId>());

        return new InstalledAddonsProviderFactory(
            config.Object,
            new Mock<ICacheAdder<Stream>>().Object,
            new OriginalCampaignsProvider(config.Object),
            new MetadataProvider(new OfflineApiInterface(NullLogger<OfflineApiInterface>.Instance), NullLogger<MetadataProvider>.Instance),
            NullLoggerFactory.Instance
            );
    }
}
