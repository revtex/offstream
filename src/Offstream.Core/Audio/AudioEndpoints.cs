using NAudio.CoreAudioApi;

namespace Offstream.Core.Audio;

public sealed record RenderDevice(string Id, string Name, bool IsDefault);

/// <summary>Enumerates render endpoints. Verifies NAudio 2.x device APIs on .NET 10.</summary>
public static class AudioEndpoints
{
    public static IReadOnlyList<RenderDevice> ListRender()
    {
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }

        var results = new List<RenderDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                results.Add(new RenderDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return results;
    }

    /// <summary>Resolves a device by id, or the default endpoint when <paramref name="id"/> is null.</summary>
    /// <remarks>
    /// The enumerator is closed here and the endpoint outlives it: the two are separately
    /// reference-counted COM objects, and the device does not need the enumerator that found it.
    /// Keeping the enumerator instead left one behind per call, on a path the track detector runs
    /// several times a second while recording.
    /// </remarks>
    public static MMDevice Resolve(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (string.IsNullOrWhiteSpace(id))
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        return enumerator.GetDevice(id);
    }

    /// <summary>Finds a render endpoint whose name contains <paramref name="fragment"/>.</summary>
    public static RenderDevice? FindByName(string fragment) =>
        ListRender().FirstOrDefault(d => d.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
