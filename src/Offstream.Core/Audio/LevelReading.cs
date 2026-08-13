namespace Offstream.Core.Audio;

/// <summary>
/// How loud one interval of audio was, in both the forms a display needs.
/// </summary>
/// <param name="Level">
/// Loudness as 0–1, with the meter's decibel floor at 0 and full scale at 1. What a bar's width
/// is set from.
/// </param>
/// <param name="Decibels">
/// The same loudness in dBFS, unclamped, and <see cref="double.NegativeInfinity"/> for silence.
/// What a numeric readout shows, and what a control with a scale of its own maps for itself.
/// </param>
/// <remarks>
/// Both, rather than one derived from the other at the call site, because they answer different
/// questions and disagreeing about the floor is how a bar ends up not lining up with the ruler
/// printed under it. A reading carries the decibel figure it was actually built from.
/// </remarks>
public readonly record struct LevelReading(float Level, double Decibels)
{
    /// <summary>An interval with no audio in it — or none this meter could read.</summary>
    public static LevelReading Silent { get; } = new(0f, double.NegativeInfinity);
}
