using Avalonia.Controls;

namespace Avalonia.Desktop.Controls;

/// <summary>
///     Lays out the main tabs: game tabs stacked in the game column, and tabs with the
///     <c>util</c> class as a right-aligned row of pills in the top bar.
/// </summary>
public sealed class LauncherTabsPanel : Panel
{
    private const double GameLeft = 24;

    private const double GameWidth = 234;

    private const double GamesTop = 104;

    private const double GamesBottom = 44;

    private const double GameHeight = 52;

    private const double PillTop = 13;

    private const double PillHeight = 34;

    private const double PillSpacing = 8;

    /// <summary> Leaves room at the right of the top bar for the fullscreen button. </summary>
    private const double PillsRightInset = 70;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(IsPill(child) ? new Size(double.PositiveInfinity, PillHeight) : new Size(GameWidth, GameHeight));
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height
            );
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var games = Children.Where(static c => c.IsVisible && !IsPill(c)).ToArray();
        var pills = Children.Where(static c => c.IsVisible && IsPill(c)).ToArray();

        var gameHeight = games.Length == 0
            ? GameHeight
            : Math.Clamp((finalSize.Height - GamesTop - GamesBottom) / games.Length, 24, GameHeight);

        var y = GamesTop;

        foreach (var game in games)
        {
            game.Arrange(new Rect(GameLeft, y, GameWidth, gameHeight));
            y += gameHeight;
        }

        var x = finalSize.Width - PillsRightInset;

        foreach (var pill in pills.AsEnumerable().Reverse())
        {
            x -= pill.DesiredSize.Width;
            pill.Arrange(new Rect(x, PillTop, pill.DesiredSize.Width, PillHeight));
            x -= PillSpacing;
        }

        return finalSize;
    }

    /// <summary>
    ///     Determines whether a tab belongs in the top bar.
    /// </summary>
    /// <param name="child">The tab to check.</param>
    /// <returns><see langword="true" /> if the tab has the <c>util</c> class.</returns>
    private static bool IsPill(Control child) => child.Classes.Contains("util");
}
