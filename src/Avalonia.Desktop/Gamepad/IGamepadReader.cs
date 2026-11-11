namespace Avalonia.Desktop.Gamepad;

/// <summary>
///     Reads the combined state of the connected game controllers.
/// </summary>
public interface IGamepadReader
{
    /// <summary>
    ///     Reads the current controller state.
    /// </summary>
    /// <returns>
    ///     The merged state of all connected controllers, or a default state when none is connected.
    /// </returns>
    GamepadState Read();
}
