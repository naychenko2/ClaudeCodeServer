using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Персона-исполнитель и персона-постановщик задачи и превью модели по персоне — допуск по
/// проекту задачи, ключ — арендатор (W3-B7, разрез
/// docs/research/shared-projects-personas-cut-2026-11.md §3.2, строки «TasksController» и
/// «ModelsController :133»; И-1, И-2).
///
/// Инвариант шага: задачу общего проекта можно поручить только персоне этого проекта (её
/// кросс-проектная ProjectTasks-привязка — юниты TaskPersonaValidatorTests); личная персона
/// (глобальная, сферная) доступна только своему владельцу: глобальная персона владельца
/// участнику недоступна ни как исполнитель, ни как автор, и своя глобальная персона участника
/// не берёт задачу чужого проекта. До шага обе REST-ручки резолвили персону по sub
/// вызывающего: редактор не мог поручить задачу проектной персоне своего проекта
/// («Персона не найдена или недоступна») — при том, что агентский путь (TasksToolset)
/// ключует context.OwnerId и это мог: две точки правды на одном правиле.
///
/// Форма отказа сохранена: 400 с прежним текстом валидатора («нет» и «чужое» не различаем),
/// при отказе запись не создаётся и не меняется. Личная задача — прежний путь: её арендатор
/// сам вызывающий, своя глобальная персона открыта, проектная — отказ.
///
/// Превью модели (GET /api/models/preview?personaId): допуск — ResolvePersona по праву Read,
/// ключ резолва — арендатор персоны: для проектной персоны общего проекта модель считается по
/// слотам и деньгам владельца проекта, а не участника. Чужая и несуществующая personaId — 404:
/// раньше чужая personaId молча давала превью места вместо отказа.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaTaskTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectPersonaTaskTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();

    private string MemberId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private static async Task<JsonElement> BodyOfAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static async Task<string> PostAsync(HttpClient client, string path, object body)
    {
        var r = await client.PostAsJsonAsync(path, body);
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private async Task<string> GlobalPersonaAsync(HttpClient client) =>
        await PostAsync(client, "/api/personas",
            new { name = "Глобальная-" + Guid.NewGuid().ToString("N")[..6], scope = "global" });

    /// <summary>Проектная персона проекта (fields — доп. поля карточки: ячейки уровня для
    /// тестов превью модели).</summary>
    private static async Task<string> ProjectPersonaAsync(HttpClient owner, string projectId,
        string? tierStrong = null)
    {
        var body = new Dictionary<string, object>
        {
            ["name"] = "Проектная-" + Guid.NewGuid().ToString("N")[..6],
            ["scope"] = "project",
            ["projectId"] = projectId,
        };
        if (tierStrong is not null) body["tierStrong"] = tierStrong;
        var r = await owner.PostAsJsonAsync("/api/personas", body);
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private string NewDir(string tag) => Directory.CreateDirectory(
        Path.Combine(_factory.TempDir, $"{tag}_{Guid.NewGuid():N}"[..(tag.Length + 12)])).FullName;

    /// <summary>Проект владельца с проектной персоной этого проекта; второй пользователь —
    /// участник роли memberRole (null — участников нет: он посторонний).</summary>
    private async Task<(string ProjectId, string PersonaId)> SharedAsync(string? memberRole = "editor")
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        if (memberRole is not null) await SetFlagAsync(Member());

        var projectId = await PostAsync(owner, "/api/projects",
            new { name = "Персоны задач" + Guid.NewGuid().ToString("N")[..6], rootPath = NewDir("spt") });
        if (memberRole is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role = memberRole }))
                .EnsureSuccessStatusCode();

        return (projectId, await ProjectPersonaAsync(owner, projectId));
    }

    private async Task<string> ProjectTaskIdAsync(string projectId, string title) =>
        await PostAsync(Owner(), $"/api/projects/{projectId}/tasks", new { title });

    // ── POST /api/projects/{pid}/tasks: персона-исполнитель ──────────────────────────

    [Fact]
    public async Task РедакторПоручаетЗадачуПроектаЕгоПроектнойПерсоне_200_ПерсонаЗаписана()
    {
        var (projectId, personaId) = await SharedAsync();

        var created = await Member().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача редактора проектной персоне", assignee = "claude", personaId });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.OK,
            $"до шага это было 400 «Персона не найдена или недоступна»: {body}");

        var task = Tasks.GetById(IdOf(body))!;
        task.PersonaId.Should().Be(personaId, "персона записана, а не отброшена молча");
    }

    [Fact]
    public async Task РедакторНеПоручаетЗадачуГлобальнойПерсонеВладельца_400_ЗадачиНет()
    {
        var (projectId, _) = await SharedAsync();
        var ownerGlobal = await GlobalPersonaAsync(Owner());
        var before = Tasks.GetByProject(projectId).Count;

        var denied = await Member().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача личной персоне владельца", assignee = "claude", personaId = ownerGlobal });

        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "глобальная персона владельца участнику не видна (И-3): ни как исполнитель, ни как автор");
        (await BodyOfAsync(denied)).GetProperty("error").GetString()
            .Should().Contain("не найдена или недоступна", "прежняя форма отказа сохранена");
        Tasks.GetByProject(projectId).Count.Should().Be(before, "отказ до записи");
    }

    [Fact]
    public async Task РедакторНеПоручаетЗадачуЧужогоПроектаСвоейГлобальнойПерсоне_400()
    {
        var (projectId, _) = await SharedAsync();
        var ownGlobal = await GlobalPersonaAsync(Member());

        var denied = await Member().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача своей глобальной персоне", assignee = "claude", personaId = ownGlobal });

        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "задача общего проекта берёт только персону этого проекта: своя глобальная — вне его области");
        Tasks.GetByProject(projectId).Should().NotContain(t => t.PersonaId == ownGlobal);
    }

    [Fact]
    public async Task РедакторНеПоручаетЗадачуПроектнойПерсонеДругогоПроекта_400()
    {
        var (projectId, _) = await SharedAsync();
        // Второй проект того же владельца, участника в нём нет: персона живого проекта,
        // но не проекта задачи — допуск мерится по проекту задачи
        var otherProject = await PostAsync(Owner(), "/api/projects",
            new { name = "Другой" + Guid.NewGuid().ToString("N")[..6], rootPath = NewDir("spt_o") });
        var otherPersona = await ProjectPersonaAsync(Owner(), otherProject);

        var denied = await Member().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача персоне чужого проекта", assignee = "claude", personaId = otherPersona });

        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Tasks.GetByProject(projectId).Should().NotContain(t => t.PersonaId == otherPersona);
    }

    [Fact]
    public async Task ВладелецПоручаетЗадачуПроекта_ПроектнойИСвоейГлобальнойПерсоне_КакПрежде()
    {
        var (projectId, projectPersona) = await SharedAsync();
        var ownGlobal = await GlobalPersonaAsync(Owner());

        var byProject = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача проектной персоне", assignee = "claude", personaId = projectPersona });
        byProject.StatusCode.Should().Be(HttpStatusCode.OK);
        Tasks.GetById(IdOf(await byProject.Content.ReadAsStringAsync()))!
            .PersonaId.Should().Be(projectPersona);

        var byGlobal = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача своей глобальной персоне", assignee = "claude", personaId = ownGlobal });
        byGlobal.StatusCode.Should().Be(HttpStatusCode.OK,
            "для владельца его глобальная персона — своя: ключ совпадает, прежний путь");
        Tasks.GetById(IdOf(await byGlobal.Content.ReadAsStringAsync()))!.PersonaId.Should().Be(ownGlobal);
    }

    // ── CreatedByPersonaId (происхождение): то же правило ────────────────────────────

    [Fact]
    public async Task РедакторСтавитПостановщикомПроектнуюПерсону_200_ГлобальнуюПерсонуВладельца_400()
    {
        var (projectId, personaId) = await SharedAsync();
        var ownerGlobal = await GlobalPersonaAsync(Owner());
        var before = Tasks.GetByProject(projectId).Count;

        var ok = await Member().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача с автором-персоной проекта", createdByPersonaId = personaId });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var task = Tasks.GetById(IdOf(await ok.Content.ReadAsStringAsync()))!;
        task.CreatedByPersonaId.Should().Be(personaId, "происхождение записано");
        task.CreatedByUserId.Should().Be(MemberId, "автор человека — вызывающий: персона не подменяет его");

        var denied = await Member().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача с чужим автором-персоной", createdByPersonaId = ownerGlobal });
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Tasks.GetByProject(projectId).Count.Should().Be(before + 1, "отказанное создание не заводит задачу");
    }

    // ── PUT /api/tasks/{id}: та же граница на правке ─────────────────────────────────

    [Fact]
    public async Task РедакторПоручаетСуществующуюЗадачуПроектаПроектнойПерсоне_200_ГлобальнойПерсонеВладельца_400ЗаписьЦела()
    {
        var (projectId, personaId) = await SharedAsync();
        var ownerGlobal = await GlobalPersonaAsync(Owner());
        var taskId = await ProjectTaskIdAsync(projectId, "Задача проекта");

        var ok = await Member().PutAsJsonAsync($"/api/tasks/{taskId}",
            new { assignee = "claude", personaId });
        var body = await ok.Content.ReadAsStringAsync();
        ok.StatusCode.Should().Be(HttpStatusCode.OK, $"правка с той же границей, что создание: {body}");
        Tasks.GetById(taskId)!.PersonaId.Should().Be(personaId);

        var denied = await Member().PutAsJsonAsync($"/api/tasks/{taskId}", new { personaId = ownerGlobal });
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Tasks.GetById(taskId)!.PersonaId.Should().Be(personaId, "отказ не меняет запись: исполнитель остался");
    }

    // ── POST /api/tasks (личная задача): её арендатор — сам вызывающий ────────────────

    [Fact]
    public async Task ЛичнаяЗадачаСвоейГлобальнойПерсоне_200_ПроектнойПерсонеЧужогоПроекта_400()
    {
        var (_, projectPersona) = await SharedAsync();
        var ownGlobal = await GlobalPersonaAsync(Member());

        var ok = await Member().PostAsJsonAsync("/api/tasks",
            new { title = "Личная своей персоне", assignee = "claude", personaId = ownGlobal });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "личная задача — личное пространство вызывающего: прежний путь");
        Tasks.GetById(IdOf(await ok.Content.ReadAsStringAsync()))!.PersonaId.Should().Be(ownGlobal);

        var denied = await Member().PostAsJsonAsync("/api/tasks",
            new { title = "Личная проектной персоне", assignee = "claude", personaId = projectPersona });
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest, "проектная персона личную задачу не берёт");
    }

    // ── GET /api/models/preview?personaId: допуск и ключ ─────────────────────────────

    private async Task<JsonElement> PreviewAsync(HttpClient client, string query)
    {
        var r = await client.GetAsync($"/api/models/preview?{query}");
        return await BodyOfAsync(r);
    }

    private Task<HttpResponseMessage> PreviewRawAsync(HttpClient client, string query) =>
        client.GetAsync($"/api/models/preview?{query}");

    [Fact]
    public async Task РедакторПревьюМоделиПроектнойПерсоны_200_ЕёЯчейка()
    {
        var owner = Owner();
        var (projectId, _) = await SharedAsync();
        var personaWithCell = await ProjectPersonaAsync(owner, projectId, tierStrong: "persona-opus");

        var d = await PreviewAsync(Member(), $"place=chat-persona&personaId={personaWithCell}");

        d.GetProperty("model").GetString().Should().Be("persona-opus",
            "участник видит модель той персоны, чей ход в его проекте он запускает");
        d.GetProperty("source").GetString().Should().Be("persona-cell");
    }

    [Fact]
    public async Task ПревьюПроектнойПерсоныБезЯчеек_ПоСлотамАрендатораANеУчастника()
    {
        var (projectId, personaId) = await SharedAsync();
        // Известные слоты обоих: резолв обязан пройти по арендатору задачи (И-2) —
        // по слотам и деньгам владельца проекта, а не того, кто смотрит карточку
        (await Owner().PutAsJsonAsync("/api/me/model-tiers",
            new { strong = "tenant-strong", medium = "", weak = "" })).EnsureSuccessStatusCode();
        (await Member().PutAsJsonAsync("/api/me/model-tiers",
            new { strong = "member-strong", medium = "", weak = "" })).EnsureSuccessStatusCode();

        var d = await PreviewAsync(Member(), $"place=chat-persona&personaId={personaId}");

        d.GetProperty("model").GetString().Should().Be("tenant-strong",
            "ключ резолва модели проектной персоны — её арендатор: иначе участнику показали бы его личные слоты");
        d.GetProperty("source").GetString().Should().Be("owner-slot");
    }

    [Fact]
    public async Task ПостороннийПревьюПоПроектнойПерсоне_404()
    {
        var (projectId, personaId) = await SharedAsync(memberRole: null);

        var r = await PreviewRawAsync(Member(), $"place=chat-persona&personaId={personaId}");

        r.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "чужая персона: существования не подтверждаем (раньше был 200 с превью места)");
    }

    [Fact]
    public async Task РедакторПревьюПоГлобальнойПерсонеВладельца_404()
    {
        var (_, _) = await SharedAsync();
        var ownerGlobal = await GlobalPersonaAsync(Owner());

        var r = await PreviewRawAsync(Member(), $"place=chat-persona&personaId={ownerGlobal}");

        r.StatusCode.Should().Be(HttpStatusCode.NotFound, "личная персона владельца участнику не видна (И-3)");
    }

    [Fact]
    public async Task СвояГлобальнаяПерсона_ПревьюКакПрежде_ЧужаяПерсонаБезIdНеЛомаетПутьМеста()
    {
        await SharedAsync();
        var ownGlobal = await GlobalPersonaAsync(Member());
        (await Member().PutAsJsonAsync("/api/me/model-tiers",
            new { strong = "member-strong", medium = "", weak = "" })).EnsureSuccessStatusCode();

        var own = await PreviewAsync(Member(), $"place=chat-persona&personaId={ownGlobal}");
        own.GetProperty("model").GetString().Should().Be("member-strong", "своя персона — по владению, как прежде");

        // Без personaId путь места не тронут резолвом персоны
        var plain = await PreviewRawAsync(Member(), "place=chat-new");
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
