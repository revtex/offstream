namespace Offstream.Core.Encoding;

/// <summary>
/// How a lossy encoder spends the bitrate it is given.
/// </summary>
/// <remarks>
/// <para>
/// This is a two-valued choice about rate allocation, not the predecessor's <c>LAMEPreset</c>
/// coming back through a side door: the bitrate itself stays a plain kbps number, and this
/// says what the encoder is allowed to do with it. The distinction matters because a preset
/// enum bundles the two together, which is what made the old setting impossible to validate.
/// </para>
/// <para>
/// <see cref="Average"/> is the zero value on purpose. Enums are persisted as strings, but
/// <c>SettingsJsonContext</c>'s generator yields <c>default</c> for a key that is absent, so a
/// <c>settings.json</c> written before this setting existed loads as the new default rather
/// than as whichever member happened to be declared first.
/// </para>
/// </remarks>
public enum BitrateMode
{
    /// <summary>
    /// The encoder aims for the chosen rate across the recording and varies frame to frame,
    /// spending fewer bits on passages that do not need them.
    /// </summary>
    Average,

    /// <summary>
    /// Every frame is the chosen rate, whether the audio needs it or not.
    /// </summary>
    Constant,
}
