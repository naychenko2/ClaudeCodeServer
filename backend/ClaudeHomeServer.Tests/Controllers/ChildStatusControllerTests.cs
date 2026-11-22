using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-B5e: GET /api/child/status и POST /api/child/heartbeat (ADR-041 «Контракт», ручки №1–2)
/// и isChild/childSpaces в /api/auth/me (№11).
///
/// Стаб IOwnerSpendReader (0.1/0.5 USD) регистрируется на хосте класса: состояние «ok» должно
/// быть решено тестом, а не тем, загружен ли в этом хосте динамический модуль Spend — без швы
/// калькулятор по своей fail-cлогике даёт budget_exhausted на любом профиле, и «ok» нельзя было
/// бы отличить от «модуль не поднят».
///
/// Окно «Уроков» генерируется от текущего московского времени (±2 ч): у ребёнка без
/// User.TimeZone таймзона — ChildMode:DefaultTimeZone = Europe/Moscow (appsettings, решение
/// Андрея 3), а «ok» требует быть в окне в момент запроса. «HH:mm» оборачивает циферблат —
/// окно через полночь законно (ADR, ChildProfileValidator.cs:70).
///
/// 503 policy_unavailable (хвосты R-KM-B5e2): сбой записи журнала — настоящим store'ом, у
/// которого путь журнала этого ребёнка занят каталогом (Heartbeat_СбойЗаписиЖурнала…);
/// нечитаемый профиль — через гонку гейта и контроллера (Status_ГонкаСГатом…): гейт читает
/// профиль до правки, контроллер — после (ChildStatusController.cs:21). С Г14 статичный
/// DenyAll-ребёнок доходит до этой ветки и напрямую — гонка остаётся одной из дорог к ней.
/// </summary>
[Trait("Category", "Host")]
public class ChildStatusControllerTests : IClassFixture<ChildStatusControllerTests.Hosts>
{
    /// <summary>Хост на класс (не на тест): стаб швы регистрируется после встроенных регистраций
    /// и именно его резолвит GetService&lt;IOwnerSpendReader&gt; в фабрике калькулятора (Program.cs:611).</summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraServices = services =>
        {
            services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
            // Политика — обёртка RacePolicy (см. ниже): для одного ребёнка по имени она роняет
            // профиль после первого чтения, для всех прочих — точная делегация боевой политике.
            services.AddSingleton<IChildModePolicy>(sp => new RacePolicy(sp.GetRequiredService<IUserStore>()));
        };
    }

    // Расход ниже любого бюджета профиля: «бюджет не проверить» не должно быть ни в одном тесте.
    private sealed class FixedSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0.1m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0.5m;
    }

    // Ребёнок теста-гонки (Status_ГонкаСГатом…): единственный, чей профиль обёртка роняет
    // после первого успешного чтения; остальные имена проходят насквозь без изменений.
    private const string RaceUsername = "kid-denyall-race";

    /// <summary>
    /// Обёртка боевой политики, воспроизводящая гонку гейта и контроллера — одну из дорог к
    /// ветке DenyAll в контроллере (ChildStatusController.cs:21). С Г14 статичный DenyAll-ребёнок
    /// доходит до неё и напрямую (гейт не отсекает), но гонка остаётся отдельной проверкой той же
    /// ветки. Первое успешное чтение профиля RaceUsername отдаётся как есть (гейт проходит) и сразу
    /// роняет профиль в сторе — правка «взрослого» посреди запроса; следующее чтение (контроллера)
    /// — настоящий DenyAll боевого ChildModePolicy, без синтетических снимков.
    /// </summary>
    private sealed class RacePolicy(IUserStore users) : IChildModePolicy
    {
        private readonly ChildModePolicy _inner = new(users);

        public ChildPolicySnapshot? For(string ownerId)
        {
            var snapshot = _inner.For(ownerId);
            if (snapshot is { DenyAll: false } && users.GetById(ownerId) is { Username: RaceUsername, ChildMode: { } profile })
                profile.TutorPersonaId = ""; // невалиден по IsValid (ChildModePolicy.cs:36) → следующее чтение — DenyAll
            return snapshot;
        }
    }

    private const string KidsKey = "kids-api";
    private const string KidsApiKey = "sk-ant-test-kids";
    private const string ChildPassword = "kids-test-password";

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly DateTime RulesT0 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;

    public ChildStatusControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = KidsApiKey;
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
    }

    // ─── преамбула: ребёнок через боевые точки записи (админский PUT профиля, как KM-B1e) ───

    private static (string From, string To) WindowAroundNow()
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        return (
            local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture),
            local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>Контейнерный пользователь + включённый режим через админскую ручку:
    /// «Уроки» с окном ±2 ч и дневным лимитом 40 минут (withLessons=false — профиль без
    /// пространства: законное значение, которое проверяет 403 space_disabled).
    /// fixedWindow — фиксированное окно вместо «вокруг сейчас» (тестам добавочных полей
    /// статус важен сам формат, а не состояние); dailyMinutes=null — лимита нет;
    /// noWindows — запись пространства без окон (законное значение: «каждый день, без окон»).</summary>
    private async Task<string> NewChild(
        string name, bool withLessons = true, int? dailyMinutes = 40,
        (string From, string To)? fixedWindow = null, bool noWindows = false)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name,
            password = ChildPassword,
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var (fromH, toH) = fixedWindow ?? WindowAroundNow();
        object[] windowRecords = noWindows ? [] : [new { from = fromH, to = toH }];
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = withLessons
                ? new Dictionary<string, object>
                {
                    ["lessons"] = new { windows = windowRecords, dailyMinutes },
                }
                : new Dictionary<string, object>(),
            dailyBudgetUsd = 2,
            monthlyBudgetUsd = 40,
            acknowledgeRouteRisk = true, // без согласия профиля маршрут ребёнка не принимается (И2″)
        };
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", body);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private HttpClient ChildClient(string username) => _hosts.CreateAuthenticatedClient(username, ChildPassword);

    /// <summary>Согласие правил — прямая запись в профиль стора (тот же приём, что SeedRules
    /// у UsersChildProfileTests): без неё старшая причина всегда rules_pending.</summary>
    private void AgreeRules(string childId)
    {
        var profile = _users.GetById(childId)!.ChildMode!;
        profile.RulesAgreedAt = RulesT0;
        profile.RulesSummary = "Уроки по будням после школы";
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string CodeOf(JsonElement body) =>
        body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    private static string[] StringArray(JsonElement arr) =>
        arr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

    // ─── ручка №1: status ───

    [Fact]
    public async Task Status_Ребёнок_200_Базовые9ПолейChildStatus()
    {
        var id = await NewChild("kid-status-ok");
        AgreeRules(id);
        var client = ChildClient("kid-status-ok");

        var (status, body) = await ReadAsync(await client.GetAsync("/api/child/status"));

        status.Should().Be(HttpStatusCode.OK);
        // Все девять полей контракта (ADR-041 «DTO»), имена — camelCase, как ждёт фронт
        // (features/childMode/types.ts).
        body.GetProperty("state").GetString().Should().Be("ok");
        body.GetProperty("space").GetString().Should().Be("lessons");
        body.GetProperty("minutesLeft").GetInt32().Should().Be(40); // лимит 40, присутствия нет
        body.TryGetProperty("windowEndsAt", out var ends).Should().BeTrue();
        ends.ValueKind.Should().Be(JsonValueKind.String, "сейчас внутри окна — конец текущего окна есть");
        DateTimeOffset.Parse(ends.GetString()!, CultureInfo.InvariantCulture).Should().BeAfter(DateTimeOffset.UtcNow);
        body.GetProperty("nextWindowAt").ValueKind.Should().Be(JsonValueKind.Null, "не out_of_window — по контракту null");
        body.GetProperty("warnMinutes").GetInt32().Should().Be(5);
        body.GetProperty("maxAttachmentBytes").GetInt64().Should().Be(10485760);
        StringArray(body.GetProperty("attachmentTypes")).Should()
            .Equal("image/png", "image/jpeg", "image/webp", "application/pdf", "text/plain");
        body.GetProperty("rulesAgreedAt").GetString().Should().Be(RulesT0.ToString("O"));
    }

    [Fact]
    public async Task Status_Взрослый_403_not_child()
    {
        var (status, body) = await ReadAsync(await _admin.GetAsync("/api/child/status"));

        status.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(body).Should().Be("not_child");
        // Тело — контракт {code, message}; message — текст для ребёнка по-русски.
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Status_НеизвестноеПространство_400_unknown_space()
    {
        await NewChild("kid-status-unknown");
        var client = ChildClient("kid-status-unknown");

        var (status, body) = await ReadAsync(await client.GetAsync("/api/child/status?space=foo"));

        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(body).Should().Be("unknown_space");
    }

    [Fact]
    public async Task Status_ПространстваНетВПрофиле_403_space_disabled()
    {
        // Законный профиль без записей пространств: ключ известен системе, но этому ребёнку
        // пространство не открыто — 403, а не 400.
        await NewChild("kid-status-disabled", withLessons: false);
        var client = ChildClient("kid-status-disabled");

        var (status, body) = await ReadAsync(await client.GetAsync("/api/child/status"));

        status.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(body).Should().Be("space_disabled");
    }

    [Fact]
    public async Task Status_ГонкаСГатом_НечитаемыйПрофиль_503_PolicyUnavailable()
    {
        var id = await NewChild(RaceUsername);
        AgreeRules(id);
        var client = ChildClient(RaceUsername);

        // Единственная достижимая через HTTP дорога к отказу «профиль не читается» (И3, ADR №1):
        // гейт читает профиль валидным и пропускает запрос; к чтению контроллера профиль уже
        // нечитаем (RacePolicy — «правка взрослого» посреди запроса). Ответ — 503, не 403 гейта
        // и не 500: необработанного исключения наружу нет (ChildStatusController.cs:68).
        var (status, body) = await ReadAsync(await client.GetAsync("/api/child/status"));

        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(body).Should().Be("policy_unavailable");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ─── добавочные поля статуса (KM-B5e2): dailyMinutes, windows, minutesToday ───

    [Fact]
    public async Task Status_ПрофильСУстановками_ДобавочныеПоляСтатуса()
    {
        // Фиксированное окно 16:00–18:00 и лимит 40: поля статуса — цифры профиля как есть,
        // а не подстановка minutesLeft и пустых окон (решение 3 финала 703c954f).
        var id = await NewChild("kid-status-fields", fixedWindow: ("16:00", "18:00"));
        AgreeRules(id);

        // 12 целых минут сегодня — записью в боевой store (тот же путь журнала, что у
        // heartbeat): 02:00 МСК текущего местного дня — всегда внутри него, тест не зависит
        // от того, насколько «сейчас» близко к полуночи.
        var usage = _hosts.Services.GetRequiredService<ChildUsageStore>();
        var dayStart = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).Date;
        for (var m = 0; m < 12; m++)
            usage.RecordMinute(id, "lessons", new DateTimeOffset(dayStart.AddHours(2).AddMinutes(m), TimeSpan.FromHours(3)), Moscow);

        var client = ChildClient("kid-status-fields");
        var (status, body) = await ReadAsync(await client.GetAsync("/api/child/status"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("dailyMinutes").GetInt32().Should().Be(40);
        var windows = body.GetProperty("windows").EnumerateArray().ToList();
        windows.Should().HaveCount(1, "окна отдаются из профиля как есть, в формате профиля");
        windows[0].GetProperty("from").GetString().Should().Be("16:00");
        windows[0].GetProperty("to").GetString().Should().Be("18:00");
        // Мутация «minutesToday всегда 0» краснит эту строку: итог дня — из журнала, не константа.
        body.GetProperty("minutesToday").GetInt32().Should().Be(12);
        // Существующее поле согласно новому: остаток = лимит − итог дня.
        body.GetProperty("minutesLeft").GetInt32().Should().Be(28);
    }

    [Fact]
    public async Task Status_ПрофильБезЛимитаИОкон_DailyMinutesNull_WindowsEmpty()
    {
        await NewChild("kid-status-nolimit", dailyMinutes: null, noWindows: true);
        var client = ChildClient("kid-status-nolimit");

        var (status, body) = await ReadAsync(await client.GetAsync("/api/child/status"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("dailyMinutes").ValueKind.Should().Be(JsonValueKind.Null, "лимита нет — null, не 0");
        body.GetProperty("windows").GetArrayLength().Should().Be(0, "окон нет — пустой список, не null");
        // Итог дня виден и без лимита: журнала нет — честный 0.
        body.GetProperty("minutesToday").GetInt32().Should().Be(0);
        body.GetProperty("minutesLeft").ValueKind.Should().Be(JsonValueKind.Null, "без лимита остатка нет");
    }

    // ─── ручка №2: heartbeat ───

    [Fact]
    public async Task Heartbeat_ЗасчитываетМинуту_ИОтдаётТотЖеStatus()
    {
        var id = await NewChild("kid-hb-basic");
        AgreeRules(id);
        var client = ChildClient("kid-hb-basic");

        var (status, hb) = await ReadAsync(await client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" }));

        status.Should().Be(HttpStatusCode.OK);
        // Ответ heartbeat — тот же ChildStatus (экономит второй запрос) и отражает только что
        // засчитанную минуту: остаток 40−1.
        hb.GetProperty("state").GetString().Should().Be("ok");
        hb.GetProperty("space").GetString().Should().Be("lessons");
        hb.GetProperty("minutesLeft").GetInt32().Should().Be(39);

        var (_, after) = await ReadAsync(await client.GetAsync("/api/child/status"));
        after.GetProperty("minutesLeft").GetInt32().Should().Be(39, "минута в журнале — у обеих ручек одна");
    }

    [Fact]
    public async Task Heartbeat_ДубльВТотуЖеМинуту_НеДублируется()
    {
        var id = await NewChild("kid-hb-dup");
        AgreeRules(id);
        var client = ChildClient("kid-hb-dup");

        var first = await client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" });
        var (status, second) = await ReadAsync(await client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" }));
        (await first.Content.ReadAsStringAsync()).Should().NotBeEmpty();

        status.Should().Be(HttpStatusCode.OK);
        second.GetProperty("minutesLeft").GetInt32().Should().Be(39,
            "ключ записи — (день, пространство, минута): повтор той же минуты минуту не добавляет");
    }

    [Fact]
    public async Task Heartbeat_TwoParallelRequests_CountsOneMinute()
    {
        var id = await NewChild("kid-hb-race");
        AgreeRules(id);
        var client = ChildClient("kid-hb-race");

        // Две вкладки одного ребёнка: два heartbeat одновременно в одну минуту.
        var a = client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" });
        var b = client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" });
        var results = await Task.WhenAll(a, b);
        results[0].StatusCode.Should().Be(HttpStatusCode.OK);
        results[1].StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, after) = await ReadAsync(await client.GetAsync("/api/child/status"));
        after.GetProperty("minutesLeft").GetInt32().Should().Be(39,
            "одновременные вызовы засчитывают одну минуту: read-modify-write под общим локом по пути файла");
    }

    [Fact]
    public async Task Heartbeat_СбойЗаписиЖурнала_503_PolicyUnavailable_МинутаНеЗасчитана()
    {
        var id = await NewChild("kid-hb-journal-fail");
        AgreeRules(id);
        var client = ChildClient("kid-hb-journal-fail");

        // Боевой store, отказ на записи: путь журнала этого ребёнка занят каталогом —
        // File.Move внутри JsonFileStore.Save бросает. Подмены стора нет: тот же
        // ChildUsageStore, что поднят в хосте, по тому же корню (RootFromConfig).
        var root = ChildUsageStore.RootFromConfig(_hosts.Services.GetRequiredService<IConfiguration>());
        Directory.CreateDirectory(Path.Combine(root, id + ".json"));

        var (status, body) = await ReadAsync(await client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" }));

        // Сбой журнала — 503 с причиной, не 500: необработанное исключение из try/catch
        // вокруг RecordMinute (ChildStatusController.cs:79-91) наружу не выходит.
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(body).Should().Be("policy_unavailable");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        // Минута не засчитана: запись не состоялась — журнал пуст, остаток полный.
        // Read минуты не читает каталог как файл (File.Exists false → 0), поэтому status
        // отвечает обычно: 503 heartbeat не должен ломать и саму ручку статуса.
        var (afterStatus, after) = await ReadAsync(await client.GetAsync("/api/child/status"));
        afterStatus.Should().Be(HttpStatusCode.OK);
        after.GetProperty("minutesLeft").GetInt32().Should().Be(40,
            "не состоявшаяся запись минуты не добавляет минуты в журнал");
    }

    // ─── ручка №11: isChild/childSpaces в /api/auth/me ───

    [Fact]
    public async Task Me_Ребёнок_IsChildTrue_ChildSpacesLessons()
    {
        await NewChild("kid-me");
        var client = ChildClient("kid-me");

        var (status, body) = await ReadAsync(await client.GetAsync("/api/auth/me"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("isChild").GetBoolean().Should().BeTrue();
        StringArray(body.GetProperty("childSpaces")).Should().Equal("lessons");
        // Существующий ответ не изменился: ребёнку /me остаётся доступным (гейт, KM-B2).
        body.GetProperty("username").GetString().Should().Be("kid-me");
    }

    [Fact]
    public async Task Me_Взрослый_IsChildFalse_ChildSpacesEmpty()
    {
        var (status, body) = await ReadAsync(await _admin.GetAsync("/api/auth/me"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("isChild").GetBoolean().Should().BeFalse();
        body.GetProperty("childSpaces").GetArrayLength().Should().Be(0, "у не-ребёнка — пустой массив, не null");
    }
}
