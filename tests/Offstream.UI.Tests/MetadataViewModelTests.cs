using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.App.ViewModels;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The Metadata page's three steps, and the rule that only the last one touches a file.
/// </summary>
public sealed class MetadataViewModelTests
{
    /// <summary>The page opens on wherever recordings are being written.</summary>
    /// <remarks>
    /// Not a hard-coded path: the user can move their output folder, and a page that kept
    /// pointing at the old one would show an empty library and look broken.
    /// </remarks>
    [Fact]
    public void Folder_DefaultsToTheConfiguredOutputPath()
    {
        var document = SettingsFakes.DocumentWith(
            SettingsFakes.Document().Current with
            {
                Output = SettingsFakes.Document().Current.Output with { Path = @"C:\Recordings" },
            });

        Assert.Equal(@"C:\Recordings", Build(document: document).Folder);
    }

    /// <summary>Scanning fills the grid.</summary>
    [Fact]
    public async Task Scan_PopulatesTheRows()
    {
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", null), Scanned("b.mp3", "B", "U", "Al"));
        var viewModel = Build(scanner);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Tracks.Count);
        Assert.True(viewModel.HasTracks);
    }

    /// <summary>The summary counts the WAV files it passed over.</summary>
    [Fact]
    public async Task Scan_SaysHowManyWaveFilesWereSkipped()
    {
        var scanner = new FakeScanner([Scanned("a.mp3", "A", "T", null)], skippedWaveFiles: 3);
        var viewModel = Build(scanner);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Contains("3", viewModel.StatusMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file that is already fully tagged is not looked up.
    /// </summary>
    /// <remarks>
    /// The rule that keeps a two-hundred-file library from costing two hundred requests, and the
    /// one that stops a provider quietly replacing tags the user curated by hand.
    /// </remarks>
    [Fact]
    public async Task AutoFetch_SkipsFilesThatAreAlreadyComplete()
    {
        var provider = new CountingProvider();
        var scanner = new FakeScanner(
            Scanned("complete.mp3", "A", "T", "Al"),
            Scanned("thin.mp3", "B", "U", null));

        var viewModel = Build(scanner, chain: new FakeChain(provider));

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.AutoFetchCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.Calls);
    }

    /// <summary>A row can be looked up deliberately even when it looks complete.</summary>
    [Fact]
    public async Task Refetch_LooksUpARowAutoFetchWouldHaveSkipped()
    {
        var provider = new CountingProvider();
        var scanner = new FakeScanner(Scanned("complete.mp3", "A", "T", "Al"));
        var viewModel = Build(scanner, chain: new FakeChain(provider));

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.RefetchCommand.ExecuteAsync(viewModel.Tracks[0]);

        Assert.Equal(1, provider.Calls);
    }

    /// <summary>A lookup that has no genre does not offer to remove the one the file has.</summary>
    /// <remarks>
    /// Every provider assigns genre and year unconditionally, which is correct where they are
    /// used to tag a recording — the track starts empty there. Here it starts as the file's own
    /// tags, so a provider that knows the song and has no genre for it used to hand back an empty
    /// list. The writer never wrote it, but the row said "will change" and the panel under it
    /// showed the genre being replaced by nothing.
    /// </remarks>
    [Fact]
    public async Task Refetch_DoesNotOfferToEraseAGenreTheProviderHasNoAnswerFor()
    {
        var provider = new CountingProvider
        {
            Result = true,
            FetchedTitle = "T",
            FetchedArtist = "A",
            BlanksGenreAndYear = true,
        };

        var scanned = new LibraryTrack(
            @"C:\Music\curated.mp3",
            new Track
            {
                Artist = "A",
                Title = "T",
                Album = "Al",
                Genres = ["pop", "video"],
                Year = 1999,
            });

        var viewModel = Build(new FakeScanner(scanned), chain: new FakeChain(provider));

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.RefetchCommand.ExecuteAsync(viewModel.Tracks[0]);

        Assert.False(viewModel.Tracks[0].HasGenreChange);
        Assert.False(viewModel.Tracks[0].HasPendingChanges);
    }

    /// <summary>With nothing configured, the page says so rather than failing every row.</summary>
    [Fact]
    public async Task AutoFetch_SaysWhenNoSourceIsConfigured()
    {
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", null));
        var viewModel = Build(scanner, chain: new FakeChain(null));

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.AutoFetchCommand.ExecuteAsync(null);

        Assert.Contains("Settings", viewModel.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LibraryTrackStatus.Untagged, viewModel.Tracks[0].Status);
    }

    /// <summary>
    /// A lookup failure marks its own row and the run carries on.
    /// </summary>
    /// <remarks>
    /// One expired token in the middle of a folder must cost that file, not the other
    /// hundred and ninety-nine.
    /// </remarks>
    [Fact]
    public async Task AutoFetch_MarksAFailedRowAndKeepsGoing()
    {
        var scanner = new FakeScanner(
            Scanned("a.mp3", "A", "T", null),
            Scanned("b.mp3", "B", "U", null));

        var provider = new CountingProvider { Throws = new MetadataLookupException("Spotify said no") };
        var viewModel = Build(scanner, chain: new FakeChain(provider));

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.AutoFetchCommand.ExecuteAsync(null);

        Assert.Equal(2, provider.Calls);
        Assert.All(viewModel.Tracks, row => Assert.Equal(LibraryTrackStatus.Failed, row.Status));
        Assert.All(viewModel.Tracks, row => Assert.Contains("Spotify said no", row.FailureReason!, StringComparison.Ordinal));
    }

    /// <summary>Nothing is written until Save runs.</summary>
    [Fact]
    public async Task AutoFetch_WritesNothingToDisk()
    {
        var writer = new FakeWriter();
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", null));
        var viewModel = Build(scanner, writer, new FakeChain(new CountingProvider { Result = true }));

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.AutoFetchCommand.ExecuteAsync(null);

        Assert.Empty(writer.Saved);
    }

    /// <summary>Save writes the ticked rows and reports what happened.</summary>
    [Fact]
    public async Task Save_WritesSelectedRowsOnly()
    {
        var writer = new FakeWriter();
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", "Al"), Scanned("b.mp3", "B", "U", "Al"));
        var viewModel = Build(scanner, writer);

        await viewModel.ScanCommand.ExecuteAsync(null);
        viewModel.Tracks[1].IsSelected = false;
        await viewModel.SaveSelectedCommand.ExecuteAsync(null);

        Assert.Single(writer.Saved);
        Assert.Equal(LibraryTrackStatus.Saved, viewModel.Tracks[0].Status);
    }

    /// <summary>
    /// A row with a validation error is not written.
    /// </summary>
    /// <remarks>
    /// Clearing the title box and pressing Save must not be a way to erase a tag, so the row is
    /// excluded rather than written with an empty field.
    /// </remarks>
    [Fact]
    public async Task Save_SkipsRowsWithValidationErrors()
    {
        var writer = new FakeWriter();
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", "Al"));
        var viewModel = Build(scanner, writer);

        await viewModel.ScanCommand.ExecuteAsync(null);
        viewModel.Tracks[0].Title = string.Empty;
        await viewModel.SaveSelectedCommand.ExecuteAsync(null);

        Assert.Empty(writer.Saved);
    }

    /// <summary>A failed write leaves its reason on the row.</summary>
    [Fact]
    public async Task Save_ReportsAFailureOnTheRow()
    {
        var writer = new FakeWriter { Failure = "'a.mp3' is in use or read-only." };
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", "Al"));
        var viewModel = Build(scanner, writer);

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.SaveSelectedCommand.ExecuteAsync(null);

        Assert.Equal(LibraryTrackStatus.Failed, viewModel.Tracks[0].Status);
        Assert.Contains("in use", viewModel.Tracks[0].FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>An edit made after a fetch is the one that gets written.</b>
    /// </summary>
    /// <remarks>
    /// <see cref="Track"/> keeps two values per text field and the API one always wins, which is
    /// exactly right while a provider is filling gaps and exactly wrong once a person has
    /// corrected the provider. Without the row writing the API side too, a correction typed over
    /// a wrong match is accepted by the box, shown in the grid, and silently discarded at the
    /// point of writing — the failure the review step exists to prevent.
    /// </remarks>
    [Fact]
    public async Task EditingARow_AfterAFetch_OverridesWhatWasFetched()
    {
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", null));
        var viewModel = Build(scanner, chain: new FakeChain(new CountingProvider
        {
            Result = true,
            FetchedTitle = "Almost Right",
            FetchedArtist = "Almost Right Too",
        }));

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.AutoFetchCommand.ExecuteAsync(null);

        Assert.Equal("Almost Right", viewModel.Tracks[0].Title);

        viewModel.Tracks[0].Title = "Corrected";
        viewModel.Tracks[0].Artist = "Corrected Artist";

        Assert.Equal("Corrected", viewModel.Tracks[0].Track.Suggested.Title);
        Assert.Equal("Corrected Artist", viewModel.Tracks[0].Track.Suggested.Artist);
    }

    /// <summary>Editing a row's box reaches the track that would be saved.</summary>
    [Fact]
    public async Task EditingARow_ChangesWhatWouldBeWritten()
    {
        var scanner = new FakeScanner(Scanned("a.mp3", "A", "T", "Al"));
        var viewModel = Build(scanner);

        await viewModel.ScanCommand.ExecuteAsync(null);
        viewModel.Tracks[0].Album = "Corrected";

        Assert.Equal("Corrected", viewModel.Tracks[0].Track.Suggested.Album);
        Assert.True(viewModel.Tracks[0].Track.HasChanges);
    }

    /// <summary>Browsing starts where the box already points.</summary>
    [Fact]
    public void Browse_OpensAtTheCurrentFolder()
    {
        var picker = new FakeFolderPicker { Result = @"C:\Elsewhere" };
        var viewModel = Build(picker: picker);

        viewModel.Folder = @"C:\Somewhere";
        viewModel.BrowseCommand.Execute(null);

        Assert.Equal(@"C:\Somewhere", picker.StartingFolder);
        Assert.Equal(@"C:\Elsewhere", viewModel.Folder);
    }

    /// <summary>Cancelling the dialog leaves the folder alone.</summary>
    [Fact]
    public void Browse_KeepsTheFolderWhenCancelled()
    {
        var viewModel = Build(picker: new FakeFolderPicker { Result = null });

        viewModel.Folder = @"C:\Somewhere";
        viewModel.BrowseCommand.Execute(null);

        Assert.Equal(@"C:\Somewhere", viewModel.Folder);
    }

    /// <summary>The filter narrows what is drawn.</summary>
    [Fact]
    public async Task Filter_NarrowsTheVisibleRows()
    {
        var viewModel = Build(new FakeScanner(
            Scanned("kate.mp3", "Kate Bush", "Cloudbusting", "Hounds of Love"),
            Scanned("run.mp3", "AWOLNATION", "Run", "Run")));

        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.Filter = "kate";

        Assert.Single(viewModel.VisibleTracks);
        Assert.Equal("Cloudbusting", viewModel.VisibleTracks[0].Title);
    }

    /// <summary>It matches the file name too, not just the tags.</summary>
    /// <remarks>
    /// The row most in need of repair is routinely the one whose tags are wrong and whose name is
    /// the only true thing about it, so a filter over tags alone cannot reach it.
    /// </remarks>
    [Fact]
    public async Task Filter_MatchesTheFileName()
    {
        var viewModel = Build(new FakeScanner(
            Scanned("cloudbusting.mp3", "Wrong", "Wrong", "Wrong"),
            Scanned("run.mp3", "AWOLNATION", "Run", "Run")));

        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.Filter = "cloudbust";

        Assert.Single(viewModel.VisibleTracks);
    }

    /// <summary>A filter that hides a ticked row does not stop it being saved.</summary>
    /// <remarks>
    /// The discriminating test for the whole feature. Filtering is a way of looking at the list,
    /// not a way of choosing what Save writes — a filter that quietly reduced the write would
    /// silently lose work the user had already approved, and the tick box is the thing that says
    /// what Save touches.
    /// </remarks>
    [Fact]
    public async Task Filter_DoesNotChangeWhatSaveWrites()
    {
        var writer = new FakeWriter();
        var viewModel = Build(
            new FakeScanner(
                Scanned("kate.mp3", "Kate Bush", "Cloudbusting", "Hounds of Love"),
                Scanned("run.mp3", "AWOLNATION", "Run", "Run")),
            writer);

        await viewModel.ScanCommand.ExecuteAsync(null);

        foreach (var row in viewModel.Tracks) row.Album = "Edited";

        viewModel.Filter = "kate";

        await viewModel.SaveSelectedCommand.ExecuteAsync(null);

        Assert.Equal(2, writer.Saved.Count);
    }

    /// <summary>The counts describe the library, not the filter.</summary>
    [Fact]
    public async Task Filter_LeavesTheLibraryCountsAlone()
    {
        var viewModel = Build(new FakeScanner(
            Scanned("kate.mp3", "Kate Bush", "Cloudbusting", "Hounds of Love"),
            Scanned("run.mp3", "AWOLNATION", "Run", "Run")));

        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.Filter = "kate";

        Assert.Equal(2, viewModel.Tracks.Count);
        Assert.True(viewModel.HasTracks);
    }

    /// <summary>A filter matching nothing says so rather than looking like an empty scan.</summary>
    [Fact]
    public async Task Filter_ThatMatchesNothingIsDistinctFromAnEmptyScan()
    {
        var viewModel = Build(new FakeScanner(Scanned("run.mp3", "AWOLNATION", "Run", "Run")));

        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.Filter = "nothing here";

        Assert.Empty(viewModel.VisibleTracks);
        Assert.True(viewModel.HasNoMatches);
        Assert.True(viewModel.HasTracks);
    }

    /// <summary>Clearing the filter brings the list back.</summary>
    [Fact]
    public async Task Filter_ClearedShowsEverythingAgain()
    {
        var viewModel = Build(new FakeScanner(
            Scanned("kate.mp3", "Kate Bush", "Cloudbusting", "Hounds of Love"),
            Scanned("run.mp3", "AWOLNATION", "Run", "Run")));

        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.Filter = "kate";
        viewModel.Filter = string.Empty;

        Assert.Equal(2, viewModel.VisibleTracks.Count);
        Assert.False(viewModel.IsFiltered);
    }

    /// <summary>A manual search fills the row with results to choose from.</summary>
    [Fact]
    public async Task SearchMatches_OffersWhatSpotifyReturned()
    {
        var search = new FakeMatchSearch(
            Candidate("1", "Mr. Wendal", "Arrested Development", "3 Years", 1992),
            Candidate("2", "Mr. Wendal - Live", "Arrested Development", "Unplugged", 1993));

        var viewModel = Build(
            new FakeScanner(Scanned("a.mp3", "Wrong Artist", "Mr Wendal", null)),
            chain: new FakeChain(null) { MatchSearch = search });

        await viewModel.ScanCommand.ExecuteAsync(null);

        var row = viewModel.Tracks[0];
        row.MatchQuery = "arrested development mr wendal";

        await viewModel.SearchMatchesCommand.ExecuteAsync(row);

        Assert.Equal(2, row.Candidates.Count);
        Assert.True(row.HasCandidates);
        Assert.Equal("arrested development mr wendal", search.LastQuery);
    }

    /// <summary>
    /// Choosing a result rewrites the row, including over a wrong artist.
    /// </summary>
    /// <remarks>
    /// The case nothing else on the page can reach. Auto-fetch skips a row with all three fields
    /// filled in, and re-fetch searches on those same fields and then refuses any result whose
    /// artist disagrees with them — so a file confidently tagged with the wrong artist is exactly
    /// the file the automatic path can never correct, however many times it is run.
    /// </remarks>
    [Fact]
    public async Task UseMatch_RewritesTheRowFromTheChosenResult()
    {
        var search = new FakeMatchSearch(
            Candidate("1", "Mr. Wendal", "Arrested Development", "3 Years", 1992));

        var viewModel = Build(
            new FakeScanner(Scanned("a.mp3", "Wrong Artist", "Mr Wendal", "Wrong Album")),
            chain: new FakeChain(null) { MatchSearch = search });

        await viewModel.ScanCommand.ExecuteAsync(null);

        var row = viewModel.Tracks[0];
        row.MatchQuery = "arrested development mr wendal";

        await viewModel.SearchMatchesCommand.ExecuteAsync(row);
        await viewModel.UseMatchCommand.ExecuteAsync(row.Candidates[0]);

        Assert.Equal("Mr. Wendal", row.Title);
        Assert.Equal("Arrested Development", row.Artist);
        Assert.Equal("3 Years", row.Album);
        Assert.Equal(LibraryTrackStatus.Fetched, row.Status);
        Assert.True(row.HasPendingChanges);
    }

    /// <summary>The results close once one has been taken.</summary>
    [Fact]
    public async Task UseMatch_ClearsTheResultsAfterwards()
    {
        var search = new FakeMatchSearch(
            Candidate("1", "Mr. Wendal", "Arrested Development", "3 Years", 1992));

        var viewModel = Build(
            new FakeScanner(Scanned("a.mp3", "A", "T", null)),
            chain: new FakeChain(null) { MatchSearch = search });

        await viewModel.ScanCommand.ExecuteAsync(null);

        var row = viewModel.Tracks[0];
        row.MatchQuery = "anything";

        await viewModel.SearchMatchesCommand.ExecuteAsync(row);
        await viewModel.UseMatchCommand.ExecuteAsync(row.Candidates[0]);

        Assert.Empty(row.Candidates);
        Assert.False(row.HasCandidates);
    }

    /// <summary>A search that matched nothing says so on the row.</summary>
    [Fact]
    public async Task SearchMatches_SaysWhenNothingMatched()
    {
        var viewModel = Build(
            new FakeScanner(Scanned("a.mp3", "A", "T", null)),
            chain: new FakeChain(null) { MatchSearch = new FakeMatchSearch() });

        await viewModel.ScanCommand.ExecuteAsync(null);

        var row = viewModel.Tracks[0];
        row.MatchQuery = "nothing at all";

        await viewModel.SearchMatchesCommand.ExecuteAsync(row);

        Assert.Empty(row.Candidates);
        Assert.Equal(Strings.MetadataSearchNoResults, row.SearchMessage);
    }

    /// <summary>Spotify's own words reach the row when a search fails.</summary>
    [Fact]
    public async Task SearchMatches_SurfacesTheProvidersMessage()
    {
        var search = new FakeMatchSearch { Failure = "Spotify refused the request: quota exceeded." };

        var viewModel = Build(
            new FakeScanner(Scanned("a.mp3", "A", "T", null)),
            chain: new FakeChain(null) { MatchSearch = search });

        await viewModel.ScanCommand.ExecuteAsync(null);

        var row = viewModel.Tracks[0];
        row.MatchQuery = "anything";

        await viewModel.SearchMatchesCommand.ExecuteAsync(row);

        Assert.Equal("Spotify refused the request: quota exceeded.", row.SearchMessage);
        Assert.False(row.IsSearching);
    }

    /// <summary>Without a Spotify sign-in the row says what is missing.</summary>
    /// <remarks>
    /// Last.fm cannot stand in here. Its lookup answers a question about a named artist and
    /// title — it has no results to offer someone who does not yet know what the track is called.
    /// </remarks>
    [Fact]
    public async Task SearchMatches_SaysWhenSpotifyIsNotSignedIn()
    {
        var viewModel = Build(
            new FakeScanner(Scanned("a.mp3", "A", "T", null)),
            chain: new FakeChain(null));

        await viewModel.ScanCommand.ExecuteAsync(null);

        var row = viewModel.Tracks[0];
        row.MatchQuery = "anything";

        await viewModel.SearchMatchesCommand.ExecuteAsync(row);

        Assert.Equal(Strings.MetadataSearchNeedsSpotify, row.SearchMessage);
        Assert.Empty(row.Candidates);
    }

    private static LibraryMatchCandidate Candidate(
        string id,
        string title,
        string artist,
        string album,
        int? year) =>
        new(id, title, artist, album, year, null);

    private static MetadataViewModel Build(
        ILibraryScanner? scanner = null,
        ILibraryTagWriter? writer = null,
        ILibraryMetadataChain? chain = null,
        IFolderPicker? picker = null,
        SettingsDocument? document = null) =>
        new(scanner ?? new FakeScanner(),
            writer ?? new FakeWriter(),
            chain ?? new FakeChain(new CountingProvider { Result = true }),
            picker ?? new FakeFolderPicker(),
            document ?? SettingsFakes.Document());

    private static LibraryTrack Scanned(string name, string artist, string title, string? album) =>
        new($@"C:\Music\{name}", new Track { Artist = artist, Title = title, Album = album });

    private sealed class FakeScanner(params LibraryTrack[] tracks) : ILibraryScanner
    {
        private readonly int _skippedWaveFiles;

        public FakeScanner(LibraryTrack[] tracks, int skippedWaveFiles)
            : this(tracks) => _skippedWaveFiles = skippedWaveFiles;

        public Task<LibraryScan> ScanAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LibraryScan(tracks, _skippedWaveFiles, []));
    }

    private sealed class FakeWriter : ILibraryTagWriter
    {
        public List<string> Saved { get; } = [];

        public string? Failure { get; init; }

        public Task<LibraryWriteResult> SaveAsync(LibraryTrack track, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) return Task.FromResult(LibraryWriteResult.Failed(Failure));

            Saved.Add(track.Path);

            return Task.FromResult(LibraryWriteResult.Success);
        }
    }

    private sealed class FakeChain(IMetadataProvider? provider) : ILibraryMetadataChain
    {
        public ILibraryMatchSearch? MatchSearch { get; init; }

        public FallbackMetadataProvider Create() =>
            provider is null ? new FallbackMetadataProvider() : new FallbackMetadataProvider(provider);

        public ILibraryMatchSearch? CreateMatchSearch() => MatchSearch;
    }

    /// <summary>A manual search that answers with whatever it was handed.</summary>
    private sealed class FakeMatchSearch(params LibraryMatchCandidate[] results) : ILibraryMatchSearch
    {
        public string? LastQuery { get; private set; }

        public LibraryMatchCandidate? Applied { get; private set; }

        public string? Failure { get; init; }

        public Task<IReadOnlyList<LibraryMatchCandidate>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;

            if (Failure is not null) throw new MetadataLookupException(Failure);

            return Task.FromResult<IReadOnlyList<LibraryMatchCandidate>>(results);
        }

        public Task ApplyAsync(
            Track track,
            LibraryMatchCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            Applied = candidate;

            track.SetTitleFromApi(candidate.Title);
            track.SetArtistFromApi(candidate.Artist);
            track.Album = candidate.Album;
            track.Year = candidate.Year;

            return Task.CompletedTask;
        }
    }

    private sealed class CountingProvider : IMetadataProvider
    {
        public MetadataProvider Kind => MetadataProvider.Spotify;

        public bool Result { get; init; }

        public Exception? Throws { get; init; }

        public string? FetchedTitle { get; init; }

        public string? FetchedArtist { get; init; }

        /// <summary>Answers with an empty genre list and no year, the way a thin match does.</summary>
        public bool BlanksGenreAndYear { get; init; }

        public int Calls { get; private set; }

        public Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;

            if (Throws is not null) return Task.FromException<bool>(Throws);

            // The API setters, because that is how every real provider writes a match.
            track.SetTitleFromApi(FetchedTitle);
            track.SetArtistFromApi(FetchedArtist);

            if (BlanksGenreAndYear)
            {
                track.Genres = [];
                track.Year = null;
            }

            return Task.FromResult(Result);
        }
    }
}
