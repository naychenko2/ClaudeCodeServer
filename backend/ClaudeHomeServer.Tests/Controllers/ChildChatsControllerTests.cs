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
/// KM-B6a: чат пространства «Уроки» — POST /api/child/spaces/{space}/chat, GET .../chats,
/// PUT /api/child/chats/{id}/voice (ADR-041 «Контракт», ручки №3–5). Профиль ребёнка заводится
/// боевой админской ручкой (тьютор создаётся ею же), стаб расхода — как у ChildStatusControllerTests.
/// </summary>
[Trait("Category", "Host")]
public class ChildChatsControllerTests : IClassFixture<ChildChatsControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraServices = services =>
        {
            services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
            // Обёртка над боевым гейтом: по флагу отказывает на старте хода (И1-бис), иначе прозрачна
            services.AddSingleton(sp => new RefusingGate(new ChildModeGate(
                sp.GetRequiredService<IChildModePolicy>(), sp.GetService<ChildLimitsCalculator>(), null,
                sp.GetService<ClaudeHomeServer.Services.Llm.ClaudeSubscriptionPool>(),
                sp.GetService<ClaudeHomeServer.Services.Llm.LlmProviderRegistry>())));
            services.AddSingleton<IChildModeGate>(sp => sp.GetRequiredService<RefusingGate>());
        };
    }

    public sealed class RefusingGate(IChildModeGate inner) : IChildModeGate
    {
        public volatile bool Refuse;
        public string? Denied(string server, string tool, string? ownerId) => inner.Denied(server, tool, ownerId);
        public bool AllowsServer(string? ownerId, string server) => inner.AllowsServer(ownerId, server);
        public bool IsChild(string? ownerId) => inner.IsChild(ownerId);
        public string? LaunchMismatch(string? ownerId, Session session)
            => Refuse ? "тестовый отказ старта" : inner.LaunchMismatch(ownerId, session);
        public string? LaunchLimitRefusal(string? ownerId, Session session, DateTimeOffset now)
            => inner.LaunchLimitRefusal(ownerId, session, now);
        public ChildPolicySnapshot? PolicyFor(string ownerId) => inner.PolicyFor(ownerId);
        public ChildRepinTarget? LiveRouteFor(string? ownerId, Session session)
            => inner.LiveRouteFor(ownerId, session);
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

    public ChildChatsControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        _hosts.ExtraConfig["ClaudeSubscriptions:math-pool:ApiKey"] = "sk-ant-test-math";
        // Ребёнок — container-владелец: корень его чатов лежит в песочнице (CliRoute.ResolveChatRoot)
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-child-chats-" + Guid.NewGuid().ToString("N"));
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
    }

    private async Task<string> NewChild(string name, bool withLessons = true, bool inWindow = true,
        int dailyMinutes = 40, decimal dailyBudgetUsd = 2)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        // inWindow=false — окно в 6–4 часах от «сейчас»: заведомо не сейчас
        var shift = inWindow ? 0 : 6;
        var from = local.AddHours(shift - 2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var to = local.AddHours(shift - 1 + (inWindow ? 3 : 0)).ToString("HH:mm", CultureInfo.InvariantCulture);
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, providerKey = KidsKey, model = "sonnet",
            spaces = withLessons
                ? new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes } }
                : new Dictionary<string, object>(),
            dailyBudgetUsd, monthlyBudgetUsd = 40,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        ChildTestSubjects.Seed(_hosts.Services, id);
        return id;
    }

    private void AgreeRules(string childId)
    {
        var p = _users.GetById(childId)!.ChildMode!;
        p.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        p.RulesSummary = "правила";
    }

    private HttpClient ChildClient(string name) => _hosts.CreateAuthenticatedClient(name, ChildPassword);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string CodeOf(JsonElement b) => b.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    private async Task<string> CreateChatAs(HttpClient client)
    {
        var (status, body) = await ReadAsync(await ChildTestSubjects.CreateChat(client));
        status.Should().Be(HttpStatusCode.Created);
        return body.GetProperty("sessionId").GetString()!;
    }

    // ─── №3: создание ───

    [Fact]
    public async Task Create_201_ЗакреплённыеПоляИзПрофиля_ПроходятИ1бис()
    {
        var id = await NewChild("kid-chat-create");
        AgreeRules(id);
        var sid = await CreateChatAs(ChildClient("kid-chat-create"));

        var chat = Sessions.GetById(sid)!;
        var profile = _users.GetById(id)!.ChildMode!;
        chat.OwnerId.Should().Be(id);
        chat.ProjectId.Should().BeNull();
        chat.ChildSpace.Should().Be("lessons");
        chat.PersonaId.Should().Be(profile.TutorPersonaId).And.NotBeNullOrEmpty();
        chat.Provider.Should().Be(KidsKey);
        chat.Model.Should().Be("sonnet");
        chat.Mode.Should().Be(ChildModeLaunch.RequiredMode);

        // И1-бис на первом ходу — тот же гейт, что в точке запуска
        var gate = _hosts.Services.GetRequiredService<IChildModeGate>();
        gate.LaunchMismatch(id, chat).Should().BeNull();
    }

    // Предмет со своими полями, отличными от профиля: тест ловит «поля из профиля, а не из предмета»
    private void AddMath(string childId, bool enabled = true) =>
        _users.GetById(childId)!.ChildMode!.Subjects.Add(new ChildSubject
        {
            Id = "math", Label = "Математика", Glyph = "×÷", Color = "rose", TutorPersonaId = "tutor-math",
            ProviderKey = "math-pool", Model = "haiku", Enabled = enabled,
        });

    [Fact]
    public async Task Create_Предмет_ПоляИзЗаписиПредмета_ВСтореСессий()
    {
        var id = await NewChild("kid-chat-subj");
        AgreeRules(id);
        AddMath(id);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient("kid-chat-subj"), "math"));
        s.Should().Be(HttpStatusCode.Created);

        var chat = Sessions.GetById(b.GetProperty("sessionId").GetString()!)!;
        chat.ChildSubject.Should().Be("math");
        chat.PersonaId.Should().Be("tutor-math");
        chat.Provider.Should().Be("math-pool");
        chat.Model.Should().Be("haiku");
        chat.ChildSpace.Should().Be("lessons");
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().BeNull();
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Create_НеизвестныйПредмет_400_unknown_subject(string? subject)
    {
        var name = "kid-chat-nosubj-" + Guid.NewGuid().ToString("N")[..8];
        var id = await NewChild(name);
        AgreeRules(id);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name), subject));
        s.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(b).Should().Be("unknown_subject");
        Sessions.GetProjectlessChats(id).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ПредметВыключен_403_subject_disabled()
    {
        var id = await NewChild("kid-chat-off");
        AgreeRules(id);
        AddMath(id, enabled: false);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient("kid-chat-off"), "math"));
        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("subject_disabled");
        Sessions.GetProjectlessChats(id).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_SubjectDisabledAfterSnapshot_TurnRefused()
    {
        var id = await NewChild("kid-chat-late");
        AgreeRules(id);
        AddMath(id);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient("kid-chat-late"), "math"));
        s.Should().Be(HttpStatusCode.Created);
        var chat = Sessions.GetById(b.GetProperty("sessionId").GetString()!)!;

        // предмет выключили уже после создания: чат есть, ход не стартует
        _users.GetById(id)!.ChildMode!.Subjects.Single(x => x.Id == "math").Enabled = false;
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().Be("Этот предмет закрыт");
    }

    [Fact]
    public async Task List_ОтдаётSubjectИSubjectClosed_ЗакрытыеОстаются()
    {
        var id = await NewChild("kid-chat-closed");
        AgreeRules(id);
        AddMath(id);
        var client = ChildClient("kid-chat-closed");
        var (_, mb) = await ReadAsync(await ChildTestSubjects.CreateChat(client, "math"));
        var mathId = mb.GetProperty("sessionId").GetString()!;
        var russianId = await CreateChatAs(client);

        _users.GetById(id)!.ChildMode!.Subjects.Single(x => x.Id == "math").Enabled = false;
        // Озвучка включена на одном чате: список несёт voiceMode, оболочка восстанавливает
        // её при открытии (субзадача Т3c из отчёта Т3a)
        Sessions.GetById(mathId)!.VoiceMode = true;
        var (ls, lb) = await ReadAsync(await client.GetAsync("/api/child/spaces/lessons/chats"));
        ls.Should().Be(HttpStatusCode.OK);
        var chats = lb.GetProperty("chats").EnumerateArray().ToDictionary(c => c.GetProperty("sessionId").GetString()!);
        chats[mathId].GetProperty("subject").GetString().Should().Be("math");
        chats[mathId].GetProperty("subjectClosed").GetBoolean().Should().BeTrue();
        chats[mathId].GetProperty("voiceMode").GetBoolean().Should().BeTrue();
        chats[russianId].GetProperty("subject").GetString().Should().Be("russian");
        chats[russianId].GetProperty("subjectClosed").GetBoolean().Should().BeFalse();
        chats[russianId].GetProperty("voiceMode").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Create_Взрослый_403_not_child()
    {
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(_admin));
        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("not_child");
    }

    [Fact]
    public async Task Create_НеизвестноеПространство_400()
    {
        await NewChild("kid-chat-unk");
        var (s, b) = await ReadAsync(await ChildClient("kid-chat-unk").PostAsync("/api/child/spaces/foo/chat", null));
        s.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(b).Should().Be("unknown_space");
    }

    [Fact]
    public async Task Create_ПространстваНетВПрофиле_403_space_disabled()
    {
        await NewChild("kid-chat-dis", withLessons: false);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient("kid-chat-dis")));
        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("space_disabled");
    }

    [Fact]
    public async Task Create_ПравилаНеСогласованы_409_rules_pending_ЧатНеСоздан()
    {
        var id = await NewChild("kid-chat-rules");
        var before = Sessions.GetProjectlessChats(id).Count;
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient("kid-chat-rules")));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("rules_pending");
        Sessions.GetProjectlessChats(id).Count.Should().Be(before);
    }

    [Fact]
    public async Task Create_ВнеОкна_409_out_of_window()
    {
        var id = await NewChild("kid-chat-win", inWindow: false);
        AgreeRules(id);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient("kid-chat-win")));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("out_of_window");
    }

    [Fact]
    public async Task Create_БюджетИсчерпан_409_budget_exhausted()
    {
        // Стаб расхода отдаёт 0.1 USD за день — дневной бюджет 0.05 уже превышен
        var id = await NewChild("kid-chat-budget", dailyBudgetUsd: 0.05m);
        AgreeRules(id);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient("kid-chat-budget")));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("budget_exhausted");
    }

    [Fact]
    public async Task Create_ВремяВышло_409_time_up()
    {
        var id = await NewChild("kid-chat-timeup", dailyMinutes: 1);
        AgreeRules(id);
        var client = ChildClient("kid-chat-timeup");
        // Минута присутствия исчерпывает дневной лимит в 1 минуту
        (await client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(client));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("time_up");
    }

    [Fact]
    public async Task Create_ДваСоздания_ДваРазныхЗакреплённыхЧата()
    {
        var id = await NewChild("kid-chat-two");
        AgreeRules(id);
        var client = ChildClient("kid-chat-two");
        var ids = await Task.WhenAll(CreateChatAs(client), CreateChatAs(client));
        ids[0].Should().NotBe(ids[1]);
        foreach (var sid in ids)
            _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, Sessions.GetById(sid)!).Should().BeNull();
    }

    [Fact]
    public async Task Create_СостояниеWarn_201()
    {
        // Лимит 5 минут при пороге «предупредить за» 5 (по умолчанию): осталось ровно 5 — Warn
        var id = await NewChild("kid-chat-warn", dailyMinutes: 5);
        AgreeRules(id);
        _hosts.Services.GetRequiredService<ChildLimitsCalculator>()
            .Evaluate(id, ChildSpaces.Lessons, DateTimeOffset.UtcNow)!.State.Should().Be(ChildState.Warn);

        var sid = await CreateChatAs(ChildClient("kid-chat-warn"));
        Sessions.GetById(sid)!.ChildSpace.Should().Be("lessons");
    }

    [Fact]
    public async Task Create_ГейтОтказалНаСтарте_503_ЧатНеОстаётсяВСписке()
    {
        var id = await NewChild("kid-chat-refuse");
        AgreeRules(id);
        var client = ChildClient("kid-chat-refuse");
        var gate = _hosts.Services.GetRequiredService<RefusingGate>();
        gate.Refuse = true;
        try
        {
            var (s, _) = await ReadAsync(await ChildTestSubjects.CreateChat(client));
            s.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally { gate.Refuse = false; }

        var (ls, lb) = await ReadAsync(await client.GetAsync("/api/child/spaces/lessons/chats"));
        ls.Should().Be(HttpStatusCode.OK);
        lb.GetProperty("chats").GetArrayLength().Should().Be(0);
        Sessions.GetProjectlessChats(id).Should().BeEmpty();
    }

    // ─── №4: список ───

    [Fact]
    public async Task List_ТолькоСвоиДетскиеЧаты_НовыеСверху_БезОнбординга()
    {
        var id = await NewChild("kid-chat-list");
        AgreeRules(id);
        var other = await NewChild("kid-chat-list-other");
        AgreeRules(other);
        var client = ChildClient("kid-chat-list");

        var first = await CreateChatAs(client);
        var second = await CreateChatAs(client);
        await CreateChatAs(ChildClient("kid-chat-list-other"));
        // онбординг правил: детский чат с меткой — в список не попадает
        var profile = _users.GetById(id)!.ChildMode!;
        await Sessions.CreateChatAsync(id, ChildModeLaunch.RequiredMode, model: profile.Model,
            personaId: profile.TutorPersonaId, onboardingKind: OnboardingKinds.ChildRules,
            childSpace: ChildSpaces.Lessons, providerKey: profile.ProviderKey);
        // «второй» новее первого
        Sessions.GetById(first)!.UpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        Sessions.GetById(second)!.UpdatedAt = DateTime.UtcNow;
        Sessions.SaveSessions();

        var (s, b) = await ReadAsync(await client.GetAsync("/api/child/spaces/lessons/chats"));
        s.Should().Be(HttpStatusCode.OK);
        var ids = b.GetProperty("chats").EnumerateArray().Select(c => c.GetProperty("sessionId").GetString()).ToArray();
        ids.Should().Equal(second, first);
        var item = b.GetProperty("chats")[0];
        item.TryGetProperty("name", out _).Should().BeTrue();
        item.TryGetProperty("updatedAt", out _).Should().BeTrue();
    }

    [Fact]
    public async Task List_Пустой_200_ПустойМассив()
    {
        var id = await NewChild("kid-chat-empty");
        AgreeRules(id);
        var (s, b) = await ReadAsync(await ChildClient("kid-chat-empty").GetAsync("/api/child/spaces/lessons/chats"));
        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("chats").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_НеизвестноеИОтключённоеПространство()
    {
        await NewChild("kid-chat-list-unk", withLessons: false);
        var client = ChildClient("kid-chat-list-unk");
        var (s1, b1) = await ReadAsync(await client.GetAsync("/api/child/spaces/foo/chats"));
        s1.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(b1).Should().Be("unknown_space");
        var (s2, b2) = await ReadAsync(await client.GetAsync("/api/child/spaces/lessons/chats"));
        s2.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b2).Should().Be("space_disabled");
    }

    // ─── №5: голос ───

    [Fact]
    public async Task Voice_ВключаетИСтавитСтиль()
    {
        var id = await NewChild("kid-voice-ok");
        AgreeRules(id);
        var client = ChildClient("kid-voice-ok");
        var sid = await CreateChatAs(client);

        var (s, b) = await ReadAsync(await client.PutAsJsonAsync($"/api/child/chats/{sid}/voice", new { enabled = true, style = "digest" }));
        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("voiceMode").GetBoolean().Should().BeTrue();
        b.GetProperty("voiceStyle").GetString().Should().Be("digest");
        Sessions.GetById(sid)!.VoiceMode.Should().BeTrue();
    }

    [Fact]
    public async Task Voice_НеизвестныйСтиль_400_invalid_style_ПолеНеМеняется()
    {
        var id = await NewChild("kid-voice-bad");
        AgreeRules(id);
        var client = ChildClient("kid-voice-bad");
        var sid = await CreateChatAs(client);

        var (s, b) = await ReadAsync(await client.PutAsJsonAsync($"/api/child/chats/{sid}/voice", new { enabled = true, style = "loud" }));
        s.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(b).Should().Be("invalid_style");
        Sessions.GetById(sid)!.VoiceMode.Should().BeFalse();
    }

    [Fact]
    public async Task Voice_ЧужойБезChildSpaceИНесуществующий_404_chat_not_found()
    {
        var id = await NewChild("kid-voice-404");
        AgreeRules(id);
        var client = ChildClient("kid-voice-404");
        var adultChat = (await Sessions.CreateChatAsync(
            _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, ClaudeMode.Auto, name: "взрослый")).Id;

        foreach (var target in new[] { adultChat, Guid.NewGuid().ToString("N") })
        {
            var (s, b) = await ReadAsync(await client.PutAsJsonAsync($"/api/child/chats/{target}/voice", new { enabled = true }));
            s.Should().Be(HttpStatusCode.NotFound);
            CodeOf(b).Should().Be("chat_not_found");
        }
        Sessions.GetById(adultChat)!.VoiceMode.Should().BeFalse();
    }

    [Fact]
    public async Task Voice_ЧужойДетскийЧат_404_chat_not_found_ПоляНеМеняются()
    {
        var a = await NewChild("kid-voice-a");
        var b = await NewChild("kid-voice-b");
        AgreeRules(a);
        AgreeRules(b);
        var sidB = await CreateChatAs(ChildClient("kid-voice-b"));
        var clientA = ChildClient("kid-voice-a");
        var before = Sessions.GetById(sidB)!;
        var (voiceBefore, styleBefore) = (before.VoiceMode, before.VoiceStyle);

        var (s, body) = await ReadAsync(await clientA.PutAsJsonAsync($"/api/child/chats/{sidB}/voice",
            new { enabled = !voiceBefore, style = "digest" }));
        var (sMissing, bodyMissing) = await ReadAsync(await clientA.PutAsJsonAsync(
            $"/api/child/chats/{Guid.NewGuid():N}/voice", new { enabled = true }));

        s.Should().Be(HttpStatusCode.NotFound);
        CodeOf(body).Should().Be("chat_not_found");
        // существование чужого чата не раскрывается: тело то же, что у несуществующего
        sMissing.Should().Be(s);
        body.GetRawText().Should().Be(bodyMissing.GetRawText());
        var after = Sessions.GetById(sidB)!;
        after.VoiceMode.Should().Be(voiceBefore);
        after.VoiceStyle.Should().Be(styleBefore);
    }

    [Fact]
    public async Task Voice_Взрослый_403_not_child()
    {
        var (s, b) = await ReadAsync(await _admin.PutAsJsonAsync("/api/child/chats/x/voice", new { enabled = true }));
        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("not_child");
    }

    [Fact]
    public async Task Voice_ParallelPuts_DoNotTouchPinnedFields()
    {
        var id = await NewChild("kid-voice-race");
        AgreeRules(id);
        var client = ChildClient("kid-voice-race");
        var sid = await CreateChatAs(client);

        var calls = Enumerable.Range(0, 20).Select(i => client.PutAsJsonAsync($"/api/child/chats/{sid}/voice",
            new { enabled = i % 2 == 0, style = i % 3 == 0 ? "digest" : "talk" }));
        var resps = await Task.WhenAll(calls);
        resps.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        // последний побеждает: итог — одно из допустимых значений, закреплённые поля целы
        var chat = Sessions.GetById(sid)!;
        VoiceStyles.IsKnown(chat.VoiceStyle).Should().BeTrue();
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().BeNull();
        chat.ChildSpace.Should().Be("lessons");
    }
}
