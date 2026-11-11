namespace Avalonia.Desktop.Gamepad;

/// <summary>
///     Digital game controller buttons, using the XInput bit layout.
/// </summary>
[Flags]
public enum GamepadButtons : ushort
{
    /// <summary>
    ///     No button is pressed.
    /// </summary>
    None = 0,

    /// <summary>
    ///     D-pad up.
    /// </summary>
    DPadUp = 0x0001,

    /// <summary>
    ///     D-pad down.
    /// </summary>
    DPadDown = 0x0002,

    /// <summary>
    ///     D-pad left.
    /// </summary>
    DPadLeft = 0x0004,

    /// <summary>
    ///     D-pad right.
    /// </summary>
    DPadRight = 0x0008,

    /// <summary>
    ///     Start or Menu button.
    /// </summary>
    Start = 0x0010,

    /// <summary>
    ///     Back or View button.
    /// </summary>
    Back = 0x0020,

    /// <summary>
    ///     Left stick click.
    /// </summary>
    LeftThumb = 0x0040,

    /// <summary>
    ///     Right stick click.
    /// </summary>
    RightThumb = 0x0080,

    /// <summary>
    ///     Left bumper.
    /// </summary>
    LeftShoulder = 0x0100,

    /// <summary>
    ///     Right bumper.
    /// </summary>
    RightShoulder = 0x0200,

    /// <summary>
    ///     Bottom face button (A on Xbox, Cross on PlayStation).
    /// </summary>
    A = 0x1000,

    /// <summary>
    ///     Right face button (B on Xbox, Circle on PlayStation).
    /// </summary>
    B = 0x2000,

    /// <summary>
    ///     Left face button (X on Xbox, Square on PlayStation).
    /// </summary>
    X = 0x4000,

    /// <summary>
    ///     Top face button (Y on Xbox, Triangle on PlayStation).
    /// </summary>
    Y = 0x8000
}
