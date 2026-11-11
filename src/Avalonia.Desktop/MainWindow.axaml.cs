using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Controls;
using Avalonia.Desktop.Gamepad;
using Avalonia.Desktop.Helpers;
using Avalonia.Input;
using Avalonia.Desktop.ViewModels;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Core.Client.Interfaces;
using Games.Providers;

namespace Avalonia.Desktop;

/// <summary>
///     Represents the main application window.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    /// <summary> The persisted application settings. </summary>
    private readonly IConfigProvider _config;

    /// <summary> Records best-effort settings persistence failures. </summary>
    private readonly ILogger<MainWindow> _logger = NullLogger<MainWindow>.Instance;

    /// <summary> The installed games used for the default tab selection. </summary>
    private readonly InstalledGamesProvider _installedGamesProvider;

    /// <summary> The last normal bounds, retained while maximized or minimized. </summary>
    private WindowPlacement? _normalPlacement;

    /// <summary> Whether startup restoration has completed. </summary>
    private bool _isOpened;

    /// <summary> The last non-minimized window state. </summary>
    private bool _wasMaximized;

    /// <summary>
    ///     Controller navigation for this window, or <see langword="null" /> in design mode.
    /// </summary>
    private GamepadNavigator? _gamepadNavigator;

    /// <summary>
    ///     The window state to restore when leaving fullscreen.
    /// </summary>
    private WindowState _stateBeforeFullscreen = WindowState.Maximized;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MainWindow" /> class.
    /// </summary>
    public MainWindow()
    {
        _installedGamesProvider = null!;
        _config = null!;

        InitializeComponent();
        ApplyAccent();
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="MainWindow" /> class.
    /// </summary>
    /// <param name="installedGamesProvider">The installed games provider.</param>
    /// <param name="config">The configuration provider.</param>
    /// <param name="logger">Records settings persistence failures.</param>
    public MainWindow(
        InstalledGamesProvider installedGamesProvider,
        IConfigProvider config,
        ILogger<MainWindow>? logger = null
        )
    {
        ArgumentNullException.ThrowIfNull(installedGamesProvider);
        ArgumentNullException.ThrowIfNull(config);

        _installedGamesProvider = installedGamesProvider;
        _config = config;
        _logger = logger ?? NullLogger<MainWindow>.Instance;

        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

        InitializeComponent();
        ApplyAccent();
        ActualThemeVariantChanged += OnActualThemeVariantChanged;

        RestoreWindowPlacement();
        PositionChanged += OnPositionChanged;
        EnableSystemBackdrop();

        if (_config.LauncherFullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }

        _gamepadNavigator = new GamepadNavigator(this, new XInputGamepadReader(), _config, ToggleFullscreen, ShowQuitPage);
    }

    /// <summary>
    ///     Selects the Quit page and focuses its Quit button.
    /// </summary>
    public void ShowQuitPage()
    {
        MainTabs.SelectedItem = QuitTab;

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => QuitButton.Focus(NavigationMethod.Directional),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     Toggles fullscreen from the top-bar button.
    /// </summary>
    private void OnFullscreenButtonClick(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    /// <summary>
    ///     Re-tints the interface when the main tab changes, ignoring selection events bubbling from inner lists.
    /// </summary>
    private void OnMainTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, MainTabs))
        {
            ApplyAccent();
        }
    }

    /// <summary>
    ///     Re-tints the interface for the new light or dark variant.
    /// </summary>
    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => ApplyAccent();

    /// <summary>
    ///     Gets the accent color for a main tab.
    /// </summary>
    /// <param name="tabName">The tab name.</param>
    /// <returns>The accent color for that game, or the default ember for other pages.</returns>
    private static Color GetAccentColor(string? tabName) => tabName switch
    {
        nameof(DukeTab) => Color.FromRgb(0xF2, 0xB2, 0x2B),
        nameof(BloodTab) => Color.FromRgb(0xE2, 0x3A, 0x2C),
        nameof(WangTab) => Color.FromRgb(0x5F, 0xC9, 0x9E),
        nameof(FuryTab) => Color.FromRgb(0xE8, 0x5C, 0xC4),
        nameof(RedneckTab) => Color.FromRgb(0xD9, 0x82, 0x3F),
        nameof(SlaveTab) => Color.FromRgb(0x4F, 0xC3, 0xD6),
        nameof(NamTab) => Color.FromRgb(0xA3, 0xB5, 0x5A),
        nameof(WW2GITab) => Color.FromRgb(0xC8, 0xB4, 0x7A),
        nameof(WitchavenTab) => Color.FromRgb(0xA9, 0x82, 0xF0),
        nameof(TekWarTab) => Color.FromRgb(0x4A, 0xA8, 0xFF),
        _ => Color.FromRgb(0xFF, 0x7A, 0x2A)
    };

    /// <summary>
    ///     Mixes a color toward another.
    /// </summary>
    /// <param name="from">The starting color.</param>
    /// <param name="to">The color to mix toward.</param>
    /// <param name="amount">The mix amount, from 0 to 1.</param>
    /// <returns>The mixed opaque color.</returns>
    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * amount)),
        (byte)(from.G + ((to.G - from.G) * amount)),
        (byte)(from.B + ((to.B - from.B) * amount))
        );

    /// <summary>
    ///     Applies the selected tab's accent to the window's resources.
    /// </summary>
    private void ApplyAccent()
    {
        var isLight = ActualThemeVariant == ThemeVariant.Light;
        var baseAccent = GetAccentColor((MainTabs.SelectedItem as TabItem)?.Name);
        var accent = isLight ? Mix(baseAccent, Colors.Black, 0.38) : baseAccent;

        Resources["SystemAccentColor"] = accent;
        Resources["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.2);
        Resources["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.4);
        Resources["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.6);
        Resources["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.2);
        Resources["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.4);
        Resources["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.6);

        var focus = isLight ? Mix(accent, Colors.Black, 0.25) : Mix(accent, Colors.White, 0.45);

        Resources["LauncherAccentBrush"] = new SolidColorBrush(accent);
        Resources["LauncherAccentInkBrush"] = new SolidColorBrush(isLight ? Colors.White : Color.FromRgb(0x12, 0x0C, 0x07));
        Resources["LauncherAccentLineBrush"] = new SolidColorBrush(Color.FromArgb(0x80, accent.R, accent.G, accent.B));
        Resources["LauncherSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0x48, accent.R, accent.G, accent.B));
        Resources["LauncherFocusBrush"] = new SolidColorBrush(focus);
        Resources["LauncherFocusShadow"] = new BoxShadows(new BoxShadow { Blur = 14, Spread = 2, Color = Color.FromArgb(0x90, focus.R, focus.G, focus.B) });

        var glow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.35, 0.8, RelativeUnit.Relative)
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, accent.R, accent.G, accent.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1));
        Resources["LauncherGlowBrush"] = glow;
    }

    /// <summary>
    ///     Closes the launcher.
    /// </summary>
    private void OnQuitButtonClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    ///     Switches the launcher between borderless fullscreen and its previous window state, and remembers the choice.
    /// </summary>
    public void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullscreen == WindowState.FullScreen ? WindowState.Maximized : _stateBeforeFullscreen;
        }
        else
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }

        try
        {
            _config.LauncherFullscreen = WindowState == WindowState.FullScreen;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the fullscreen setting.");
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!e.Handled && e.Key == Key.F11 && !Design.IsDesignMode)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gamepadNavigator?.Dispose();
        _gamepadNavigator = null;
        _config.ParameterChangedEvent -= OnConfigParameterChanged;
        PropertyChanged -= OnWindowPropertyChanged;
        PositionChanged -= OnPositionChanged;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }

    /// <summary>
    ///     Handles the window opened event.
    /// </summary>
    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        UpdateBackdropState();

        if (!_config.IsConsented)
        {
            ConsentWindow!.IsVisible = true;
        }

        MainTabs.SelectedItem = SettingsTab;

        if (_installedGamesProvider.IsDukeInstalled)
        {
            DukeTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsBloodInstalled)
        {
            BloodTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsWangInstalled)
        {
            WangTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsFuryInstalled)
        {
            FuryTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsRedneckInstalled)
        {
            RedneckTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsSlaveInstalled)
        {
            SlaveTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsNamInstalled)
        {
            NamTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsWW2GIInstalled)
        {
            WW2GITab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsWitchavenInstalled)
        {
            WitchavenTab!.IsSelected = true;
        }
        else if (_installedGamesProvider.IsTekWarInstalled)
        {
            TekWarTab!.IsSelected = true;
        }

        var savedTabName = _config.ActiveTab;
        var savedTab = MainTabs.Items.OfType<TabItem>().FirstOrDefault(tab =>
            tab.Name == savedTabName && tab.IsEnabled && tab.IsVisible);

        if (savedTab is not null)
        {
            MainTabs.SelectedItem = savedTab;
        }

        _isOpened = true;
        CaptureWindowPlacement();
    }

    /// <summary> Restores bounds before showing the window and fits them to the current monitor layout. </summary>
    private void RestoreWindowPlacement()
    {
        var saved = WindowPlacement.Parse(_config.WindowPlacement);
        var placement = saved ?? new WindowPlacement(Position, new Size(1300, 800), true);
        var fitted = placement.FitToScreens(Screens.All.Select(screen => (screen.WorkingArea, screen.Scaling)).ToArray());

        MinWidth = Math.Min(1300, fitted.Size.Width);
        MinHeight = Math.Min(800, fitted.Size.Height);
        Width = fitted.Size.Width;
        Height = fitted.Size.Height;
        _normalPlacement = fitted;
        _wasMaximized = fitted.IsMaximized;

        if (saved is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = fitted.Position;
            WindowState = fitted.IsMaximized ? WindowState.Maximized : WindowState.Normal;
        }
    }

    /// <summary> Captures normal bounds without replacing them with maximized or minimized bounds. </summary>
    private void CaptureWindowPlacement()
    {
        if (!_isOpened)
        {
            return;
        }

        if (WindowState is not (WindowState.Minimized or WindowState.FullScreen))
        {
            _wasMaximized = WindowState == WindowState.Maximized;
        }

        if (WindowState == WindowState.Normal && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _normalPlacement = new WindowPlacement(Position, Bounds.Size, false);
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty || change.Property == WindowStateProperty)
        {
            CaptureWindowPlacement();
        }
    }

    /// <summary> Captures the normal window position when it moves. </summary>
    /// <param name="sender">The window that moved.</param>
    /// <param name="e">The new desktop position.</param>
    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => CaptureWindowPlacement();

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || !_isOpened)
        {
            return;
        }

        CaptureWindowPlacement();

        try
        {
            if (_normalPlacement is not null)
            {
                _config.WindowPlacement = (_normalPlacement with { IsMaximized = _wasMaximized }).ToString();
            }

            if (MainTabs.SelectedItem is TabItem { Name: not null } tab && tab != QuitTab)
            {
                _config.ActiveTab = tab.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save main window settings.");
        }
    }

    /// <summary>
    ///     Handles the consent button click event.
    /// </summary>
    private void OnConsentButtonClick(object? sender, RoutedEventArgs e)
    {
        ConsentWindow!.IsVisible = false;
        _config.IsConsented = true;
    }

    /// <summary> Requests a system backdrop on Windows, falling back to acrylic and blur when Mica is unavailable. </summary>
    private void EnableSystemBackdrop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PropertyChanged += OnWindowPropertyChanged;
        _config.ParameterChangedEvent += OnConfigParameterChanged;

        ApplyBackdrop();
    }

    /// <summary> Applies or removes the system backdrop according to the current configuration. </summary>
    private void ApplyBackdrop()
    {
        TransparencyLevelHint = _config.UseMica
            ?
            [
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None
            ]
            : [WindowTransparencyLevel.None];

        UpdateBackdropState();
    }

    /// <summary> Re-applies the system backdrop when the Mica setting changes. </summary>
    /// <param name="parameterName"> The name of the changed configuration parameter. </param>
    private void OnConfigParameterChanged(string? parameterName)
    {
        if (parameterName == nameof(IConfigProvider.UseMica))
        {
            ApplyBackdrop();
        }
    }

    /// <summary> Enables the transparent window background when the platform provided an actual system backdrop. </summary>
    private void UpdateBackdropState()
    {
        var hasBackdrop =
            ActualTransparencyLevel.Equals(WindowTransparencyLevel.Mica)
         || ActualTransparencyLevel.Equals(WindowTransparencyLevel.AcrylicBlur)
         || ActualTransparencyLevel.Equals(WindowTransparencyLevel.Blur);

        Classes.Set("mica", hasBackdrop);
    }

    /// <summary> Updates the backdrop state when the platform changes the achieved transparency level. </summary>
    /// <param name="sender"> The event source. </param>
    /// <param name="e"> The property change arguments. </param>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ActualTransparencyLevelProperty)
        {
            return;
        }

        UpdateBackdropState();
    }
}
