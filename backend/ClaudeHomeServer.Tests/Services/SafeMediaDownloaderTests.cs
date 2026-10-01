using System.Net;
using System.Net.Sockets;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Скачивание результата генерации по ссылке поставщика: SSRF-рубежи (схема, адрес до запроса,
// адрес соединения, редиректы) и потолок размера. Сеть наружу не трогаем: транспорт — фейк,
// а проверки боевого транспорта идут на loopback.
public class SafeMediaDownloaderTests
{
    private const long Max = 1024;

    // Журнал запросов и маршрут ответа; тело можно отдать без Content-Length
    private sealed class Route(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    // Поток без длины, считает отданные байты: по нему видно, что чтение оборвали
    private sealed class CountingStream(long total) : Stream
    {
        public long Served { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, total - Served);
            Served += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // IP-литералы проверяет настоящий SsrfGuard, вымышленные домены считаются публичными
    private static Task<SsrfGuard.AddressCheck> LiteralsOnly(Uri uri, CancellationToken ct) =>
        uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? SsrfGuard.CheckAsync(uri, ct)
            : Task.FromResult(SsrfGuard.AddressCheck.Public);

    private static HttpResponseMessage Png(int size = 16) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[size]) { Headers = { ContentType = new("image/png") } } };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public async Task ВнешняяHttpsСсылка_Скачивается()
    {
        var http = new Route(_ => Png());
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("https://cdn.example/a.png", Max, default);

        result.Ok.Should().BeTrue();
        result.Bytes.Should().HaveCount(16);
        result.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Http_Отказ_БезЗапроса()
    {
        var http = new Route(_ => Png());
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("http://cdn.example/a.png", Max, default);

        result.Error.Should().Be("not-https");
        http.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://127.0.0.1/a.png")]
    [InlineData("https://10.0.0.1/a.png")]
    [InlineData("https://[::1]/a.png")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task ВнутреннийАдрес_Отказ_БезЗапроса(string url)
    {
        var http = new Route(_ => Png());
        // Проверка по умолчанию — настоящий SsrfGuard
        var result = await new SafeMediaDownloader(http).DownloadAsync(url, Max, default);

        result.Error.Should().Be("private-address");
        http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ДоменВоВнутреннийАдрес_Отказ_БезЗапроса()
    {
        var http = new Route(_ => Png());
        var result = await new SafeMediaDownloader(http).DownloadAsync("https://localhost/a.png", Max, default);

        result.Error.Should().Be("private-address");
        http.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://10.0.0.1/a.png", "private-address")]
    [InlineData("https://169.254.169.254/latest/meta-data", "private-address")]
    [InlineData("http://cdn.example/a.png", "not-https")]
    public async Task РедиректВоВнутреннююСеть_Отказ_БезЗапросаКЦели(string target, string error)
    {
        var http = new Route(r => r.RequestUri!.Host == "cdn.example" && r.RequestUri.Scheme == "https"
            ? Redirect(target)
            : Png());
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("https://cdn.example/start", Max, default);

        result.Error.Should().Be(error);
        http.Calls.Should().ContainSingle().Which.Should().Be(new Uri("https://cdn.example/start"));
    }

    [Fact]
    public async Task ВнешнийРедирект_Следуем()
    {
        var http = new Route(r => r.RequestUri!.AbsolutePath == "/start" ? Redirect("/final.png") : Png());
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("https://cdn.example/start", Max, default);

        result.Ok.Should().BeTrue();
        http.Calls.Select(c => c.ToString()).Should().Equal("https://cdn.example/start", "https://cdn.example/final.png");
    }

    [Fact]
    public async Task БесконечныйРедирект_Отказ()
    {
        var http = new Route(_ => Redirect("https://cdn.example/again"));
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("https://cdn.example/start", Max, default);

        result.Error.Should().Be("too-many-redirects");
        http.Calls.Should().HaveCount(SafeMediaDownloader.MaxRedirects + 1);
    }

    [Fact]
    public async Task ContentLengthСверхПотолка_Отказ_ТелоНеЧитается()
    {
        var body = new CountingStream(Max * 4);
        var http = new Route(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body) { Headers = { ContentLength = Max * 4 } },
        });
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("https://cdn.example/big.png", Max, default);

        result.Error.Should().Be("too-large");
        body.Served.Should().Be(0);
    }

    [Fact]
    public async Task ПотокБезДлиныСверхПотолка_Отказ_ЧтениеОборвано()
    {
        var body = new CountingStream(Max * 1000);
        var http = new Route(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("https://cdn.example/big.png", Max, default);

        result.Error.Should().Be("too-large");
        body.Served.Should().BeLessThan(Max * 1000);
    }

    [Fact]
    public async Task ТелоРовноПотолок_Скачивается()
    {
        var http = new Route(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CountingStream(Max)) });
        var result = await new SafeMediaDownloader(http, LiteralsOnly).DownloadAsync("https://cdn.example/a.png", Max, default);

        result.Bytes.Should().HaveCount((int)Max);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.1")]
    public async Task АдресСоединения_ВнутреннийНеРазрешён(string host)
    {
        (await SafeMediaDownloader.ResolvePublicAsync(host, default)).Should().BeNull();
    }

    [Fact]
    public async Task АдресСоединения_ПубличныйЛитерал_Разрешён()
    {
        (await SafeMediaDownloader.ResolvePublicAsync("8.8.8.8", default)).Should().Be(IPAddress.Parse("8.8.8.8"));
    }

    [Fact]
    public async Task БоевойТранспорт_ПроверкаДоЗапросаОбойдена_СоединениеСЦельюНеОткрывается()
    {
        // DNS rebinding: предварительная проверка «увидела» публичный адрес, а соединение
        // идёт на loopback. ConnectCallback обязан отказать до TCP-рукопожатия.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var handler = SafeMediaDownloader.CreateHandler();
        var downloader = new SafeMediaDownloader(handler, (_, _) => Task.FromResult(SsrfGuard.AddressCheck.Public));

        var result = await downloader.DownloadAsync($"https://127.0.0.1:{port}/a.png", Max, default);

        result.Error.Should().Be("network");
        listener.Pending().Should().BeFalse("до цели не должно дойти ни одного соединения");
    }
}
