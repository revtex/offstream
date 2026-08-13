using System.Net;
using System.Net.Http;

// There is an Offstream.Core.Tests.Encoding namespace, which shadows System.Text.Encoding for
// anything in this assembly. Aliasing is clearer here than fully qualifying every use.
using TextEncoding = System.Text.Encoding;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// Answers requests from a script rather than from the network.
/// </summary>
/// <remarks>
/// Plan §9.3 is explicit that no unit test makes a network call — the reference suite had one
/// hitting the live Last.fm API, and it failed offline. Everything the providers do is HTTP, so
/// the seam has to be the handler.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Every request that was made, in order, for asserting what was asked for.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>Answers every request with the same XML body.</summary>
    public static StubHttpMessageHandler Xml(params string[] bodies)
    {
        var index = 0;

        return new StubHttpMessageHandler(_ =>
        {
            // The last body repeats, so a test that only cares about the first call does not have
            // to spell out how many follow.
            var body = bodies[Math.Min(index++, bodies.Length - 1)];

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, TextEncoding.UTF8, "text/xml"),
            };
        });
    }

    /// <summary>Answers every request with the same bytes, as an image would arrive.</summary>
    public static StubHttpMessageHandler Bytes(byte[] content, string mediaType = "image/jpeg") =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) },
            },
        });

    /// <summary>Fails every request the way an outage does.</summary>
    public static StubHttpMessageHandler Failing(HttpStatusCode status = HttpStatusCode.ServiceUnavailable) =>
        new(_ => new HttpResponseMessage(status));

    public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = null };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not null) Requests.Add(request.RequestUri);

        return Task.FromResult(_respond(request));
    }
}
