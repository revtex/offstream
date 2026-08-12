using Windows.Win32;
using Windows.Win32.System.Power;

namespace Offstream.Core.Interop;

/// <summary>Keeps the machine awake while a recording is in progress.</summary>
public interface IPowerManagement
{
    /// <summary>Asks Windows not to sleep. Must be paired with <see cref="AllowSleep"/>.</summary>
    void PreventSleep();

    /// <summary>Releases the request, letting normal idle timers resume.</summary>
    void AllowSleep();
}

/// <summary>
/// <see cref="IPowerManagement"/> over <c>SetThreadExecutionState</c>.
/// </summary>
/// <remarks>
/// <para>
/// Recording is a long unattended operation with no user input, so without this Windows
/// suspends partway through and the recording is lost.
/// </para>
/// <para>
/// Deliberately <em>not</em> <c>ES_DISPLAY_REQUIRED</c>: the screen may sleep, only the
/// system may not. Keeping a display awake all night to record audio would be rude.
/// </para>
/// <para>
/// The request is thread-scoped and cleared when the thread exits, which is why the
/// recording session must call <see cref="AllowSleep"/> on the same thread that called
/// <see cref="PreventSleep"/>.
/// </para>
/// </remarks>
public sealed class PowerManagement : IPowerManagement
{
    public void PreventSleep() =>
        PInvoke.SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);

    public void AllowSleep() =>
        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
}
