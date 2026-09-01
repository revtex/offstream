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
    [Fact]
    public async Task Write_ThenRead_RoundTripsEveryField()
    {
        var path = await EncodeAsync(MediaFormat.Mp3);

        _store.Write(path, new Track
        {
            Title = "The Mother We Share",
            Artist = "Chvrches",
            Album = "The Bones of What You Believe",
            Year = 2013,
            Genres = ["synthpop"],
        }, coverArt: null);

        var read = _store.Read(path);

        Assert.Equal("The Mother We Share", read.Title);
        Assert.Equal("Chvrches", read.Artist);
        Assert.Equal("The Bones of What You Believe", read.Album);
        Assert.Equal(2013, read.Year);
        Assert.Equal(["synthpop"], read.Genres!);
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
