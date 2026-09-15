using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
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

    // --- Фаза 3.1: учёт трат Higgsfield (SpendSources.Higgsfield + сниффер генерации) ---

    // TryExtractHiggsfieldGeneration: распознаёт генерации по имени инструмента,
    // но не служебные вызовы (job_status, models_explore).
    [Fact]
    public void TryExtractHiggsfieldGeneration_ТолькоГенерации_НеСлужебные()
    {
        SpendMapping.TryExtractHiggsfieldGeneration("mcp__higgsfield__generate_image").Should().BeTrue();
        SpendMapping.TryExtractHiggsfieldGeneration("mcp__higgsfield__generate_video").Should().BeTrue();
        SpendMapping.TryExtractHiggsfieldGeneration("mcp__higgsfield__generate_audio").Should().BeTrue();
        SpendMapping.TryExtractHiggsfieldGeneration("mcp__higgsfield__job_status").Should().BeFalse();
        SpendMapping.TryExtractHiggsfieldGeneration("mcp__higgsfield__models_explore").Should().BeFalse();
        SpendMapping.TryExtractHiggsfieldGeneration("mcp__websearch__web_search").Should().BeFalse();
        SpendMapping.TryExtractHiggsfieldGeneration(null).Should().BeFalse();
    }

    // RecordHiggsfieldGeneration: создаёт SpendRecord с provider=higgsfield, generations=1, costUsd=null.
    [Fact]
    public void RecordHiggsfieldGeneration_СоздаётВернуюЗапись()
    {
        var recorded = new List<SpendRecord>();
        var spend = new FakeSpend(recording: recorded);
        var session = new Session
        {
            Id = "sess-1",
            ProjectId = "proj-1",
            TaskId = "task-1",
            PersonaId = "persona-1",
            Provider = "higgsfield",
        };

        SpendMapping.RecordHiggsfieldGeneration(spend, s => s.OwnerId, null, session);

        var r = recorded.Should().ContainSingle().Which;
        r.Provider.Should().Be("higgsfield");
        r.Source.Should().Be("higgsfield");
        r.Generations.Should().Be(1);
        r.CostUsd.Should().BeNull();
        r.SessionId.Should().Be("sess-1");
    }

    // RecordHiggsfieldGeneration: null-collector — не падает.
    [Fact]
    public void RecordHiggsfieldGeneration_NullCollector_НеПадает()
    {
        var session = new Session { Id = "sess-2", OwnerId = "owner-1" };
        SpendMapping.RecordHiggsfieldGeneration(null, s => "o", null, session); // no exception
    }

    // IsTokenless: higgsfield — источник без токенов (кредиты ≠ токены).
    [Fact]
    public void IsTokenless_Higgsfield()
    {
        SpendSources.IsTokenless(SpendSources.Higgsfield).Should().BeTrue();
    }

    // McpCallLog: higgsfield — продуктовый http-MCP-тулсет, поэтому вызовы автоматически
    // попадают в McpCallLog (механизм общий для всех http-тулсетов). Тест фиксирует, что
    // higgsfield-инструмент попадает в таблицу как отдельная строка.
    [Fact]
    public void McpCallLog_Higgsfield_ВызовПопадаетВЖурнал()
    {
        var log = new ClaudeHomeServer.Services.Mcp.McpCallLog();

        // Имитация того, что McpTransportController.NameCallForLog кладёт в заголовок:
        // для tools/call — имя инструмента из тела JSON-RPC
        var displayed = log.Record("generate_image", "sess-1", "/mcp/higgsfield", 200, 42);

        displayed.Should().Be("generate_image");

        var stats = log.Stats();
        stats.Should().Contain(s => s.Tool == "generate_image" && s.Calls == 1);
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

internal sealed class FakeSpend(List<SpendRecord> recording) : ISpendCollector
{
    public void Record(SpendRecord record) => recording.Add(record);
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
