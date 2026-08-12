using System.IO;
using Microsoft.Win32;

namespace Offstream.App.Services;

/// <summary>Asks the user for a folder.</summary>
/// <remarks>
/// A seam over the shell dialog: a ViewModel that opens one directly cannot be tested, because
/// the dialog blocks on a message loop that a test does not have.
/// </remarks>
public interface IFolderPicker
{
    /// <summary>The folder chosen, or null when the dialog was cancelled.</summary>
    /// <param name="startingFolder">Where to open, when it still exists.</param>
    string? Pick(string? startingFolder);
}

/// <inheritdoc />
/// <remarks>
/// <see cref="OpenFolderDialog"/> is the modern shell picker, available to WPF since .NET 8.
/// The predecessor used <c>System.Windows.Forms.FolderBrowserDialog</c> and therefore dragged
/// WinForms into a WPF process; nothing here needs that.
/// </remarks>
public sealed class FolderPicker : IFolderPicker
{
    /// <inheritdoc />
    public string? Pick(string? startingFolder)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };

        if (!string.IsNullOrWhiteSpace(startingFolder) && Directory.Exists(startingFolder))
        {
            dialog.InitialDirectory = startingFolder;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
