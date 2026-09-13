using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Subsystems;

/// <summary>
/// Подсистема Notes выключена — тест УРОВНЯ ХОСТА: реальная маршрутизация MVC, реальный
/// состав MCP-тулсета и реальный набор контрибьюторов промпта у поднятого приложения.
///
/// Не пересекается с <c>NotesSubsystemGateTests</c>: тот про DI-контейнер (reflection по
/// конструкторам плюс изолированный <c>ServiceCollection</c>) и по построению не видит ни
/// роутинг, ни `ApplicationPart`, ни состав `tools/list`. Здесь — наоборот: контейнер не
/// трогаем вовсе, смотрим только наблюдаемое поведение хоста.
///
/// ═══ Как сюда попадает `Subsystems:Notes:Enabled=false` (главная грабля задачи) ═══
///
/// Гейт читается в `AddSubsystems` — то есть ПОСЛЕ `WebApplication.CreateBuilder(args)`,
/// но ДО того, как отработают `ConfigureAppConfiguration`-колбэки фабрики. Поэтому
/// `TestWebApplicationFactory.ExtraConfig` (он же `AddInMemoryCollection`) сюда не успевает:
/// «`ConfigureWebHost` применяется ОТЛОЖЕННО» — та же грабля, что описана в
/// `TestEnvironmentGuard` и в шапке `NotesSubsystemGateTests`.
///
/// Прежний вывод «единственный способ — переменная окружения ПРОЦЕССА» неверен, и это
/// принципиально: env-переменная процесса протекала бы в ЛЮБОЙ конкурентно строящийся хост
/// (`AddEnvironmentVariables()` есть у каждого), а `xunit.runner.json` держит
/// `parallelizeTestCollections: true` — полный прогон ловил это падением `NotesControllerTests`
/// 500-ми. Здесь используется `IWebHostBuilder.UseSetting`, и он от этой болезни свободен:
///
///   `UseSetting` пишет в `_config` объекта `GenericWebHostBuilder`, а тот подмешан в
///   ХОСТ-конфигурацию через `ConfigureHostConfiguration`. У `WebApplicationFactory` роль
///   `IHostBuilder` играет `DeferredHostBuilder`, который в `Build()` разворачивает всю
///   накопленную хост-конфигурацию в аргументы командной строки (`--ключ=значение`) и
///   передаёт их в точку входа. `WebApplication.CreateBuilder(args)` добавляет
///   `AddCommandLine(args)` — значение оказывается в конфигурации ДО `AddSubsystems`.
///
/// Ключевое следствие: настройка едет ВНУТРИ этого хоста и в процесс не просачивается —
/// соседние тестовые хосты в тех же потоках её не видят, и гонки нет по конструкции, а не
/// по расписанию. Изолировать коллекцию xunit или заводить отдельную тестовую сборку не
/// потребовалось.
///
/// Что настройка реально доехала — проверяется не косвенно, а явно: `GET /api/admin/subsystems`
/// обязан показать `notes: enabled=false, active=false` (тест
/// <see cref="Гейт_ДоехалДоХоста_ПодсистемаНеАктивна"/>). Без этой проверки любой отказ
/// маршрута ниже мог бы оказаться ложноположительным — например, от опечатки в маршруте.
///
/// ═══ Что именно утверждают HTTP-кейсы (и чего они НЕ утверждают) ═══
///
/// Предмет проверки — «MVC не сопоставил action маршруту заметок», а НЕ «клиент получает 404».
/// 404 здесь — способ это наблюдать, а не обещание продукта: в тестовом хосте нет собранного
/// фронта, поэтому `MapFallbackToFile("index.html")` (`Program.cs`, ветка «фронт найден») не
/// срабатывает и несопоставленный запрос доходит до общего 404 роутинга.
///
/// НА ПРОДЕ ТОТ ЖЕ ЗАПРОС ДАЁТ 200 `text/html`: `MapFallbackToFile` стоит без ограничения
/// пути, и любой несопоставленный `/api/**` перехватывает SPA-фолбэк. Это свойство всего
/// приложения, а не пилота (на `master` то же самое), заведено дефектом `db09722c`. Гейт при
/// этом работает: 200 `text/html` означает ровно то же, что 404 здесь, — маршрут вышел из MVC.
/// Сломанный гейт выглядел бы иначе: 500 плюс запись `UnhandledExceptionHandler` в логе.
///
/// Как сделать честнее (правильно, но меняет предмет теста — не делалось перед вливанием):
/// утверждать про `EndpointDataSource` хоста — ни одного эндпоинта `NotesController` при
/// закрытом гейте, контроль при открытом; такое утверждение не зависит от окружения и
/// переживёт починку `db09722c`, после которой нынешние 404 станут проверять фолбэк.
/// </summary>
public class NotesDisabledTests : IDisposable
{
    // Хост с выключенной вертикалью заметок.
    private readonly DisabledNotesFactory _disabled = new();

