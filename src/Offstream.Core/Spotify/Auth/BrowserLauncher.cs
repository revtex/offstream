using System.Diagnostics;

namespace Offstream.Core.Spotify.Auth;

/// <summary>Opens a URI in the user's default browser.</summary>
public interface IBrowserLauncher
{
    void Open(Uri uri);
}

/// <summary>
/// Shells out to the OS's URI handler via <c>ShellExecute</c>, exactly as double-clicking a
/// link would.
/// </summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SpotifyAuthException($"Could not open a browser for '{uri}'.", ex);
        }
    }
}
