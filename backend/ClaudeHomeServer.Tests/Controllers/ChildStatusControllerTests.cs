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
/// </summary>
[Trait("Category", "Host")]
public class ChildStatusControllerTests : IClassFixture<ChildStatusControllerTests.Hosts>
{
    /// <summary>Хост на класс (не на тест): стаб швы регистрируется после встроенных регистраций
    /// и именно его резолвит GetService&lt;IOwnerSpendReader&gt; в фабрике калькулятора (Program.cs:611).</summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraServices = services => services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
    }

    // Расход ниже любого бюджета профиля: «бюджет не проверить» не должно быть ни в одном тесте.
    private sealed class FixedSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0.1m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0.5m;
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
    /// пространства: законное значение, которое проверяет 403 space_disabled).</summary>
    private async Task<string> NewChild(string name, bool withLessons = true)
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

        var (fromH, toH) = WindowAroundNow();
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = withLessons
                ? new Dictionary<string, object>
                {
                    ["lessons"] = new { windows = new[] { new { from = fromH, to = toH } }, dailyMinutes = 40 },
                }
                : new Dictionary<string, object>(),
            dailyBudgetUsd = 2,
            monthlyBudgetUsd = 40,
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
    public async Task Status_Ребёнок_200_Все9ПолейChildStatus()
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