    // Контрольный хост: та же фабрика без оверрайда — подсистема включена (дефолт true).
    // Нужен именно рядом: он доказывает, что все «пусто/404» ниже вызваны ИМЕННО гейтом,
    // а не тем, что проверка написана мимо (мимо маршрута, мимо имени тулсета, мимо ключа).
    private readonly TestWebApplicationFactory _enabled = new();

    public void Dispose()
    {
        _disabled.Dispose();
        _enabled.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class DisabledNotesFactory : TestWebApplicationFactory
    {
        // Записи лога уровня Error поднятого хоста: ими проверяется, что отказ брифа ушёл
        // границей контроллера, а не через UnhandledExceptionHandler (тот пишет LogError
        // со стектрейсом на каждое необработанное исключение).
        public ErrorLogSink Errors { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Едет в хост-конфигурацию → командной строкой в CreateBuilder(args) → успевает
            // к AddSubsystems. Разбор механики и почему это не гонка — в шапке класса.
            builder.UseSetting("Subsystems:Notes:Enabled", "false");
            // Dynamic module: без этого ModuleLoader загрузит dll и зарегистрирует все
            // сервисы вертикали, обходя Subsystems:Notes:Enabled (forwarders).
            builder.UseSetting("DynamicModules:1:Enabled", "false");
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Errors));
        }
    }

    // Сток записей лога уровня Error: у TestServer нет консольного вывода, а проверить нужно
    // сам факт записи. Уровнем ниже Error не интересуемся — предмет проверки ровно один:
    // не появилось ли записи от UnhandledExceptionHandler.
    public sealed class ErrorLogSink : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Snapshot()
        {
            lock (_entries) return [.. _entries];
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        public void Dispose() { }

        private void Add(string entry)
        {
            lock (_entries) _entries.Add(entry);
        }

        private sealed class Sink(ErrorLogSink owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error) return;
                owner.Add($"{category}: {formatter(state, exception)}");
            }
        }
    }

    // ─── Гейт доехал ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Гейт_ДоехалДоХоста_ПодсистемаНеАктивна()
    {
        var subsystems = await _disabled.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        var notes = subsystems.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "notes");

        notes.GetProperty("enabled").GetBoolean().Should().BeFalse("настройка обязана доехать до AddSubsystems");
        notes.GetProperty("active").GetBoolean().Should().BeFalse("Register выключенной подсистемы не вызывается");

        // Контроль: у соседнего хоста та же подсистема активна — значит false выше пришло
        // от UseSetting, а не от дефолта/сломанного эндпоинта.
        var control = await _enabled.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        control.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "notes")
            .GetProperty("active").GetBoolean().Should().BeTrue();
    }

    // ─── 1. Маршруты заметок выходят из MVC ──────────────────────────────────────

    // Контроллер заметок живёт в сборке вертикали и подключается к MVC отдельным
    // `ApplicationPart` (`Program.cs` снимает часть при закрытом гейте). При выключенной
    // подсистеме сборка не должна попадать в `ApplicationPartManager`: MVC не находит
    // action, и запрос уходит дальше по конвейеру — в тестовом хосте до общего 404.
    //
    // Наблюдаемый код ответа тут — следствие окружения (разбор в шапке класса: на проде
    // это 200 `text/html` от SPA-фолбэка, дефект `db09722c`), а утверждается им ровно одно:
    // action НЕ сопоставлен. Значимая альтернатива — 500: он означал бы, что action НАЙДЕН
    // и MVC пытается активировать контроллер, чьи зависимости (`NotesService` и остальные
    // три синглтона вертикали) в контейнер не попали. Это не «некрасивый код ответа», а
    // необработанное исключение на каждый запрос — и признак того, что отключение
    // подсистемы не изолировало её от остального приложения.
    // Маршруты подобраны так, чтобы отказ мог прийти ТОЛЬКО от роутинга: каждый из них при
    // включённой подсистеме отвечает содержательно на пустых данных. Поэтому здесь нет
    // `GET /api/notes/{id}` — у него 404 «нет такой заметки» неотличим от «нет action»,
    // и контрольный кейс на нём был бы зелёным по неверной причине.
    public static IEnumerable<object[]> NotesRoutes =>
    [
        ["GET", "/api/notes"],
        ["GET", "/api/notes/sources"],
        ["GET", "/api/notes/caps"],
        ["POST", "/api/notes"],
    ];

    [Theory]
    [MemberData(nameof(NotesRoutes))]
    public async Task МаршрутыЗаметок_ПриВыключеннойПодсистеме_MvcНеСопоставляетAction(string method, string url)
    {
        var response = await SendAsync(_disabled, method, url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"{method} {url}: ApplicationPart сборки ClaudeHomeServer.Notes не подключён — "
            + "MVC не сопоставляет action, и запрос уходит дальше по конвейеру. Утверждается "
            + "именно это; 404 — то, чем несопоставленный маршрут заканчивается в тестовом "
            + "хосте (на проде его перехватит SPA-фолбэк 200 text/html, дефект db09722c, "
            + "разбор в шапке класса). 500 означал бы, что контроллер найден и MVC пытается "
            + "активировать его без зависимостей выключенной вертикали");
    }

    // Тот же набор маршрутов на включённой подсистеме обязан отвечать НЕ 404 — иначе
    // тест выше был бы зелёным просто оттого, что маршруты написаны с ошибкой.
    [Theory]
    [MemberData(nameof(NotesRoutes))]
    public async Task МаршрутыЗаметок_ПриВключённойПодсистеме_НеТеряютсяРоутером(string method, string url)
    {
        var response = await SendAsync(_enabled, method, url);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            $"{method} {url}: при включённой подсистеме роутер обязан находить action "
            + "(содержательный код ответа — дело самих тестов заметок)");
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    private static Task<HttpResponseMessage> SendAsync(
        TestWebApplicationFactory factory, string method, string url)
    {
        var client = factory.CreateAuthenticatedClient();
        return method == "POST"
            // Тело намеренно валидное по форме: цель — дойти до роутинга, а не поймать 400.
            ? client.PostAsJsonAsync(url, new { title = "проба", source = "personal" })
            : client.GetAsync(url);
    }

    // ─── 2. Соседние вертикали не сломаны ────────────────────────────────────────

    [Theory]
    [InlineData("/api/tasks")]
    [InlineData("/api/projects")]
    public async Task СоседниеВертикали_ПриВыключенныхЗаметках_ОтвечаютШтатно(string url)
    {
        var response = await _disabled.CreateAuthenticatedClient().GetAsync(url);

        // `TasksController` берёт `NoteTaskSyncService` из вертикали заметок, а
        // `ProjectsController` — `NotesKnowledgeService`; оба параметра опциональны
        // (`NotesSubsystemGateTests` сторожит это по конструкторам). Здесь проверяется
        // следствие уровня хоста: соседние разделы продолжают работать, а не отдают 500.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"{url} не зависит от подсистемы заметок и обязан отвечать штатно");
    }

    // ─── 3. Состав MCP-тулсета заметок ───────────────────────────────────────────

    // Динамический модуль: при `DynamicModules:1:Enabled=false` DLL не грузится вовсе,
    // `NotesToolset` в DI не попадает, и `McpToolsetRegistry.Find("notes")` = null → 404.
    // Контрольный хост (модуль на месте) обязан вернуть >0 инструментов.
    [Fact]
    public async Task СоставИнструментовЗаметок_ПриВыключеннойПодсистеме_Пуст()
    {
        var status = await ListNotesToolsStatusAsync(_disabled);
        status.Should().Be(404,
            "динамический модуль не загружен — тулсета «notes» в DI нет, MCP-транспорт "
            + "отвечает unknown_mcp_server (404), а не 200 с пустым списком");

        (await ListNotesToolsAsync(_enabled)).Should().BeGreaterThan(0,
            "контроль: при включённой подсистеме (модуль на месте) состав непуст — "
            + "значит 404 выше даёт именно гейт, а не ошибку в маршруте");
    }

    private static async Task<int> ListNotesToolsAsync(TestWebApplicationFactory factory)
    {
        var client = factory.CreateAuthenticatedClient();

        // Хвост маршрута — сессия-вызыватель; она обязана принадлежать владельцу токена.
        // Заодно это ещё одно свидетельство к пункту 2: проекты и сессии при выключенных
        // заметках создаются как обычно.
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"notes-off-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var list = await client.PostAsJsonAsync($"/mcp/notes/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        list.EnsureSuccessStatusCode();

        return (await list.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("tools").GetArrayLength();
    }

    /// <summary>
    /// Статус HTTP-ответа `tools/list` без броска: 404 — тулсет в DI отсутствует
    /// (динамический модуль не загружен), 200 — тулсет на месте.
    /// </summary>
    private static async Task<int> ListNotesToolsStatusAsync(TestWebApplicationFactory factory)
    {
        var client = factory.CreateAuthenticatedClient();
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"notes-st-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var list = await client.PostAsJsonAsync($"/mcp/notes/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        return (int)list.StatusCode;
    }

    // ─── 4. Ход чата идёт без секции заметок ─────────────────────────────────────

    // Секции системного промпта собираются шиной из `IEnumerable<IPromptSectionContributor>`
    // (ADR-013): что зарегистрировано в контейнере ХОСТА, то и уедет в промпт хода. При
    // выключенной подсистеме `NotesRecallContributor` не регистрируется вовсе — секции
    // «recall-notes» на ходу не появится, и ход не упирается в неразрешимый
    // `NotesKnowledgeService`.
    //
    // Проверяется набор поднятого приложения, а не синтетическая коллекция: именно здесь
    // видно, что живой DI-граф со ВСЕМИ подсистемами сходится без заметок.
    [Fact]
    public void КонтрибьюторыПромптаХоста_ПриВыключеннойПодсистеме_БезСекцииЗаметок()
    {
        var off = ContributorKeys(_disabled);
        var on = ContributorKeys(_enabled);

        off.Should().NotContain("recall-notes",
            "контрибьютор заметок регистрируется внутри NotesSubsystem.Register, который "
            + "при выключенном гейте не вызывается");
        on.Should().Contain("recall-notes",
            "контроль: при включённой подсистеме секция на месте — значит её отсутствие "
            + "выше даёт гейт, а не опечатка в ключе секции");

        // Остальные секции промпта на месте: выключение заметок не проредило чужие
        // вертикали (иначе «нет recall-notes» было бы правдой по неверной причине).
        off.Should().BeEquivalentTo(on.Except(["recall-notes"]),
            "гейт заметок обязан снять ровно одну секцию");
    }

    private static IReadOnlyList<string> ContributorKeys(TestWebApplicationFactory factory) =>
        [.. factory.Services.GetServices<IPromptSectionContributor>().Select(c => c.Key)];

    // ─── 5. Утренний бриф: заявленный отказ вместо 500 ───────────────────────────

    // `BriefingController` живёт в сборке Main и маршрутизируется при любом гейте — здесь
    // «404 от роутинга» из пункта 1 не работает: запрос доходит до `DailyBriefingService`,
    // а тому без vault писать бриф некуда (бриф по конструкции пишется в дневниковую заметку).
    //
    // Отказ обязан быть заявленным (503 + текст причины), а не необработанным исключением:
    // 500 означал бы, что REST-путь не защищён ничем — фронт кнопку брифа при выключенной
    // подсистеме прячет (`notesOn` в lib/ai/actions.tsx), но REST дёргают и мимо UI, и каждый
    // такой запрос оставлял бы в логе стектрейс от `UnhandledExceptionHandler`.
    [Fact]
    public async Task УтреннийБриф_ПриВыключеннойПодсистеме_ОтказБезПятисоткиИБезLogError()
    {
        // Проба стока: без неё «новых записей об ошибке нет» было бы правдой и у провайдера,
        // который к логгеру хоста не подключён вовсе, — проверка стала бы вакуумной.
        _disabled.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("проба-стока").LogError("сток лога подключён");
        var errorsBefore = _disabled.Errors.Snapshot();
        errorsBefore.Should().Contain(e => e.Contains("сток лога подключён"),
            "сток обязан ловить записи логгера ХОСТА — иначе проверка ниже ничего не значит");

        var response = await _disabled.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/briefing/today", new { });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "подсистема заметок выключена — бриф писать некуда, и это состояние инстанса, "
            + "а не сбой: клиент обязан получить заявленный отказ, а не ProblemDetails 500");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Заметки",
            "в отказе нужна причина: без неё клиенту нечем отличить выключенную подсистему от поломки");

        // Фоновые сервисы хоста логируют своё — сравниваем не «ноль ошибок вообще», а именно
        // отсутствие записи последнего рубежа пайплайна (UnhandledExceptionHandler.cs:41-47).
        // Проверено мутацией (возврат к InvalidOperationException): ассерт краснеет с записью
        // «Необработанное исключение на POST … System.InvalidOperationException в
        // DailyBriefingService.BuildAndWriteAsync» — то есть ловит именно то, ради чего стоит.
        _disabled.Errors.Snapshot().Skip(errorsBefore.Count)
            .Should().NotContain(e => e.Contains("Необработанное исключение"),
                "исключение поймано контроллером — до UnhandledExceptionHandler не доходит");
    }

    // Контроль рядом: тот же запрос на включённой подсистеме доходит до конца. Без него 503
    // выше мог бы приходить по неверной причине — от опечатки в маршруте, формы тела или
    // упавшего ICheapTextRunner (в тестовом хосте он застаблен и отвечает мгновенно).
    [Fact]
    public async Task УтреннийБриф_ПриВключённойПодсистеме_СобираетсяШтатно()
    {
        var response = await _enabled.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/briefing/today", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "с включённой подсистемой бриф записывается в дневниковую заметку и возвращается ею");
    }
}
