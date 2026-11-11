using Avalonia.Controls;
using Avalonia.Desktop.Gamepad;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Core.Client.Config;
using Avalonia.Themes.Fluent;
using Tests.Unit.Sequential.Helpers;

namespace Tests.Unit.Sequential;

/// <summary>
///     Headless tests for <see cref="GamepadNavigator" />.
/// </summary>
public sealed class GamepadNavigatorTests
{
    private static readonly GamepadState _released = new(GamepadButtons.None);

    /// <summary>
    ///     The d-pad moves focus to the nearest control in that direction when the focused control does not use arrows.
    /// </summary>
    [AvaloniaFact]
    public void DPad_MovesFocusSpatially()
    {
        HeadlessView.Prepare();

        var left = new Button { Content = "Left" };
        var right = new Button { Content = "Right" };
        var below = new Button { Content = "Below" };
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto"), RowDefinitions = new("Auto,Auto") };
        Grid.SetColumn(right, 1);
        Grid.SetRow(below, 1);
        grid.Children.AddRange([left, right, below]);

        using var view = new ThemedWindow(grid);
        using var navigator = CreateNavigator(view.Window);

        Assert.True(left.Focus());

        Press(navigator, new GamepadState(GamepadButtons.DPadRight), 0);
        Assert.True(right.IsFocused);

        Press(navigator, new GamepadState(GamepadButtons.DPadLeft), 100);
        Assert.True(left.IsFocused);

        Press(navigator, new GamepadState(GamepadButtons.None, LeftThumbY: short.MinValue), 200);
        Assert.True(below.IsFocused);
    }

    /// <summary>
    ///     Directions are sent to lists first, so the d-pad moves the list selection.
    /// </summary>
    [AvaloniaFact]
    public void DPad_MovesListSelection()
    {
        HeadlessView.Prepare();

        var list = new ListBox { ItemsSource = new[] { "One", "Two", "Three" }, SelectedIndex = 0 };

        using var view = new ThemedWindow(list);
        using var navigator = CreateNavigator(view.Window);

        view.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var first = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
        Assert.True(first.Focus(NavigationMethod.Directional));

        Press(navigator, new GamepadState(GamepadButtons.DPadDown), 0);
        Press(navigator, new GamepadState(GamepadButtons.DPadDown), 100);

        Assert.Equal(2, list.SelectedIndex);
    }

    /// <summary>
    ///     A activates the focused button.
    /// </summary>
    [AvaloniaFact]
    public void A_ClicksFocusedButton()
    {
        HeadlessView.Prepare();

        var clicks = 0;
        var button = new Button { Content = "Go" };
        button.Click += (_, _) => clicks++;

        using var view = new ThemedWindow(button);
        using var navigator = CreateNavigator(view.Window);

        Assert.True(button.Focus());
        Press(navigator, new GamepadState(GamepadButtons.A), 0);

        Assert.Equal(1, clicks);
    }

