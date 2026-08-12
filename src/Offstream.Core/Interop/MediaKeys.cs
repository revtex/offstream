using Windows.Win32;
using Windows.Win32.Foundation;

namespace Offstream.Core.Interop;

/// <summary>Sends media transport commands to a specific window.</summary>
public interface IMediaKeys
{
    /// <summary>Tells the window that owns <paramref name="windowHandle"/> to skip to the next track.</summary>
    void SendNextTrack(nint windowHandle);
}

/// <summary>
/// <see cref="IMediaKeys"/> over <c>WM_APPCOMMAND</c>.
/// </summary>
/// <remarks>
/// Used by the "force Spotify to skip an already-recorded track" feature. The command is
/// posted to Spotify's own window rather than broadcast, so it does not hijack media keys
/// for other players that happen to be running.
/// </remarks>
public sealed class MediaKeys : IMediaKeys
{
    private const uint WmAppCommand = 0x0319;

    /// <summary>APPCOMMAND_MEDIA_NEXTTRACK.</summary>
    private const int AppCommandMediaNextTrack = 11;

    /// <summary>WM_APPCOMMAND packs the command into the high word of lParam.</summary>
    private const int AppCommandShift = 16;

    public void SendNextTrack(nint windowHandle)
    {
        if (windowHandle == 0) return;

        PInvoke.SendMessage(
            (HWND)windowHandle,
            WmAppCommand,
            wParam: 0,
            lParam: AppCommandMediaNextTrack << AppCommandShift);
    }
}
