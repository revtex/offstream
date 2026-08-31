using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Offstream.Core.Tests.Encoding;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>
/// Reading and writing tags on a file that already exists.
/// </summary>
/// <remarks>
/// <para>
/// Tagged <c>Ffmpeg</c> because the only honest way to test a tag writer is against a real
/// encoded file, and ffmpeg is what makes one. The assertions read the file back rather than
/// trusting the write — the same discipline as <see cref="CoverArtIntegrationTests"/>, and for
/// the same reason: the predecessor's Opus tagging looked correct for years because nothing
/// checked the finished file.
/// </para>
/// </remarks>
[Trait("Category", "Ffmpeg")]
public sealed class TagLibTagStoreTests : IDisposable
{
    private readonly EncodeWorkspace _workspace = new();
    private readonly TagLibTagStore _store = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>What is written comes back.</summary>
    /// <remarks>
    /// Every tag the recording path writes, because the page's promise is that a tag Offstream
    /// puts into a file it can also put right. A field that reads back as null here is one the
    /// user can type into a box and lose on the next scan.
    /// </remarks>
    [Fact]
    public async Task Write_ThenRead_RoundTripsEveryField()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track
        {
            Title = "The Mother We Share",
            Artist = "Chvrches",
            Album = "The Bones of What You Believe",
            AlbumArtists = ["Chvrches"],
            Year = 2013,
            Genres = ["synthpop"],
            AlbumPosition = 2,
            AlbumTrackCount = 12,
            Disc = 1,
            Copyright = "2013 Goodbye Records",
        }, coverArt: null);

        var read = _store.Read(path);

        Assert.Equal("The Mother We Share", read.Title);
        Assert.Equal("Chvrches", read.Artist);
        Assert.Equal("The Bones of What You Believe", read.Album);
        Assert.Equal(["Chvrches"], read.AlbumArtists!);
        Assert.Equal(2013, read.Year);
        Assert.Equal(["synthpop"], read.Genres!);
        Assert.Equal(2, read.AlbumPosition);
        Assert.Equal(12, read.AlbumTrackCount);
        Assert.Equal(1, read.Disc);
        Assert.Equal("2013 Goodbye Records", read.Copyright);
    }

    /// <summary>
    /// The numbers survive in every container the page will open.
    /// </summary>
    /// <remarks>
    /// Copyright is the field worth checking per container rather than once: it is the thinnest
    /// support of the set, stored in a different place by each of ID3, Vorbis comments and the
    /// MPEG-4 atom tree. A container that dropped it would give the user a box that accepts a
    /// value, reports it saved and shows it gone on the next scan.
    /// </remarks>
    [Theory]
    [InlineData(MediaFormat.Mp3)]
    [InlineData(MediaFormat.Flac)]
    [InlineData(MediaFormat.Aac)]
    [InlineData(MediaFormat.Opus)]
    public async Task Write_ThenRead_KeepsTheNumbersInEveryContainer(MediaFormat format)
    {
        var path = await EncodeAsync(format);

        _store.Write(path, new Track
        {
            Title = "Recover",
            Artist = "Chvrches",
            AlbumPosition = 4,
            AlbumTrackCount = 12,
            Disc = 2,
            Copyright = "2013 Goodbye Records",
        }, coverArt: null);

        var read = _store.Read(path);

        Assert.Equal(4, read.AlbumPosition);
        Assert.Equal(12, read.AlbumTrackCount);
        Assert.Equal(2, read.Disc);
        Assert.Equal("2013 Goodbye Records", read.Copyright);
    }

    /// <summary>
    /// An album artist the user typed outranks the one derived from the artist.
    /// </summary>
    /// <remarks>
    /// The writer fills the album artist in from the artist when the file has none, which is what
    /// stops a corrected track being filed under the old name. That fallback runs after the
    /// explicit write on purpose — the other order let it win, and a compilation retagged with
    /// "Various Artists" came back credited to whoever performed the track.
    /// </remarks>
    [Fact]
    public async Task Write_KeepsAnAlbumArtistThatDisagreesWithTheArtist()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track
        {
            Title = "Sabotage",
            Artist = "Beastie Boys",
            AlbumArtists = ["Various Artists"],
        }, coverArt: null);

        var read = _store.Read(path);

        Assert.Equal("Beastie Boys", read.Artist);
        Assert.Equal(["Various Artists"], read.AlbumArtists!);
    }

    /// <summary>
    /// A scan and a save with nothing edited must give the file back exactly the artist it had,
    /// however many values that tag holds.
    /// </summary>
    /// <remarks>
    /// ID3v2.3 separates artists with a slash, so a file recorded as "AC/DC" comes back as the
    /// two values "AC" and "DC". The page has one artist box, it is filled from the first of
    /// them, and writing that box alone used to narrow the tag to "AC" — the repair page
    /// destroying the tag it was opened to repair.
    /// </remarks>
    [Fact]
    public async Task Write_KeepsEveryArtistOnATagThatHoldsMoreThanOne()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track { Title = "Who Made Who", Artist = "AC", Performers = ["AC", "DC"] }, coverArt: null);

        var read = _store.Read(path);

        Assert.Equal(["AC", "DC"], read.Performers!);
        Assert.Equal("AC", read.Artist);
    }

    /// <summary>Typing over the box is the one thing that does collapse the list.</summary>
    [Fact]
    public async Task Write_ReplacesEveryArtistWhenTheArtistWasEdited()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track { Title = "Who Made Who", Artist = "Acca Dacca", Performers = ["AC", "DC"] }, coverArt: null);

        var read = _store.Read(path);

        Assert.Equal(["Acca Dacca"], read.Performers!);
    }

    /// <summary>An empty value leaves the file's own alone rather than erasing it.</summary>
    [Fact]
    public async Task Write_DoesNotClearATagItHasNothingToSayAbout()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track { Title = "Gun", Artist = "Chvrches", Disc = 3 }, coverArt: null);
        _store.Write(path, new Track { Title = "Gun", Artist = "Chvrches" }, coverArt: null);

        Assert.Equal(3, _store.Read(path).Disc);
    }

    /// <summary>
    /// <b>An MP3 stays ID3v2.3 after a rewrite.</b>
    /// </summary>
    /// <remarks>
    /// TagLib# writes v2.4 by default, and this project deliberately writes v2.3 — the version
    /// Windows Explorer, Windows Media Player and a long tail of car stereos actually read. A
    /// retag that silently upgraded the tag would make the tags disappear from exactly the places
    /// the user is most likely to look, on files that displayed correctly before Offstream
    /// touched them. That is a worse outcome than not tagging at all, and it would be invisible
    /// to every other assertion here.
    /// </remarks>
    [Fact]
    public async Task Write_KeepsMp3TagsAtId3v23()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track { Title = "T", Artist = "A", Album = "Al" }, coverArt: null);

        using var file = TagLib.File.Create(path);
        var id3v2 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);

        Assert.Equal(3, id3v2.Version);
    }

    /// <summary>Cover art is written in the same pass as the text, and reads back.</summary>
    [Fact]
    public async Task Write_EmbedsCoverArt()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);
        var image = await File.ReadAllBytesAsync(await _workspace.CreateCoverArtAsync());

        _store.Write(path, new Track { Title = "T", Artist = "A", Album = "Al" }, image);

        using var file = TagLib.File.Create(path);

        Assert.NotEmpty(file.Tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, file.Tag.Pictures[0].Type);
    }

    /// <summary>Ogg keeps its tags at the stream level, and TagLib# handles that container too.</summary>
    [Fact]
    public async Task Write_RoundTripsOpus()
    {
        var path = await EncodeAsync(MediaFormat.Opus);

        _store.Write(path, new Track { Title = "Opus Title", Artist = "Opus Artist", Album = "Opus Album" }, null);

        var read = _store.Read(path);

        Assert.Equal("Opus Title", read.Title);
        Assert.Equal("Opus Artist", read.Artist);
    }

    /// <summary>
    /// A locked file is a result, not an exception escaping to the caller.
    /// </summary>
    /// <remarks>
    /// The single most likely failure in real use: the user is listening to the track they are
    /// trying to retag. It has to name the cause, because Windows reports it as an
    /// <c>IOException</c> whose text mentions no application at all.
    /// </remarks>
    [Fact]
    public async Task Write_ToALockedFileFailsWithAReadableReason()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<LibraryTagException>(() =>
            _store.Write(path, new Track { Title = "T", Artist = "A", Album = "Al" }, null));

        Assert.Contains("in use", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reading a locked file fails the same readable way.</summary>
    [Fact]
    public async Task Read_OfALockedFileFailsWithAReadableReason()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<LibraryTagException>(() => _store.Read(path));
    }

    /// <summary>A file that is not audio is reported, not thrown out of.</summary>
    [Fact]
    public void Read_OfSomethingThatIsNotAudioIsReported()
    {
        var path = _workspace.PathTo("notes.mp3");

        File.WriteAllText(path, "this is not an MP3");

        Assert.Throws<LibraryTagException>(() => _store.Read(path));
    }

    /// <summary>
    /// A blank field leaves what the file already had.
    /// </summary>
    /// <remarks>
    /// The user clearing a box means "I have nothing to add", not "erase what is there". Treating
    /// it as an erase would let one empty field quietly strip a tag the file had all along.
    /// </remarks>
    [Fact]
    public async Task Write_LeavesExistingValuesWhenAFieldIsBlank()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track { Title = "T", Artist = "A", Album = "Original Album" }, null);
        _store.Write(path, new Track { Title = "T2", Artist = "A", Album = null }, null);

        Assert.Equal("Original Album", _store.Read(path).Album);
        Assert.Equal("T2", _store.Read(path).Title);
    }

    private async Task<string> EncodeAsync(MediaFormat format)
    {
        var source = await _workspace.CreateSourceWavAsync();
        var output = _workspace.PathFor(format, "tagged");

        await new AudioEncoder(_workspace.Runner).EncodeAsync(
            new EncodeRequest(source, output, format, 192, new Track
            {
                Artist = "Seed Artist",
                Title = "Seed Title",
            }, null));

        return output;
    }
}
