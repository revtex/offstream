using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Xunit;

namespace Offstream.Core.Tests.Naming;

/// <summary>
/// Ported from the reference suite's <c>FileNameTemplateTests</c>, assertions unchanged.
/// </summary>
/// <remarks>
/// The original's <c>FromLegacySettings_ReproducesOldLayout</c> cases are intentionally
/// absent: that helper rebuilt a template from the predecessor's pre-template settings, and
/// Offstream ships no settings importer (plan §6), so nothing can supply its inputs.
/// </remarks>
public sealed class FileNameTemplateTests
{
    private static readonly DateTime Now = new(2021, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

    private static Track FullTrack => new()
    {
        Artist = "Artist",
        Title = "Title",
        Album = "Album",
        AlbumArtists = ["Album Artist"],
        AlbumPosition = 4,
        Disc = 2,
        Year = 1999,
    };

    private static (string[] Folders, string Name) Render(string template, Track? track = null, int? counter = null) =>
        FileNameTemplate.Render(template, track ?? FullTrack, counter, Now, -1, -1);

    [Fact]
    public void Render_DefaultTemplate_ReturnsArtistAndTitle()
    {
        var (folders, name) = Render(FileNameTemplate.Default);

        Assert.Empty(folders);
        Assert.Equal("Album Artist - Title", name);
    }

    [Fact]
    public void Render_BackslashCreatesFolders()
    {
        var (folders, name) = Render(@"{artist}\{album}\{title}");

        Assert.Equal(["Album Artist", "Album"], folders);
        Assert.Equal("Title", name);
    }

    [Fact]
    public void Render_PadsNumbers()
    {
        var (_, name) = Render("{track:00} - {title}");

        Assert.Equal("04 - Title", name);
    }

    [Fact]
    public void Render_PadsCounter()
    {
        var (_, name) = Render("{count:000} {title}", counter: 7);

        Assert.Equal("007 Title", name);
    }

    [Fact]
    public void Render_DropsEmptySegments()
    {
        var track = FullTrack;
        track.Album = null;

        var (folders, name) = Render(@"{artist}\{album}\{title}", track);

        // No album means no empty directory level.
        Assert.Equal(["Album Artist"], folders);
        Assert.Equal("Title", name);
    }

    [Fact]
    public void Render_TidiesSeparatorLeftByAnEmptyToken()
    {
        var track = FullTrack;
        track.AlbumPosition = null;

        var (_, name) = Render("{track:00} - {title}", track);

        Assert.Equal("Title", name);
    }

    [Fact]
    public void Render_RemovesEmptyParentheses()
    {
        var track = FullTrack;
        track.Year = null;

        var (folders, _) = Render(@"{album} ({year})\{title}", track);

        Assert.Equal(["Album"], folders);
    }

    [Fact]
    public void Render_UnknownTokenRendersEmpty()
    {
        var (_, name) = Render("{title}{nope}");

        Assert.Equal("Title", name);
    }

    [Fact]
    public void Render_FormatsDateAndTime()
    {
        var (_, name) = Render("{date} {time} {title}");

        Assert.Equal("2021-03-04 050607 Title", name);
    }

    [Fact]
    public void Render_StripsInvalidFileNameCharacters()
    {
        var track = FullTrack;
        track.Title = "A/B:C*D";

        var (_, name) = Render("{title}", track);

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
    }

    [Theory]
    [InlineData("{count:000}", 999)]
    [InlineData("{count:00}", 99)]
    [InlineData("{count}", int.MaxValue)]
    [InlineData("{title}", int.MaxValue)]
    public void GetCounterMax_DerivesFromPadding(string template, int expected) =>
        Assert.Equal(expected, FileNameTemplate.GetCounterMax(template));

    [Theory]
    [InlineData("{count:000} {title}", true)]
    [InlineData("{artist} - {title}", false)]
    public void UsesCounter_DetectsToken(string template, bool expected) =>
        Assert.Equal(expected, FileNameTemplate.UsesCounter(template));

    [Theory]
    [InlineData(@"{artist} - {title}")]
    [InlineData(@"{artist}\{album}\{track:00} {title}")]
    public void Validate_AcceptsUsableTemplates(string template) =>
        Assert.Null(FileNameTemplate.Validate(template));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData(@"C:\music\{title}", "relative")]
    [InlineData(@"\{title}", "relative")]
    [InlineData("{nope} - {title}", "Unknown")]
    [InlineData("{artist} - {title}?", "not allowed")]
    [InlineData(@"{artist}\literal", "at least one token")]
    public void Validate_RejectsBadTemplates(string template, string expectedFragment)
    {
        var error = FileNameTemplate.Validate(template);

        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{count:000}", "000")]
    [InlineData("{count:00}", "00")]
    [InlineData("{count}", "000")]
    [InlineData("{title}", "000")]
    public void GetCounterMask_DerivesFromPadding(string template, string expected) =>
        Assert.Equal(expected, FileNameTemplate.GetCounterMask(template));

    [Fact]
    public void Render_ThrowsWhenEverySegmentIsEmpty()
    {
        var track = new Track();

        Assert.Throws<InvalidOperationException>(() => Render("{album}", track));
    }
}
