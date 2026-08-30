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
        public FallbackMetadataProvider Create() =>
            provider is null ? new FallbackMetadataProvider() : new FallbackMetadataProvider(provider);
    }

    private sealed class CountingProvider : IMetadataProvider
    {
        public MetadataProvider Kind => MetadataProvider.Spotify;

        public bool Result { get; init; }

        public Exception? Throws { get; init; }

        public string? FetchedTitle { get; init; }

        public string? FetchedArtist { get; init; }

        public int Calls { get; private set; }

        public Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;

            if (Throws is not null) return Task.FromException<bool>(Throws);

            // The API setters, because that is how every real provider writes a match.
            track.SetTitleFromApi(FetchedTitle);
            track.SetArtistFromApi(FetchedArtist);

            return Task.FromResult(Result);
        }
    }
}
