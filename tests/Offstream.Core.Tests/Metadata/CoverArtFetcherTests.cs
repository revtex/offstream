using System.IO.Abstractions.TestingHelpers;
using Offstream.Core.Metadata;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// Fetching the cover to a file, which is what both embedding routes need.
/// </summary>
/// <remarks>
/// ffmpeg takes the picture as a second input — a path — and <see cref="CoverArtWriter"/> reads
/// one too, so a single fetch to disk serves every container.
/// </remarks>
public sealed class CoverArtFetcherTests
{
    private static readonly byte[] Image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static Track WithArt(string? url) => new() { Artist = "Artist", Title = "Title", AlbumArtUrl = url };

    [Fact]
    public async Task FetchAsync_WritesTheImageToATempFile()
    {
        var fileSystem = new MockFileSystem();
        using var handler = StubHttpMessageHandler.Bytes(Image);
        using var httpClient = handler.Client();

        var path = await new CoverArtFetcher(httpClient, fileSystem)
            .FetchAsync(WithArt("https://example.invalid/300x300/cover.jpg"));

        Assert.NotNull(path);
        Assert.True(fileSystem.File.Exists(path));
        Assert.Equal(Image, await fileSystem.File.ReadAllBytesAsync(path));
    }

    /// <summary>
    /// <see cref="CoverArtWriter"/> derives the MIME type it writes into the picture frame from
    /// the extension, so dropping it would tag every PNG as a JPEG.
    /// </summary>
    [Theory]
    [InlineData("https://example.invalid/cover.png", ".png")]
    [InlineData("https://example.invalid/cover.jpg", ".jpg")]
    [InlineData("https://example.invalid/cover.jpeg", ".jpeg")]
    [InlineData("https://example.invalid/300x300/ab12cd", ".jpg")]
    public async Task FetchAsync_KeepsTheImageExtension(string url, string expected)
    {
        var fileSystem = new MockFileSystem();
        using var handler = StubHttpMessageHandler.Bytes(Image);
        using var httpClient = handler.Client();

        var path = await new CoverArtFetcher(httpClient, fileSystem).FetchAsync(WithArt(url));

        Assert.EndsWith(expected, path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_WithoutAnArtUrl_FetchesNothing()
    {
        var fileSystem = new MockFileSystem();
        using var handler = StubHttpMessageHandler.Bytes(Image);
        using var httpClient = handler.Client();

        Assert.Null(await new CoverArtFetcher(httpClient, fileSystem).FetchAsync(WithArt(null)));
        Assert.Empty(handler.Requests);
    }

    /// <summary>A provider that hands back something unusable must not be followed.</summary>
    [Theory]
    [InlineData("not a url")]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("ftp://example.invalid/cover.jpg")]
    public async Task FetchAsync_WithAnAddressThatIsNotHttp_FetchesNothing(string url)
    {
        var fileSystem = new MockFileSystem();
        using var handler = StubHttpMessageHandler.Bytes(Image);
        using var httpClient = handler.Client();

        Assert.Null(await new CoverArtFetcher(httpClient, fileSystem).FetchAsync(WithArt(url)));
        Assert.Empty(handler.Requests);
    }

    /// <summary>Art is worth nothing on its own; losing it must never cost the audio.</summary>
    [Fact]
    public async Task FetchAsync_WhenTheRequestFails_ReturnsNullRatherThanThrowing()
    {
        var fileSystem = new MockFileSystem();
        using var handler = StubHttpMessageHandler.Failing();
        using var httpClient = handler.Client();

        Assert.Null(await new CoverArtFetcher(httpClient, fileSystem)
            .FetchAsync(WithArt("https://example.invalid/cover.jpg")));
    }

    [Fact]
    public async Task FetchAsync_WhenTheResponseIsEmpty_ReturnsNull()
    {
        var fileSystem = new MockFileSystem();
        using var handler = StubHttpMessageHandler.Bytes([]);
        using var httpClient = handler.Client();

        Assert.Null(await new CoverArtFetcher(httpClient, fileSystem)
            .FetchAsync(WithArt("https://example.invalid/cover.jpg")));
    }

    /// <summary>
    /// A response this size is a redirect to something that is not an image; embedding it would
    /// bloat every file the session writes.
    /// </summary>
    [Fact]
    public async Task FetchAsync_WhenTheImageIsAbsurdlyLarge_RefusesIt()
    {
        var fileSystem = new MockFileSystem();
        using var handler = StubHttpMessageHandler.Bytes(new byte[CoverArtFetcher.MaximumBytes + 1]);
        using var httpClient = handler.Client();

        Assert.Null(await new CoverArtFetcher(httpClient, fileSystem)
            .FetchAsync(WithArt("https://example.invalid/cover.jpg")));
    }
}
