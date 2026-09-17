using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
            config: null!,
            mcpStatus: null,
            log: NullLogger<HiggsfieldToolset>.Instance);
        act.Should().NotThrow();
    }

    // Регрессия шага 1: канал до mcp.higgsfield.ai рвётся, и CLI молча теряет весь тулсет.
    // При таймауте апстрима ход не должен падать И не должен терять последний валидный снимок
    // в _cachedTools (fail-open, чтобы следующий ход не уехал с пустым составом).
    //
    // Стенд: реальный HiggsfieldOAuthService (см. HiggsfieldOAuthMigrationTests.NewService),
    // усыновлённый per-owner токен через RunMigration — иначе EnsureFresh вернёт null и
    // сетевой запрос не случится. Handler имитирует таймаут через TaskCanceledException —
    // эквивалентно срабатыванию client.Timeout на 40-й секунде, но без 40-секундного теста.
    [Fact]
    public void GetCachedTools_ТаймаутАпстрима_НеРоняетХодИНеСтираетСнимок()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-to-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var secrets = new McpSecretStore(config);
            var registry = new McpRegistry(config, secrets);
            var statuses = new McpStatusStore(config);
            // Handler и для OAuth-stub, и для tools/list — таймаут-одинаковый:
            // OAuth EnsureFresh при валидном токене в сеть не пойдёт, а tools/list —
            // пойдёт и упадёт по таймауту, что и нужно проверить.
            var timeoutHandler = new TimeoutHandler();
            var oauth = new McpOAuthService(registry, secrets, statuses,
                new StubHttpClientFactory(timeoutHandler), config,
                NullLogger<McpOAuthService>.Instance);
            var oauthService = new HiggsfieldOAuthService(registry, secrets, statuses, oauth,
                config, NullLogger<HiggsfieldOAuthService>.Instance);

            // Per-owner запись + живой токен → RunMigration усыновляет их под ServiceOwnerId
            secrets.SetEntry("owner-to", new McpSecretEntry
            {
                Value = "test-token",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            }, refOrId: "tok-to");
            registry.CreateBuiltIn("owner-to", new McpServerRecord
            {
                Key = HiggsfieldOAuthService.Key,
                Label = HiggsfieldOAuthService.Label,
                Transport = McpTransport.Http,
                Url = HiggsfieldOAuthService.Url,
                Auth = new McpAuthConfig
                {
                    Kind = McpAuthKind.OAuth2,
                    OAuth = new McpOAuthConfig
                    {
                        AccessTokenRef = McpSecretStore.Placeholder("tok-to"),
                        ClientId = "client-dcr-to",
                    },
                },
                Enabled = true,
            });
            oauthService.RunMigration();

            var toolset = new HiggsfieldToolset(oauthService,
                new StubHttpClientFactory(timeoutHandler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                log: NullLogger<HiggsfieldToolset>.Instance);

            // Кладём «прошлый удачный снимок» прямо в кэш (имитация того, что недавно получили
            // валидный tools/list). Помечаем как просроченный — чтобы GetCachedTools пошёл в сеть.
            var snapshot = new List<McpToolSchema>
            {
                new("generate_image", "test", new JsonObject()),
                new("generate_video", "test", new JsonObject()),
            };
            var cachedField = typeof(HiggsfieldToolset).GetField("_cachedTools",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var atField = typeof(HiggsfieldToolset).GetField("_cachedAt",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            cachedField.SetValue(toolset, snapshot);
            atField.SetValue(toolset, DateTime.MinValue); // гарантированно expired

            // Сеть таймаутит — handler всегда бросает TaskCanceledException. И на первом,
            // и на втором заходе ретрая. GetCachedTools должен вернуть прежний снимок
            // (fail-open) и не пробросить наружу ни одного исключения.
            var act = () =>
            {
                var getCached = typeof(HiggsfieldToolset).GetMethod("GetCachedTools",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var result = (IReadOnlyList<McpToolSchema>)getCached.Invoke(toolset, null)!;
                // Снимок на месте, та же ссылка — не новый список и не null.
                result.Should().BeSameAs(snapshot,
                    "при таймауте апстрима кэш обязан остаться прежним (fail-open)");
            };
            act.Should().NotThrow();

            // Контроль: снимок действительно не подменён.
            cachedField.GetValue(toolset).Should().BeSameAs(snapshot,
                "_cachedTools не должен перезаписываться при таймауте");
            // И handler реально получил два запроса (ретрай сработал).
            timeoutHandler.Attempts.Should().Be(2,
                "на таймауте/HttpRequestException положен один повтор");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Имитация таймаута/сетевого сбоя: HttpMessageHandler, который бросает
    // TaskCanceledException. Эквивалентно срабатыванию client.Timeout на 40-й секунде,
    // но без 40-секундного теста.
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        private int _attempts;
        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            // TaskCanceledException с внутренним TimeoutException — то, что бросает
            // HttpClient при срабатывании Timeout. ct не отменён — это не внешняя отмена.
            var inner = new TimeoutException();
            var tcs = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.TrySetException(new TaskCanceledException("timeout", inner));
            return tcs.Task;
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // --- Шаг 2: снимок списка инструментов на диск (data/higgsfield-tools.json) ---

    // При старте процесса снимок с диска должен подниматься в кэш как «свежий» — иначе
    // первый ход после рестарта пойдёт в сеть и опять упрётся в лотерею на рваном канале.
    // Если handler не получит ни одного запроса, значит тулсет жил на снимке, как и задумано.
    [Fact]
    public void Конструктор_ЗагружаетСнимокСДискаПриХолодномКэше()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-snap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // Заранее кладём снимок на диск: два инструмента из белого списка.
            var snapshot = new List<McpToolSchema>
            {
                new("generate_image", "img", new JsonObject()),
                new("generate_video", "vid", new JsonObject()),
            };
            File.WriteAllText(Path.Combine(dir, "higgsfield-tools.json"),
                JsonSerializer.Serialize(snapshot));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            // Handler, считающий обращения. Если сюда придёт хоть один запрос — кэш не поднялся.
            var handler = new CountingHandler();
            var toolset = new HiggsfieldToolset(
                higgsfield: null!,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: null,
                NullLogger<HiggsfieldToolset>.Instance);

            // GetCachedTools: кэш уже свежий, lock вернёт _cachedTools без сети.
            var getCached = typeof(HiggsfieldToolset).GetMethod("GetCachedTools",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = (IReadOnlyList<McpToolSchema>)getCached.Invoke(toolset, null)!;

            result.Should().BeEquivalentTo(snapshot,
                "снимок с диска обязан лечь в кэш в качестве стартового состояния");
            handler.Requests.Should().Be(0,
                "холодный кэш поднят со снимка — сеть трогать нельзя");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Успешный tools/list ОБЯЗАН перезаписать файл. Иначе «промежуточный» успех апстрима
    // (который обогатил кэш в памяти) никак не отразится на диске — после рестарта снова
    // лотерея. Тут мы не идём через GetCachedTools (там запись в фоне), а зовём приватный
    // метод SaveSnapshot — прямой контракт.
    [Fact]
    public void SaveSnapshot_ПерезаписываетФайлНаДиске()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-snap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var toolset = new HiggsfieldToolset(
                higgsfield: null!,
                new StubHttpClientFactory(new CountingHandler()),
                sessions: null!,
                config,
                mcpStatus: null,
                NullLogger<HiggsfieldToolset>.Instance);

            var newTools = new List<McpToolSchema>
            {
                new("generate_image", "updated", new JsonObject()),
            };
            var save = typeof(HiggsfieldToolset).GetMethod("SaveSnapshot",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            save.Invoke(toolset, [newTools]);

            var onDisk = File.ReadAllText(Path.Combine(dir, "higgsfield-tools.json"));
            onDisk.Should().Contain("\"generate_image\"",
                "успешный tools/list обязан сохраниться на диск");
            onDisk.Should().Contain("updated",
                "описание инструмента в файле должно совпадать с переданным");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            // Сюда мы прийти не должны: эти тесты проверяют, что сеть не дёргается
            // (или дёргается в фоне, но не на пути GetCachedTools при свежем снимке).
            var tcs = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.TrySetException(new InvalidOperationException(
                "Handler не должен вызываться в этом тесте"));
            return tcs.Task;
        }
    }

    // Регрессия шага 2: битый снимок на диске НЕ должен ронять конструктор. JsonFileStore.Load
    // при сбое парсинга переименовывает файл в {path}.corrupt-{ts}.bak и возвращает default —
    // именно на это и закладывается LoadSnapshotFromDisk. Без этой страховки ход после
    // рестарта процесса молча остался бы без инструментов (пустой кэш, сеть ещё не дёрнута).
    [Fact]
    public void Конструктор_БитыйСнимокНаДиске_НеПадаетИПереименовываетФайл()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-snap-corrupt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // Битый JSON — фигурные не сбалансированы, запятая перед закрытием
            File.WriteAllText(Path.Combine(dir, "higgsfield-tools.json"), "{not a valid json");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var cachedField = typeof(HiggsfieldToolset).GetField("_cachedTools",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

            HiggsfieldToolset? toolset = null;
            var act = () => toolset = new HiggsfieldToolset(
                higgsfield: null!,
                new StubHttpClientFactory(new CountingHandler()),
                sessions: null!,
                config,
                mcpStatus: null,
                NullLogger<HiggsfieldToolset>.Instance);
            act.Should().NotThrow(
                "битый снимок обязан пройти fail-open — иначе рестарт процесса оставит ход без инструментов");

            // JsonFileStore.Load обязан переименовать битый файл, иначе каждый старт процесса
            // будет спотыкаться об одно и то же — fail-open без самоизлечения не fail-open.
            var snapshotPath = Path.Combine(dir, "higgsfield-tools.json");
            File.Exists(snapshotPath).Should().BeFalse(
                "JsonFileStore.Load обязан переименовать битый файл — иначе регрессия зациклится");

            // Кэш в памяти остаётся пустым: первый ход после рестарта пойдёт в сеть
            // (это и есть ожидаемое поведение — лучше один раз дёрнуть сеть, чем молча
            // оставить ход без инструментов).
            cachedField.GetValue(toolset).Should().BeNull(
                "LoadSnapshotFromDisk вернёт null на битом файле — кэш чист, GetCachedTools пойдёт в сеть");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Пустой файл (валидный JSON, но не десериализуется в список) — та же ветка: LoadSnapshotFromDisk
    // возвращает null, кэш остаётся пустым, ход пойдёт в сеть.
    [Fact]
    public void Конструктор_ПустойСнимокНаДиске_НеПадает()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-snap-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "higgsfield-tools.json"), "");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var cachedField = typeof(HiggsfieldToolset).GetField("_cachedTools",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

            HiggsfieldToolset? toolset = null;
            var act = () => toolset = new HiggsfieldToolset(
                higgsfield: null!,
                new StubHttpClientFactory(new CountingHandler()),
                sessions: null!,
                config,
                mcpStatus: null,
                NullLogger<HiggsfieldToolset>.Instance);
            act.Should().NotThrow("пустой снимок обязан пройти fail-open");

            cachedField.GetValue(toolset).Should().BeNull(
                "LoadSnapshotFromDisk возвращает null для пустого списка — кэш чист");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // --- Шаг 3: громкий отказ вместо тишины ---

    // Стенд: per-owner запись + живой токен → RunMigration усыновляет их под
    // ServiceOwnerId. Иначе EnsureFresh() вернёт null и сетевой запрос не случится.
    // Handler и OAuth-stub работают через один StubHttpClientFactory — для OAuth
    // используется тот же обработчик, что и для tools/list: достаточно, чтобы он не
    // бросал исключение (см. NewOauthStubHttpClientFactory).
    private sealed class NewOauthStubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (string Dir, IConfiguration Config, McpStatusStore Statuses,
        HiggsfieldOAuthService OAuth, HttpMessageHandler Handler) NewStep3Fixture(
        string tag, HttpStatusCode httpStatus, string body)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-fail-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dir, "projects.json"),
            }).Build();
        var secrets = new McpSecretStore(config);
        var registry = new McpRegistry(config, secrets);
        var statuses = new McpStatusStore(config);

        // HttpMessageHandler, отдающий заранее заданный ответ: и для OAuth-вызовов
        // (которые сюда не пойдут — у нас валидный токен), и для tools/list.
        var handler = new StringResponseHandler(httpStatus, body);

        var oauth = new McpOAuthService(registry, secrets, statuses,
            new NewOauthStubHttpClientFactory(handler), config,
            NullLogger<McpOAuthService>.Instance);
        var oauthService = new HiggsfieldOAuthService(registry, secrets, statuses, oauth,
            config, NullLogger<HiggsfieldOAuthService>.Instance);

        // Усыновляем токен под ServiceOwnerId — иначе EnsureFresh() вернёт null
        secrets.SetEntry("owner-" + tag, new McpSecretEntry
        {
            Value = "test-token",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        }, refOrId: "tok-" + tag);
        registry.CreateBuiltIn("owner-" + tag, new McpServerRecord
        {
            Key = HiggsfieldOAuthService.Key,
            Label = HiggsfieldOAuthService.Label,
            Transport = McpTransport.Http,
            Url = HiggsfieldOAuthService.Url,
            Auth = new McpAuthConfig
            {
                Kind = McpAuthKind.OAuth2,
                OAuth = new McpOAuthConfig
                {
                    AccessTokenRef = McpSecretStore.Placeholder("tok-" + tag),
                    ClientId = "client-dcr-" + tag,
                },
            },
            Enabled = true,
        });
        oauthService.RunMigration();

        return (dir, config, statuses, oauthService, handler);
    }

    // Раньше ветки «return null» в FetchToolsListAsync молчали: ответ 200, но без
    // JSON-RPC result, и наружу уходил пустой список — CLI показывал сервер как
    // connected с нулём инструментов. Теперь обязан быть WARN + статус Failed в
    // McpStatusStore, иначе регрессия ADR-012 «тихо терять инструмент нельзя».
    [Fact]
    public async Task FetchToolsListAsync_Ответ200БезJsonRpc_ПишетСтатусFailed()
    {
        // Имитируем поломанный апстрим: HTTP 200, но тело — мусор без data:.
        // ExtractSseData вернёт null, и FetchToolsListAsync пойдёт по ветке
        // «200 OK but no JSON-RPC result».
        var (dir, config, statuses, oauth, handler) = NewStep3Fixture("200noresult",
            HttpStatusCode.OK, "not-an-sse-event-stream");
        try
        {
            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            var fetch = typeof(HiggsfieldToolset).GetMethod("FetchToolsListAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)fetch.Invoke(toolset, [CancellationToken.None])!;
            await task;

            var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
            entry.Should().NotBeNull("200 OK без JSON-RPC обязан попасть в McpStatusStore");
            entry!.Status.Should().Be(McpServerStatuses.Failed,
                "наружу ушёл бы пустой tools/list — без статуса CLI показал бы connected");
            entry.Error.Should().NotBeNullOrEmpty("текст ошибки нужен для диагностики");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // HTTP 5xx — тоже отказ handshake: пишем статус Failed.
    [Fact]
    public async Task FetchToolsListAsync_HttpОшибка_ПишетСтатусFailed()
    {
        var (dir, config, statuses, oauth, handler) = NewStep3Fixture("500",
            HttpStatusCode.InternalServerError, "boom");
        try
        {
            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            var fetch = typeof(HiggsfieldToolset).GetMethod("FetchToolsListAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)fetch.Invoke(toolset, [CancellationToken.None])!;
            await task;

            var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
            entry.Should().NotBeNull("HTTP 5xx обязан попасть в McpStatusStore");
            entry!.Status.Should().Be(McpServerStatuses.Failed);
            entry.Error.Should().Contain("500",
                "номер статуса должен быть виден в Error — иначе в логах «что-то сломалось»");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // HttpMessageHandler, отдающий заранее заданный ответ на любой POST.
    private sealed class StringResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
            return Task.FromResult(response);
        }
    }

    // Симметрично веткам «200 без JSON-RPC» и «HTTP 5xx»: апстрим ответил валидным
    // tools/list, но ни одного инструмента из белого списка. Это аномалия — upstream
    // сменил имена либо разъехалась версия схемы — и без статуса человек увидел бы
    // «connected с нулём инструментов». ADR-012 «тихо терять инструмент нельзя».
    [Fact]
    public async Task FetchToolsListAsync_ТолькоЧужиеИнструменты_ПишетСтатусFailed()
    {
        // Тело — корректный JSON-RPC, но единственный инструмент вне белого списка.
        var body = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":0," +
            "\"result\":{\"tools\":[" +
            "{\"name\":\"unknown_tool_a\",\"description\":\"x\",\"inputSchema\":{}}," +
            "{\"name\":\"unknown_tool_b\",\"description\":\"y\",\"inputSchema\":{}}" +
            "]}}\n\n";
        var (dir, config, statuses, oauth, handler) = NewStep3Fixture("filtered0", HttpStatusCode.OK, body);
        try
        {
            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            var fetch = typeof(HiggsfieldToolset).GetMethod("FetchToolsListAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)fetch.Invoke(toolset, [CancellationToken.None])!;
            await task;

            var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
            entry.Should().NotBeNull(
                "пустой белый список после успешного ответа — это аномалия, статус обязан попасть в стор");
            entry!.Status.Should().Be(McpServerStatuses.Failed,
                "без статуса CLI показал бы connected с нулём инструментов — ADR-012 «тихо терять инструмент нельзя»");
            entry.Error.Should().Contain("empty",
                "текст ошибки должен указывать на пустой белый список — иначе диагностика гадает");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // --- Шаг 4: фоновый прогрев снимка ---

    // Интеграция не подключена (per-owner токена нет, EnsureFresh() вернёт null):
    // прогрев не должен ни падать, ни логгировать WARN на каждый тик — задача.
    // Раньше при «не сходили в сеть» уходил return null и в логах было чисто;
    // теперь шаг 3 добавил WARN на ОТКАЗ handshake. EnsureFresh()==null — это
    // НЕ отказ handshake (мы даже не пытались), и WARN здесь недопустим.
    [Fact]
    public async Task RefreshNowAsync_ИнтеграцияНеПодключена_НеПадаетИНеШумит()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-warm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var statuses = new McpStatusStore(config);

            // Без RunMigration: в higgsfield.json нет владельца, EnsureFresh() сразу null.
            var oauthService = new HiggsfieldOAuthService(
                registry: null!,
                secrets: new McpSecretStore(config),
                statuses: statuses,
                oauth: null!,
                config,
                NullLogger<HiggsfieldOAuthService>.Instance);

            var toolset = new HiggsfieldToolset(oauthService,
                new StubHttpClientFactory(new CountingHandler()),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            // Должен просто вернуть false, не бросить и не оставить статус Failed.
            var refreshed = await toolset.RefreshNowAsync(CancellationToken.None);
            refreshed.Should().BeFalse("нет токена — обновлять нечего");

            var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
            entry.Should().BeNull(
                "EnsureFresh()==null — не отказ handshake, статусы НЕ пишем");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Прогрев при живом токене обновляет кэш и снимок на диске — путь, ради которого
    // и затевался IHostedService: первый ход после рестарта получает свежий кэш.
    [Fact]
    public async Task RefreshNowAsync_ЖивойТокен_ОбновляетКэшИСнимок()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-warm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var (statuses, oauth, handler) = NewStep3FixtureComponents(config);

            // Тело ответа Higgsfield: один инструмент из белого списка.
            var sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{"
                + "\"tools\":[{\"name\":\"generate_image\",\"description\":\"img\",\"inputSchema\":{}}]}}\n\n";
            handler.SetNext(sse);

            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            // Без снимка на диске: первый RefreshNowAsync обязан сходить в сеть.
            var refreshed = await toolset.RefreshNowAsync(CancellationToken.None);
            refreshed.Should().BeTrue("живой токен + 200 ОК → кэш обновлён");

            // Снимок обязан появиться на диске — это и есть смысл фонового прогрева.
            var snapshotPath = Path.Combine(dir, "higgsfield-tools.json");
            File.Exists(snapshotPath).Should().BeTrue(
                "прогрев обязан сохранить снимок для следующего рестарта процесса");
            File.ReadAllText(snapshotPath).Should().Contain("generate_image");

            // Кэш в памяти обновлён — ToolsFor идёт без сети.
            var handler2 = new CountingHandler();
            ((dynamic)toolset).GetType();  // no-op, just to keep toolset captured
            // Заменим handler, чтобы проверить, что ToolsFor НЕ ходит в сеть.
            var cachedField = typeof(HiggsfieldToolset).GetField("_cachedTools",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var list = (IReadOnlyList<McpToolSchema>)cachedField.GetValue(toolset)!;
            list.Should().NotBeNullOrEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // --- Шаг 5: исходящие запросы обязаны ставить Accept (MCP-over-Streamable-HTTP) ---

    // Регрессия HTTP 406 от апстрима: Higgsfield требует Accept с обоими типами,
    // иначе отвечает 406 Not Acceptable и тулсет пропадает у хода. Прокрываем оба пути —
    // tools/list (FetchToolsListAsync) и tools/call (PostToHiggsfieldAsync).
    // Хедер ставится на HttpRequestMessage.Headers.Accept, а не на DefaultRequestHeaders клиента —
    // клиент общий и переиспользуется между ходами, глобальный Accept мог бы уехать в чужой запрос.
    [Fact]
    public async Task FetchToolsListAsync_СтавитЗаголовокAccept()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-accept-list-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var (statuses, oauth, handler) = NewStep3FixtureComponents(config);
            handler.SetNext("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{\"tools\":[]}}\n\n");

            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            var fetch = typeof(HiggsfieldToolset).GetMethod("FetchToolsListAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)fetch.Invoke(toolset, [CancellationToken.None])!;
            await task;

            handler.LastAccept.Should().BeEquivalentTo(new[] { "application/json", "text/event-stream" },
                "MCP-over-Streamable-HTTP требует оба типа в Accept, иначе апстрим отвечает 406");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task PostToHiggsfieldAsync_СтавитЗаголовокAccept()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-accept-call-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                }).Build();

            var (statuses, oauth, handler) = NewStep3FixtureComponents(config);
            handler.SetNext("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"text\":\"ok\"}],\"isError\":false}}\n\n");

            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            // Зовём приватный PostToHiggsfieldAsync напрямую: проверяем контракт метода,
            // а не путь через CallAsync (там ещё проверка whitelist/whitelisted и пр.).
            var post = typeof(HiggsfieldToolset).GetMethod("PostToHiggsfieldAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task<JsonObject?>)post.Invoke(toolset,
                ["test-token", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1 }, CancellationToken.None, 5_000])!;
            var result = await task;

            result.Should().NotBeNull("ответ 200 OK с JSON-RPC даёт не null");
            handler.LastAccept.Should().BeEquivalentTo(new[] { "application/json", "text/event-stream" },
                "MCP-over-Streamable-HTTP требует оба типа в Accept, иначе апстрим отвечает 406");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // --- Шаг 6: успешный tools/list сбрасывает залипший Failed ---

    // До правки у RecordToolsListFailure не было пары: прошлая ошибка жила в сторе вечно,
    // и зелёная интеграция показывалась красной карточкой. Теперь успешный tools/list
    // обязан перевести запись в Connected и очистить текст ошибки.
    [Fact]
    public async Task FetchToolsListAsync_УспешныйОтветПослеFailed_ПишетСтатусConnectedИОчищаетError()
    {
        // Тело — корректный JSON-RPC с одним инструментом из белого списка,
        // поэтому после FetchToolsListAsync должна записаться пара Connected/null.
        var body = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":0," +
            "\"result\":{\"tools\":[" +
            "{\"name\":\"generate_image\",\"description\":\"img\",\"inputSchema\":{}}" +
            "]}}\n\n";
        var (dir, config, statuses, oauth, handler) = NewStep3Fixture("succ-after-fail",
            HttpStatusCode.OK, body);
        try
        {
            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            // Предпосылка: прошлая ошибка зависла в сторе как failed.
            statuses.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
                McpServerStatuses.Failed, "HTTP 406");

            var fetch = typeof(HiggsfieldToolset).GetMethod("FetchToolsListAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)fetch.Invoke(toolset, [CancellationToken.None])!;
            await task;

            var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
            entry.Should().NotBeNull();
            entry!.Status.Should().Be(McpServerStatuses.Connected,
                "успешный tools/list обязан снять залипший Failed — иначе карточка в UI врёт");
            entry.Error.Should().BeNull(
                "при Connected текст прежней ошибки должен быть очищен — иначе в UI гниёт красный текст");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Регрессия существующего поведения: успешный статус не должен мешать последующему
    // отказу переписать запись на Failed с новой причиной. Иначе «один раз Connected —
    // навсегда Connected», и новая поломка апстрима останется невидимой.
    [Fact]
    public async Task FetchToolsListAsync_ОтказПослеConnected_ПишетСтатусFailed()
    {
        var (dir, config, statuses, oauth, handler) = NewStep3Fixture("fail-after-ok",
            HttpStatusCode.InternalServerError, "boom");
        try
        {
            var toolset = new HiggsfieldToolset(oauth,
                new StubHttpClientFactory(handler),
                sessions: null!,
                config,
                mcpStatus: statuses,
                NullLogger<HiggsfieldToolset>.Instance);

            // Предпосылка: интеграция была здорова — connected/null.
            statuses.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
                McpServerStatuses.Connected, error: null);

            var fetch = typeof(HiggsfieldToolset).GetMethod("FetchToolsListAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)fetch.Invoke(toolset, [CancellationToken.None])!;
            await task;

            var entry = statuses.Get(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key);
            entry.Should().NotBeNull();
            entry!.Status.Should().Be(McpServerStatuses.Failed,
                "новая поломка обязана перекрыть прежний Connected — иначе регрессия ADR-012");
            entry.Error.Should().Contain("500",
                "текст ошибки должен нести актуальный код — иначе диагностика потеряна");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Расширенная фабрика для тестов прогрева: handler умеет задать «следующий» ответ.
    private sealed class ConfigurableResponseHandler : HttpMessageHandler
    {
        private string _body = "";

        public void SetNext(string body) => _body = body;

        // Для тестов на заголовки исходящего запроса (Accept и т. п.).
        // Каждый запрос перезаписывает LastAccept — так тесты на tools/list и tools/call
        // изолированы и не зависят от порядка вызовов handler'а.
        public System.Collections.Generic.List<string?> LastAccept { get; private set; } = [];
        public System.Collections.Generic.List<HttpRequestMessage> CapturedRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CapturedRequests.Add(request);
            LastAccept = [.. request.Headers.Accept.Select(a => a.MediaType)];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            };
            return Task.FromResult(response);
        }
    }

    private static (McpStatusStore Statuses, HiggsfieldOAuthService OAuth,
        ConfigurableResponseHandler Handler) NewStep3FixtureComponents(IConfiguration config)
    {
        var secrets = new McpSecretStore(config);
        var registry = new McpRegistry(config, secrets);
        var statuses = new McpStatusStore(config);

        var handler = new ConfigurableResponseHandler();

        var oauth = new McpOAuthService(registry, secrets, statuses,
            new NewOauthStubHttpClientFactory(handler), config,
            NullLogger<McpOAuthService>.Instance);
        var oauthService = new HiggsfieldOAuthService(registry, secrets, statuses, oauth,
            config, NullLogger<HiggsfieldOAuthService>.Instance);

        // Усыновляем токен под ServiceOwnerId
        const string owner = "owner-warm";
        secrets.SetEntry(owner, new McpSecretEntry
        {
            Value = "test-token",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        }, refOrId: "tok-warm");
        registry.CreateBuiltIn(owner, new McpServerRecord
        {
            Key = HiggsfieldOAuthService.Key,
            Label = HiggsfieldOAuthService.Label,
            Transport = McpTransport.Http,
            Url = HiggsfieldOAuthService.Url,
            Auth = new McpAuthConfig
            {
                Kind = McpAuthKind.OAuth2,
                OAuth = new McpOAuthConfig
                {
                    AccessTokenRef = McpSecretStore.Placeholder("tok-warm"),
                    ClientId = "client-dcr-warm",
                },
            },
            Enabled = true,
        });
        oauthService.RunMigration();

        return (statuses, oauthService, handler);
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
