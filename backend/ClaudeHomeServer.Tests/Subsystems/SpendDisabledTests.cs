using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Subsystems;

/// <summary>
/// Подсистема Spend выключена (гейт `DynamicModules:2:Enabled=false`) — тест уровня
/// ХОСТА: реальная маршрутизация MVC, не DI-изолированная проверка.
///
/// Spend — динамический модуль (сценарий Б): ModuleLoader грузит dll по пути из
/// `DynamicModules`-конфига. При `Enabled=false` dll не загружается вовсе,
/// `SpendController` не попадает в MVC-части → роутер не находит action → 404
/// (в тестовом хосте), а НЕ 500 от DI-активации контроллера без зависимостей.
///
/// Механизм передачи — `UseSetting` (аналог NotesDisabledTests): значение едет
/// в хост-конфигурацию → командная строка → CreateBuilder(args) → успевает
/// к ModuleLoader'у, который читает `DynamicModules:2:Enabled` из конфига.
/// </summary>
public class SpendDisabledTests : IDisposable
{
    private readonly DisabledSpendFactory _disabled = new();
    private readonly TestWebApplicationFactory _enabled = new();

    public void Dispose()
    {
        _disabled.Dispose();
        _enabled.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class DisabledSpendFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Двойной гейт (аналог NotesDisabledTests):
            // 1. Subsystems:spend:Enabled=false — традиционный гейт: делает enabled=false в снимке;
            // 2. DynamicModules:2:Enabled=false — ModuleLoader пропускает dll, active=false.
            builder.UseSetting("Subsystems:spend:Enabled", "false");
            builder.UseSetting("DynamicModules:2:Enabled", "false");
        }
    }

    // ─── Гейт доехал ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Гейт_ДоехалДоХоста_ПодсистемаНеАктивна()
    {
        var subsystems = await _disabled.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        var spend = subsystems.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "spend");

        spend.GetProperty("enabled").GetBoolean().Should().BeFalse("настройка обязана доехать");
        spend.GetProperty("active").GetBoolean().Should().BeFalse("Register выключенной подсистемы не вызывается");

        // Контроль: соседний хост (дефолт true) активен.
        var control = await _enabled.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        control.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "spend")
            .GetProperty("active").GetBoolean().Should().BeTrue();
    }

    // ─── Маршруты Spend выходят из MVC ───────────────────────────────────────────

    [Theory]
    [InlineData("/api/spend/overview")]
    [InlineData("/api/spend/widget")]
    [InlineData("/api/spend/pivot?groupBy=source")]
    public async Task МаршрутыSpend_ПриВыключеннойПодсистеме_MvcНеСопоставляетAction(string url)
    {
        var client = _disabled.CreateAuthenticatedClient();
        var response = await client.GetAsync(url);

        // 404 в тестовом хосте: динамический модуль не загружен,
        // ApplicationPart ClaudeHomeServer.Spend не подключён,
        // роутер не находит action → запрос уходит дальше (нет SPA в тесте → общий 404).
        // 500 означал бы, что ApplicationPart ещё подключён: MVC нашёл action,
        // попытался активировать SpendController, но DI не имеет SpendAnalyticsService.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"{url}: динамический модуль Spend не загружен (Enabled=false) — "
            + "MVC не находит action и запрос уходит дальше (404 в тестовом хосте). "
            + "500 означало бы, что контроллер активирован без зависимостей (модуль всё же загрузился)");
    }

    // Контроль: при включённой подсистеме тот же запрос обязан дойти до контроллера
    // (200/403 — но НЕ 404, иначе 404 выше мог быть по неверной причине — опечатка в маршруте).
    [Theory]
    [InlineData("/api/spend/overview")]
    [InlineData("/api/spend/widget")]
    [InlineData("/api/spend/pivot?groupBy=source")]
    public async Task МаршрутыSpend_ПриВключённойПодсистеме_Не404(string url)
    {
        var response = await _enabled.CreateAuthenticatedClient().GetAsync(url);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            $"{url}: при включённой подсистеме роутер обязан находить action");
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            $"{url}: включённая подсистема не даёт 500 — значит DI-зависимости на месте");
    }

    // ─── Соседние подсистемы живы без Spend (H1) ──────────────────────────────────
    //
    // ДЕФЕКТ (зоид живого хоста 2026-09-15): при `DynamicModules:2:Enabled=false` отвалился
    // НЕ ТОЛЬКО `/api/spend/*`, но и весь остальной продукт — `/api/tts/*` отдавал 500,
    // `/api/yandex/*` отдавал 500, резолв `IEnumerable<IMcpToolset>` падал с
    // InvalidOperationException у WebSearchToolset, `IIncidentLocalContext` не активировался
    // по той же причине.
    //
    // Корневая причина — nullable-параметры в конструкторах Main-side потребителей Spend
    // (`ISpendCollector?`/`ISpendDetailReader?`/`ISpendAnalytics`) без дефолта `= null`:
    // nullable-аннотация НЕ делает параметр опциональным для Microsoft DI, и без
    // включённой подсистемы резолв этих зависимостей проваливался. Покрытие ниже —
    // HOST-уровень (контроллеры резолвятся и не отдают 500), плюс DI-уровень (составы
    // MCP-тулсетов и инцидентов собираются без исключений). Без тестов этого слоя
    // существующий SpendDisabledTests был бы зелёным по неверной причине — он проверял
    // только маршруты spend/* (404), и не падал, потому что у него нет проверки
    // соседей. Аналог у `NotesDisabledTests.СоседниеВертикали_*` — оттуда форма кейсов.

    // 1. Резолв IMcpToolset у хоста не должен бросать при выключенной подсистеме Spend:
    //    без неё активация WebSearchToolset (и любого другого продукта с опциональной
    //    Spend-зависимостью) не пройдёт через DI, и ВСЕ инструменты упадут, не только websearch.
    [Fact]
    public void McpТулсеты_РезолвятсяПриВыключеннойПодсистемеSpend()
    {
        var act = () => _disabled.Services.GetServices<IMcpToolset>().ToList();

        act.Should().NotThrow<InvalidOperationException>(
            "IEnumerable<IMcpToolset> обязан резолвиться без Spend — иначе отказ одного " +
            "конструктора ложит ВСЕ продуктовые MCP-инструменты, не только websearch");

        // Контроль: при включённой подсистеме состав непуст — иначе «зелёный резолв» мог быть
        // достигнут по неверной причине (например, ранний возврат до списка тулсетов).
        _enabled.Services.GetServices<IMcpToolset>().Should().NotBeEmpty(
            "контрольный хост должен держать хотя бы один тулсет — иначе резолв выше нечего ловить");
    }

    // 2. IIncidentLocalContext (внутренний потребитель `ISpendDetailReader`) живёт
    //    в Main (Telemetry), регистрируется хостом — и должен резолвиться без Spend.
    //    Зоид: InvalidOperationException 'Unable to resolve ISpendDetailReader' ронял
    //    инциденты при выключенной аналитике.
    [Fact]
    public void IIncidentLocalContext_РезолвитсяПриВыключеннойПодсистемеSpend()
    {
        var act = () => _disabled.Services.GetService<ClaudeHomeServer.Telemetry.Incidents.IIncidentLocalContext>();

        act.Should().NotThrow<InvalidOperationException>(
            "IIncidentLocalContext обязан резолвиться без Spend — иначе инциденты не работают " +
            "у тех, кто выключил аналитику");
        act().Should().NotBeNull(
            "контекст инцидентов обязан собираться даже без аналитики расхода");
    }

    // 3. /api/tts — фронт вызывает в голосовом режиме, ошибка 500 от DI ломает
    //    голос для всех, кто выключил Spend. Без подсистемы tts всё равно отдаёт
    //    503 not_configured (Yandex:SpeechKit не настроен в тесте) — но это 503,
    //    а не 500 от неразрешённой зависимости.
    [Fact]
    public async Task Tts_ПриВыключеннойПодсистемеSpend_Не500()
    {
        var response = await _disabled.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/tts", new { text = "проверка" });

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            "выключение Spend не должно ломать синтез речи — DI-зависимость TtsController " +
            "от Spend обязана быть опциональной (покрыто сборкой через ISpendCollector?=null)");

        // Контракт 503 «not_configured» идёт из самого tts-контроллера (Yandex:SpeechKit не задан
        // в тесте) — это правильный код отказа. Альтернатива «500 от DI» была бы зоидом.
        // 503 принимаем как ожидаемый путь, главное — НЕ 500.
    }

    // 4. /api/yandex/account — Яндекс-биллинг живёт в Main и зовёт ISpendAnalytics для
    //    строки «spend» в выдаче. Без Spend у контроллера нет данных аналитики, и
    //    правильный путь — 503 + причина «Подсистема аналитики расхода выключена»
    //    (YandexController.Account проверяет analytics на null). 500 = дефект.
    [Fact]
    public async Task YandexAccount_ПриВыключеннойПодсистемеSpend_Возвращает503Не500()
    {
        var response = await _disabled.CreateAuthenticatedClient()
            .GetAsync("/api/yandex/account");

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            "выключение Spend не должно ломать /api/yandex/account — DI-зависимость " +
            "YandexController от ISpendAnalytics обязана быть опциональной");

        // Без Spend у YandexController единственный честный путь — 503 «выключено» (см. Account).
        // Альтернативы:
        // 200 с пустым spend = молчаливый успех (хуже ошибки: человек не понимает, почему блок пустой);
        // 404 = роутер потерял action (Yandex всегда живёт в Main, такого быть не должно).
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "без подсистемы Spend эндпоинт отдаёт 503 + причину «Подсистема аналитики расхода выключена»");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("выключена",
            "без причины клиент не отличит выключенную подсистему от сбоя");
    }

    // 5. Фикс `/api/{**rest}`-исключения (задача 6c7f3d34, ревью c66e4127):
    //    до фикса это была `app.Map("/api", ... api.Run(...404...))` — терминальная
    //    ветка middleware, выполнявшаяся В ПАЙПЛАЙНЕ до MVC и рубившая ВСЕ /api/* как 404
    //    (включая живые контроллеры). После фикса — `MapFallback("/api/{**rest}", ...)`,
    //    более специфичный, чем `MapFallbackToFile` — выигрывает у SPA-фолбэка для
    //    несопоставленных URL, но ПРОИГРЫВАЕТ реальным контроллерам. Чтобы этот
    //    тест «реально проверял фикс», хост поднимается с wwwroot/index.html
    //    (`TestWebApplicationFactory.UseFrontend=true` по умолчанию) — иначе
    //    `Program.cs` стоит в «else»-ветке БЕЗ MapFallback, и «404 при выключенном»
    //    зеленеет в вакууме (получает 404 от общего роутинга).
    [Fact]
    public async Task ApiSpend_ПриВыключеннойПодсистеме_Отдаёт404НеHtml()
    {
        var response = await _disabled.CreateAuthenticatedClient()
            .GetAsync("/api/spend/overview");

        // 404 приходит из `MapFallback("/api/{**rest}")` (Program.cs:1858) — fallback-
        // эндпоинт, более специфичный, чем MapFallbackToFile, но проигрывающий реальным
        // контроллерам. До фикса (`app.Map("/api", ...)`) тот же запрос рубился ВСЕ
        // РАНЬШЕ любого эндпоинта — здесь это видно по живому API в контрольном кейсе.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "выключенный модуль Spend — ApplicationPart не подключён, MVC не находит action, " +
            "MapFallback('/api/{**rest}') обязан перехватить несопоставленный запрос и отдать 404");
        response.Content.Headers.ContentType?.MediaType.Should().NotStartWith("text/html",
            "404 с text/html — тот же зоид, что был ДО фикса: SPA-фолбэк перехватывает /api/* и " +
            "клиент не отличит 404 от живой чат-страницы. С MapFallback('/api/{**rest}') content-type " +
            "НЕ выставляется (handler только StatusCode пишет) — значит проверка валидна");
    }

    // Контрольная пара: на включённой подсистеме тот же запрос идёт через MVC action
    // и отдаёт 200 JSON. Без неё 404 выше мог быть «зелёным по неверной причине» —
    // например, если бы MapFallback стоял ПЕРЕД контроллерами (по типу прежнего
    // `app.Map("/api", ...)`). Вместе эти два кейса закрывают «фикс не сломал живой API».
    [Fact]
    public async Task ApiSpend_ПриВключённойПодсистеме_Json200НеHtml()
    {
        var response = await _enabled.CreateAuthenticatedClient()
            .GetAsync("/api/spend/overview");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "при включённой подсистеме MVC обязан найти action и отдать 200 — это " +
            "главное доказательство, что MapFallback('/api/{**rest}') НЕ перебивает контроллеры");
        response.Content.Headers.ContentType?.MediaType.Should().StartWith("application/json",
            "MVC-ответ живого контроллера — JSON, а не SPA-fallback");
    }

    // ─── Снапшот подсистем при расхождении двух рубильников ─────────────────
    //
    // DEFFECT: `Subsystems:{key}:Enabled=false` + `DynamicModules:N:Enabled=true` —
    // модуль не попадал ни в `RecordActive` (ModuleLoader.LoadAll возвращает null по
    // гейту подсистемы), ни в `RecordDisabled` (цикл `!m.Enabled` фильтрует только
    // по DynamicModules). В снимке подсистема просто исчезала — админ не отличал
    // «выключено намеренно» от «забыли подключить». Покрытие ниже — единственный
    // случай этой развилки: гейт выключен, модуль явно в реестре DynamicModules.
    //
    // Существующий `Гейт_ДоехалДоХоста_ПодсистемаНеАктивна` (выше) выключает ОБА
    // рубильника сразу и не видит проблему: при двойном выключении DisabledModuleStub
    // корректно попадает в снимок через цикл `!m.Enabled`.
    private sealed class SubsystemsGateOffFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // ВКЛючаем DynamicModules — чтобы регистрация модуля прошла через ModuleLoader,
            // но SubsystemGate вернёт null (гейт Subsystems офф) — и цикл записи
            // RecordDisabled с ТРИГГЕРОМ по `!m.Enabled` здесь не сработает.
            builder.UseSetting("Subsystems:spend:Enabled", "false");
            builder.UseSetting("DynamicModules:2:Enabled", "true");
        }
    }

    [Fact]
    public async Task СнапшотПриSubsystemsOffDynamicModulesOn_ПоказываетВыключеноНамеренно()
    {
        using var factory = new SubsystemsGateOffFactory();
        var subsystems = await factory.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        var spend = subsystems.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "spend");

        // Запись обходит оба цикла в Program.cs (`RecordActive` через ModuleLoader,
        // `RecordDisabled` через `!m.Enabled`) — отдельный цикл по
        // `m.Enabled && !SubsystemGate.IsEnabled` (Program.cs:191-203 после фикса) обязан
        // дать запись, иначе `SpendController.GetAsync` отдаст 401/404 без следов
        // подсистемы.
        spend.GetProperty("key").GetString().Should().Be("spend",
            "запись spend обязана быть в снимке даже при Subsystems гейт off + DynamicModules on — " +
            "иначе админ не отличит «выключено намеренно» от «забыли подключить»");
        spend.GetProperty("enabled").GetBoolean().Should().BeFalse(
            "Subsystems:spend:Enabled=false обязан доехать до гейта");
        spend.GetProperty("active").GetBoolean().Should().BeFalse(
            "RecordDisabled через ModuleLoader.TryLoadOne НЕ вызывался (гейт выключил модуль " +
            "до загрузки dll), плюс цикл `!m.Enabled` для этого случая не работает — есть " +
            "только отдельный цикл `m.Enabled && !SubsystemGate.IsEnabled` (Program.cs)");
    }
}