using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Offstream.App.Resources;
using Offstream.Core.Metadata.Library;
using Serilog;

namespace Offstream.App.ViewModels;

/// <summary>One row of the Metadata page.</summary>
/// <remarks>
/// <para>
/// Edits land on <see cref="LibraryTrack.Suggested"/> as they are typed, never on the file. The
/// row is a proposal until Save runs, which is what makes it safe to fetch over a whole folder
/// and then change your mind about all of it.
/// </para>
/// <para>
/// The "current" columns are captured once in the constructor rather than read from
/// <see cref="LibraryTrack.Existing"/> on demand. They describe the file as it was found, and a
/// row that has just been saved should still show what it changed *from* — otherwise the before
/// and after columns become identical the instant the save succeeds, and the user loses the only
/// evidence of what the page did.
/// </para>
/// </remarks>
public sealed partial class LibraryTrackViewModel : ObservableValidator
{
    private readonly LibraryTrack _track;
    private readonly string? _existingTitle;
    private readonly string? _existingArtist;
    private readonly string? _existingAlbum;
    private readonly string? _existingAlbumArtist;
    private readonly string? _existingGenres;
    private readonly string? _existingYear;
    private readonly string? _existingTrackNumber;
    private readonly string? _existingTrackCount;
    private readonly string? _existingDisc;
    private readonly string? _existingCopyright;

    /// <summary>Wraps a scanned file for display.</summary>
    public LibraryTrackViewModel(LibraryTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        _track = track;

        FileName = track.FileName;
        Path = track.Path;

        // The raw values drive the comparisons; the Current* ones are for display and carry a
        // placeholder where the file had nothing, which would never compare equal to anything.
        _existingTitle = track.Existing.Title;
        _existingArtist = track.Existing.Artist;
        _existingAlbum = track.Existing.Album;
        _existingAlbumArtist = List(track.Existing.AlbumArtists);
        _existingGenres = List(track.Existing.Genres);
        _existingYear = Number(track.Existing.Year);
        _existingTrackNumber = Number(track.Existing.AlbumPosition);
        _existingTrackCount = Number(track.Existing.AlbumTrackCount);
        _existingDisc = Number(track.Existing.Disc);
        _existingCopyright = track.Existing.Copyright;

        CurrentTitle = Or(track.Existing.Title, Strings.MetadataNoValue);
        CurrentArtist = Or(track.Existing.Artist, Strings.MetadataNoValue);
        CurrentAlbum = Or(track.Existing.Album, Strings.MetadataNoValue);
        CurrentAlbumArtist = Or(_existingAlbumArtist, Strings.MetadataNoValue);
        CurrentGenres = Or(_existingGenres, Strings.MetadataNoValue);
        CurrentYear = Or(_existingYear, Strings.MetadataNoValue);
        CurrentTrackNumber = Or(_existingTrackNumber, Strings.MetadataNoValue);
        CurrentTrackCount = Or(_existingTrackCount, Strings.MetadataNoValue);
        CurrentDisc = Or(_existingDisc, Strings.MetadataNoValue);
        CurrentCopyright = Or(_existingCopyright, Strings.MetadataNoValue);

        _title = track.Suggested.Title ?? string.Empty;
        _artist = track.Suggested.Artist ?? string.Empty;
        _album = track.Suggested.Album ?? string.Empty;
        _albumArtist = List(track.Suggested.AlbumArtists) ?? string.Empty;
        _genres = List(track.Suggested.Genres) ?? string.Empty;
        _year = Number(track.Suggested.Year) ?? string.Empty;
        _trackNumber = Number(track.Suggested.AlbumPosition) ?? string.Empty;
        _trackCount = Number(track.Suggested.AlbumTrackCount) ?? string.Empty;
        _disc = Number(track.Suggested.Disc) ?? string.Empty;
        _copyright = track.Suggested.Copyright ?? string.Empty;
        _status = track.Status;

        CoverArt = LoadCoverArt(track.Existing.AlbumArtImage);
        ExistingCoverArt = CoverArt;
    }

    /// <summary>The file's own name, which is what identifies the row.</summary>
    public string FileName { get; }

    /// <summary>Full path, shown as the row's tooltip so nested folders stay distinguishable.</summary>
    public string Path { get; }

