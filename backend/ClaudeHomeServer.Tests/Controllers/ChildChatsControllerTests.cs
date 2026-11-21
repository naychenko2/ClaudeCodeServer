using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Чат пространства «Уроки»: создание, список, голос (KM-B6a, ADR-041 «Контракт», ручки №3–5).
/// Б9b (решение Андрея 19.11, ADR-041 ред. 8): POST …/chat — единственная точка создания
/// урока, и урок — чат проекта-предмета от лица его руководителя (Project.DefaultPersonaId),
/// без PrivateToUserId («куратор видит все уроки»); «старые уроки» вне проекта остаются в
/// списке только для чтения (readOnly). Профиль ребёнка — боевая админская ручка, предмет —
/// фикстура ChildTestSubjects: его проект и репетитор создаются провижером Б17a на первом же
/// POST (тот же путь, что у админской записи предмета).
/// </summary>
[Trait("Category", "Host")]
public class ChildChatsControllerTests : IClassFixture<ChildChatsControllerTests.Hosts>,
    IClassFixture<ChildChatsControllerTests.NoSandboxHosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            ExtraServices = services =>
            {
                services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
                // Обёртка над боевым гейтом: по флагу отказывает на старте хода (И1-бис), иначе прозрачна.
                // Живой маршрут урока (И10) строится по проекту-предмету, персоне-руководителю и
                // резолверу модели — все три передаются: тест проверяет настоящий путь урока, а не
                // переходную ветку по записанным полям предмета.
                services.AddSingleton(sp => new RefusingGate(new ChildModeGate(
                    sp.GetRequiredService<IChildModePolicy>(), sp.GetService<ChildLimitsCalculator>(), null,
                    sp.GetService<ClaudeSubscriptionPool>(), sp.GetService<LlmProviderRegistry>(),
                    sp.GetService<ProjectManager>(), sp.GetService<PersonaManager>(),
                    sp.GetService<ModelAssignmentResolver>())));
                services.AddSingleton<IChildModeGate>(sp => sp.GetRequiredService<RefusingGate>());
            };
        }
    }

    // Хост без Sandbox:ProjectsRoot: дом container-ребёнка не резолвится, провижн
    // проекта-предмета невозможен (контракт ChildSubjectProjectProvisioner) — на этом
    // проверяется 409 subject_not_ready.
    public sealed class NoSandboxHosts : TestWebApplicationFactory
    {
        public NoSandboxHosts()
        {
            // Та же подписка, что в основном хосте: 400 на профиле здесь — не про эту проверку
            ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
            ExtraServices = services =>
                services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
        }
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
        // Вердикт хода (И10): тот же флаг, что у LaunchMismatch выше — двойка остаётся двойкой и на
        // новой точке (внутренний гейт о флаге не знает, он читает только свой LaunchMismatch).
        public string? TurnRefusalFor(string? ownerId, Session session, DateTimeOffset now)
            => Refuse ? "тестовый отказ старта" : inner.TurnRefusalFor(ownerId, session, now);
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
    private const string MathKey = "math-pool";
    private const string ChildPassword = "kids-test-password";

    // Модель репетитора-руководителя. Провижер Б17a создаёт его без своей модели (модель
    // задаёт куратор штатной дверью персоны, разрез §14.3), а живой маршрут урока берётся
    // именно от неё (И10) — без неё репетитор не разворачивается и урока нет. В тестовом хосте
    // она приходит назначением места «чат с персоной» — тем же админским входом, что UI
    // «Назначения»: слотов тиров в тестовом конфиге нет.
    private const string TutorModel = "sonnet";

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private readonly Hosts _hosts;
    private readonly NoSandboxHosts _noSandbox;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();
    private ProjectManager Projects => _hosts.Services.GetRequiredService<ProjectManager>();
    private PersonaManager Personas => _hosts.Services.GetRequiredService<PersonaManager>();

    public ChildChatsControllerTests(Hosts hosts, NoSandboxHosts noSandbox)
    {
        _hosts = hosts;
        _noSandbox = noSandbox;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{MathKey}:ApiKey"] = "sk-ant-test-math";
        // Рубильник общих проектов: куратор проекта-предмета — участник общего проекта
        // (SharedProjectsGate), без него доступ к проекту-предмету закрыт
        _hosts.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        // Ребёнок — container-владелец: папка предмета и его проектные чаты лежат в песочнице
        // (UserHomeResolver.SubjectHome → {дом}/{Предметы}/{subjectId})
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-child-chats-" + Guid.NewGuid().ToString("N"));
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
    }

    // Окно присутствия: inWindow=true — «сейчас», false — в 6–4 часах от сейчас (заведомо не сейчас)
    private static (string From, string To) Window(bool inWindow = true)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        var shift = inWindow ? 0 : 6;
        return (
            local.AddHours(shift - 2).ToString("HH:mm", CultureInfo.InvariantCulture),
            local.AddHours(shift - 1 + (inWindow ? 3 : 0)).ToString("HH:mm", CultureInfo.InvariantCulture));
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
        var (from, to) = Window(inWindow);
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, acknowledgeRouteRisk = true, providerKey = KidsKey, model = TutorModel,
            spaces = withLessons
                ? new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes } }
                : new Dictionary<string, object>(),
            dailyBudgetUsd, monthlyBudgetUsd = 40,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        ChildTestSubjects.Seed(_hosts.Services, id);
        // Живой маршрут репетитора (И10) — модель персоны-руководителя; её назначение здесь.
        (_hosts.Services.GetRequiredService<LocalActionOverridesStore>()
            .Set(LocalActionCatalog.ChatPersona, TutorModel)).Should().BeTrue();
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

    // Проект-предмета ребёнка по маркеру — та же точка чтения, что у ChildModeGate и провижнера
    private Project SubjectProjectOf(string childId, string subjectId) =>
        Projects.GetByOwner(childId).Single(p => ProjectCapabilities.IsChildSubject(p)
            && p.ChildSubjectId == subjectId);

    // «Старый урок» — детский чат вне проекта, каким он был до Б9b. Такой чат нельзя создать
    // ребёнком (Б9a отказывает ему старт), поэтому он строится, пока детской проверки хода нет
    // (тот же приём, что в ChildChatReadTests), и остаётся в списке только для чтения.
    private async Task<string> LegacyLessonAsync(string childId, string subjectId)
    {
        var user = _users.GetById(childId)!;
        var profile = user.ChildMode!;
        user.ChildMode = null;
        try
        {
            var chat = await Sessions.CreateChatAsync(childId, ChildModeLaunch.RequiredMode,
                model: profile.Model, personaId: profile.TutorPersonaId, childSpace: ChildSpaces.Lessons,
                providerKey: profile.ProviderKey, childSubject: subjectId);
            return chat.Id;
        }
        finally { user.ChildMode = profile; }
    }

    // ─── №3: создание (Б9b: одна точка — урок в проекте-предмете) ───

    [Fact]
    public async Task Create_201_УрокВПроектеПредмета_Руководитель_БезПриватности()
    {
        var name = "kid-lesson-create";
        var id = await NewChild(name);
        AgreeRules(id);

        var (status, body) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name)));
        status.Should().Be(HttpStatusCode.Created);
        var chat = Sessions.GetById(body.GetProperty("sessionId").GetString()!)!;

        var project = SubjectProjectOf(id, ChildTestSubjects.Key);
        project.DefaultPersonaId.Should().NotBeNullOrEmpty();

        // Урок — чат проекта-предмета этого предмета (cwd фабрики — RootPath проекта; владелец
        // проектной сессии — по проекту, OwnerId сессии по контракту пуст)
        chat.ProjectId.Should().Be(project.Id);
        project.OwnerId.Should().Be(id);
        project.RootPath.Should().NotBeNullOrEmpty();
        chat.CreatedByUserId.Should().Be(id);
        // Персона — руководитель проекта (единственная точка правды репетитора, И10)
        chat.PersonaId.Should().Be(project.DefaultPersonaId);
        // «Куратор видит все уроки»: урок не приватен и приватности у него нет (Б9a, И9′)
        chat.PrivateToUserId.Should().BeNull();
        chat.ExcludeFromDossiers.Should().BeFalse("приватный чат исключён из «Истории решений» — приватности нет");
        chat.ChildSpace.Should().Be(ChildSpaces.Lessons);
        chat.ChildSubject.Should().Be(ChildTestSubjects.Key);
        chat.Mode.Should().Be(ChildModeLaunch.RequiredMode);
        // Маршрут — живой путь репетитора (И10), а не модель места
        chat.Model.Should().Be(TutorModel);
        // И2′: поставщик — ровно записанный у предмета, Pick не зовётся
        chat.Provider.Should().Be(KidsKey);

        // И1-бис на этом же чате — тот же гейт, что в точке запуска
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().BeNull();
    }

    [Fact]
    public async Task Create_ПровиженНевозможен_409_subject_not_ready_БезИсключения()
    {
        // Песочница не настроена: дом ребёнка не резолвится → проект-предмет не создать
        var admin = _noSandbox.CreateAuthenticatedClient();
        var name = "kid-lesson-nosandbox";
        var resp = await admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var (from, to) = Window();
        var put = await admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, acknowledgeRouteRisk = true, providerKey = KidsKey, model = TutorModel,
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes = 40 },
            },
            dailyBudgetUsd = 2m, monthlyBudgetUsd = 40,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        ChildTestSubjects.Seed(_noSandbox.Services, id);
        var nbUsers = _noSandbox.Services.GetRequiredService<UserStore>();
        var nbProfile = nbUsers.GetById(id)!.ChildMode!;
        nbProfile.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        nbProfile.RulesSummary = "правила";

        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(_noSandbox.CreateAuthenticatedClient(name, ChildPassword)));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("subject_not_ready");
        // Отказанный запрос не оставил ни чата, ни полупроекта
        _noSandbox.Services.GetRequiredService<ProjectManager>().GetByOwner(id).Should().BeEmpty();
    }

    // Предмет со своей записью и своей подписью (отличный от «russian»): тест ловит «урок
    // берёт свой предмет, а не профиль». Ключа тьютора у него нет (провижер Б17a его не
    // пишет): репетитор урока — руководитель проекта предмета.
    // Полей маршрута у предмета нет (Б17c): репетитор — руководитель проекта (И10), аккаунт — профиля.
    private void AddSubject(string childId, string subjectId, string label, bool enabled = true)
    {
        var profile = _users.GetById(childId)!.ChildMode!;
        profile.Subjects.Add(new ChildSubject
        {
            Id = subjectId, Label = label, Glyph = "×÷", Color = "rose", Enabled = enabled,
        });
    }

    [Fact]
    public async Task Create_Предмет_СвойПроектИСвойРепетитор_ВСтореСессий()
    {
        var name = "kid-lesson-subj";
        var id = await NewChild(name);
        AgreeRules(id);
        AddSubject(id, "math", "Математика");
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name), "math"));
        s.Should().Be(HttpStatusCode.Created, "body: {0}", b.GetRawText());

        var chat = Sessions.GetById(b.GetProperty("sessionId").GetString()!)!;
        var mathProject = SubjectProjectOf(id, "math");
        chat.ChildSubject.Should().Be("math");
        chat.ProjectId.Should().Be(mathProject.Id);
        // Репетитор предмета — руководитель ЕГО проекта, а не тьютор профиля и не руководитель чужого предмета
        chat.PersonaId.Should().Be(mathProject.DefaultPersonaId)
            .And.NotBe(_users.GetById(id)!.ChildMode!.TutorPersonaId);
        // И2′: поставщик — ровно аккаунт профиля ребёнка, а не выбранный пулом
        chat.Provider.Should().Be(_users.GetById(id)!.ChildMode!.ProviderKey);
        // И модель — живого пути репетитора
        chat.Model.Should().Be(TutorModel);
        chat.ChildSpace.Should().Be(ChildSpaces.Lessons);
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
        Projects.GetByOwner(id).Should().BeEmpty("отказ по предмету не провит проект");
    }

    [Fact]
    public async Task Create_ПредметВыключен_403_subject_disabled()
    {
        var name = "kid-chat-off";
        var id = await NewChild(name);
        AgreeRules(id);
        AddSubject(id, "math", "Математика", enabled: false);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name), "math"));
        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("subject_disabled");
        Sessions.GetProjectlessChats(id).Should().BeEmpty();
        Projects.GetByOwner(id).Should().BeEmpty("закрытый предмет не провится ради урока");
    }

    [Fact]
    public async Task Create_SubjectDisabledAfterSnapshot_TurnRefused()
    {
        var name = "kid-chat-late";
        var id = await NewChild(name);
        AgreeRules(id);
        AddSubject(id, "math", "Математика");
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name), "math"));
        s.Should().Be(HttpStatusCode.Created);
        var chat = Sessions.GetById(b.GetProperty("sessionId").GetString()!)!;

        // предмет выключили уже после создания: чат есть, ход не стартует
        _users.GetById(id)!.ChildMode!.Subjects.Single(x => x.Id == "math").Enabled = false;
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().Be("Этот предмет закрыт");
    }

    [Fact]
    public async Task Create_ТумблерПриватности_РебёнкуЗакрыт_УрокОстаётсяНеприватным()
    {
        var name = "kid-lesson-private";
        var id = await NewChild(name);
        AgreeRules(id);
        var client = ChildClient(name);
        var sid = await CreateChatAs(client);

        // Общая ручка видимости (PUT /api/chats/{id}/visibility) ребёнку не открыта вовсе:
        // у неё нет [ChildAllowed] — отказ даёт гейт И1 до тела ручки (тот же 403, что у всех
        // не открытых ребёнку ручек).
        var (s, _) = await ReadAsync(await client.PutAsJsonAsync($"/api/chats/{sid}/visibility", new { @private = true }));
        s.Should().Be(HttpStatusCode.Forbidden);
        Sessions.GetById(sid)!.PrivateToUserId.Should().BeNull();

        // И через сервисную точку правы (тот же путь, что делает ручка): урок приватным не
        // сделан — «куратор видит все уроки» не обходится тумблером.
        Sessions.SetChatVisibility(sid, id, true).Outcome.Should().NotBe(ChatVisibilityOutcome.Ok);
        Sessions.GetById(sid)!.PrivateToUserId.Should().BeNull();
    }

    [Fact]
    public async Task List_ЖивыеУрокиИСтарые_ReadOnlyРазличает()
    {
        var name = "kid-chat-closed";
        var id = await NewChild(name);
        AgreeRules(id);
        AddSubject(id, "math", "Математика");
        // Предмет «для удаления»: его урок создаётся, а запись потом вычёркивается из профиля —
        // подписи взять неоткуда (ХВ2-Б1)
        AddSubject(id, "history", "История");
        var client = ChildClient(name);
        var (_, mb) = await ReadAsync(await ChildTestSubjects.CreateChat(client, "math"));
        var mathId = mb.GetProperty("sessionId").GetString()!;
        var russianId = await CreateChatAs(client);
        var (_, hb) = await ReadAsync(await ChildTestSubjects.CreateChat(client, "history"));
        var historyId = hb.GetProperty("sessionId").GetString()!;
        // «Старый урок» вне проекта — то, что создавалось до Б9b
        var legacyId = await LegacyLessonAsync(id, "math");

        _users.GetById(id)!.ChildMode!.Subjects.Single(x => x.Id == "math").Enabled = false;
        _users.GetById(id)!.ChildMode!.Subjects.RemoveAll(x => x.Id == "history");
        // Озвучка живого урока: список несёт voiceMode, оболочка восстанавливает её при
        // открытии (субзадача Т3c из отчёта Т3a)
        Sessions.GetById(mathId)!.VoiceMode = true;

        var (ls, lb) = await ReadAsync(await client.GetAsync("/api/child/spaces/lessons/chats"));
        ls.Should().Be(HttpStatusCode.OK);
        var chats = lb.GetProperty("chats").EnumerateArray().ToDictionary(c => c.GetProperty("sessionId").GetString()!);

        // Живые уроки — чаты проектов-предметов: readOnly=false
        chats[mathId].GetProperty("subject").GetString().Should().Be("math");
        chats[mathId].GetProperty("subjectClosed").GetBoolean().Should().BeTrue();
        chats[mathId].GetProperty("subjectLabel").GetString().Should().Be("Математика");
        chats[mathId].GetProperty("voiceMode").GetBoolean().Should().BeTrue();
        chats[mathId].GetProperty("readOnly").GetBoolean().Should().BeFalse();
        chats[russianId].GetProperty("subject").GetString().Should().Be(ChildTestSubjects.Key);
        chats[russianId].GetProperty("subjectClosed").GetBoolean().Should().BeFalse();
        chats[russianId].GetProperty("subjectLabel").GetString().Should().Be("Русский язык");
        chats[russianId].GetProperty("voiceMode").GetBoolean().Should().BeFalse();
        chats[russianId].GetProperty("readOnly").GetBoolean().Should().BeFalse();
        // Предмет удалён из профиля: поле в ответе есть и равно null (ХВ2-Б1)
        chats[historyId].GetProperty("subject").GetString().Should().Be("history");
        chats[historyId].GetProperty("subjectClosed").GetBoolean().Should().BeTrue();
        chats[historyId].GetProperty("subjectLabel").ValueKind.Should().Be(JsonValueKind.Null);
        chats[historyId].GetProperty("readOnly").GetBoolean().Should().BeFalse();
        // Старый урок вне проекта — только для чтения (Б9a)
        chats[legacyId].GetProperty("subject").GetString().Should().Be("math");
        chats[legacyId].GetProperty("readOnly").GetBoolean().Should().BeTrue();
        chats.Keys.Should().HaveCount(4);
    }

    [Fact]
    public async Task List_УрокПроектаВиденКуратору_СписокПроекта()
    {
        var name = "kid-lesson-curator";
        var id = await NewChild(name);
        AgreeRules(id);
        var lessonId = await CreateChatAs(ChildClient(name));
        var project = SubjectProjectOf(id, ChildTestSubjects.Key);

        // Куратор — опекун с ролью Editor общего проекта (тот же состав участника, что пишет
        // SyncChildCuratorsAsync) и флагом общих проектов
        var curator = _users.Add("kid-cur-" + name, ChildPassword, "user");
        Projects.SetMembers(project.Id,
            [new ProjectMember { UserId = curator.Id, Role = ProjectRole.Editor, AddedBy = id }]);
        _users.SetFeatureFlag(curator.Id, FeatureFlagKeys.SharedProjects, true);

        // «Куратор видит все уроки»: урок — обычный чат общего проекта, он в его списке
        var resp = await _hosts.CreateAuthenticatedClient("kid-cur-" + name, ChildPassword)
            .GetAsync($"/api/projects/{project.Id}/sessions");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sessions = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        sessions.Should().Contain(lessonId);
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
        var name = "kid-chat-rules";
        var id = await NewChild(name);
        var before = Sessions.GetProjectlessChats(id).Count;
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name)));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("rules_pending");
        Sessions.GetProjectlessChats(id).Count.Should().Be(before);
    }

    [Fact]
    public async Task Create_ВнеОкна_409_out_of_window()
    {
        var name = "kid-chat-win";
        var id = await NewChild(name, inWindow: false);
        AgreeRules(id);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name)));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("out_of_window");
    }

    [Fact]
    public async Task Create_БюджетИсчерпан_409_budget_exhausted()
    {
        // Стаб расхода отдаёт 0.1 USD за день — дневной бюджет 0.05 уже превышен
        var name = "kid-chat-budget";
        var id = await NewChild(name, dailyBudgetUsd: 0.05m);
        AgreeRules(id);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(ChildClient(name)));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("budget_exhausted");
    }

    [Fact]
    public async Task Create_ВремяВышло_409_time_up()
    {
        var name = "kid-chat-timeup";
        var id = await NewChild(name, dailyMinutes: 1);
        AgreeRules(id);
        var client = ChildClient(name);
        // Минута присутствия исчерпывает дневной лимит в 1 минуту
        (await client.PostAsJsonAsync("/api/child/heartbeat", new { space = "lessons" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var (s, b) = await ReadAsync(await ChildTestSubjects.CreateChat(client));
        s.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b).Should().Be("time_up");
    }

    [Fact]
    public async Task Create_ДваОдновременныхPOST_ДваУрока_ОбаПроходятСверку()
    {
        var name = "kid-chat-two";
        var id = await NewChild(name);
        AgreeRules(id);
        var client = ChildClient(name);
        var ids = await Task.WhenAll(CreateChatAs(client), CreateChatAs(client));
        ids[0].Should().NotBe(ids[1]);
        var project = SubjectProjectOf(id, ChildTestSubjects.Key);
        foreach (var sid in ids)
        {
            var chat = Sessions.GetById(sid)!;
            // Оба урока — в одном проекте-предмете, провижн под per-ключевым замком одного
            // предмета не создал второго проекта
            chat.ProjectId.Should().Be(project.Id);
            _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().BeNull();
        }
        Projects.GetByOwner(id).Count(p => p.ChildSubjectId == ChildTestSubjects.Key).Should().Be(1);
    }

    [Fact]
    public async Task Create_СостояниеWarn_201()
    {
        var name = "kid-chat-warn";
        var id = await NewChild(name, dailyMinutes: 5);
        AgreeRules(id);
        // Лимит 5 минут при пороге «предупредить за» 5 (по умолчанию): осталось ровно 5 — Warn
        _hosts.Services.GetRequiredService<ChildLimitsCalculator>()
            .Evaluate(id, ChildSpaces.Lessons, DateTimeOffset.UtcNow)!.State.Should().Be(ChildState.Warn);

        var sid = await CreateChatAs(ChildClient(name));
        Sessions.GetById(sid)!.ChildSpace.Should().Be(ChildSpaces.Lessons);
    }

    [Fact]
    public async Task Create_ГейтОтказалНаСтарте_503_ЧатНеОстаётсяВСписке()
    {
        var name = "kid-chat-refuse";
        var id = await NewChild(name);
        AgreeRules(id);
        var client = ChildClient(name);
        var gate = _hosts.Services.GetRequiredService<RefusingGate>();
        gate.Refuse = true;
        try
        {
            var (s, _) = await ReadAsync(await ChildTestSubjects.CreateChat(client));
            s.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally { gate.Refuse = false; }

        var project = SubjectProjectOf(id, ChildTestSubjects.Key);
        Sessions.GetByProject(project.Id).Should().BeEmpty("отказанный старт не оставляет чата в проекте");
        var (ls, lb) = await ReadAsync(await client.GetAsync("/api/child/spaces/lessons/chats"));
        ls.Should().Be(HttpStatusCode.OK);
        lb.GetProperty("chats").GetArrayLength().Should().Be(0);
    }

    // ─── №4: список ───

    [Fact]
    public async Task List_ТолькоСвоиДетскиеЧаты_НовыеСверху_БезОнбординга()
    {
        var name = "kid-chat-list";
        var id = await NewChild(name);
        AgreeRules(id);
        var otherName = "kid-chat-list-other";
        var other = await NewChild(otherName);
        AgreeRules(other);
        var client = ChildClient(name);

        var first = await CreateChatAs(client);
        var second = await CreateChatAs(client);
        await CreateChatAs(ChildClient(otherName));
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
        var name = "kid-chat-empty";
        var id = await NewChild(name);
        AgreeRules(id);
        var (s, b) = await ReadAsync(await ChildClient(name).GetAsync("/api/child/spaces/lessons/chats"));
        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("chats").GetArrayLength().Should().Be(0);
        // Пустой список — и пустой провижн: без POST проект-предмет не заводится
        Projects.GetByOwner(id).Should().BeEmpty();
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
        var name = "kid-voice-ok";
        var id = await NewChild(name);
        AgreeRules(id);
        var client = ChildClient(name);
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
        var name = "kid-voice-bad";
        var id = await NewChild(name);
        AgreeRules(id);
        var client = ChildClient(name);
        var sid = await CreateChatAs(client);

        var (s, b) = await ReadAsync(await client.PutAsJsonAsync($"/api/child/chats/{sid}/voice", new { enabled = true, style = "loud" }));
        s.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(b).Should().Be("invalid_style");
        Sessions.GetById(sid)!.VoiceMode.Should().BeFalse();
    }

    [Fact]
    public async Task Voice_ЧужойБезChildSpaceИНесуществующий_404_chat_not_found()
    {
        var name = "kid-voice-404";
        var id = await NewChild(name);
        AgreeRules(id);
        var client = ChildClient(name);
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
        var nameA = "kid-voice-a";
        var nameB = "kid-voice-b";
        var a = await NewChild(nameA);
        var b = await NewChild(nameB);
        AgreeRules(a);
        AgreeRules(b);
        var sidB = await CreateChatAs(ChildClient(nameB));
        var clientA = ChildClient(nameA);
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
        var name = "kid-voice-race";
        var id = await NewChild(name);
        AgreeRules(id);
        var client = ChildClient(name);
        var sid = await CreateChatAs(client);

        var calls = Enumerable.Range(0, 20).Select(i => client.PutAsJsonAsync($"/api/child/chats/{sid}/voice",
            new { enabled = i % 2 == 0, style = i % 3 == 0 ? "digest" : "talk" }));
        var resps = await Task.WhenAll(calls);
        resps.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        // последний побеждает: итог — одно из допустимых значений, закреплённые поля целы
        var chat = Sessions.GetById(sid)!;
        VoiceStyles.IsKnown(chat.VoiceStyle).Should().BeTrue();
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().BeNull();
        chat.ChildSpace.Should().Be(ChildSpaces.Lessons);
        chat.PersonaId.Should().Be(SubjectProjectOf(id, ChildTestSubjects.Key).DefaultPersonaId);
        chat.PrivateToUserId.Should().BeNull();
    }
}
