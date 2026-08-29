using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Video;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Video;

/// <summary>
/// Провайдер эфиров СМОТРИМ. Сеть — через подставной обработчик, живых запросов нет
/// (в CI их не будет, а на машине с системным прокси они бы ещё и врали).
///
/// Главное, что здесь стережётся: признак «канал можно играть у себя» вычисляется из
/// ответа сервиса, а не хардкодится списком id, и частичный отказ не роняет весь список.
/// </summary>
public class SmotrimProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Карточка канала: со своим потоком (играбельный) или без него.</summary>
    private static string Card(string title, bool withStream, string? program = "Передача") =>
        $$"""
        {
          "status": "OK",
          "data": {
            "title": "{{title}}",
            "splash": { "medium": "https://cdn.example/logo.png" },
            "epg": { "programName": "{{program}}" },
            "streams": {{(withStream ? """{ "m3u8": "https://live.example/index.m3u8" }""" : "null")}},
            "qualities": []
          }
        }
        """;

    private static SmotrimProvider Create(HttpMessageHandler handler) =>
        new(new StubFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SmotrimProvider>.Instance);

    private static int IdOf(HttpRequestMessage req) =>
        int.Parse(req.RequestUri!.Segments[^1].TrimEnd('/'));

    [Fact]
    public async Task Канал_со_своим_потоком_помечается_играбельным()
    {
        var provider = Create(new StubHandler(_ => Json(Card("Россия 24", withStream: true))));

        var result = await provider.ListChannelsAsync("owner", CancellationToken.None);

        result.Failed.Should().BeFalse();
        result.Items.Should().OnlyContain(c => c.Embeddable);
        result.Items.Should().OnlyContain(c => c.EmbedUrl != null && c.EmbedUrl.Contains("player.smotrim.ru"));
        // Автозапуск: без него кадр открывается стоп-кадром, и эфир идёт только после
        // второго нажатия — уже внутри чужого плеера. Флаг живёт СЕГМЕНТОМ пути
        result.Items.Should().OnlyContain(c => c.EmbedUrl!.EndsWith("/isPlay/true"));
    }

    // Ключевой инвариант: часть каталога (кнопочные федеральные — Первый, НТВ, СТС…)
    // вещается чужим плеером по домену-реферреру. Признак должен приходить из ответа,
    // иначе через месяц список играбельных разъедется с реальностью.
    [Fact]
    public async Task Канал_без_потока_отдаётся_карточкой_со_ссылкой_наружу()
    {
        var provider = Create(new StubHandler(_ => Json(Card("Первый канал", withStream: false))));

        var result = await provider.ListChannelsAsync("owner", CancellationToken.None);

        result.Items.Should().OnlyContain(c => !c.Embeddable);
        result.Items.Should().OnlyContain(c => c.EmbedUrl == null);
        result.Items.Should().OnlyContain(c => c.ExternalUrl!.StartsWith("https://smotrim.ru/channel/"));
        // Программа передач приходит и у неиграбельных — карточка не должна быть пустой
        result.Items.Should().OnlyContain(c => c.NowPlaying == "Передача");
    }

    [Fact]
    public async Task Отказ_одного_канала_не_роняет_весь_список()
    {
        // Первый канал каталога отвечает 500, остальные — нормально
        var failing = SmotrimCatalog.Channels[0].Id;
        var provider = Create(new StubHandler(req => IdOf(req) == failing
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json(Card("Канал", withStream: true))));

        var result = await provider.ListChannelsAsync("owner", CancellationToken.None);

        result.Failed.Should().BeFalse();
        result.Items.Should().HaveCount(SmotrimCatalog.Channels.Count);
        // Упавший канал остаётся в списке — с названием из каталога и без плеера
        result.Items.Should().Contain(c => c.Id == failing.ToString() && !c.Embeddable);
    }

    [Fact]
    public async Task Битый_ответ_вырождается_в_каталог_без_плееров()
    {
        var provider = Create(new StubHandler(_ => Json("{ это не json")));

        var act = async () => await provider.ListChannelsAsync("owner", CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Не просто «не упало»: список обязан остаться полным, иначе тест остался бы
        // зелёным и при молчаливом возврате пустоты
        var result = await provider.ListChannelsAsync("owner", CancellationToken.None);
        result.Items.Should().HaveCount(SmotrimCatalog.Channels.Count);
        result.Items.Should().OnlyContain(c => !c.Embeddable);
        result.Items.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Title));
    }

    [Fact]
    public async Task Играбельные_каналы_идут_первыми()
    {
        // Играбелен только один канал из середины каталога
        var playable = SmotrimCatalog.Channels[5].Id;
        var provider = Create(new StubHandler(req =>
            Json(Card("Канал", withStream: IdOf(req) == playable))));

        var result = await provider.ListChannelsAsync("owner", CancellationToken.None);

        result.Items[0].Id.Should().Be(playable.ToString(),
            "то, что реально можно смотреть, не должно теряться среди ссылок наружу");
    }

    [Fact]
    public async Task Повторный_вызов_берёт_кеш_вместо_новых_запросов()
    {
        var handler = new StubHandler(_ => Json(Card("Канал", withStream: true)));
        var provider = Create(handler);

        await provider.ListChannelsAsync("owner", CancellationToken.None);
        var afterFirst = handler.Calls;
        await provider.ListChannelsAsync("owner", CancellationToken.None);

        afterFirst.Should().Be(SmotrimCatalog.Channels.Count);
        handler.Calls.Should().Be(afterFirst, "программа передач кешируется на минуту");
    }

    [Fact]
    public async Task Эфирный_провайдер_лент_не_отдаёт()
    {
        var provider = Create(new StubHandler(_ => Json(Card("Канал", withStream: true))));

        var result = await provider.ListItemsAsync("owner", null, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.Failed.Should().BeFalse();
    }

    [Fact]
    public void Каталог_не_содержит_повторов()
    {
        SmotrimCatalog.Channels.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        SmotrimCatalog.Channels.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Title));
    }
}