    /// <summary>The picture the file carried when it was scanned, kept for the before-and-after.</summary>
    /// <remarks>
    /// <see cref="CoverArt"/> is replaced when a match brings a different picture, because the
    /// row's thumbnail shows what would be written. This one is not, so the two can be shown side
    /// by side — which is the only way artwork changing is visible at all.
    /// </remarks>
    public BitmapImage? ExistingCoverArt { get; }

    /// <summary>What the file carried when it was scanned.</summary>
    public string CurrentTitle { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentArtist { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentAlbum { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentAlbumArtist { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentYear { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentGenres { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentTrackNumber { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentTrackCount { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentDisc { get; }

    /// <inheritdoc cref="CurrentTitle" />
    public string CurrentCopyright { get; }

    /// <summary>The scanned track this row edits.</summary>
    internal LibraryTrack Track => _track;

    /// <summary>Whether Save should include this row.</summary>
    /// <remarks>
    /// Defaults to true so the common case — scan, fetch, save the lot — takes no clicking. A row
    /// nothing changed is skipped by the writer regardless of this, so leaving everything ticked
    /// costs nothing.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>Cover art to preview, from the file or from a fetched suggestion.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuggestedCoverArtSource))]
    private BitmapImage? _coverArt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private LibraryTrackStatus _status;

    /// <summary>Why the row failed, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private string? _failureReason;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(HasTitleChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [Required(ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.MetadataTitleRequired))]
    private string _title;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(HasArtistChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [Required(ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.MetadataArtistRequired))]
    private string _artist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(HasAlbumChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private string _album;

    /// <summary>Who the album is filed under, which is not always who performed the track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbumArtistChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private string _albumArtist;

    /// <summary>Genres, comma-separated, because every container stores a list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenreChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private string _genres;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(HasYearChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [CustomValidation(typeof(LibraryTrackViewModel), nameof(ValidateYear))]
    private string _year;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(HasTrackNumberChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [CustomValidation(typeof(LibraryTrackViewModel), nameof(ValidateCount))]
    private string _trackNumber;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(HasTrackCountChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [CustomValidation(typeof(LibraryTrackViewModel), nameof(ValidateCount))]
    private string _trackCount;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(HasDiscChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [CustomValidation(typeof(LibraryTrackViewModel), nameof(ValidateCount))]
    private string _disc;

    /// <summary>The copyright line. Filled by a match far more often than it is typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyrightChange))]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private string _copyright;

    /// <summary>The status, translated.</summary>
    public string StatusText => Status switch
    {
        LibraryTrackStatus.Untagged => Strings.MetadataStatusUntagged,
        LibraryTrackStatus.Matched => Strings.MetadataStatusMatched,
        LibraryTrackStatus.Fetching => Strings.MetadataStatusFetching,
        LibraryTrackStatus.Fetched => Strings.MetadataStatusFetched,
        LibraryTrackStatus.Saved => Strings.MetadataStatusSaved,
        _ => Strings.MetadataStatusFailed,
    };

    /// <summary>Whether to show the failure line.</summary>
    public bool HasFailed => Status == LibraryTrackStatus.Failed && !string.IsNullOrWhiteSpace(FailureReason);

    /// <summary>The artist and album on one line, under the title.</summary>
    /// <remarks>
    /// The collapsed row shows what *will be written*, because that is the thing being approved.
    /// Artist and album share a line because they are read together — "who, and off what" — and
    /// giving each its own would make the row half as dense for no gain in legibility.
    /// </remarks>
    public string Summary => string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} · {Album}";

    /// <summary>Whether saving this row would change anything on disk.</summary>
    /// <remarks>
    /// Shown in the collapsed row so the rows Save will actually touch can be picked out without
    /// opening every one of them. A saved row stops advertising it: <see cref="LibraryTrack.Existing"/>
    /// is deliberately never updated, so the underlying comparison stays true forever and the
    /// status is what carries the news afterwards. Editing a row after saving it therefore has to
    /// clear that status — see <see cref="MarkEdited"/>.
    /// </remarks>
    public bool HasPendingChanges => Status != LibraryTrackStatus.Saved && _track.HasChanges;

    /// <summary>Whether the title differs from the one in the file.</summary>
    /// <remarks>
    /// Drives the "was …" line under each box. Only a field that actually changed gets one: a
    /// before-and-after on every row is three extra lines of text that say nothing three times
    /// out of four, and it buries the one line that matters.
    /// </remarks>
    public bool HasTitleChange => Differs(_existingTitle, Title);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasArtistChange => Differs(_existingArtist, Artist);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasAlbumChange => Differs(_existingAlbum, Album);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasAlbumArtistChange => Differs(_existingAlbumArtist, AlbumArtist);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasGenreChange => Differs(_existingGenres, Genres);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasYearChange => Differs(_existingYear, Year);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasTrackNumberChange => Differs(_existingTrackNumber, TrackNumber);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasTrackCountChange => Differs(_existingTrackCount, TrackCount);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasDiscChange => Differs(_existingDisc, Disc);

    /// <inheritdoc cref="HasTitleChange" />
    public bool HasCopyrightChange => Differs(_existingCopyright, Copyright);

    /// <summary>Whether saving would put a different picture in the file.</summary>
    public bool HasCoverArtChange => _track.CoverArtWouldChange;

    /// <summary>The artwork that would actually be written, however it is going to get there.</summary>
    /// <remarks>
    /// <para>
    /// Returns either a decoded picture or a <see cref="Uri"/>, and WPF's <c>Image.Source</c>
    /// accepts both — the second is left for the framework to fetch rather than downloaded here,
    /// which keeps the network off the view model and off whatever thread a fetch happened to
    /// finish on.
    /// </para>
    /// <para>
    /// Both cases are real. A lookup that finds artwork usually hands back a URL and no bytes,
    /// and the writer downloads it at save time — so binding the thumbnail to the decoded image
    /// alone showed the artwork the file already had while claiming to show what saving would do.
    /// After a hand-picked match, where the old picture is dropped deliberately, that meant the
    /// before-and-after displayed the replaced track's sleeve on both sides.
    /// </para>
    /// </remarks>
    public object? SuggestedCoverArtSource
    {
        get
        {
            if (_track.Suggested.AlbumArtImage is { Length: > 0 }) return CoverArt;

            return Uri.TryCreate(_track.Suggested.AlbumArtUrl, UriKind.Absolute, out var url)
                ? url
                : CoverArt;
        }
    }


    /// <summary>Pulls a fetched suggestion back onto the row.</summary>
    /// <remarks>
    /// Called after a provider has enriched the underlying track. The editable fields are
    /// overwritten because the whole point of a fetch is to replace the guess — and every field
    /// the writer touches now has a box, so there is nothing a provider can change that the page
    /// does not show.
    /// </remarks>
    public void RefreshFromSuggestion()
    {
        Title = _track.Suggested.Title ?? string.Empty;
        Artist = _track.Suggested.Artist ?? string.Empty;
        Album = _track.Suggested.Album ?? string.Empty;
        AlbumArtist = List(_track.Suggested.AlbumArtists) ?? string.Empty;
        Genres = List(_track.Suggested.Genres) ?? string.Empty;
        Year = Number(_track.Suggested.Year) ?? string.Empty;
        TrackNumber = Number(_track.Suggested.AlbumPosition) ?? string.Empty;
        TrackCount = Number(_track.Suggested.AlbumTrackCount) ?? string.Empty;
        Disc = Number(_track.Suggested.Disc) ?? string.Empty;
        Copyright = _track.Suggested.Copyright ?? string.Empty;

        Status = _track.Status;
        FailureReason = _track.FailureReason;

        if (_track.Suggested.AlbumArtImage is { Length: > 0 } image)
        {
            CoverArt = LoadCoverArt(image);
        }

        // A fetch that confirmed what the file already said changes no field, so nothing above
        // raised a notification - but it may still have brought cover art.
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(HasCoverArtChange));
        OnPropertyChanged(nameof(SuggestedCoverArtSource));
    }

    /// <summary>
    /// A typed correction outranks a fetched one, which takes both of <see cref="Track"/>'s tiers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Track"/> keeps two values per text field — the scraped guess and the provider's
    /// answer — and the getter returns the provider's whenever it has one. That is the right rule
    /// while a provider is filling gaps, and the wrong one the moment a person corrects the
    /// provider: writing only the ordinary setter leaves the fetched value in front of the edit,
    /// so a correction typed over a wrong match is accepted by the box, shown in the grid, and
    /// then discarded at the point of writing. Both tiers are set so the edit wins whether or not
    /// anything was fetched first.
    /// </para>
    /// <para>
    /// <c>Album</c> needs only the one, because it is an ordinary property with no API tier.
    /// </para>
    /// </remarks>
    partial void OnTitleChanged(string value)
    {
        _track.Suggested.Title = value;
        _track.Suggested.SetTitleFromApi(value);
        MarkEdited();
    }

    /// <inheritdoc cref="OnTitleChanged" />
    partial void OnArtistChanged(string value)
    {
        _track.Suggested.Artist = value;
        _track.Suggested.SetArtistFromApi(value);
        MarkEdited();
    }

    /// <inheritdoc cref="OnTitleChanged" />
    partial void OnAlbumChanged(string value)
    {
        _track.Suggested.Album = value;
        MarkEdited();
    }

    /// <inheritdoc cref="OnTitleChanged" />
    partial void OnAlbumArtistChanged(string value)
    {
        _track.Suggested.AlbumArtists = AsList(value, _track.Suggested.AlbumArtists);
        MarkEdited();
    }

    /// <inheritdoc cref="OnTitleChanged" />
    partial void OnGenresChanged(string value)
    {
        _track.Suggested.Genres = SplitList(value);
        MarkEdited();
    }

    /// <summary>
    /// A number the user is halfway through typing is not a reason to throw the old one away.
    /// </summary>
    /// <remarks>
    /// The boxes update on every keystroke, so "19" is a state every four-digit year passes
    /// through. Writing whatever parses would put 19 into the track and then 198 and then 1984,
    /// which is harmless — but a value that does <i>not</i> parse has to leave the field alone
    /// rather than null it, or backspacing over a year to retype it clears the tag underneath and
    /// the validation message that says so never gets a chance to stop the save.
    /// </remarks>
    partial void OnYearChanged(string value) =>
        SetNumber(value, number => _track.Suggested.Year = number);

    /// <inheritdoc cref="OnYearChanged" />
    partial void OnTrackNumberChanged(string value) =>
        SetNumber(value, number => _track.Suggested.AlbumPosition = number);

    /// <inheritdoc cref="OnYearChanged" />
    partial void OnTrackCountChanged(string value) =>
        SetNumber(value, number => _track.Suggested.AlbumTrackCount = number);

    /// <inheritdoc cref="OnYearChanged" />
    partial void OnDiscChanged(string value) =>
        SetNumber(value, number => _track.Suggested.Disc = number);

    /// <inheritdoc cref="OnTitleChanged" />
    partial void OnCopyrightChanged(string value)
    {
        _track.Suggested.Copyright = value;
        MarkEdited();
    }

    /// <summary>Assigns a numeric box, leaving the tag alone while the value is unusable.</summary>
    private void SetNumber(string value, Action<int?> assign)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            assign(null);
        }
        else if (ParseNumber(value) is { } number)
        {
            assign(number);
        }

        MarkEdited();
    }

    /// <summary>A year is four digits, and everything Offstream can tag was recorded after 1000.</summary>
    /// <remarks>
    /// Empty passes, here and in every numeric box on the page. A file that has never had a track
    /// number is not in error for still not having one, and the writer leaves an empty value
    /// alone rather than clearing the file's own.
    /// </remarks>
    public static ValidationResult? ValidateYear(string? value, ValidationContext context) =>
        string.IsNullOrWhiteSpace(value) || ParseNumber(value) is >= 1000 and <= 9999
            ? ValidationResult.Success
            : new ValidationResult(Strings.MetadataYearInvalid);

    /// <inheritdoc cref="ValidateYear" />
    public static ValidationResult? ValidateCount(string? value, ValidationContext context) =>
        string.IsNullOrWhiteSpace(value) || ParseNumber(value) is not null
            ? ValidationResult.Success
            : new ValidationResult(Strings.MetadataNumberInvalid);

    /// <summary>Takes a row back out of the saved state when it is edited again.</summary>
    /// <remarks>
    /// Save is what makes a row Saved, and "Saved" is also what suppresses the "will change"
    /// badge — so without this, spotting a typo in a row you have just written leaves the badge
    /// dark on the one row that now needs saving again. The write itself was always correct
    /// (<c>LibraryTagWriter</c> re-checks the comparison), which is exactly what made it worth
    /// fixing: the page was telling the user there was nothing to do while there was.
    /// </remarks>
    private void MarkEdited()
    {
        if (Status == LibraryTrackStatus.Saved)
        {
            Status = LibraryTrackStatus.Fetched;
        }
    }

    /// <summary>Results of the last manual search on this row.</summary>
    public ObservableCollection<LibraryMatchViewModel> Candidates { get; } = [];

    /// <summary>What to search the catalogue for.</summary>
    /// <remarks>
    /// Seeded from the row rather than left blank, because the query the user wants is almost
    /// always a correction of the one already there — an artist misspelt, a featured act that
    /// belongs in the title, a remaster suffix to drop.
    /// </remarks>
    [ObservableProperty]
    private string _matchQuery = string.Empty;

    /// <summary>Whether a manual search is running on this row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSearching))]
    private bool _isSearching;

    /// <summary>Whether the row's search button is available.</summary>
    public bool IsNotSearching => !IsSearching;

    /// <summary>Why the last search returned nothing, or null.</summary>
    [ObservableProperty]
    private string? _searchMessage;

    /// <summary>Whether there are results to choose from.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>Announces that <see cref="Candidates"/> has been refilled.</summary>
    public void NotifyCandidatesChanged() => OnPropertyChanged(nameof(HasCandidates));

    /// <summary>Fills the search box from whatever the row says now.</summary>
    /// <remarks>
    /// Called when the row becomes the selected one, not from the constructor, so the box
    /// reflects a fetch or an edit that happened in between rather than the values the file
    /// carried when it was scanned.
    /// </remarks>
    public void SeedMatchQuery()
    {
        if (!string.IsNullOrWhiteSpace(MatchQuery)) return;

        MatchQuery = string.Join(' ', new[] { Artist, Title }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>A tag list as one editable line, or nothing at all.</summary>
    private static string? List(string[]? values) =>
        values is { Length: > 0 } ? string.Join(", ", values) : null;

    /// <summary>The inverse of <see cref="List"/>. Blank entries are dropped, not stored empty.</summary>
    private static string[]? SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A name list edited as one line, without reading the commas inside a name.</summary>
    /// <remarks>
    /// Artist names contain commas — "Earth, Wind &amp; Fire" is one band — so splitting this box
    /// the way <see cref="SplitList"/> splits genres would file that album under three artists,
    /// which is a worse corruption than the multi-value tag it was meant to preserve. The box is
    /// filled from <paramref name="current"/>, so text that still matches means nobody has typed
    /// over it and the list goes back untouched, however many values it holds; text that does not
    /// match is one name the user chose. Genres keep the split — a genre list really is a list,
    /// and commas inside a single genre are vanishingly rare.
    /// </remarks>
    private static string[]? AsList(string? value, string[]? current) =>
        string.IsNullOrWhiteSpace(value) ? null
            : string.Equals(List(current), value, StringComparison.Ordinal) ? current
            : [value.Trim()];

    private static string? Number(int? value) =>
        value is > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Reads a box back as a number, invariantly.
    /// </summary>
    /// <remarks>
    /// <see cref="NumberStyles.None"/> is what rejects "-3", "1,984" and " 12 " — a tag number is
    /// digits and nothing else, and a group separator that parses in one locale and fails in the
    /// next would make the same typed year valid or invalid depending on the machine.
    /// </remarks>
    private static int? ParseNumber(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0
            ? number
            : null;

    /// <summary>Whether a proposed value says something the file does not already say.</summary>
    /// <remarks>
    /// A file with no album at all and a box still empty is not a change, so a blank proposal
    /// against a blank original compares equal rather than reading as "cleared".
    /// </remarks>
    private static bool Differs(string? existing, string? proposed)
    {
        if (string.IsNullOrWhiteSpace(proposed)) return false;

        return !string.Equals(existing?.Trim(), proposed.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Decodes embedded art into something bindable, or nothing.</summary>
    /// <remarks>
    /// <c>OnLoad</c> plus <c>Freeze</c> is what lets the stream be disposed immediately and the
    /// image be handed between threads — a fetch runs off the UI thread and the result is bound
    /// on it. A file with a damaged picture frame is common enough to be worth swallowing: the
    /// row is still perfectly editable without a thumbnail.
    /// </remarks>
    private static BitmapImage? LoadCoverArt(byte[]? image)
    {
        if (image is null or { Length: 0 }) return null;

        try
        {
            using var stream = new MemoryStream(image);
            var bitmap = new BitmapImage();

            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (NotSupportedException ex)
        {
            Log.Debug(ex, "Could not decode embedded cover art.");

            return null;
        }
        catch (ArgumentException ex)
        {
            Log.Debug(ex, "Embedded cover art is not a readable image.");

            return null;
        }
    }
}
