using Ports.Helpers;

namespace Tests.Unit;

/// <summary>
///     Tests for <see cref="DisplayConfigHelper" />.
/// </summary>
public sealed class DisplayConfigHelperTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "BL_DisplayConfig_" + Guid.NewGuid().ToString("N"));

    /// <summary>
    ///     Initializes a new instance of the <see cref="DisplayConfigHelperTests" /> class.
    /// </summary>
    public DisplayConfigHelperTests() => _ = Directory.CreateDirectory(_folder);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    /// <summary>
    ///     Existing keys are replaced in place, missing keys are added to the section, and other sections are untouched.
    /// </summary>
    [Fact]
    public void SetIniValues_ExistingSection_ReplacesAndAddsKeys()
    {
        var path = Path.Combine(_folder, "eduke32.cfg");
        File.WriteAllLines(path,
        [
            "[Setup]",
            "ForceSetup = 1",
            "",
            "[Screen Setup]",
            "ScreenMode = 1",
            "ScreenWidth = 2560",
            "",
            "[Controls]",
            "ScreenMode = untouched"
        ]);

        DisplayConfigHelper.SetIniValues(path, "Screen Setup", new Dictionary<string, string>
        {
            ["ScreenMode"] = "0",
            ["ScreenHeight"] = "1440"
        });

        var lines = File.ReadAllLines(path);

        Assert.Contains("ScreenMode = 0", lines);
        Assert.Contains("ScreenHeight = 1440", lines);
        Assert.Contains("ScreenWidth = 2560", lines);
        Assert.Contains("ScreenMode = untouched", lines);
        Assert.Contains("ForceSetup = 1", lines);
        Assert.Equal(0, DisplayConfigHelper.GetIniInt(path, "Screen Setup", "ScreenMode"));
        Assert.Equal(1440, DisplayConfigHelper.GetIniInt(path, "Screen Setup", "ScreenHeight"));
        Assert.True(Array.IndexOf(lines, "ScreenHeight = 1440") < Array.IndexOf(lines, "[Controls]"));
    }

    /// <summary>
    ///     A missing file or section is created.
    /// </summary>
    [Fact]
    public void SetIniValues_MissingFile_CreatesSection()
    {
        var path = Path.Combine(_folder, "sub", "nblood.cfg");

        DisplayConfigHelper.SetIniValues(path, "Screen Setup", new Dictionary<string, string> { ["ScreenMode"] = "0" });

        Assert.Equal(["[Screen Setup]", "ScreenMode = 0"], File.ReadAllLines(path));
        Assert.Null(DisplayConfigHelper.GetIniInt(path, "Screen Setup", "ScreenWidth"));
    }

    /// <summary>
    ///     A cvar line is replaced when present and appended otherwise, without matching cvars that share a prefix.
    /// </summary>
    [Fact]
    public void SetCvar_ReplacesOrAppends()
    {
        var path = Path.Combine(_folder, "settings.cfg");
        File.WriteAllLines(path, ["r_borderless \"2\"", "r_borderlessfoo \"5\"", "bind \"W\" \"gamefunc_Move_Forward\""]);

        DisplayConfigHelper.SetCvar(path, "r_borderless", "1");
        DisplayConfigHelper.SetCvar(path, "r_displayindex", "0");

        var lines = File.ReadAllLines(path);

        Assert.Equal("r_borderless \"1\"", lines[0]);
        Assert.Equal("r_borderlessfoo \"5\"", lines[1]);
        Assert.Equal("r_displayindex \"0\"", lines[^1]);
    }
}
