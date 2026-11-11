using CommunityToolkit.Mvvm.Input;

namespace Core.Client.Interfaces;

/// <summary>
///     Defines the interface for port button controls used in the ports view.
/// </summary>
public interface IPortsButtonControl
{
    /// <summary>
    ///     Gets the command to open the port installation folder.
    /// </summary>
    IRelayCommand? OpenFolderCommand { get; }

    /// <summary>
    ///     Gets the command to refresh the ports list.
    /// </summary>
    IAsyncRelayCommand? RefreshListCommand { get; }

    /// <summary>
    ///     Gets the command that lets the user pick addon archives or maps to import.
    /// </summary>
    IAsyncRelayCommand? ImportAddonsCommand { get; }

    /// <summary>
    ///     Gets the command that opens the game's import folder.
    /// </summary>
    IRelayCommand? OpenImportFolderCommand { get; }

    /// <summary>
    ///     Gets the command that imports the files in the game's import folder now.
    /// </summary>
    IAsyncRelayCommand? ScanImportFolderCommand { get; }

    /// <summary>
    ///     Gets the command that lets the user choose a different import folder for the game.
    /// </summary>
    IAsyncRelayCommand? ChangeImportFolderCommand { get; }

    /// <summary>
    ///     Gets the command that switches the game back to its default import folder.
    /// </summary>
    IRelayCommand? ResetImportFolderCommand { get; }

    /// <summary>
    ///     Gets the full path of the game's current import folder.
    /// </summary>
    string ImportFolderPath { get; }

    /// <summary>
    ///     Gets whether the port buttons are visible.
    /// </summary>
    bool IsPortsButtonsVisible { get; }

    /// <summary>
    ///     Gets or sets whether a port operation is currently in progress.
    /// </summary>
    bool IsInProgress { get; set; }
}
