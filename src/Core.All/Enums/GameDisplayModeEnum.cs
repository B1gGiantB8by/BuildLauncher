namespace Core.All.Enums;

/// <summary>
///     Specifies how a source port should present the game window.
/// </summary>
public enum GameDisplayModeEnum : byte
{
    /// <summary>
    ///     Leave the port's own display settings unchanged.
    /// </summary>
    PortDefault,

    /// <summary>
    ///     Exclusive fullscreen at the port's configured resolution.
    /// </summary>
    Fullscreen,

    /// <summary>
    ///     A borderless window covering the primary display at its native resolution.
    /// </summary>
    BorderlessFullscreen,

    /// <summary>
    ///     A regular window with borders.
    /// </summary>
    Windowed
}
