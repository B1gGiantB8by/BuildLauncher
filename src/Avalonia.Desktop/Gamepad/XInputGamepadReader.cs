using System.Runtime.InteropServices;

namespace Avalonia.Desktop.Gamepad;

/// <summary>
///     Reads Xbox-compatible controllers through the XInput library shipped with Windows.
/// </summary>
/// <remarks>
///     PlayStation and other controllers are supported when Steam Input or a similar driver exposes them as
///     XInput devices. On other platforms the reader reports no controller.
/// </remarks>
public sealed partial class XInputGamepadReader : IGamepadReader
{
    private const uint ErrorSuccess = 0;

    private const int MaxControllers = 4;

    private const long ReconnectIntervalMs = 2000;

    private readonly bool[] _isConnected = new bool[MaxControllers];

    private readonly long[] _nextProbe = new long[MaxControllers];

    private bool _isAvailable = OperatingSystem.IsWindows();

    /// <inheritdoc />
    public GamepadState Read()
    {
        if (!_isAvailable)
        {
            return default;
        }

        var now = Environment.TickCount64;
        var buttons = GamepadButtons.None;
        byte leftTrigger = 0;
        byte rightTrigger = 0;
        short lx = 0;
        short ly = 0;
        short rx = 0;
        short ry = 0;

        for (var i = 0; i < MaxControllers; i++)
        {
            //probing a disconnected slot is slow, so empty slots are only rechecked periodically
            if (!_isConnected[i] && now < _nextProbe[i])
            {
                continue;
            }

            uint result;
            XInputState state;

            try
            {
                result = XInputGetState((uint)i, out state);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _isAvailable = false;

                return default;
            }

            if (result != ErrorSuccess)
            {
                _isConnected[i] = false;
                _nextProbe[i] = now + ReconnectIntervalMs;

                continue;
            }

            _isConnected[i] = true;

            var pad = state.Gamepad;
            buttons |= (GamepadButtons)pad.Buttons;
            leftTrigger = Math.Max(leftTrigger, pad.LeftTrigger);
            rightTrigger = Math.Max(rightTrigger, pad.RightTrigger);
            lx = Stronger(lx, pad.ThumbLX);
            ly = Stronger(ly, pad.ThumbLY);
            rx = Stronger(rx, pad.ThumbRX);
            ry = Stronger(ry, pad.ThumbRY);
        }

        return new(buttons, leftTrigger, rightTrigger, lx, ly, rx, ry);
    }

    /// <summary>
    ///     Returns the axis value with the larger deflection.
    /// </summary>
    private static short Stronger(short current, short candidate) => Math.Abs((int)candidate) > Math.Abs((int)current) ? candidate : current;

    /// <summary>
    ///     Retrieves the current state of an XInput controller.
    /// </summary>
    [LibraryImport("xinput1_4.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint XInputGetState(uint userIndex, out XInputState state);

    /// <summary>
    ///     Native XINPUT_GAMEPAD structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        /// <summary>Pressed buttons.</summary>
        public ushort Buttons;

        /// <summary>Left trigger.</summary>
        public byte LeftTrigger;

        /// <summary>Right trigger.</summary>
        public byte RightTrigger;

        /// <summary>Left stick X.</summary>
        public short ThumbLX;

        /// <summary>Left stick Y.</summary>
        public short ThumbLY;

        /// <summary>Right stick X.</summary>
        public short ThumbRX;

        /// <summary>Right stick Y.</summary>
        public short ThumbRY;
    }

    /// <summary>
    ///     Native XINPUT_STATE structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        /// <summary>State packet number.</summary>
        public uint PacketNumber;

        /// <summary>Controller state.</summary>
        public XInputGamepad Gamepad;
    }
}
