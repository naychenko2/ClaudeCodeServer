using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

// Тесты на источник правды белого списка HiggsfieldToolset (фаза 1.2).
// Цель: состав хода — это Whitelist + WhitelistSet, не отдача upstream.
// GetOwnerSession/EnsureFresh намеренно не используются: тесты должны
// падать, только если Whitelist или извлечение SSE/JSON сломались.
public class HiggsfieldToolsetTests
{
    // Whitelist — единственный источник правды для состава хода.
    // Если кто-то добавит инструмент «сверху» (минуя константу),
    // модель увидит его в Higgsfield, но в конфиг хода он не попадёт.
    [Fact]
    public void Whitelist_СодержитРовно10ОбъявленныхИнструментов()
    {
        var expected = new[]
        {
            "generate_image",
            "generate_video",
            "generate_audio",
            "job_status",
            "jobs_wait",
            "show_generation_by_ids",
            "models_explore",
            "media_import_url",
            "media_upload",
            "media_upload_widget",
        };
        HiggsfieldToolset.Whitelist.Should().BeEquivalentTo(expected,
            "whitelist — единственный источник правды для состава хода");
    }

    // SSE-парсер — это и есть ключ к чтению любого ответа Higgsfield.
    // Сейчас сервер отвечает в формате event: message + data: {json}.
    // Если upstream когда-нибудь переключится на Streamable HTTP с Mcp-Session-Id,
    // парсер должен упасть первым (не молча сломать прод).
    [Fact]
    public void ExtractSseData_ВозвращаетJsonИзDataСтроки()
    {
        var sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{\"tools\":[]}}\n\n";
        var result = HiggsfieldToolset.ExtractSseData(sse);
        result.Should().NotBeNull();
        result!["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
    }

    // Грабли из пробного прогона протокола (фаза 1.1, 2026-09-15):
    // Higgsfield в ответах НЕ возвращает заголовок Mcp-Session-Id — сервер безсессионный.
    // Парсер рассчитан на это: пустой ответ, без data:, без JSON-RPC-объекта — null.
    [Fact]
    public void ExtractSseData_ПустойОтветВозвращаетNull()
    {
        var empty = "";
        HiggsfieldToolset.ExtractSseData(empty).Should().BeNull();
    }

    // Защита от регрессии: пустое поле data: (валидный SSE при инициализации без payload)
    // тоже даёт null — мы не пытаемся парсить пустую строку как JSON.
    [Fact]
    public void ExtractSseData_ПустойDataВозвращаетNull()
    {
        var sse = "event: message\ndata: \n\n";
        HiggsfieldToolset.ExtractSseData(sse).Should().BeNull();
    }

    // Защита от мульти-дата-строк: парсер возвращает ПЕРВЫЙ валидный JSON-объект
    // (early return в foreach). Это согласуется с init-стилем SSE (несколько
    // уведомлений в одном потоке) и с контрактом JsonRpc: один запрос — один ответ.
    // Если поведение должно смениться на «последний» — менять и тест, и прокси.
    [Fact]
    public void ExtractSseData_НесколькоDataСтрокБерётПервуюВалидную()
    {
        var sse = string.Join('\n',
            "data: {\"id\":1}",
            "data: {\"id\":2}",
            "",
            "");
        var result = HiggsfieldToolset.ExtractSseData(sse)!;
        result["id"]!.GetValue<int>().Should().Be(1);
    }

    // Серверное имя берётся из Core-константы, чтобы URL конфига хода
    // и код использовали одну и ту же строку. Дрейф = тихая пропажа сервера.
    [Fact]
    public void ServerName_СовпадаетСКонстантойMcpEndpoints()
    {
        HiggsfieldToolset.ServerName.Should().Be(McpEndpoints.HiggsfieldName);
        McpEndpoints.HiggsfieldName.Should().Be("higgsfield");
    }

    // URL эндпоинта для конфига хода строится по общей формуле Core:
    // {api}/mcp/{server}/{tail}. Это контракт сетевого шва — сломать = разорвать ход.
    [Fact]
    public void EndpointFor_СтроитМаршрутИзХвостаSessionId()
    {
        var api = "http://localhost:5000";
        var url = HiggsfieldToolset.EndpointFor(api, "abc123");
        url.Should().Be("http://localhost:5000/mcp/higgsfield/abc123");
    }

    // JsonOpts — лениво инициализируемая статика; не делаем mutable state, но и
    // не падаем при параллельном построении составов. Здесь просто проверка,
    // что класс не делает вид, что хранит per-instance состояние.
    [Fact]
    public void Ctor_НеПадаетБезHttpКлиентов()
    {
        // Аргументы переданы null там, где тулсет не дёргает их в конструкторе:
        // кэш ленивый, фабрика — lazy, OAuth — фасад. Реальные подключения
        // проверяются интеграционно, а конструктор обязан быть дешёвым.
        var act = () => new HiggsfieldToolset(
            higgsfield: null!,
            clientFactory: null!,
            sessions: null!,
            log: NullLogger<HiggsfieldToolset>.Instance);
        act.Should().NotThrow();
    }
}

internal sealed class NullLogger<T> : ILogger<T>
{
    public static readonly NullLogger<T> Instance = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) { }
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
