using Offstream.Core.Settings;
using Serilog;

namespace Offstream.App.Services;

/// <summary>
/// The settings file as the two settings pages edit it: one working copy, saved on change.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no OK button, and that is a decision.</b> Plan §10 Phase 6 rules out modal
/// dialogs in favour of inline validation, and a form that validates inline but still needs
/// confirming has the worst of both — errors shown immediately, changes applied only later.
/// Every valid edit is written straight through, the way the Windows Settings app behaves.
/// The write is atomic (see <see cref="SettingsStore"/>), so saving per keystroke costs a
/// rename rather than risking a torn file.
/// </para>
/// <para>
/// <b><see cref="Current"/> only advances when the save succeeds.</b> A change that could not be
/// written must not linger in memory looking as though it applied — the next recording reads the
/// file, not this object, so the two disagreeing is exactly the bug that would be reported as
/// "it forgot my settings".
/// </para>
/// <para>
/// Shared by both pages because they edit one file between them. Two stores would race: the
/// Advanced page would write a document built from whatever the Settings page had last loaded,
/// silently reverting the other tab's edits.
/// </para>
/// </remarks>
public sealed class SettingsDocument
{
    private readonly SettingsStore _store;

    public SettingsDocument(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        Current = store.LoadOrDefault(out var problem);
        LoadProblem = problem;
    }

    /// <summary>The settings as last successfully read or written.</summary>
    public OffstreamSettings Current { get; private set; }

    /// <summary>Why the file could not be read, or null. The shell shows this at startup.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Where the file lives, for the "settings are stored here" line on the page.</summary>
    public string Path => _store.Path;

    /// <summary>Raised after a change is written, so the other page can pick it up.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads the file, replacing <see cref="Current"/> and <see cref="LoadProblem"/>.</summary>
    /// <remarks>
    /// Called when a recording session starts, for two reasons. It is what makes a settings file
    /// corrected outside the app — or corrected and re-saved after the shell reported a problem
    /// at startup — usable without a restart. And it means the counter the session is about to
    /// increment, and the Spotify token it may rotate, are written back onto what is actually on
    /// disk rather than onto a copy read when the window opened.
    /// </remarks>
    public OffstreamSettings Reload()
    {
        Current = _store.LoadOrDefault(out var problem);
        LoadProblem = problem;

        Changed?.Invoke(this, EventArgs.Empty);

        return Current;
    }

    /// <summary>Applies a change and writes it.</summary>
    /// <param name="change">Produces the new settings from the current ones.</param>
    /// <returns>Null when saved, otherwise why the save was refused.</returns>
    public string? Update(Func<OffstreamSettings, OffstreamSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var updated = change(Current);

        try
        {
            _store.Save(updated);
        }
        catch (SettingsException ex)
        {
            // The page validates with the same rules before getting here, so this is the file
            // itself refusing - a read-only folder, a full disk - not a value the user typed.
            Log.Warning(ex, "Settings could not be saved.");
            return ex.Message;
        }

        Current = updated;
        Changed?.Invoke(this, EventArgs.Empty);

        return null;
    }
}
