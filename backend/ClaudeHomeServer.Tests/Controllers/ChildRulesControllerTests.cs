using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-B6b: онбординг правил — POST /api/child/rules/start и POST /api/child/rules/agree
/// (ADR-041 «Контракт», ручки №6–7). Профиль ребёнка заводится боевой админской ручкой
/// (тьютор создаётся ею же), стаб расхода — как у ChildChatsControllerTests. Окна профиля
/// включают «сейчас»: чат правил подчиняется окну и лимитам И3 (rulesExempt снимает только
/// rules_pending), и первый ход тьютора должен иметь шанс состояться.
/// </summary>
[Trait("Category", "Host")]
public class ChildRulesControllerTests : IClassFixture<ChildRulesControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraServices = services =>
        {
            services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
            // Политика — обёртка RacePolicy (см. ниже): для одного ребёнка по имени она роняет
            // профиль после первого чтения (тот же приём, что у ChildStatusControllerTests),
            // для всех прочих — точная делегация боевой политике.
            services.AddSingleton<IChildModePolicy>(sp => new RacePolicy(sp.GetRequiredService<IUserStore>()));
        };
    }

    // Ребёнок теста-гонки: единственный, чей профиль обёртка роняет после первого
    // успешного чтения; остальные имена проходят насквозь без изменений.
    private const string RaceUsername = "kid-rules-denyall-race";

    /// <summary>
    /// Обёртка боевой политики, воспроизводящая гонку гейта и контроллера — единственная
    /// HTTP-дорога к ветке DenyAll в контроллере: middleware не пускает DenyAll-ребёнка
    /// ни на какой эндпоинт (ChildModeGateMiddleware.cs:37). Первое успешное чтение профиля
    /// RaceUsername отдаётся как есть (гейт проходит) и сразу роняет профиль в сторе —
    /// правка «взрослого» посреди запроса; следующее чтение (контроллера) — настоящий DenyAll.
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

    private sealed class FixedSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0.1m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0.5m;
    }

    private const string KidsKey = "kids-api";
    private const string ChildPassword = "kids-test-password";
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();

    public ChildRulesControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        // Ребёнок — container-владелец: корень его чатов лежит в песочнице (CliRoute.ResolveChatRoot)
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-child-rules-" + Guid.NewGuid().ToString("N"));
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
    }

    // Ребёнок с профилем «lessons», окно которого накрывает сейчас (иначе окно И3
    // не пустит первый ход тьютора)
    private async Task<string> NewChild(string name, int dailyMinutes = 40)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        var from = local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var to = local.AddHours(3).ToString("HH:mm", CultureInfo.InvariantCulture);
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, providerKey = KidsKey, model = "sonnet",
            spaces = new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes } },
            dailyBudgetUsd = 2m, monthlyBudgetUsd = 40m,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private HttpClient ChildClient(string name) => _hosts.CreateAuthenticatedClient(name, ChildPassword);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string CodeOf(JsonElement b) => b.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    // start → 200 с sessionId
    private async Task<string> StartAs(HttpClient client)
    {
        var (status, body) = await ReadAsync(await client.PostAsync("/api/child/rules/start", null));
        status.Should().Be(HttpStatusCode.OK);
        return body.GetProperty("sessionId").GetString()!;
    }

    private async Task<(HttpStatusCode, JsonElement)> AgreeAsync(HttpClient client, string sessionId, string summary) =>
        await ReadAsync(await client.PostAsJsonAsync("/api/child/rules/agree", new { sessionId, summary }));

    // ─── №6: start ───

    [Fact]
    public async Task Start_200_ЗакреплённыеПоляКакУЧатаУроков_ПервоеСообщениеТьютораОбъясняетПравила()
    {
        var id = await NewChild("kid-rules-start");
        var sid = await StartAs(ChildClient("kid-rules-start"));

        var chat = Sessions.GetById(sid)!;
        var profile = _users.GetById(id)!.ChildMode!;
        chat.OwnerId.Should().Be(id);
        chat.ProjectId.Should().BeNull();
        chat.ChildSpace.Should().Be("lessons");
        chat.OnboardingKind.Should().Be(OnboardingKinds.ChildRules);
        chat.PersonaId.Should().Be(profile.TutorPersonaId).And.NotBeNullOrEmpty();
        chat.Provider.Should().Be(KidsKey);
        chat.Model.Should().Be("sonnet");
        chat.Mode.Should().Be(ChildModeLaunch.RequiredMode);

        // «Немого» онбординга нет: тьютор начинает первым, и его затравка пересказывает
        // правила из профиля (дневный предел — факт из него), а не общий текст знакомства.
        var adapter = _hosts.LlmAdapters.Adapters[sid];
        adapter.SentMessages.Should().ContainSingle("старт онбординга запускает ровно один ход тьютора");
        adapter.SentMessages[0].Should().Contain("Системный старт онбординга правил")
            .And.Contain("40 мин", "затравка переносит дневный предел из профиля");
        var history = await Sessions.GetHistoryAsync(sid);
        history.OfType<StoredUserMessage>().Should().Contain(m => m.SystemDirective == true,
            "директива записана в историю служебным сообщением");
    }

    [Fact]
    public async Task Start_Повторный_ТотЖеЧат_БезВторогоХода()
    {
        var id = await NewChild("kid-rules-resume");
        var client = ChildClient("kid-rules-resume");
        var first = await StartAs(client);
        var second = await StartAs(client);

        second.Should().Be(first, "незавершённый чат онбординга возвращается как есть");
        _hosts.LlmAdapters.Adapters[first].SentMessages.Should().ContainSingle(
            "kickoff в полёте (статус Working) — повторный не уходит");
        Sessions.GetProjectlessChats(id).Count(s => s.OnboardingKind == OnboardingKinds.ChildRules)
            .Should().Be(1);
    }

    [Fact]
    public async Task Start_УжеДоговорены_409_rules_already_agreed()
    {
        var id = await NewChild("kid-rules-agreed-first");
        var client = ChildClient("kid-rules-agreed-first");
        var sid = await StartAs(client);
        (await AgreeAsync(client, sid, "Уроки по будням, время меряет сердцебиение.")).Item1
            .Should().Be(HttpStatusCode.OK);

        var (s, b) = await ReadAsync(await client.PostAsync("/api/child/rules/start", null));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("rules_already_agreed");
    }

    [Fact]
    public async Task Start_Взрослый_403_not_child()
    {
        var (s, b) = await ReadAsync(await _admin.PostAsync("/api/child/rules/start", null));
        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("not_child");
    }

    [Fact]
    public async Task Start_ГонкаСГатом_НечитаемыйПрофиль_503_PolicyUnavailable()
    {
        await NewChild(RaceUsername);
        // Единственная достижимая через HTTP дорога к «профиль не читается»: гейт читает
        // профиль валидным и пропускает запрос; к чтению контроллера он уже нечитаем
        // (RacePolicy — «правка взрослого» посреди запроса). Ответ — 503, не 403 гейта и не 500.
        var (s, b) = await ReadAsync(await ChildClient(RaceUsername).PostAsync("/api/child/rules/start", null));
        s.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(b).Should().Be("policy_unavailable");
    }

    [Fact]
    public async Task Start_Parallel_CreatesOneChat()
    {
        var id = await NewChild("kid-rules-race-start");
        var client = ChildClient("kid-rules-race-start");
        var ids = await Task.WhenAll(StartAs(client), StartAs(client));
        ids[1].Should().Be(ids[0], "два start под локами ownerId — один чат онбординга");
        Sessions.GetProjectlessChats(id).Count(s => s.OnboardingKind == OnboardingKinds.ChildRules)
            .Should().Be(1);
        _hosts.LlmAdapters.Adapters[ids[0]].SentMessages.Should().ContainSingle(
            "второй start — резюм, второго kickoff нет");
    }

    // ─── №7: agree ───

    [Fact]
    public async Task Agree_200_ПишетТолькоПравила_ЛимитыИЧатЦелы()
    {
        var id = await NewChild("kid-rules-agree");
        var client = ChildClient("kid-rules-agree");
        var sid = await StartAs(client);
        var profile = _users.GetById(id)!.ChildMode!;
        var before = (profile.Spaces["lessons"].DailyMinutes, profile.DailyBudgetUsd,
            profile.MonthlyBudgetUsd, profile.ProviderKey, profile.Model);

        var t0 = DateTimeOffset.UtcNow;
        var (s, b) = await AgreeAsync(client, sid, "Уроки по будням в окно, время считает сердцебиение, неприятно — кнопка.");
        var t1 = DateTimeOffset.UtcNow;
        s.Should().Be(HttpStatusCode.OK);
        var agreedAt = DateTimeOffset.Parse(b.GetProperty("rulesAgreedAt").GetString()!, CultureInfo.InvariantCulture);
        agreedAt.ToUniversalTime().Should().BeOnOrAfter(t0).And.BeOnOrBefore(t1);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().NotBeNull();
        stored.RulesSummary.Should().Be("Уроки по будням в окно, время считает сердцебиение, неприятно — кнопка.");
        stored.Spaces["lessons"].DailyMinutes.Should().Be(before.Item1);
        stored.DailyBudgetUsd.Should().Be(before.Item2);
        stored.MonthlyBudgetUsd.Should().Be(before.Item3);
        stored.ProviderKey.Should().Be(before.Item4);
        stored.Model.Should().Be(before.Item5);

        // Чат онбординга после согласия не меняется: agree пишет только факт согласия
        Sessions.GetById(sid)!.OnboardingKind.Should().Be(OnboardingKinds.ChildRules);

        // Согласие открывает «Уроки»: обычный чат пространства создаётся
        var (cs, _) = await ReadAsync(await client.PostAsync("/api/child/spaces/lessons/chat", null));
        cs.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Agree_ПустойSummary_400_summary_empty()
    {
        var id = await NewChild("kid-rules-empty");
        var client = ChildClient("kid-rules-empty");
        var sid = await StartAs(client);
        foreach (var summary in new[] { "", "   " })
        {
            var (s, b) = await AgreeAsync(client, sid, summary);
            s.Should().Be(HttpStatusCode.BadRequest);
            CodeOf(b).Should().Be("summary_empty");
        }
        _users.GetById(id)!.ChildMode!.RulesAgreedAt.Should().BeNull("отказ ничего не записал");
    }

    [Fact]
    public async Task Agree_SummaryДлиннее2000_400_summary_too_long()
    {
        var id = await NewChild("kid-rules-long");
        var client = ChildClient("kid-rules-long");
        var sid = await StartAs(client);
        var (s, b) = await AgreeAsync(client, sid, new string('а', 2001));
        s.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(b).Should().Be("summary_too_long");
        // Граница бюджета: ровно 2000 — проходит
        (await AgreeAsync(client, sid, new string('а', 2000))).Item1.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Agree_НеЧатПравил_404_not_rules_chat()
    {
        var id = await NewChild("kid-rules-wrong-chat");
        var client = ChildClient("kid-rules-wrong-chat");
        var sid = await StartAs(client);
        (await AgreeAsync(client, sid, "договорили")).Item1.Should().Be(HttpStatusCode.OK);
        // Обычный чат «Уроков» (после согласия создаётся) — не чат онбординга
        var (cs, cb) = await ReadAsync(await client.PostAsync("/api/child/spaces/lessons/chat", null));
        cs.Should().Be(HttpStatusCode.Created);
        var (s1, b1) = await AgreeAsync(client, cb.GetProperty("sessionId").GetString()!, "не туда");
        s1.Should().Be(HttpStatusCode.NotFound);
        CodeOf(b1).Should().Be("not_rules_chat");
        // Чужого id нет — то же 404 (существование не раскрываем)
        var (s2, b2) = await AgreeAsync(client, Guid.NewGuid().ToString("N"), "не туда");
        s2.Should().Be(HttpStatusCode.NotFound);
        CodeOf(b2).Should().Be("not_rules_chat");
    }

    [Fact]
    public async Task Agree_ЧужогоРебёнка_404_not_rules_chat()
    {
        var other = await NewChild("kid-rules-other-owner");
        var otherSid = await StartAs(ChildClient("kid-rules-other-owner"));
        await NewChild("kid-rules-me");
        var (s, b) = await AgreeAsync(ChildClient("kid-rules-me"), otherSid, "не мой чат");
        s.Should().Be(HttpStatusCode.NotFound);
        CodeOf(b).Should().Be("not_rules_chat");
        _users.GetById(other)!.ChildMode!.RulesAgreedAt.Should().BeNull("чужой agree не пишет чужие правила");
    }

    [Fact]
    public async Task Agree_Повторный_409_rules_already_agreed()
    {
        var id = await NewChild("kid-rules-twice");
        var client = ChildClient("kid-rules-twice");
        var sid = await StartAs(client);
        (await AgreeAsync(client, sid, "первые правила")).Item1.Should().Be(HttpStatusCode.OK);
        var (s, b) = await AgreeAsync(client, sid, "вторые правила");
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("rules_already_agreed");
        _users.GetById(id)!.ChildMode!.RulesSummary.Should().Be("первые правила", "повтор не перезаписывает");
    }

    [Fact]
    public async Task Agree_Взрослый_403_not_child()
    {
        var id = await NewChild("kid-rules-adult-agree");
        var sid = await StartAs(ChildClient("kid-rules-adult-agree"));
        var (s, b) = await AgreeAsync(_admin, sid, "взрослый не может согласить за ребёнка");
        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("not_child");
    }

    [Fact]
    public async Task Agree_Parallel_OneWins()
    {
        var id = await NewChild("kid-rules-race-agree");
        var client = ChildClient("kid-rules-race-agree");
        var sid = await StartAs(client);
        // Порядок прихода двух concurrent-запросов к стору не детерминирован: контракт —
        // ровно один 200 и один 409, а в сторе summary того, чей вызов вернул 200.
        var summaries = new[] { "первый", "второй" };
        var results = await Task.WhenAll(AgreeAsync(client, sid, summaries[0]), AgreeAsync(client, sid, summaries[1]));
        results.Select(r => r.Item1).Should().ContainSingle(c => c == HttpStatusCode.OK)
            .And.ContainSingle(c => c == HttpStatusCode.Conflict);
        results.First(r => r.Item1 == HttpStatusCode.Conflict).Item2.GetProperty("code").GetString()
            .Should().Be("rules_already_agreed");
        var winner = results[0].Item1 == HttpStatusCode.OK ? summaries[0] : summaries[1];
        var stored = _users.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().NotBeNull();
        stored.RulesSummary.Should().Be(winner, "запись сделал победитель гонки, проигравший не затёр");
    }

    [Fact]
    public async Task Agree_ConcurrentWithAdminPut_KeepsBoth()
    {
        var id = await NewChild("kid-rules-race-put");
        var client = ChildClient("kid-rules-race-put");
        var sid = await StartAs(client);

        // Тело с длинным списком опекунов: путь PUT до записи под локом — десятки обращений
        // к стору, и agree успевает встать в ту же очередь (тот же утяжелитель, что у
        // UsersChildProfileTests.PutProfile_ConcurrentWithAgree)
        var manyGuardians = Enumerable.Repeat(_users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, 60).ToArray();
        var put = _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, guardianUserIds = manyGuardians, providerKey = KidsKey, model = "sonnet",
            spaces = new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from = "06:00", to = "22:00" } }, dailyMinutes = 99 } },
            dailyBudgetUsd = 3m, monthlyBudgetUsd = 50m,
        });

        var agreeTask = Task.Run(() => AgreeAsync(client, sid, "согласие во время правки"));
        var putResp = await put;
        var (agreeStatus, _) = await agreeTask;

        putResp.StatusCode.Should().Be(HttpStatusCode.OK);
        agreeStatus.Should().Be(HttpStatusCode.OK, "согласие не потерялось под параллельным PUT");
        var stored = _users.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().NotBeNull();
        stored.RulesSummary.Should().Be("согласие во время правки");
        // Лимиты — целиком из PUT админа: agree их не тронул, PUT их не затёр
        stored.Spaces["lessons"].DailyMinutes.Should().Be(99);
        stored.DailyBudgetUsd.Should().Be(3m);
        stored.MonthlyBudgetUsd.Should().Be(50m);
    }
}
