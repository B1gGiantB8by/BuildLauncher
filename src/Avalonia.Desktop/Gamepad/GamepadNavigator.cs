using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Client.Interfaces;

namespace Avalonia.Desktop.Gamepad;

/// <summary>
///     Translates game controller input into focus navigation and activation in the launcher window.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>D-pad or left stick: move focus (lists move their selection first).</item>
///         <item>A: activate; on an addon, jump to its port buttons.</item>
///         <item>B: back or close a menu.</item>
///         <item>X: open the context menu.</item>
///         <item>Start: play the selected addon with the first available port.</item>
///         <item>Back/View: tap to toggle launcher fullscreen, hold to go to the quit page.</item>
///         <item>LB/RB: previous/next game; LT/RT: previous/next page within a game.</item>
///         <item>Right stick: scroll the description or current page.</item>
///     </list>
///     Input is ignored while the window is not active, so a running game never drives the launcher.
/// </remarks>
public sealed class GamepadNavigator : IDisposable
{
    private const short StickThreshold = 16000;

    private const short ScrollDeadzone = 8000;

    private const byte TriggerThreshold = 100;

    private const double ScrollPixelsPerTick = 30;

    private const long RepeatDelayMs = 400;

    private const long RepeatIntervalMs = 110;

    private const long QuitHoldMs = 800;

    private const string MainTabsName = "MainTabs";

    private const string PortsPanelName = "PortsButtonsPanel";

    private const string RightPanelName = "RightPanel";

    private const string DownloadButtonName = "DownloadButton";

    private readonly Window _window;

    private readonly IGamepadReader _reader;

    private readonly IConfigProvider _config;

    private readonly Action _toggleFullscreen;

    private readonly Action _showQuit;

    private readonly DispatcherTimer _timer;

    private NavButtons _previous;

    private NavButtons _repeatButton;

    private long _nextRepeat;

    private long _backPressedAt = -1;

    private bool _backHoldHandled;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GamepadNavigator" /> class and starts polling when enabled.
    /// </summary>
    /// <param name="window">
    ///     The window to navigate.
    /// </param>
    /// <param name="reader">
    ///     The controller reader.
    /// </param>
    /// <param name="config">
    ///     The configuration provider; <see cref="IConfigProvider.UseController" /> turns polling on and off.
    /// </param>
    /// <param name="toggleFullscreen">
    ///     Toggles the launcher between fullscreen and its previous window state.
    /// </param>
    /// <param name="showQuit">
    ///     Shows the quit page; invoked when the Back/View button is held.
    /// </param>
    public GamepadNavigator(Window window, IGamepadReader reader, IConfigProvider config, Action toggleFullscreen, Action showQuit)
    {
        _window = window;
        _reader = reader;
        _config = config;
        _toggleFullscreen = toggleFullscreen;
        _showQuit = showQuit;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, OnTick);

        _config.ParameterChangedEvent += OnConfigParameterChanged;

