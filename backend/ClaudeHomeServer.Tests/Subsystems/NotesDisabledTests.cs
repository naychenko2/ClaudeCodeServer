using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
/// <see cref="Гейт_ДоехалДоХоста_ПодсистемаНеАктивна"/>). Без этой проверки любой 404 ниже
/// мог бы оказаться ложноположительным — например, от опечатки в маршруте.
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
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Едет в хост-конфигурацию → командной строкой в CreateBuilder(args) → успевает
            // к AddSubsystems. Разбор механики и почему это не гонка — в шапке класса.
            builder.UseSetting("Subsystems:Notes:Enabled", "false");
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

    // ─── 1. Маршруты заметок: 404, а не 500 ──────────────────────────────────────

    // Контроллер заметок живёт в сборке вертикали и подключается к MVC отдельным
    // `ApplicationPart` (`NotesSubsystem`). При выключенной подсистеме сборка не должна
    // попадать в `ApplicationPartManager`: MVC не находит action и отдаёт ОБЩИЙ 404.
    //
    // Разница 404 vs 500 здесь не косметическая, а суть гейта: 500 означает, что action
    // НАЙДЕН и MVC пытается активировать контроллер, чьи зависимости (`NotesService` и
    // остальные три синглтона вертикали) в контейнер не попали. Это не «некрасивый код
    // ответа», а необработанное исключение на каждый запрос — и признак того, что
    // отключение подсистемы не изолировало её от остального приложения.
    // Маршруты подобраны так, чтобы 404 мог прийти ТОЛЬКО от роутинга: каждый из них при
    // включённой подсистеме отвечает содержательно на пустых данных. Поэтому здесь нет
    // `GET /api/notes/{id}` — у него 404 «нет такой заметки» неотличим от 404 «нет action»,
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
    public async Task МаршрутыЗаметок_ПриВыключеннойПодсистеме_404АНе500(string method, string url)
    {
        var response = await SendAsync(_disabled, method, url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"{method} {url}: ApplicationPart сборки ClaudeHomeServer.Notes не подключён — "
            + "MVC не находит action. 500 означал бы, что контроллер найден и MVC пытается "
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

    // `tools/list` — то, что видит модель на ходу. Пустой состав при выключенной подсистеме
    // и непустой при включённой: тулсет объявлен всегда (маршрут `/mcp/notes/{sessionId}`
    // жив), но инструменты не показываются — модель не получает того, что не отработает.
    [Fact]
    public async Task СоставИнструментовЗаметок_ПриВыключеннойПодсистеме_Пуст()
    {
        (await ListNotesToolsAsync(_disabled)).Should().Be(0,
            "инструменты заметок не должны объявляться при выключенной вертикали");

        (await ListNotesToolsAsync(_enabled)).Should().BeGreaterThan(0,
            "контроль: при включённой подсистеме состав непуст — значит ноль выше даёт "
            + "именно гейт, а не ошибка в вызове tools/list");
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
}
