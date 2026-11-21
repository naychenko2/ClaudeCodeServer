using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Б16c (И10, ADR-041 ред. 7, решения 69/73 и таблица И10; разрез §13 «Что куратор может,
/// а что держит детский слой»): персона проекта-предмета — репетитор уроков ребёнка.
///
/// Держит детский слой — 409 с машинным кодом child_subject_persona, ЛЮБОМУ вызывающему,
/// до которого доходит дверь (обоим кураторам-редакторам):
/// - смена `Scope` или `ProjectId` (PUT /{id}) — репетитор не уходит из проекта и не переносится
///   в другой: его зону задал провижн предмета (решение 70);
/// - удаление руководителя (DELETE /{id}, в том числе с преемником) — на нём держатся уроки;
///   выключить репетитора — выключением предмета. Закрывает дыру R-Б16b: после перевода двери
///   на ConfigurePersonas куратор-редактор проходил DELETE и мог удалить репетитора;
/// - смена руководителя (POST /{id}/make-default) — его ставит провижн предмета, а не дверь;
/// - все write-двери правил автоматики — проактивный ход репетитора шёл бы вне детского чата
///   и нарушил бы его окна, лимиты и приватность (решение 73: «проактивного репетитора в этой
///   волне нет»).
/// Свободно куратору: характер, голос. Модель — не поле этой двери: она настройки владельца
/// (гейт owner_settings, PersonasController.cs:410), а ребёнок-владелец к дверям не доходит
/// (403 гейта И1 п.15). Модель урока — не поле предмета (снято Б17c): это модель персоны-
/// репетитора, развёрнутая штатной цепочкой на запуске (явная модель → матрицы персоны →
/// слоты ребёнка, ChildModeGate.LiveRouteOf), и проверяется она на запуске (Б2b); модель
/// чата правил — ChildProfile.Model ребёнка (тот же LiveRouteOf, ветка профиля).
///
/// Двери автоматики по коду PersonasController (GET {id}/automation — чтение, отказа нет):
/// POST {id}/automation (AddAutomationRule), PUT {id}/automation (SetAutomationRules),
/// PUT {id}/automation/{ruleId} (UpdateAutomationRule — правка и включение Enabled),
/// DELETE {id}/automation/{ruleId} (DeleteAutomationRule),
/// POST {id}/automation/{ruleId}/test (TestAutomationRule),
/// POST {id}/automation/suggest (SuggestAutomation), POST {id}/automation/generate (GenerateAutomation).
///
/// Фитчюра (доработка Б16c по дефекту 11c0e6f0): проект-предмет — как в продукте (форма Б8b):
/// владелец — сам ребёнок, связь — маркер ChildSubjectId; куратор входит не ручным приглашением
/// (Б14b отвечает ей 409 managed_by_child_profile), а как в продукте: опекуном ChildProfile.
/// GuardianUserIds и синхронизацией кураторов (Б14a, образец — ChildCuratorSyncTests). Оба
/// «куратора» теста — живые опекуны профиля: границы И10 не делают различия между ними, и оба
/// получают 409. Сам ребёнок к дверям персон не допускается (у них нет [ChildAllowed], И1 п.15 —
/// 403 child_mode раньше любой границы), поэтому «любой вызывающий» здесь — два куратора;
/// «409 и владельцу» в прежнем смысле (взрослый-владелец) в продукте некому проверить:
/// взрослый-владелец проекту-предмету не владелец (провижн Б8b), а ребёнок-владелец — 403 гейта.
/// Руководитель — Project.DefaultPersonaId (та же точка правды, что у провижна предмета).
///
/// Последний тест — обычный проект: те же три двери (make-default, автоматика, удаление
/// лидера с преемником) открыты и работают как раньше: граница не течёт за пределы предмета.
///
/// Мутации, на которых файл краснеет: ChildSubjectProjectOf → `return null` (краснеют все
/// кейсы 409); снять проверку DefaultPersonaId в Delete (краснеет кейс удаления); снять guard
/// с любой двери автоматики (краснеет её строка теории); снять вызов синка из ArrangeSubjectAsync
/// (краснеют все 14 кейсов проекта-предмета: без синка кураторов в проекте нет — замерено
/// на копии мутации, 14 FAILED / 2 passed, краснеет само утверждение «куратор в проекте»);
/// заменить код гейта owner-settings в теле Update (PersonasController) на другой — краснеет
/// кейс модели: сравнение с литералом wire-контракта, не с константой гейта (Х-W4-1).
/// </summary>
[Trait("Category", "Host")]
public class ChildTutorPersonaDoorsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Refused = "child_subject_persona";
    private const string KidPassword = "b16c-kid-pw-123456";

    private readonly TestWebApplicationFactory _factory;

    public ChildTutorPersonaDoorsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient OwnerClient() => _factory.CreateAuthenticatedClient();

    private HttpClient CuratorClient() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();
    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();

    // Оба «куратора» — живые опекуны профиля: граница И10 не различает их, и каждый запрос
    // обязан ответить 409, а не только «чужому» (тот же состав участника, что пишет синк Б14a).
    private (string Who, HttpClient Client)[] Callers() =>
        [("куратор-опекун (админ фикстуры)", OwnerClient()), ("куратор-опекун (второй)", CuratorClient())];

    private static string RunId() => Guid.NewGuid().ToString("N")[..8];

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private static async Task<(int Status, string? Code)> SendAsync(HttpClient client, HttpMethod method,
        string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        string? code = null;
        if (text.Length > 0)
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(text);
                if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c))
                    code = c.GetString();
            }
            catch (JsonException) { /* тело не JSON — кода нет */ }
        }
        return ((int)response.StatusCode, code);
    }

    /// <summary>Проект-предмет — как в продукте (форма Б8b: маркер ChildSubjectId, владелец —
    /// ребёнок) с двумя кураторами-опекунами (ChildProfile.GuardianUserIds + синхронизация
    /// Б14a, образец — ChildCuratorSyncTests) и его репетитором: проектная персона ребёнка +
    /// закрепление руководителем (та же цепочка, что у провижна предмета: персона →
    /// DefaultPersonaId). Репетитор и кандидаты — персоны ребёнка: ни один взрослый не может
    /// создать их REST'ом (создание проектной персоны — право по проекту, OwnerId новой персоны
    /// — владелец проекта, PersonasController.cs:343), поэтому фикстура пишет их стором под
    /// ключом владельца — как провижер.</summary>
    private async Task<(string ProjectId, string KidId, string TutorId)> ArrangeSubjectAsync()
    {
        var run = RunId();
        await SetFlagAsync(OwnerClient());
        await SetFlagAsync(CuratorClient());

        var subjectId = "math-" + run;
        var kid = Users.Add("b16c-kid-" + run, KidPassword, "user");
        // Профиль с предметом и двумя опекунами — одна точка правды кураторов (Б14a, решение 61)
        Users.GetById(kid.Id)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            GuardianUserIds = [OwnerId, CuratorId],
            Subjects = [new ChildSubject { Id = subjectId, Label = "Предмет " + run, Glyph = "Аа", Color = "info" }],
        };

        var project = NewSubjectProject(kid.Id, kid.Username, "Предмет " + run, subjectId);
        ProjectCapabilities.IsChildSubject(project).Should().BeTrue("фитчюра — проект-предмет по маркеру");

        // Кураторы — синхронизацией профиля, не ручным приглашением (Б14b отвечает ему 409):
        // тот же вызов, что за сохранением профиля в продукте (UsersController.cs:423).
        await _factory.Services.GetRequiredService<ChildCuratorSync>().SyncChildCuratorsAsync(kid.Id);
        Projects.GetById(project.Id)!.Members.Should()
            .Contain(m => m.UserId == CuratorId && m.Role == ProjectRole.Editor, "фитчюра — куратор в проекте");

        var tutorId = CreateSubjectPersona(kid.Id, project.Id, "Репетитор");
        Projects.SetDefaultPersona(project.Id, tutorId);
        Projects.GetById(project.Id)!.DefaultPersonaId.Should().Be(tutorId, "руководитель — точка правды репетитора");
        return (project.Id, kid.Id, tutorId);
    }

    private string CuratorId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    /// <summary>Обычный (не-предметный) проект владельца — для тестов «граница не течёт за
    /// пределы предмета».</summary>
    private Project NewProject(string name)
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(_factory.TempDir, "b16c_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        return Projects.Create(name, dir, OwnerId, TestWebApplicationFactory.TestUsername, createDirectory: true);
    }

    /// <summary>Проект-предмет: форма провижна Б8b (маркер + папка + владелец-ребёнок);
    /// папка — тестовый TempDir (тот же приём, что у ProjectMembersControllerTests.cs:74 и
    /// ChildSubjectsEndpointsTests.cs:91: дверям персон домашняя папка предмета не нужна).</summary>
    private Project NewSubjectProject(string kidId, string kidUsername, string name, string subjectId)
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(_factory.TempDir, "b16c_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        return Projects.Create(name, dir, kidId, kidUsername,
            createDirectory: true, childSubjectId: subjectId);
    }

    /// <summary>Проектная персона проекта-предмета — стором под ключом его владельца (как
    /// провижер), имя уникально на тест.</summary>
    private string CreateSubjectPersona(string kidId, string projectId, string name)
    {
        var persona = Personas.Create(kidId, name + " " + RunId(), null, null, null, null, null,
            PersonaScope.Project, projectId, null, null, false);
        return persona.Id;
    }

    private static async Task<string> CreatePersonaAsync(HttpClient client, string projectId, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/personas",
            new { name, scope = "project", projectId });
        resp.EnsureSuccessStatusCode();
        return IdOf(await resp.Content.ReadAsStringAsync());
    }

    // --- Scope / ProjectId: репетитор не уходит из проекта ---

    [Fact]
    public async Task СменаScopeРепетитора_409_ОбоимиКураторами()
    {
        var (_, kidId, tutorId) = await ArrangeSubjectAsync();
        Personas.Get(tutorId, kidId)!.Scope.Should().Be(PersonaScope.Project, "фитчюра — проектная персона");

        foreach (var (who, client) in Callers())
        {
            var (status, code) = await SendAsync(client, HttpMethod.Put, $"/api/personas/{tutorId}",
                new { scope = "global" });
            status.Should().Be(409, $"{who}: репетитор предмета не становится глобальной персоной");
            code.Should().Be(Refused, $"{who}: машинный код границы И10");
        }

        Personas.Get(tutorId, kidId)!.Scope.Should().Be(PersonaScope.Project,
            "отказанная правка не оставила следа в сторе");
    }

    [Fact]
    public async Task ПереносРепетитораВДругойПроект_409_ОбоимиКураторами()
    {
        var (_, kidId, tutorId) = await ArrangeSubjectAsync();
        var other = NewProject("Обычный проект " + RunId());
        ProjectCapabilities.IsChildSubject(other).Should().BeFalse("второй проект — без маркера");
        var before = Personas.Get(tutorId, kidId)!.ProjectId;

        foreach (var (who, client) in Callers())
        {
            var (status, code) = await SendAsync(client, HttpMethod.Put, $"/api/personas/{tutorId}",
                new { projectId = other.Id });
            status.Should().Be(409, $"{who}: репетитор не переносится в другой проект");
            code.Should().Be(Refused, $"{who}: машинный код границы И10");
        }

        Personas.Get(tutorId, kidId)!.ProjectId.Should().Be(before,
            "отказанный перенос не оставил следа в сторе");
    }

    // --- Тот же запрос в обычном проекте: границы нет (защита от протекания) ---

    [Fact]
    public async Task СменаScopeИлиProjectIdОбычнойПроектнойПерсоны_Не409()
    {
        var run = RunId();
        var ordinary = NewProject("Обычный " + run);
        var personaId = await CreatePersonaAsync(OwnerClient(), ordinary.Id, "Персона " + run);
        var other = NewProject("Другой обычный " + run);

        foreach (var body in new object?[] { new { scope = "global" }, new { projectId = other.Id } })
        {
            var (status, code) = await SendAsync(OwnerClient(), HttpMethod.Put, $"/api/personas/{personaId}", body!);
            status.Should().NotBe(409, $"обычная проектная персона: {JsonSerializer.Serialize(body)}");
            code.Should().NotBe(Refused, "граница И10 живёт только в проекте-предмете");
        }
    }

    // --- Удаление руководителя: не удаляет никто, и с преемником тоже ---

    [Fact]
    public async Task УдалениеРуководителяПроектаПредмета_409_ОбоимиКураторами()
    {
        var (projectId, kidId, tutorId) = await ArrangeSubjectAsync();

        foreach (var (who, client) in Callers())
        {
            var (status, code) = await SendAsync(client, HttpMethod.Delete, $"/api/personas/{tutorId}");
            status.Should().Be(409, $"{who}: после Б16b куратор-редактор проходил эту дверь (R-Б16b)");
            code.Should().Be(Refused, $"{who}: машинный код границы И10");
        }

        Personas.Get(tutorId, kidId).Should().NotBeNull("отказанное удаление не тронуло персону");
        Projects.GetById(projectId)!.DefaultPersonaId.Should().Be(tutorId, "руководитель на месте");
    }

    [Fact]
    public async Task УдалениеРуководителяСПреемником_409АНеКаскадПреемника()
    {
        var (projectId, kidId, tutorId) = await ArrangeSubjectAsync();
        var successorId = CreateSubjectPersona(kidId, projectId, "Вторая");

        var (status, code) = await SendAsync(CuratorClient(), HttpMethod.Delete,
            $"/api/personas/{tutorId}?successorId={successorId}");
        status.Should().Be(409, "репетитору не подбирают преемника: его не удаляют вовсе");
        code.Should().Be(Refused);

        Projects.GetById(projectId)!.DefaultPersonaId.Should().Be(tutorId, "каскада преемника не случилось");
        Personas.Get(successorId, kidId).Should().NotBeNull("преемник не был потрачен на отказанный каскад");
    }

    // --- Инертная персона того же проекта-предмета удаляется: отказ решён по роли руководителя ---

    [Fact]
    public async Task УдалениеИнертнойПерсоныПроектаПредмета_Разрешена()
    {
        var (projectId, kidId, tutorId) = await ArrangeSubjectAsync();
        var inertId = CreateSubjectPersona(kidId, projectId, "Инертная");

        (await CuratorClient().DeleteAsync($"/api/personas/{inertId}")).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "решение 69: прочие персоны проекта-предмета инертны, их удаление не трогает уроки");

        Personas.Get(inertId, kidId).Should().BeNull();
        Projects.GetById(projectId)!.DefaultPersonaId.Should().Be(tutorId, "руководитель не тронут");
    }

    // --- Смена руководителя: его ставит провижн предмета, а не дверь make-default ---

    [Fact]
    public async Task СменаРуководителяПроектаПредмета_409_ОбоимиКураторами()
    {
        var (projectId, kidId, tutorId) = await ArrangeSubjectAsync();
        var otherId = CreateSubjectPersona(kidId, projectId, "Кандидат");

        foreach (var (who, client) in Callers())
        {
            var (status, code) = await SendAsync(client, HttpMethod.Post, $"/api/personas/{otherId}/make-default");
            status.Should().Be(409, $"{who}: руководителя проекта-предмета назначает не эта дверь");
            code.Should().Be(Refused, $"{who}: машинный код границы И10");
        }

        Projects.GetById(projectId)!.DefaultPersonaId.Should().Be(tutorId, "руководитель на месте");
    }

    // --- Автоматика: 409 на каждой write-двери правил ---

    private sealed record Door(string Title, HttpMethod Method, string PathFormat, object? Body);

    private static readonly Door[] AutomationDoors =
    [
        new("POST {id}/automation — добавить правило", HttpMethod.Post, "/api/personas/{p}/automation",
            new { name = "Правило", enabled = true, actionInstruction = "Сказать слово" }),
        new("PUT {id}/automation — заменить набор", HttpMethod.Put, "/api/personas/{p}/automation",
            new { rules = new[] { new { name = "Правило", actionInstruction = "Сказать слово" } } }),
        new("PUT {id}/automation/{r} — править и включить", HttpMethod.Put, "/api/personas/{p}/automation/{r}",
            new { enabled = true, actionInstruction = "Сказать слово" }),
        new("DELETE {id}/automation/{r} — снять правило", HttpMethod.Delete, "/api/personas/{p}/automation/{r}", null),
        new("POST {id}/automation/{r}/test — прогнать правило", HttpMethod.Post, "/api/personas/{p}/automation/{r}/test", null),
        new("POST {id}/automation/suggest — подобрать кандидатов", HttpMethod.Post, "/api/personas/{p}/automation/suggest", null),
        new("POST {id}/automation/generate — сгенерировать по запросу", HttpMethod.Post, "/api/personas/{p}/automation/generate",
            new { prompt = "Следить за уроками" }),
    ];

    public static IEnumerable<object?[]> AutomationDoorsData()
    {
        foreach (var d in AutomationDoors)
            yield return new object?[] { d.Title, d.Method, d.PathFormat, d.Body };
    }

    [Theory]
    [MemberData(nameof(AutomationDoorsData))]
    public async Task АвтоматикаРепетитора_409_НаКаждойДвери_ИОбоимиКураторами(
        string title, HttpMethod method, string pathFormat, object? body)
    {
        var (_, kidId, tutorId) = await ArrangeSubjectAsync();
        // Правило в сторе — прямой записью, не дверью: двери отказывают по признаку персоны,
        // а не по наличию правила (у репетитора их быть не должно — решение 73).
        var seeded = new PersonaAutomationRule { Name = "Фитчюра", Enabled = false };
        Personas.UpdateRules(tutorId, kidId, [seeded]);

        var path = pathFormat.Replace("{p}", tutorId).Replace("{r}", seeded.Id);
        foreach (var (who, client) in Callers())
        {
            var (status, code) = await SendAsync(client, method, path, body);
            status.Should().Be(409, $"{title} — {who}: проактивный ход репетитора шёл бы вне детского чата");
            code.Should().Be(Refused, $"{title} — {who}: машинный код границы И10");
        }

        var after = Personas.Get(tutorId, kidId)!.AutomationRules!;
        after.Should().HaveCount(1, "отказанная дверь не добавила и не сняла правило");
        after[0].Id.Should().Be(seeded.Id);
        after[0].Enabled.Should().BeFalse("включение правила тоже отказано");
        after[0].Action.Instruction.Should().NotBe("Сказать слово", "правка правила отказана");
    }

    // --- Свободно куратору: характер и голос; модель — не поле этой двери (настройки владельца) ---

    [Fact]
    public async Task РепетиторЖивёт_КураторПравитХарактерИГолос_МодельНеКуратору()
    {
        var run = RunId();
        var (_, kidId, tutorId) = await ArrangeSubjectAsync();

        var (toneStatus, _) = await SendAsync(CuratorClient(), HttpMethod.Put, $"/api/personas/{tutorId}",
            new { contract = new { tone = "Тон репетитора " + run } });
        (toneStatus >= 200 && toneStatus < 300).Should().BeTrue($"куратор правит характер репетитора → {toneStatus}");
        Personas.Get(tutorId, kidId)!.Contract?.Tone.Should().Be("Тон репетитора " + run);

        // Голос валиден по TtsVoiceCatalog: «sofia» с амплуа «casual» (тот же выбор, что в
        // SharedProjectPersonaRightsTests:442)
        var (voiceStatus, _) = await SendAsync(CuratorClient(), HttpMethod.Put, $"/api/personas/{tutorId}/voice",
            new { voice = "sofia", role = "casual" });
        (voiceStatus >= 200 && voiceStatus < 300).Should().BeTrue($"куратор правит голос репетитора → {voiceStatus}");

        // Модель — поле «зоны и денег» персоны: не-владельцу его отказывает гейт тела
        // owner_settings (PersonasController.cs:410), и это не граница И10: модель урока —
        // не поле предмета (снято Б17c), а развёрнутая на запуске модель персоны-репетитора
        // (ChildModeGate.LiveRouteOf; проверка — на запуске, Б2b). Код отказа — литерал
        // wire-контракта: сравнение с OwnerSettingsGate.ErrorCode не держало тест (обе стороны
        // менялись вместе с константой, мутация оставалась зелёной).
        var (modelStatus, modelCode) = await SendAsync(CuratorClient(), HttpMethod.Put, $"/api/personas/{tutorId}",
            new { model = "sonnet" });
        modelStatus.Should().Be(403, $"модель репетитора куратору не дана → {modelStatus}");
        modelCode.Should().Be("owner_settings", "отказ — гейт настроек владельца, не граница И10");

        // Ребёнок-владелец до этой двери не доходит вовсе: его отбивает гейт И1 п.15
        var kidClient = _factory.CreateAuthenticatedClient(Users.GetById(kidId)!.Username, KidPassword);
        var (kidStatus, _) = await SendAsync(kidClient, HttpMethod.Put, $"/api/personas/{tutorId}",
            new { model = "sonnet" });
        kidStatus.Should().Be(403, "ребёнок-владелец к дверям персон не допускается (И1 п.15)");

        Personas.Get(tutorId, kidId)!.Model.Should().NotBe("sonnet",
            "отказанная у двери модель не оставила следа в сторе: её точка — явное поле персоны, "
            + "разворачивается штатной цепочкой на запуске (LiveRouteOf)");
    }

    // --- Обычный проект: те же три двери открыты и работают как раньше ---

    [Fact]
    public async Task ОбычныйПроект_СменаРуководникаАвтоматикаИУдалениеЛидера_КакРаньше()
    {
        var run = RunId();
        var ordinary = NewProject("Обычный " + run);
        var firstId = await CreatePersonaAsync(OwnerClient(), ordinary.Id, "Первая " + run);
        var secondId = await CreatePersonaAsync(OwnerClient(), ordinary.Id, "Вторая " + run);
        Projects.SetDefaultPersona(ordinary.Id, firstId);

        var (autoStatus, autoCode) = await SendAsync(OwnerClient(), HttpMethod.Post,
            $"/api/personas/{firstId}/automation",
            new { name = "Правило " + run, enabled = true, actionInstruction = "Сказать слово" });
        (autoStatus >= 200 && autoStatus < 300).Should().BeTrue($"обычной персоне правило ставится → {autoStatus}");
        autoCode.Should().NotBe(Refused);
        Personas.Get(firstId, OwnerId)!.AutomationRules.Should().HaveCount(1);

        var (leadStatus, leadCode) = await SendAsync(OwnerClient(), HttpMethod.Post, $"/api/personas/{secondId}/make-default");
        (leadStatus >= 200 && leadStatus < 300).Should().BeTrue($"смена руководителя обычного проекта открыта → {leadStatus}");
        leadCode.Should().NotBe(Refused);
        Projects.GetById(ordinary.Id)!.DefaultPersonaId.Should().Be(secondId);

        var (delStatus, delCode) = await SendAsync(OwnerClient(), HttpMethod.Delete,
            $"/api/personas/{secondId}?successorId={firstId}");
        delStatus.Should().Be((int)HttpStatusCode.NoContent, "лидер обычного проекта удаляется с преемником");
        delCode.Should().NotBe(Refused);
        Projects.GetById(ordinary.Id)!.DefaultPersonaId.Should().Be(firstId, "каскад преемника отработал");
        Personas.Get(secondId, OwnerId).Should().BeNull();
    }
}
