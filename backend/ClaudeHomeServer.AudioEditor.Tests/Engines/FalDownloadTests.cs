using System.Net;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Engines;

public sealed class FalDownloadTests
{
    private readonly Cdn _cdn = new();

    // evil.test резолвится во внутреннюю сеть, nowhere.test не резолвится, всё прочее — публичный адрес:
    // так localhost обязан отсекаться по имени, не спрашивая DNS
    private FalDownload Download(long maxBytes = FalDownload.DefaultMaxBytes) =>
        new(new HttpClient(_cdn, disposeHandler: false), (host, _) => Task.FromResult(host switch
        {
            "evil.test" => [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")],
            "nowhere.test" => [],
            _ => new[] { IPAddress.Parse("93.184.216.34") },
        })) { MaxBytes = maxBytes };

    [Theory]
    [InlineData("http://cdn.test/a.mp3")]
    [InlineData("http://cdn.test:443/a.mp3")]
    [InlineData("https://127.0.0.1/a.mp3")]
    [InlineData("https://10.0.0.1/a.mp3")]
    [InlineData("https://localhost/a.mp3")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/a.mp3")]
    [InlineData("https://cdn.test:8443/a.mp3")]
    [InlineData("https://evil.test/a.mp3")]
    [InlineData("https://nowhere.test/a.mp3")]
    public async Task UnsafeUrl_RefusedByValue_WithoutRequest(string url)
    {
        var got = await Download().GetAsync(url, CancellationToken.None);

        got.File.Should().BeNull();
        got.Error.Should().NotBeNullOrEmpty();
        _cdn.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PublicUrl_Downloaded()
    {
        _cdn.Ok("https://cdn.test/a.mp3", [1, 2, 3]);

        var got = await Download().GetAsync("https://cdn.test/a.mp3", CancellationToken.None);

        got.Error.Should().BeNull();
        got.File!.Bytes.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task RedirectToInternal_RefusedBeforeSecondRequest()
    {
        _cdn.Redirect("https://cdn.test/a.mp3", "https://127.0.0.1/a.mp3");

        var got = await Download().GetAsync("https://cdn.test/a.mp3", CancellationToken.None);

        got.Error.Should().Contain("внутренний адрес");
        _cdn.Requests.Should().Equal("https://cdn.test/a.mp3");
    }

    [Fact]
    public async Task RedirectToPublic_Followed()
    {
        _cdn.Redirect("https://cdn.test/a.mp3", "/b.mp3");
        _cdn.Ok("https://cdn.test/b.mp3", [4]);

        var got = await Download().GetAsync("https://cdn.test/a.mp3", CancellationToken.None);

        got.File!.Bytes.Should().Equal(4);
        got.File.Uri.AbsolutePath.Should().Be("/b.mp3");
    }

    [Fact]
    public async Task OverCeiling_ByContentLength_RefusedWithoutReadingBody()
    {
        var body = new Tracked(new byte[11]);
        _cdn.Route("https://cdn.test/a.mp3", () => new StreamContent(body) { Headers = { ContentLength = 11 } });

        var got = await Download(maxBytes: 10).GetAsync("https://cdn.test/a.mp3", CancellationToken.None);

        got.Error.Should().Contain("больше");
        body.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task OverCeiling_Streamed_WithoutContentLength_Refused()
    {
        _cdn.Route("https://cdn.test/a.mp3", () => new StreamContent(new Tracked(new byte[11])));

        var got = await Download(maxBytes: 10).GetAsync("https://cdn.test/a.mp3", CancellationToken.None);

        got.File.Should().BeNull();
        got.Error.Should().Contain("больше");
    }

    [Fact]
    public async Task Empty_RefusedByValue()
    {
        _cdn.Ok("https://cdn.test/a.mp3", []);

        var got = await Download().GetAsync("https://cdn.test/a.mp3", CancellationToken.None);

        got.File.Should().BeNull();
        got.Error.Should().Be("пустой файл результата");
    }

    // Поток без длины: StreamContent не узнает Content-Length, и ограничение держит только чтение
    private sealed class Tracked(byte[] data) : Stream
    {
        private int _pos;
        public int BytesRead => _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // По байту за чтение: потолок обязан сработать посреди потока, а не на одном большом куске
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
    }

    private sealed class Cdn : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];
        public List<string> Requests { get; } = [];

        public void Route(string url, Func<HttpContent> content) =>
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = content() };

        public void Ok(string url, byte[] bytes) => Route(url, () => new ByteArrayContent(bytes));

        public void Redirect(string url, string location) =>
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(_routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
