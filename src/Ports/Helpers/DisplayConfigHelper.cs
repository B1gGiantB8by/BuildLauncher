using System.Globalization;
using System.Runtime.InteropServices;

namespace Ports.Helpers;

/// <summary>
///     Reads the display resolution and edits the INI-style and cvar configuration files used by Build ports.
/// </summary>
public static partial class DisplayConfigHelper
{
    private const int SmCxScreen = 0;

    private const int SmCyScreen = 1;

    /// <summary>
    ///     Gets the native resolution of the primary display in physical pixels.
    /// </summary>
    /// <returns>
    ///     The resolution, or <see langword="null" /> when it is not available on this platform.
    /// </returns>
    public static (int Width, int Height)? GetPrimaryScreenResolution()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);

        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>
    ///     Reads an integer value from a section of an INI-style configuration file.
    /// </summary>
    /// <param name="pathToFile">
    ///     The configuration file.
    /// </param>
    /// <param name="section">
    ///     The section name without brackets.
    /// </param>
    /// <param name="key">
    ///     The key to read.
    /// </param>
    /// <returns>
    ///     The value, or <see langword="null" /> when the file, section or key is missing or not a number.
    /// </returns>
    public static int? GetIniInt(string pathToFile, string section, string key)
    {
        if (!File.Exists(pathToFile))
        {
            return null;
        }

        var inSection = false;

        foreach (var line in File.ReadLines(pathToFile))
        {
            var trimmed = line.Trim();

            if (IsSectionHeader(trimmed))
            {
                inSection = IsSection(trimmed, section);

                continue;
            }

            if (inSection && TryParseKey(trimmed, out var name, out var value) && name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(value.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
            }
        }

        return null;
    }

    /// <summary>
    ///     Sets values in a section of an INI-style configuration file, creating the file, section or keys as needed.
    /// </summary>
    /// <param name="pathToFile">
    ///     The configuration file.
    /// </param>
    /// <param name="section">
    ///     The section name without brackets.
    /// </param>
    /// <param name="values">
    ///     The keys and values to write.
    /// </param>
    public static void SetIniValues(string pathToFile, string section, IReadOnlyDictionary<string, string> values)
    {
        var lines = File.Exists(pathToFile) ? File.ReadAllLines(pathToFile).ToList() : [];
        var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

        var sectionStart = -1;
        var sectionEnd = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            if (!IsSectionHeader(trimmed))
            {
                continue;
            }

            if (sectionStart >= 0)
            {
                sectionEnd = i;

                break;
            }

            if (IsSection(trimmed, section))
            {
                sectionStart = i;
            }
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
            lines.AddRange(pending.Select(static kv => $"{kv.Key} = {kv.Value}"));
        }
        else
        {
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                if (TryParseKey(lines[i].Trim(), out var name, out _) && pending.Remove(name, out var value))
                {
                    lines[i] = $"{name} = {value}";
                }
            }

            lines.InsertRange(sectionStart + 1, pending.Select(static kv => $"{kv.Key} = {kv.Value}"));
        }

        WriteLines(pathToFile, lines);
    }

    /// <summary>
    ///     Sets a console variable in an EDuke32-style cvar file, creating the file or line as needed.
    /// </summary>
    /// <param name="pathToFile">
    ///     The cvar file, for example settings.cfg.
    /// </param>
    /// <param name="name">
    ///     The cvar name.
    /// </param>
    /// <param name="value">
    ///     The value to write.
    /// </param>
    public static void SetCvar(string pathToFile, string name, string value)
    {
        var lines = File.Exists(pathToFile) ? File.ReadAllLines(pathToFile).ToList() : [];
        var newLine = $@"{name} ""{value}""";
        var found = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (trimmed.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > name.Length &&
                char.IsWhiteSpace(trimmed[name.Length]))
            {
                lines[i] = newLine;
                found = true;
            }
        }

        if (!found)
        {
            lines.Add(newLine);
        }

        WriteLines(pathToFile, lines);
    }

    /// <summary>
    ///     Writes lines to a file, creating its folder when needed.
    /// </summary>
    private static void WriteLines(string pathToFile, List<string> lines)
    {
        var folder = Path.GetDirectoryName(pathToFile);

        if (!string.IsNullOrEmpty(folder))
        {
            _ = Directory.CreateDirectory(folder);
        }

        File.WriteAllLines(pathToFile, lines);
    }

    /// <summary>
    ///     Gets a value indicating whether a trimmed line is a section header.
    /// </summary>
    private static bool IsSectionHeader(string trimmed) => trimmed.StartsWith('[') && trimmed.EndsWith(']');

    /// <summary>
    ///     Gets a value indicating whether a trimmed section header names the given section.
    /// </summary>
    private static bool IsSection(string trimmed, string section) => trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Splits a trimmed "Key = Value" line.
    /// </summary>
    private static bool TryParseKey(string trimmed, out string name, out string value)
    {
        var separator = trimmed.IndexOf('=', StringComparison.Ordinal);

        if (separator <= 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
        {
            name = string.Empty;
            value = string.Empty;

            return false;
        }

        name = trimmed[..separator].Trim();
        value = trimmed[(separator + 1)..].Trim();

        return name.Length > 0;
    }

    /// <summary>
    ///     Retrieves a system metric in physical pixels for DPI-aware processes.
    /// </summary>
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetSystemMetrics(int index);
}
