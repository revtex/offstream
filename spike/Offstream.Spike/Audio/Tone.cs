using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Offstream.Core.Audio;

namespace Offstream.Spike.Audio;

/// <summary>
/// Plays a sine tone through an endpoint so loopback capture has something to record.
/// </summary>
/// <remarks>
/// Needed because WASAPI loopback delivers <em>no</em> buffers at all while the endpoint is
/// idle — not buffers of silence, but no <c>DataAvailable</c> events whatsoever. Without a
/// generated signal, a capture check cannot tell "loopback is broken" from "nothing was
/// playing", which makes the acceptance run depend on a human pressing play at the right
/// moment. This keeps it self-contained and repeatable.
/// </remarks>
internal sealed class Tone : IDisposable
{
    private readonly WasapiOut _output;

    private Tone(WasapiOut output) => _output = output;

    public static Tone Play(string? deviceId, double frequency = 440, double gain = 0.2)
    {
        var device = AudioEndpoints.Resolve(deviceId);
        var output = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);

        var generator = new SignalGenerator(48000, 2)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = frequency,
            Gain = gain,
        };

        output.Init(generator);
        output.Play();

        return new Tone(output);
    }

    public void Dispose()
    {
        try
        {
            _output.Stop();
        }
        catch
        {
            // Stopping a device that has already gone away is not interesting here.
        }

        _output.Dispose();
    }
}
