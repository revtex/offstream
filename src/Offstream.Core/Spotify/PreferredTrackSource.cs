using Offstream.Core.Metadata;
using Serilog;

namespace Offstream.Core.Spotify;

/// <summary>
/// Asks the media transport controls first and the window title second, so detection survives
/// Spotify having no window.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "prefers" means here is narrow, and deliberately so.</b> The preferred source wins
/// whenever it reports a track at all. It does not win by being newer, more detailed, or more
/// confident — only by answering. Anything cleverer means two sources disagreeing mid-track and a
/// recording whose tags change halfway through.
/// </para>
/// <para>
/// <b>Silence is the handover signal.</b> A null means "I cannot see Spotify" — SMTC has no session
/// registered, or no process owns a window title — and that is the only condition that reaches the
/// fallback. A source that can see Spotify and reports it idle has answered the question, so an
/// idle answer is returned rather than second-guessed by the other source.
/// </para>
/// <para>
/// <b>A failing source is not a missing one.</b> If the preferred source throws, that is logged
/// once per transition and treated as silence rather than allowed to stop detection — SMTC is a
/// system service Offstream does not control, and its being unavailable should cost the better
/// metadata, never the recording. Logged on transition rather than per poll because this runs
/// several times a second and would otherwise bury the activity log.
/// </para>
/// </remarks>
public sealed class PreferredTrackSource : ITrackSource
{
    private readonly ITrackSource _preferred;
    private readonly ITrackSource _fallback;

    private bool _preferredIsFailing;
    private bool _hasReportedFallback;

    public PreferredTrackSource(ITrackSource preferred, ITrackSource fallback)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(fallback);

        _preferred = preferred;
        _fallback = fallback;
    }

    /// <inheritdoc />
    public async Task<Track?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        var track = await FromPreferredAsync(cancellationToken);

        if (track is not null)
        {
            if (_hasReportedFallback)
            {
                Log.Information("Spotify's media session is readable again; using it for track detection.");
                _hasReportedFallback = false;
            }

            return track;
        }

        var fallback = await _fallback.GetCurrentTrackAsync(cancellationToken);

        if (fallback is not null && !_hasReportedFallback)
        {
            Log.Debug("No Spotify media session; reading the window title instead.");
            _hasReportedFallback = true;
        }

        return fallback;
    }

    private async Task<Track?> FromPreferredAsync(CancellationToken cancellationToken)
    {
        try
        {
            var track = await _preferred.GetCurrentTrackAsync(cancellationToken);

            _preferredIsFailing = false;

            return track;
        }
        catch (OperationCanceledException)
        {
            // The session is stopping, or the poll was abandoned. Not this class's business.
            throw;
        }
#pragma warning disable CA1031 // SMTC is a system service; its faults must not stop detection.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_preferredIsFailing)
            {
                Log.Warning(
                    ex,
                    "Spotify's media session could not be read; falling back to the window title.");

                _preferredIsFailing = true;
            }

            return null;
        }
    }
}