    /// <summary>
    ///     Holding a direction repeats it only after the initial delay.
    /// </summary>
    [AvaloniaFact]
    public void HeldDirection_RepeatsAfterDelay()
    {
        HeadlessView.Prepare();

        var buttons = Enumerable.Range(0, 4).Select(static i => new Button { Content = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.AddRange(buttons);

        using var view = new ThemedWindow(panel);
        using var navigator = CreateNavigator(view.Window);

        Assert.True(buttons[0].Focus());

        var held = new GamepadState(GamepadButtons.DPadRight);
        navigator.ProcessState(held, 0);
        navigator.ProcessState(held, 200);
        Assert.True(buttons[1].IsFocused);

        navigator.ProcessState(held, 450);
        Assert.True(buttons[2].IsFocused);

        navigator.ProcessState(held, 500);
        Assert.True(buttons[2].IsFocused);

        navigator.ProcessState(held, 570);
        Assert.True(buttons[3].IsFocused);
    }

    /// <summary>
    ///     The bumpers switch between enabled main tabs, skipping disabled games.
    /// </summary>
    [AvaloniaFact]
    public void Bumpers_SwitchMainTabsSkippingDisabled()
    {
        HeadlessView.Prepare();

        var tabs = new TabControl
        {
            Name = "MainTabs",
            Items =
            {
                new TabItem { Header = "Duke", Content = new Button { Content = "D" } },
                new TabItem { Header = "Blood", IsEnabled = false, Content = new Button { Content = "B" } },
                new TabItem { Header = "Wang", Content = new Button { Content = "W" } }
            }
        };

        using var view = new ThemedWindow(tabs);
        using var navigator = CreateNavigator(view.Window);

        tabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Press(navigator, new GamepadState(GamepadButtons.RightShoulder), 0);
        Assert.Equal(2, tabs.SelectedIndex);

        Press(navigator, new GamepadState(GamepadButtons.LeftShoulder), 100);
        Assert.Equal(0, tabs.SelectedIndex);
    }

    /// <summary>
    ///     The View button invokes the fullscreen toggle.
    /// </summary>
    [AvaloniaFact]
    public void BackButton_TogglesFullscreen()
    {
        HeadlessView.Prepare();

        var toggles = 0;

        using var view = new ThemedWindow(new Button { Content = "x" });
        using var navigator = new GamepadNavigator(view.Window, new NullReader(), new ConfigProviderFake(), () => toggles++, static () => { });

        Press(navigator, new GamepadState(GamepadButtons.Back), 0);

        Assert.Equal(1, toggles);
    }

    /// <summary>
    ///     Holding the View button opens the quit page instead of toggling fullscreen.
    /// </summary>
    [AvaloniaFact]
    public void HeldBackButton_ShowsQuitInsteadOfFullscreen()
    {
        HeadlessView.Prepare();

        var toggles = 0;
        var quits = 0;

        using var view = new ThemedWindow(new Button { Content = "x" });
        using var navigator = new GamepadNavigator(view.Window, new NullReader(), new ConfigProviderFake(), () => toggles++, () => quits++);

        var held = new GamepadState(GamepadButtons.Back);
        navigator.ProcessState(held, 0);
        navigator.ProcessState(held, 500);
        Assert.Equal(0, quits);

        navigator.ProcessState(held, 900);
        navigator.ProcessState(held, 1500);
        navigator.ProcessState(_released, 1600);

        Assert.Equal(1, quits);
        Assert.Equal(0, toggles);
    }

    /// <summary>
    ///     A on a button with a menu opens it and moves focus into it, so the d-pad and A can pick an item.
    /// </summary>
    [AvaloniaFact]
    public void A_OnMenuButton_FocusesMenuAndSelectsItem()
    {
        HeadlessView.Prepare();

        var chosen = string.Empty;
        var first = new MenuItem { Header = "First", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => chosen = "First") };
        var second = new MenuItem { Header = "Second", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => chosen = "Second") };
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "Disabled", IsEnabled = false }, first, second } };
        var button = new Button { Content = "Menu", Flyout = flyout };

        using var view = new ThemedWindow(button);
        using var navigator = CreateNavigator(view.Window);

        Assert.True(button.Focus());
        Press(navigator, new GamepadState(GamepadButtons.A), 0);

        Assert.True(flyout.IsOpen);
        Assert.True(first.IsFocused);

        Press(navigator, new GamepadState(GamepadButtons.DPadDown), 100);
        Assert.True(second.IsFocused);

        Press(navigator, new GamepadState(GamepadButtons.A), 200);
        Assert.Equal("Second", chosen);
    }

    /// <summary>
    ///     Applies a state followed by a release, as a quick button tap.
    /// </summary>
    private static void Press(GamepadNavigator navigator, GamepadState state, long nowMs)
    {
        navigator.ProcessState(state, nowMs);
        navigator.ProcessState(_released, nowMs + 16);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    ///     Creates a navigator whose polling timer stays off, so tests drive it explicitly.
    /// </summary>
    private static GamepadNavigator CreateNavigator(Window window) => new(window, new NullReader(), new ConfigProviderFake(), static () => { }, static () => { });

    /// <summary>
    ///     A headless window that applies the Fluent theme before its content is attached, so list and tab items are realized.
    /// </summary>
    private sealed class ThemedWindow : IDisposable
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="ThemedWindow" /> class and shows it.
        /// </summary>
        /// <param name="content">The window content.</param>
        public ThemedWindow(Control content)
        {
            Window = new Window { Width = 1200, Height = 900 };
            Window.Styles.Add(new FluentTheme());
            Window.Content = content;
            Window.Show();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        ///     Gets the hosted window.
        /// </summary>
        public Window Window { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    ///     A reader that never reports input.
    /// </summary>
    private sealed class NullReader : IGamepadReader
    {
        /// <inheritdoc />
        public GamepadState Read() => default;
    }
}
