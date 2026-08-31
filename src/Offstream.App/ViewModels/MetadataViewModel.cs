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

    private static readonly CompositeFormat FilterSummaryFormat =
        CompositeFormat.Parse(Strings.MetadataFilterSummary);

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

    /// <summary>Every row the scan found, one per taggable file.</summary>
    /// <remarks>
    /// The authority for everything except what is drawn. Save, the counts and the auto-fetch
    /// queue all read this rather than <see cref="VisibleTracks"/>, so a filter narrows the list
    /// on screen and changes nothing about what the buttons do. A filter that quietly reduced
    /// what Save wrote would be a filter that loses work.
    /// </remarks>
    public ObservableCollection<LibraryTrackViewModel> Tracks { get; } = [];

    /// <summary>The rows the list actually shows, after <see cref="Filter"/>.</summary>
    /// <remarks>
    /// Rebuilt rather than filtered through an <c>ICollectionView</c>. The rows are the same
    /// instances either way, so nothing is lost by rebuilding, and a plain collection keeps this
    /// testable without a Dispatcher — the view models are covered by ordinary unit tests and a
    /// collection view would have dragged WPF's threading rules into them.
    /// </remarks>
    public ObservableCollection<LibraryTrackViewModel> VisibleTracks { get; } = [];

    /// <summary>The row the editor on the right is editing, or null when none is picked.</summary>
    /// <remarks>
    /// The editor used to open inside the row itself, and it never fitted: an opened row measured
    /// 539 device-independent pixels against a list viewport of 347, so choosing a track buried
    /// the library it was chosen from and pushed the search results off the bottom of the page.
    /// Hoisting the choice to the page turns the same controls into a pane that is always the
    /// same size and always in the same place, and nothing reflows when the pick changes.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LibraryTrackViewModel? _selectedTrack;

    /// <summary>Whether a row is picked, and so whether the editor has anything to show.</summary>
    public bool HasSelection => SelectedTrack is not null;

    /// <summary>Fills the picked row's search box.</summary>
    /// <remarks>
    /// Seeding on selection covers every way in — clicking a row, arrowing onto it, or the list
    /// re-filtering under a pick. It hung off an expand command once, so a row opened by its
    /// chevron rather than its title got an empty box and searching from there asked Spotify for
    /// nothing.
    /// <para>This can fire more than once for the same pick: re-filtering clears the selection
    /// and puts it back, so the row is seeded twice. That is harmless only because seeding is
    /// idempotent — it returns early on a query that is already there. Anything added here has
    /// to stay safe to repeat.</para>
    /// </remarks>
    partial void OnSelectedTrackChanged(LibraryTrackViewModel? value) => value?.SeedMatchQuery();

    /// <summary>The folder to scan. Starts at wherever recordings are being written.</summary>
    [ObservableProperty]
    private string _folder;

    /// <summary>What just happened, shown under the buttons.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Narrows the list to rows matching this text.</summary>
    /// <remarks>
    /// A hundred and twenty-seven files is a scroll; a real library is a search. Matching covers
    /// the file name as well as the three tags, because the row that needs fixing is often the
    /// one whose tags are wrong and whose name is the only true thing about it.
    /// </remarks>
    [ObservableProperty]
    private string _filter = string.Empty;

    /// <summary>Whether a scan, fetch or save is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Whether the page is accepting commands.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Whether anything has been scanned yet.</summary>
    /// <remarks>
    /// Deliberately the unfiltered count. A filter that matches nothing should say so against a
    /// list that is still there, not collapse the page back to its empty state as though the
    /// scan had been undone.
    /// </remarks>
    public bool HasTracks => Tracks.Count > 0;

    /// <summary>Whether a filter is narrowing the list.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Filter);

    /// <summary>Whether the filter is on and matched nothing.</summary>
    public bool HasNoMatches => IsFiltered && VisibleTracks.Count == 0;

    /// <summary>How much of the library is showing, when only some of it is.</summary>
    public string FilterSummary => string.Format(
        CultureInfo.CurrentCulture,
        FilterSummaryFormat,
        VisibleTracks.Count,
        Tracks.Count);

    /// <summary>Re-applies <see cref="Filter"/> to the visible list.</summary>
    private void ApplyFilter()
    {
        var needle = Filter?.Trim();

        // Clearing the collection makes the list null its own SelectedItem, which travels back
        // here through the two-way binding. Without putting the pick back, typing in the filter
        // box would empty the editor on every keystroke — including the keystrokes that still
        // match the row being edited.
        var picked = SelectedTrack;

        VisibleTracks.Clear();

        foreach (var row in Tracks)
        {
            if (Matches(row, needle)) VisibleTracks.Add(row);
        }

        SelectedTrack = picked is not null && VisibleTracks.Contains(picked) ? picked : null;

        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(FilterSummary));
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    /// <summary>Whether one row answers the filter.</summary>
    /// <remarks>
    /// Case-insensitive substring across the three tags and the file name. Not a fuzzy match:
    /// the user typing here knows what they are looking for, and a fuzzy filter over a list this
    /// long returns the whole library for a two-letter word.
    /// </remarks>
    private static bool Matches(LibraryTrackViewModel row, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;

        return Contains(row.Title, needle)
            || Contains(row.Artist, needle)
            || Contains(row.Album, needle)
            || Contains(row.FileName, needle);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) == true;

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

            SelectedTrack = null;
            Tracks.Clear();

            foreach (var track in scan.Tracks) Tracks.Add(new LibraryTrackViewModel(track));

            ApplyFilter();

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

    /// <summary>Searches the catalogue for what the user typed on one row.</summary>
    /// <remarks>
    /// The escape hatch of last resort, and the only one that can correct a match the automatic
    /// path is certain about. Re-fetch asks the same question again and gets the same answer;
    /// this asks a different question, and the automatic path's own rule — reject any result
    /// whose artist disagrees with the file — is precisely what stops it helping when the file's
    /// artist is the thing that is wrong.
    /// </remarks>
    [RelayCommand]
    private async Task SearchMatchesAsync(LibraryTrackViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.MatchQuery)) return;

        var search = _chain.CreateMatchSearch();

        if (search is null)
        {
            row.SearchMessage = Strings.MetadataSearchNeedsSpotify;

            return;
        }

        row.IsSearching = true;
        row.SearchMessage = null;
        row.Candidates.Clear();
        row.NotifyCandidatesChanged();

        try
        {
            var results = await search.SearchAsync(row.MatchQuery, CancellationToken.None);

            foreach (var result in results) row.Candidates.Add(new LibraryMatchViewModel(row, result));

            row.SearchMessage = results.Count == 0 ? Strings.MetadataSearchNoResults : null;
        }
        catch (MetadataLookupException ex)
        {
            row.SearchMessage = ex.Message;
        }
        finally
        {
            row.IsSearching = false;
            row.NotifyCandidatesChanged();
        }
    }

    /// <summary>Applies a chosen search result to its row.</summary>
    /// <remarks>
    /// The results disappear afterwards. The choice has been made and its effect is visible in
    /// the fields above, so leaving the list open invites picking a second one on top of the
    /// first — which works, but reads as though neither had been applied.
    /// </remarks>
    [RelayCommand]
    private async Task UseMatchAsync(LibraryMatchViewModel? choice)
    {
        if (choice is null) return;

        var search = _chain.CreateMatchSearch();

        if (search is null)
        {
            choice.Row.SearchMessage = Strings.MetadataSearchNeedsSpotify;

            return;
        }

        var row = choice.Row;

        row.IsSearching = true;

        try
        {
            await search.ApplyAsync(row.Track.Suggested, choice.Candidate, CancellationToken.None);

            row.Track.Status = LibraryTrackStatus.Fetched;
            row.Track.FailureReason = null;
            row.RefreshFromSuggestion();

            row.Candidates.Clear();
            row.SearchMessage = null;
        }
        catch (MetadataLookupException ex)
        {
            row.SearchMessage = ex.Message;
        }
        finally
        {
            row.IsSearching = false;
            row.NotifyCandidatesChanged();
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

        var genres = row.Track.Suggested.Genres;
        var year = row.Track.Suggested.Year;

        try
        {
            var updated = await chain.EnrichAsync(row.Track.Suggested, cancellationToken);

            // A lookup on this page adds tags. It never takes one away.
            //
            // Every provider assigns genre and year unconditionally, which is right where they
            // were written — a recording starts with an empty track and the provider is the only
            // source there is. Here the track starts as the file's own tags, and a provider that
            // knows the song but has no genre for it (Last.fm returns none for an artist nobody
            // has tagged; Spotify returns none for most of its catalogue since late 2024) hands
            // back an empty list that overwrites one the user curated. Nothing reaches the file —
            // the writer skips empty values — but the row said "will change" over a change Save
            // would not make, and the panel underneath it spelled the loss out as an offer.
            row.Track.Suggested.Genres = row.Track.Suggested.Genres is { Length: > 0 } found ? found : genres;
            row.Track.Suggested.Year ??= year;

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
