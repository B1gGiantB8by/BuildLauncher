namespace Avalonia.Desktop.Gamepad;

/// <summary>
///     A snapshot of a game controller's buttons, triggers and sticks.
/// </summary>
/// <param name="Buttons">
///     The pressed digital buttons.
/// </param>
/// <param name="LeftTrigger">
///     The left trigger position, 0 to 255.
/// </param>
/// <param name="RightTrigger">
///     The right trigger position, 0 to 255.
/// </param>
/// <param name="LeftThumbX">
///     The left stick X axis; negative is left.
/// </param>
/// <param name="LeftThumbY">
///     The left stick Y axis; positive is up.
/// </param>
/// <param name="RightThumbX">
///     The right stick X axis; negative is left.
/// </param>
/// <param name="RightThumbY">
///     The right stick Y axis; positive is up.
/// </param>
public readonly record struct GamepadState(
    GamepadButtons Buttons,
    byte LeftTrigger = 0,
    byte RightTrigger = 0,
    short LeftThumbX = 0,
    short LeftThumbY = 0,
    short RightThumbX = 0,
    short RightThumbY = 0
    );
