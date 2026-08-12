using System.Runtime.InteropServices;
using Offstream.Core.Audio;
using Serilog;

namespace Offstream.App.Services;

/// <summary>Lists the render endpoints the Settings page offers.</summary>
/// <remarks>
/// A seam over <see cref="AudioEndpoints"/> so the ViewModel can be tested without a sound card.
/// Enumeration goes through the Core Audio APIs, which need real hardware and return nothing
/// useful on a build agent.
/// </remarks>
public interface IAudioDeviceCatalog
{
    /// <summary>Every render endpoint, or an empty list when they cannot be enumerated.</summary>
    IReadOnlyList<RenderDevice> ListRender();
}

/// <inheritdoc />
public sealed class AudioDeviceCatalog : IAudioDeviceCatalog
{
    /// <inheritdoc />
    /// <remarks>
    /// Enumeration failing is not worth an exception on a settings page: the device list is a
    /// convenience over a stored id, and the recorder falls back to the default endpoint anyway.
    /// An empty list leaves "System default" selected, which is the same thing the user would
    /// have to choose.
    /// </remarks>
    public IReadOnlyList<RenderDevice> ListRender()
    {
        try
        {
            return AudioEndpoints.ListRender();
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Audio endpoints could not be listed.");
            return [];
        }
    }
}
