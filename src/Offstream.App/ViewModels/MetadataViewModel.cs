using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.Core;
using Offstream.Core.Metadata.Library;
using Serilog;

namespace Offstream.App.ViewModels;

/// <summary>
/// Backs the Metadata page: repairing the tags on recordings that are already on disk.
/// </summary>
/// <remarks>
/// <para>
/// The recording pipeline tags a track while it records it, which leaves no way to fix one
/// afterwards — a file recorded before a provider was configured, or while Last.fm was down, keeps
/// its thin tags for good. This page is that second chance, and it is deliberately a three-step
/// one: <b>scan</b> reads what is there, <b>fetch</b> proposes, and only <b>save</b> touches a
/// file. Nothing between the first two steps is destructive, so a fetch that returns nonsense
/// costs the user nothing but the time.
/// </para>
/// <para>
/// <b>Fetching is sequential on purpose.</b> A folder of two hundred files is two hundred
/// searches, and running them at once is the fastest way to meet Spotify's rate limit and then its
/// quota. One at a time lets <c>SpotifyRetryHandler</c> honour a <c>Retry-After</c> as intended
/// instead of having every parallel call punished for the others.
/// </para>
/// </remarks>
public sealed partial class MetadataViewModel : ObservableObject
{
    private static readonly CompositeFormat ScanSummaryFormat =
        CompositeFormat.Parse(Strings.MetadataScanSummary);

    private static readonly CompositeFormat FetchProgressFormat =
        CompositeFormat.Parse(Strings.MetadataFetchProgress);

    private static readonly CompositeFormat FetchSummaryFormat =
        CompositeFormat.Parse(Strings.MetadataFetchSummary);

    private static readonly CompositeFormat SaveSummaryFormat =
        CompositeFormat.Parse(Strings.MetadataSaveSummary);

    private readonly ILibraryScanner _scanner;
    private readonly ILibraryTagWriter _writer;
    private readonly ILibraryMetadataChain _chain;
    private readonly IFolderPicker _folderPicker;
    private readonly SettingsDocument _settings;

    public MetadataViewModel(
        ILibraryScanner scanner,
        ILibraryTagWriter writer,
        ILibraryMetadataChain chain,
        IFolderPicker folderPicker,
        SettingsDocument settings)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(settings);

        _scanner = scanner;
        _writer = writer;
        _chain = chain;
        _folderPicker = folderPicker;
        _settings = settings;