        if (_config.UseController)
        {
            _timer.Start();
        }
    }

    /// <summary>
    ///     Logical controls derived from the raw controller state.
    /// </summary>
    [Flags]
    private enum NavButtons
    {
        None = 0,
        Up = 1 << 0,
        Down = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        A = 1 << 4,
        B = 1 << 5,
        X = 1 << 6,
        Y = 1 << 7,
        Start = 1 << 8,
        Back = 1 << 9,
        PreviousGame = 1 << 10,
        NextGame = 1 << 11,
        PreviousPage = 1 << 12,
        NextPage = 1 << 13,
        Directions = Up | Down | Left | Right
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        _config.ParameterChangedEvent -= OnConfigParameterChanged;
    }

    /// <summary>
    ///     Applies one controller snapshot. Called by the polling timer; exposed for tests.
    /// </summary>
    /// <param name="state">
    ///     The controller state.
    /// </param>
    /// <param name="nowMs">
    ///     The current time in milliseconds, used for direction auto-repeat.
    /// </param>
    public void ProcessState(GamepadState state, long nowMs)
    {
        var current = ToNavButtons(state);
        var pressed = current & ~_previous;
        _previous = current;

        var pressedDirection = pressed & NavButtons.Directions;

        if (pressedDirection is not NavButtons.None)
        {
            _repeatButton = LowestFlag(pressedDirection);
            _nextRepeat = nowMs + RepeatDelayMs;
            Navigate(_repeatButton);
        }
        else if (_repeatButton is not NavButtons.None && current.HasFlag(_repeatButton))
        {
            if (nowMs >= _nextRepeat)
            {
                _nextRepeat = nowMs + RepeatIntervalMs;
                Navigate(_repeatButton);
            }
        }
        else
        {
            _repeatButton = NavButtons.None;
        }

        if (pressed.HasFlag(NavButtons.A))
        {
            Activate();
        }

        if (pressed.HasFlag(NavButtons.B))
        {
            GoBack();
        }

        if (pressed.HasFlag(NavButtons.X))
        {
            OpenContextMenu();
        }

        if (pressed.HasFlag(NavButtons.Start))
        {
            Play();
        }

        HandleBackButton(current, pressed, nowMs);

        if (pressed.HasFlag(NavButtons.PreviousGame))
        {
            SwitchMainTab(-1);
        }

        if (pressed.HasFlag(NavButtons.NextGame))
        {
            SwitchMainTab(1);
        }

        if (pressed.HasFlag(NavButtons.PreviousPage))
        {
            SwitchInnerTab(-1);
        }

        if (pressed.HasFlag(NavButtons.NextPage))
        {
            SwitchInnerTab(1);
        }

        if (Math.Abs((int)state.RightThumbY) > ScrollDeadzone)
        {
            Scroll(-state.RightThumbY / (double)short.MaxValue * ScrollPixelsPerTick);
        }
    }

    /// <summary>
    ///     Polls the controller while the window is active.
    /// </summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (!_window.IsActive)
        {
            _previous = NavButtons.None;
            _repeatButton = NavButtons.None;

            return;
        }

        ProcessState(_reader.Read(), Environment.TickCount64);
    }

    /// <summary>
    ///     Starts or stops polling when the controller setting changes.
    /// </summary>
    private void OnConfigParameterChanged(string? parameterName)
    {
        if (parameterName != nameof(IConfigProvider.UseController))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_config.UseController)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        });
    }

    /// <summary>
    ///     Converts raw controller input into logical buttons.
    /// </summary>
    private static NavButtons ToNavButtons(GamepadState state)
    {
        var b = state.Buttons;
        var result = NavButtons.None;

        if (b.HasFlag(GamepadButtons.DPadUp) || state.LeftThumbY > StickThreshold)
        {
            result |= NavButtons.Up;
        }

        if (b.HasFlag(GamepadButtons.DPadDown) || state.LeftThumbY < -StickThreshold)
        {
            result |= NavButtons.Down;
        }

        if (b.HasFlag(GamepadButtons.DPadLeft) || state.LeftThumbX < -StickThreshold)
        {
            result |= NavButtons.Left;
        }

        if (b.HasFlag(GamepadButtons.DPadRight) || state.LeftThumbX > StickThreshold)
        {
            result |= NavButtons.Right;
        }

        result |= b.HasFlag(GamepadButtons.A) ? NavButtons.A : NavButtons.None;
        result |= b.HasFlag(GamepadButtons.B) ? NavButtons.B : NavButtons.None;
        result |= b.HasFlag(GamepadButtons.X) ? NavButtons.X : NavButtons.None;
        result |= b.HasFlag(GamepadButtons.Y) ? NavButtons.Y : NavButtons.None;
        result |= b.HasFlag(GamepadButtons.Start) ? NavButtons.Start : NavButtons.None;
        result |= b.HasFlag(GamepadButtons.Back) ? NavButtons.Back : NavButtons.None;
        result |= b.HasFlag(GamepadButtons.LeftShoulder) ? NavButtons.PreviousGame : NavButtons.None;
        result |= b.HasFlag(GamepadButtons.RightShoulder) ? NavButtons.NextGame : NavButtons.None;
        result |= state.LeftTrigger > TriggerThreshold ? NavButtons.PreviousPage : NavButtons.None;
        result |= state.RightTrigger > TriggerThreshold ? NavButtons.NextPage : NavButtons.None;

        return result;
    }

    /// <summary>
    ///     Returns the lowest set flag of a combination.
    /// </summary>
    private static NavButtons LowestFlag(NavButtons value) => (NavButtons)((int)value & -(int)value);

    /// <summary>
    ///     Gets the element that currently has keyboard focus in the window or one of its popups.
    /// </summary>
    private Visual? GetFocused() => _window.FocusManager?.GetFocusedElement() as Visual;

    /// <summary>
    ///     Toggles fullscreen when Back/View is tapped, or shows the quit page when it is held.
    /// </summary>
    private void HandleBackButton(NavButtons current, NavButtons pressed, long nowMs)
    {
        if (pressed.HasFlag(NavButtons.Back))
        {
            _backPressedAt = nowMs;
            _backHoldHandled = false;

            return;
        }

        if (_backPressedAt < 0)
        {
            return;
        }

        if (current.HasFlag(NavButtons.Back))
        {
            if (!_backHoldHandled && nowMs - _backPressedAt >= QuitHoldMs)
            {
                _backHoldHandled = true;
                _showQuit();
            }

            return;
        }

        if (!_backHoldHandled)
        {
            _toggleFullscreen();
        }

        _backPressedAt = -1;
    }

    /// <summary>
    ///     Moves the selection or focus in a direction.
    /// </summary>
    private void Navigate(NavButtons direction)
    {
        var focused = GetFocused();

        if (focused is null || !focused.IsEffectivelyVisible)
        {
            FocusPrimary(GetMainTabs()?.SelectedContent as Visual ?? GetMainTabs());

            return;
        }

        var key = direction switch
        {
            NavButtons.Up => Key.Up,
            NavButtons.Down => Key.Down,
            NavButtons.Left => Key.Left,
            NavButtons.Right => Key.Right,
            _ => Key.None
        };

        //text boxes and closed combo boxes would swallow arrows or change their value, so move focus directly
        var sendKey = focused is not TextBox && focused is not ComboBox { IsDropDownOpen: false };

        if (sendKey && SendKey(focused, key) || GetFocused() != focused)
        {
            return;
        }

        var next = FindNextInDirection(focused, direction);
        next?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    ///     Activates the focused control.
    /// </summary>
    private void Activate()
    {
        var focused = GetFocused();

        if (focused is null)
        {
            FocusPrimary(GetMainTabs()?.SelectedContent as Visual);

            return;
        }

        switch (focused)
        {
            case TabItem tab:
                tab.IsSelected = true;
                FocusPrimary(tab.FindAncestorOfType<TabControl>()?.SelectedContent as Visual ?? tab);

                return;

            case ComboBox combo:
                combo.IsDropDownOpen = !combo.IsDropDownOpen;

                return;

            case ListBoxItem item:
                var checkBox = item.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(static c => c.IsEffectivelyEnabled);

                if (checkBox is not null)
                {
                    _ = SendKey(checkBox, Key.Enter);

                    return;
                }

                if (FocusFirstPortButton(item))
                {
                    return;
                }

                break;
        }

        if (focused.FindAncestorOfType<DataGrid>(true) is { } grid &&
            grid.FindAncestorOfType<UserControl>() is { } downloads &&
            FindNamed<Button>(downloads, DownloadButtonName) is { IsEffectivelyEnabled: true } downloadButton)
        {
            _ = downloadButton.Focus(NavigationMethod.Directional);

            return;
        }

        if (focused is Button flyoutButton && GetMenuFlyout(flyoutButton) is not null)
        {
            _ = SendKey(flyoutButton, Key.Enter);
            FocusFlyout(flyoutButton);

            return;
        }

        if (!SendKey(focused, Key.Enter))
        {
            _ = SendKey(focused, Key.Space);
            SendKeyUp(focused, Key.Space);
        }
    }

    /// <summary>
    ///     Closes the open menu, or moves focus back towards the addon list or game tabs.
    /// </summary>
    private void GoBack()
    {
        var focused = GetFocused();

        if (focused is null)
        {
            return;
        }

        if (SendKey(focused, Key.Escape) || GetFocused() != focused)
        {
            return;
        }

        var list = focused.FindAncestorOfType<AddonListControlBase>();

        if (list is not null && focused.FindAncestorOfType<ListBoxItem>(true) is null)
        {
            FocusPrimary(list);

            return;
        }

        if (GetMainTabs()?.SelectedItem is TabItem tab)
        {
            _ = tab.Focus(NavigationMethod.Directional);
        }
    }

    /// <summary>
    ///     Starts the selected addon with the first enabled port button.
    /// </summary>
    private void Play()
    {
        var context = GetFocused()?.FindAncestorOfType<AddonListControlBase>(true)
                      ?? GetMainTabs()?.GetVisualDescendants().OfType<AddonListControlBase>().FirstOrDefault(static c => c.IsEffectivelyVisible);

        var button = context is null ? null : GetPortButtons(context).FirstOrDefault();

        if (button is null)
        {
            return;
        }

        _ = button.Focus(NavigationMethod.Directional);
        _ = SendKey(button, Key.Enter);
        FocusFlyout(button);
    }

    /// <summary>
    ///     Moves focus to the first enabled item of a button's open menu flyout, so the d-pad navigates the menu.
    /// </summary>
    private static void FocusFlyout(Button button)
    {
        if (GetMenuFlyout(button) is not { } flyout)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (flyout.IsOpen && flyout.Items.OfType<MenuItem>().FirstOrDefault(static i => i.IsEffectivelyEnabled) is { } first)
                {
                    _ = first.Focus(NavigationMethod.Directional);
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     Gets the menu flyout a button opens, either its own or one attached for programmatic display.
    /// </summary>
    private static MenuFlyout? GetMenuFlyout(Button button) => (button.Flyout ?? FlyoutBase.GetAttachedFlyout(button)) as MenuFlyout;

    /// <summary>
    ///     Opens the context menu of the focused element or its nearest ancestor that has one.
    /// </summary>
    private void OpenContextMenu()
    {
        var focused = GetFocused() as Control;
        var owner = focused;

        while (owner is not null && owner.ContextMenu is null)
        {
            owner = owner.GetVisualParent() as Control;
        }

        if (owner?.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Open(owner);

        Dispatcher.UIThread.Post(
            () =>
            {
                if (menu.IsOpen && menu.ContainerFromIndex(0) is Control first)
                {
                    _ = first.Focus(NavigationMethod.Directional);
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     Selects the previous or next enabled game or page in the main tab strip.
    /// </summary>
    private void SwitchMainTab(int step)
    {
        var tabs = GetMainTabs();

        if (tabs is not null && SwitchTab(tabs, step))
        {
            Dispatcher.UIThread.Post(() => FocusPrimary(tabs.SelectedContent as Visual ?? tabs), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    ///     Selects the previous or next page (Campaigns, Maps, Mods, Downloads) of the current game.
    /// </summary>
    private void SwitchInnerTab(int step)
    {
        var mainTabs = GetMainTabs();
        var inner = mainTabs?.GetVisualDescendants().OfType<TabControl>().FirstOrDefault(t => t != mainTabs && t.IsEffectivelyVisible);

        if (inner is not null && SwitchTab(inner, step))
        {
            Dispatcher.UIThread.Post(() => FocusPrimary(inner.SelectedContent as Visual ?? inner), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    ///     Selects the next selectable tab in a direction, wrapping around.
    /// </summary>
    private static bool SwitchTab(TabControl tabs, int step)
    {
        var count = tabs.ItemCount;

        if (count == 0)
        {
            return false;
        }

        var index = tabs.SelectedIndex;

        for (var i = 0; i < count; i++)
        {
            index = (index + step + count) % count;

            if (tabs.ContainerFromIndex(index) is TabItem { IsEnabled: true, IsVisible: true })
            {
                tabs.SelectedIndex = index;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Scrolls the description panel next to the addon list, or the scroll viewer around the focused element.
    /// </summary>
    private void Scroll(double delta)
    {
        var focused = GetFocused();
        ScrollViewer? viewer = null;

        if (focused?.FindAncestorOfType<AddonListControlBase>(true) is { } list &&
            FindNamed<Control>(list, RightPanelName) is { } rightPanel)
        {
            viewer = rightPanel.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(static s => s.IsEffectivelyVisible);
        }

        viewer ??= focused?.FindAncestorOfType<ScrollViewer>(true)
                   ?? (GetMainTabs()?.SelectedContent as Visual)?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(static s => s.IsEffectivelyVisible);

        if (viewer is null)
        {
            return;
        }

        var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        viewer.Offset = new Vector(viewer.Offset.X, Math.Clamp(viewer.Offset.Y + delta, 0, maxY));
    }

    /// <summary>
    ///     Focuses the selected (or first) item of the first visible list in a container, or its first focusable element.
    /// </summary>
    private void FocusPrimary(Visual? container)
    {
        if (container is null)
        {
            return;
        }

        var descendants = container.GetVisualDescendants().Where(static v => v.IsEffectivelyVisible).ToList();

        if (descendants.OfType<ListBox>().FirstOrDefault(static l => l.ItemCount > 0) is { } listBox)
        {
            if (listBox.SelectedIndex < 0)
            {
                listBox.SelectedIndex = 0;
            }

            var index = listBox.SelectedIndex;

            if (listBox.ContainerFromIndex(index) is Control item)
            {
                _ = item.Focus(NavigationMethod.Directional);

                return;
            }

            listBox.ScrollIntoView(index);

            Dispatcher.UIThread.Post(
                () => (listBox.ContainerFromIndex(index) as Control)?.Focus(NavigationMethod.Directional),
                DispatcherPriority.Loaded);

            return;
        }

        if (descendants.OfType<DataGrid>().FirstOrDefault() is { } grid)
        {
            _ = grid.Focus(NavigationMethod.Directional);

            return;
        }

        var first = descendants.OfType<InputElement>().FirstOrDefault(IsFocusCandidate);
        first?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    ///     Moves focus from an addon list item to the first enabled port button of the same list.
    /// </summary>
    private static bool FocusFirstPortButton(Visual item)
    {
        var list = item.FindAncestorOfType<AddonListControlBase>();
        var button = list is null ? null : GetPortButtons(list).FirstOrDefault();

        return button?.Focus(NavigationMethod.Directional) is true;
    }

    /// <summary>
    ///     Gets the enabled port buttons of an addon list control.
    /// </summary>
    private static IEnumerable<Button> GetPortButtons(AddonListControlBase list) =>
        FindNamed<Panel>(list, PortsPanelName)?.Children.OfType<Button>().Where(static b => b.IsEffectivelyEnabled && b.IsEffectivelyVisible) ?? [];

    /// <summary>
    ///     Finds a named descendant control.
    /// </summary>
    private static T? FindNamed<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>
    ///     Gets the main window's game tab control.
    /// </summary>
    private TabControl? GetMainTabs() => FindNamed<TabControl>(_window, MainTabsName);

    /// <summary>
    ///     Raises a key-down event on an element.
    /// </summary>
    /// <returns>
    ///     <see langword="true" /> when the element handled the key.
    /// </returns>
    private static bool SendKey(Visual target, Key key)
    {
        if (target is not Interactive interactive)
        {
            return false;
        }

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            Source = interactive,
            KeyDeviceType = KeyDeviceType.Gamepad
        };

        interactive.RaiseEvent(args);

        return args.Handled;
    }

    /// <summary>
    ///     Raises a key-up event on an element.
    /// </summary>
    private static void SendKeyUp(Visual target, Key key)
    {
        if (target is Interactive interactive)
        {
            interactive.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyUpEvent,
                Key = key,
                Source = interactive,
                KeyDeviceType = KeyDeviceType.Gamepad
            });
        }
    }

    /// <summary>
    ///     Finds the nearest focusable element on screen in a direction from the given element.
    /// </summary>
    private static InputElement? FindNextInDirection(Visual from, NavButtons direction)
    {
        //popups such as menus and flyouts are separate top levels, so search within the one that has focus
        if (TopLevel.GetTopLevel(from) is not Visual root)
        {
            return null;
        }

        var origin = GetBoundsIn(from, root);

        if (origin is null)
        {
            return null;
        }

        var current = origin.Value;
        var rootBounds = new Rect(root.Bounds.Size);
        InputElement? best = null;
        var bestScore = double.MaxValue;

        foreach (var candidate in root.GetVisualDescendants().OfType<InputElement>())
        {
            if (candidate == from || !IsFocusCandidate(candidate) || candidate.IsVisualAncestorOf(from) || from.IsVisualAncestorOf(candidate))
            {
                continue;
            }

            if (GetBoundsIn(candidate, root) is not { } bounds || !bounds.Intersects(rootBounds))
            {
                continue;
            }

            var score = Score(current, bounds, direction);

            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    ///     Scores how well a candidate rectangle continues movement in a direction; lower is better.
    /// </summary>
    private static double Score(Rect from, Rect to, NavButtons direction)
    {
        double primary;
        double gapStart;
        double gapEnd;
        double candidateStart;
        double candidateEnd;

        switch (direction)
        {
            case NavButtons.Up:
                if (to.Center.Y >= from.Center.Y || to.Top >= from.Top)
                {
                    return double.MaxValue;
                }

                primary = Math.Max(0, from.Top - to.Bottom);
                (gapStart, gapEnd, candidateStart, candidateEnd) = (from.Left, from.Right, to.Left, to.Right);

                break;

            case NavButtons.Down:
                if (to.Center.Y <= from.Center.Y || to.Bottom <= from.Bottom)
                {
                    return double.MaxValue;
                }

                primary = Math.Max(0, to.Top - from.Bottom);
                (gapStart, gapEnd, candidateStart, candidateEnd) = (from.Left, from.Right, to.Left, to.Right);

                break;

            case NavButtons.Left:
                if (to.Center.X >= from.Center.X || to.Left >= from.Left)
                {
                    return double.MaxValue;
                }

                primary = Math.Max(0, from.Left - to.Right);
                (gapStart, gapEnd, candidateStart, candidateEnd) = (from.Top, from.Bottom, to.Top, to.Bottom);

                break;

            case NavButtons.Right:
                if (to.Center.X <= from.Center.X || to.Right <= from.Right)
                {
                    return double.MaxValue;
                }

                primary = Math.Max(0, to.Left - from.Right);
                (gapStart, gapEnd, candidateStart, candidateEnd) = (from.Top, from.Bottom, to.Top, to.Bottom);

                break;

            default:
                return double.MaxValue;
        }

        var secondary = candidateEnd < gapStart ? gapStart - candidateEnd
            : candidateStart > gapEnd ? candidateStart - gapEnd
            : 0;

        return primary + (secondary * 3);
    }

    /// <summary>
    ///     Gets the bounds of an element in the coordinate space of its visual root.
    /// </summary>
    private static Rect? GetBoundsIn(Visual visual, Visual root)
    {
        var topLeft = visual.TranslatePoint(default, root);

        return topLeft is null ? null : new Rect(topLeft.Value, visual.Bounds.Size);
    }

    /// <summary>
    ///     Gets a value indicating whether an element can receive directional focus.
    /// </summary>
    private static bool IsFocusCandidate(InputElement element) =>
        element.Focusable &&
        element.IsEffectivelyEnabled &&
        element.IsEffectivelyVisible &&
        element.Bounds.Width > 0 &&
        element.Bounds.Height > 0 &&
        element is not ScrollViewer and not ContentPresenter and not ItemsPresenter &&
        (element is not ListBox listBox || listBox.ItemCount == 0) &&
        element is not TemplatedControl { TemplatedParent: TextBox or ComboBox };
}
