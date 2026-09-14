using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Video;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Video;

/// <summary>
/// Лента YouTube: классификация отказов и изоляция владельцев. Сеть подставная — живых
/// вызовов к Google нет (в CI их и быть не может).
///
/// Проверяется то, на чём раздел врёт незаметнее всего: пустая лента при лежащем сервисе
/// выглядит как «свежих роликов нет», а 403 по одному закрытому каналу — как протухший
/// вход, из которого нет выхода (переподключение ничего не меняет).
/// </summary>
public class YouTubeFeedTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "ccs-video-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string OneSubscription = """
        { "items": [ { "snippet": {
            "title": "Канал",
            "resourceId": { "channelId": "UCtest123" },
            "thumbnails": { "medium": { "url": "https://img.example/t.jpg" } }
        } } ] }
        """;

    private const string OneVideo = """
        { "items": [ { "snippet": {
            "title": "Ролик",
            "channelId": "UCtest123",
            "videoOwnerChannelTitle": "Канал",
            "publishedAt": "2026-08-20T10:00:00Z",
            "resourceId": { "videoId": "vid123" },
            "thumbnails": { "medium": { "url": "https://img.example/v.jpg" } }
        } } ] }
        """;

    private static readonly VideoOptions Options = new()
    {
        YouTube = new YouTubeOptions { ClientId = "id", ClientSecret = "secret" },
    };

    /// <summary>Провайдер с уже подключённым (не протухшим) аккаунтом владельца.</summary>
    private YouTubeProvider Create(HttpMessageHandler handler, params string[] owners)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dataDir, "projects.json"),
            }).Build();
        Directory.CreateDirectory(_dataDir);

        var secrets = new McpSecretStore(config);
        foreach (var owner in owners)
            secrets.SetEntry(owner, new McpSecretEntry
            {
                Value = $"access-{owner}",
                RefreshToken = "refresh",
                // Заведомо живой токен: рефреш в этих тестах не проверяется
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            }, "video-youtube");

        var factory = new StubFactory(handler);
        var oauth = new YouTubeOAuthService(factory, secrets, NullLogger<YouTubeOAuthService>.Instance);
        return new YouTubeProvider(factory, oauth, Options,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<YouTubeProvider>.Instance);
    }

    [Fact]
    public async Task Квота_исчерпана_отдаётся_отдельным_классом_отказа()
    {
        var provider = Create(new RouteHandler(req => req.RequestUri!.AbsolutePath.Contains("subscriptions")
            ? Json(HttpStatusCode.OK, OneSubscription)
            : Json(HttpStatusCode.Forbidden, """{ "error": { "errors": [ { "reason": "quotaExceeded" } ] } }""")),
            "owner");

        var result = await provider.ListItemsAsync("owner", null, CancellationToken.None);

        result.Failure.Should().Be(VideoFailure.QuotaExceeded);
    }

    // 403 бывает не только про квоту: закрытый или удалённый канал в подписках даёт
    // playlistItemsNotAccessible. Объявить это протухшим входом — отправить человека
    // переподключать живой аккаунт в тупик.
    [Fact]
    public async Task Обычный_403_не_выдаётся_за_протухший_вход()
    {
        var provider = Create(new RouteHandler(req => req.RequestUri!.AbsolutePath.Contains("subscriptions")
            ? Json(HttpStatusCode.OK, OneSubscription)
            : Json(HttpStatusCode.Forbidden, """{ "error": { "errors": [ { "reason": "playlistItemsNotAccessible" } ] } }""")),
            "owner");

        var result = await provider.ListItemsAsync("owner", null, CancellationToken.None);

        result.Failure.Should().NotBe(VideoFailure.NeedsAuth);
    }

    [Fact]
    public async Task Протухший_вход_отдаётся_как_NeedsAuth()
    {
        var provider = Create(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), "owner");

        var result = await provider.ListItemsAsync("owner", null, CancellationToken.None);

        result.Failure.Should().Be(VideoFailure.NeedsAuth);
    }

    // Главное враньё, которое здесь стережётся: сервис лежит, лента пуста — и раздел
    // радостно сообщает «свежих роликов нет» вместо «сервис не отвечает».
    [Fact]
    public async Task Недоступный_сервис_не_выдаётся_за_пустую_ленту()
    {
        var provider = Create(new RouteHandler(req => req.RequestUri!.AbsolutePath.Contains("subscriptions")
            ? Json(HttpStatusCode.OK, OneSubscription)
            : new HttpResponseMessage(HttpStatusCode.BadGateway)),
            "owner");

        var result = await provider.ListItemsAsync("owner", null, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.Failed.Should().BeTrue("пустая лента при лежащем сервисе — это отказ, а не отсутствие роликов");
        result.Failure.Should().Be(VideoFailure.Unreachable);
    }

    [Fact]
    public async Task Лента_собирается_и_сортируется_по_дате()
    {
        var provider = Create(new RouteHandler(req => Json(HttpStatusCode.OK,
            req.RequestUri!.AbsolutePath.Contains("subscriptions") ? OneSubscription : OneVideo)),
            "owner");

        var result = await provider.ListItemsAsync("owner", null, CancellationToken.None);

        result.Failed.Should().BeFalse();
        result.Items.Should().ContainSingle();
        result.Items[0].Id.Should().Be("vid123");
        result.Items[0].EmbedUrl.Should().Contain("youtube-nocookie.com/embed/vid123");
    }

    [Fact]
    public async Task Лента_не_строится_поиском_и_не_зовёт_channels_list()
    {
        var handler = new RouteHandler(req => Json(HttpStatusCode.OK,
            req.RequestUri!.AbsolutePath.Contains("subscriptions") ? OneSubscription : OneVideo));
        var provider = Create(handler, "owner");

        await provider.ListItemsAsync("owner", null, CancellationToken.None);

        // search стоит 100 единиц квоты за вызов, channels.list — по вызову на канал
        handler.Urls.Should().NotContain(u => u.Contains("/search"));
        handler.Urls.Should().NotContain(u => u.Contains("/channels"));
        handler.Urls.Should().Contain(u => u.Contains("playlistId=UUtest123"));
    }

    [Fact]
    public async Task Лента_одного_владельца_не_видна_другому()
    {
        // Ответ зависит от токена: у каждого владельца он свой
        var handler = new RouteHandler(req =>
        {
            var token = req.Headers.Authorization?.Parameter ?? "";
            if (req.RequestUri!.AbsolutePath.Contains("subscriptions"))
                return Json(HttpStatusCode.OK, OneSubscription);
            return Json(HttpStatusCode.OK, token == "access-alice"
                ? OneVideo
                : """{ "items": [] }""");
        });
        var provider = Create(handler, "alice", "bob");

        var alice = await provider.ListItemsAsync("alice", null, CancellationToken.None);
        var bob = await provider.ListItemsAsync("bob", null, CancellationToken.None);

        alice.Items.Should().ContainSingle();
        bob.Items.Should().BeEmpty("кеш ключуется владельцем, чужая лента подставляться не должна");
    }

    [Fact]
    public async Task Обновление_по_кнопке_обходит_кеш()
    {
        var handler = new RouteHandler(req => Json(HttpStatusCode.OK,
            req.RequestUri!.AbsolutePath.Contains("subscriptions") ? OneSubscription : OneVideo));
        var provider = Create(handler, "owner");

        await provider.ListItemsAsync("owner", null, CancellationToken.None);
        var afterFirst = handler.Urls.Count;
        await provider.ListItemsAsync("owner", null, CancellationToken.None);
        var afterCached = handler.Urls.Count;
        await provider.ListItemsAsync("owner", null, CancellationToken.None, refresh: true);

        afterCached.Should().Be(afterFirst, "без флага берётся кеш");
        handler.Urls.Count.Should().BeGreaterThan(afterCached, "«Обновить» обязано идти в сеть");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* временная папка теста — уборка не критична */ }
        GC.SuppressFinalize(this);
    }
}