        _folder = settings.Current.Output.Path ?? OffstreamPaths.DefaultOutputDirectory;
    }

    /// <summary>The rows, one per taggable file.</summary>
    public ObservableCollection<LibraryTrackViewModel> Tracks { get; } = [];

    /// <summary>The folder to scan. Starts at wherever recordings are being written.</summary>
    [ObservableProperty]
    private string _folder;

    /// <summary>What just happened, shown under the buttons.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Whether a scan, fetch or save is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Whether the page is accepting commands.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Whether anything has been scanned yet.</summary>
    public bool HasTracks => Tracks.Count > 0;

    /// <summary>Chooses a different folder to scan.</summary>
    /// <remarks>
    /// Offered even though the page defaults to the output folder, because a library that was
    /// recorded before Offstream — or moved since — is exactly the one most in need of repair.
    /// </remarks>
    [RelayCommand]
    private void Browse()
    {
        var chosen = _folderPicker.Pick(Folder);

        if (!string.IsNullOrWhiteSpace(chosen)) Folder = chosen;
    }

    /// <summary>Reads every taggable file under <see cref="Folder"/>.</summary>
    [RelayCommand]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusMessage = Strings.MetadataScanning;

        try
        {
            var scan = await _scanner.ScanAsync(Folder, cancellationToken);

            Tracks.Clear();

            foreach (var track in scan.Tracks) Tracks.Add(new LibraryTrackViewModel(track));

            OnPropertyChanged(nameof(HasTracks));

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                ScanSummaryFormat,
                scan.Tracks.Count,
                scan.SkippedWaveFiles,
                scan.Failures.Count);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.MetadataCancelled;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Looks up every row that is missing something.</summary>
    /// <remarks>
    /// Rows that already carry title, artist and album are skipped and left as
    /// <see cref="LibraryTrackStatus.Matched"/>. That is the difference between a page that costs
    /// a handful of requests and one that spends the user's whole quota re-confirming tags they
    /// curated themselves — and a single row can still be re-fetched deliberately.
    /// </remarks>
    [RelayCommand]
    private async Task AutoFetchAsync(CancellationToken cancellationToken)
    {
        var chain = _chain.Create();

        if (chain.IsEmpty)
        {
            StatusMessage = Strings.MetadataNoProvider;

            return;
        }

        var pending = Tracks.Where(row => row.Status is not LibraryTrackStatus.Matched
            and not LibraryTrackStatus.Saved).ToList();

        IsBusy = true;

        var matched = 0;
        var failed = 0;

        try
        {
            for (var index = 0; index < pending.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StatusMessage = string.Format(
                    CultureInfo.CurrentCulture, FetchProgressFormat, index + 1, pending.Count);

                if (await FetchOneAsync(chain, pending[index], cancellationToken)) matched++;
                else if (pending[index].Status == LibraryTrackStatus.Failed) failed++;
            }

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture, FetchSummaryFormat, matched, pending.Count, failed);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.MetadataCancelled;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-runs the lookup for one row, whatever its current state.</summary>
    /// <remarks>
    /// The escape hatch for the skip rule above: a file whose three fields are all filled in but
    /// wrong is invisible to auto-fetch, and this is how the user overrules that.
    /// </remarks>
    [RelayCommand]
    private async Task RefetchAsync(LibraryTrackViewModel? row)
    {
        if (row is null) return;

        var chain = _chain.Create();

        if (chain.IsEmpty)
        {
            StatusMessage = Strings.MetadataNoProvider;

            return;
        }

        IsBusy = true;

        try
        {
            await FetchOneAsync(chain, row, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the ticked rows back to their files.</summary>
    [RelayCommand]
    private async Task SaveSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Tracks.Where(row => row.IsSelected && !row.HasErrors).ToList();

        IsBusy = true;

        var saved = 0;
        var failed = 0;

        try
        {
            foreach (var row in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await _writer.SaveAsync(row.Track, cancellationToken);

                if (result.Saved)
                {
                    row.Status = LibraryTrackStatus.Saved;
                    row.FailureReason = null;
                    saved++;
                }
                else
                {
                    row.Status = LibraryTrackStatus.Failed;
                    row.FailureReason = result.FailureReason;
                    failed++;
                }
            }

            StatusMessage = string.Format(CultureInfo.CurrentCulture, SaveSummaryFormat, saved, failed);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.MetadataCancelled;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Runs one lookup and folds the outcome into the row.</summary>
    /// <remarks>
    /// A failure marks its own row and returns; it never propagates, because one dead lookup in
    /// the middle of two hundred should cost that file and not the run. Cancellation is the
    /// exception — that is the user asking for the whole thing to stop.
    /// </remarks>
    private static async Task<bool> FetchOneAsync(
        FallbackMetadataProvider chain,
        LibraryTrackViewModel row,
        CancellationToken cancellationToken)
    {
        row.Status = LibraryTrackStatus.Fetching;
        row.FailureReason = null;

        try
        {
            var updated = await chain.EnrichAsync(row.Track.Suggested, cancellationToken);

            row.Track.Status = updated ? LibraryTrackStatus.Fetched : LibraryTrackStatus.Untagged;
            row.Track.FailureReason = null;
            row.RefreshFromSuggestion();

            return updated;
        }
        catch (OperationCanceledException)
        {
            row.Status = LibraryTrackStatus.Untagged;

            throw;
        }
        catch (MetadataLookupException ex)
        {
            Log.Warning(ex, "Metadata lookup failed for {Path}.", row.Path);

            row.Track.Status = LibraryTrackStatus.Failed;
            row.Track.FailureReason = ex.Message;
            row.RefreshFromSuggestion();

            return false;
        }
    }
}
