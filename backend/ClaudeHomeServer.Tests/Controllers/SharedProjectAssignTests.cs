using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2b-2a: назначение человека-исполнителя задачи общего проекта (REST-дверь, PUT /api/tasks/{id}).
/// Назначать может тот, у кого уже есть право править задачи проекта: владелец (право Write
/// участнику закрыто — его строка это проверяет). Исполнителем становится участник с правом
/// записи: редактор — 200; посторонний — 400 assignee_not_member; читатель — 400
/// reader_not_assignable (его право только читать); задача с персоной-исполнителем — 400
/// person_task_not_assignable (инвариант «персона ⇒ Claude» не ломается молча). Личной задаче
/// чужой id — тот же отказ: у неё нет проекта, чьим участником можно быть.
/// Отказ не меняет запись; id владельца — не отказ, а сброс поля (владелец — исполнитель
/// по умолчанию, TaskManager.ResolveMeUserId). Коды — словарь TaskManager, тот же, что
/// поставит MCP-дверь в S2b-2b.
/// </summary>
public class SharedProjectAssignTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectAssignTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();

    private string OwnerId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private string MemberId => Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private static string? CodeOf(JsonElement body) =>
        body.TryGetProperty("code", out var c) ? c.GetString() : null;

    /// <summary>Проект владельца с задачей в нём; role — роль второго пользователя
    /// ("editor"/"reader"; null — участника нет, второй пользователь остаётся посторонним).</summary>
    private async Task<(string ProjectId, string TaskId)> SharedProjectAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(Member(), true);
        var dir = Path.Combine(_factory.TempDir, "sa_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Назначение" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var task = await owner.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title = "Задача проекта" });
        task.EnsureSuccessStatusCode();
        return (projectId, IdOf(await task.Content.ReadAsStringAsync()));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> AssignAsync(
        HttpClient client, string taskId, string assigneeUserId)
    {
        var response = await client.PutAsJsonAsync($"/api/tasks/{taskId}", new { assigneeUserId });
        return (response.StatusCode,
            JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreatePersonaAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/personas", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return IdOf(await response.Content.ReadAsStringAsync());
    }

    // ─── боевая выдача GET /api/tasks (S2b-3a, доработка): MyTasksQuery на пути эндпоинта ───

    private static async Task<List<string>> ListIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tasks");
        response.EnsureSuccessStatusCode();
        var items = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return items.EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToList();
    }

    // Мутация: в MyTasksQuery вернуть фильтр «назначено мне» (ResolveMeUserId(t) == userId) —
    // участник перестаёт видеть чужую задачу проекта, тест краснеет.
    [Fact]
    public async Task Выдача_УчастникВидитВсеЗадачиПроекта_ИНазначенное()
    {
        var (projectId, taskId) = await SharedProjectAsync("editor");
        var other = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title = "Чужая" });
        var otherId = IdOf(await other.Content.ReadAsStringAsync());
        (await AssignAsync(Owner(), taskId, MemberId)).Status.Should().Be(HttpStatusCode.OK);

        var ids = await ListIdsAsync(Member());

        ids.Should().Contain([taskId, otherId]);
    }

    [Fact]
    public async Task Выдача_Читатель_ВидитЗадачиПроекта_БезОписанияИИтога()
    {
        var (projectId, _) = await SharedProjectAsync("reader");
        var created = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "С описанием", description = "секретное описание" });
        var id = IdOf(await created.Content.ReadAsStringAsync());

        var response = await Member().GetAsync("/api/tasks");
        response.EnsureSuccessStatusCode();
        var item = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == id);

        // та же облегчённая карточка, что у GET /api/projects/{p}/tasks: полный текст не едет
        item.GetProperty("description").GetString().Should().BeEmpty();
        (!item.TryGetProperty("resultMarkdown", out var result) || result.ValueKind == JsonValueKind.Null)
            .Should().BeTrue();
        item.GetProperty("listItem").GetBoolean().Should().BeTrue();
        item.GetProperty("hasDescription").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Выдача_ОтозванныйУчастник_НеВидитЗадачПроекта()
    {
        var (projectId, taskId) = await SharedProjectAsync("editor");
        (await ListIdsAsync(Member())).Should().Contain(taskId);

        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{MemberId}")).EnsureSuccessStatusCode();

        (await ListIdsAsync(Member())).Should().NotContain(taskId);
    }

    [Fact]
    public async Task Выдача_Посторонний_НеВидитНазначенногоЕмуВОбщемПроекте()
    {
        var (_, taskId) = await SharedProjectAsync(null);
        var task = Tasks.GetById(taskId)!;
        task.AssigneeUserId = MemberId; // назначение в обход двери: участником он не является

        (await ListIdsAsync(Member())).Should().NotContain(taskId);
    }

    // ─── назначение участника с правом записи ─────────────────────────────────

    [Fact]
    public async Task НазначитьРедактора_200_ИИсполнительОн()
    {
        var (_, taskId) = await SharedProjectAsync("editor");

        var (status, body) = await AssignAsync(Owner(), taskId, MemberId);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("assigneeUserId").GetString().Should().Be(MemberId);
        body.GetProperty("assignee").GetString().Should().Be("me");
        TaskManager.ResolveMeUserId(Tasks.GetById(taskId)!).Should().Be(MemberId);
    }

    // id владельца — не отказ и не значение: «Me» без поля читается как владелец. Поле
    // сбрасывается, задача остаётся на арендаторе (тот же формат записи, что до S2b-1).
    [Fact]
    public async Task НазначитьНаВладельца_СбрасываетAssigneeUserId()
    {
        var (_, taskId) = await SharedProjectAsync("editor");
        (await AssignAsync(Owner(), taskId, MemberId)).Status.Should().Be(HttpStatusCode.OK);

        var (status, body) = await AssignAsync(Owner(), taskId, OwnerId);

        status.Should().Be(HttpStatusCode.OK);
        body.TryGetProperty("assigneeUserId", out _).Should().BeFalse("«на себя» в записи не хранится");
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();
        TaskManager.ResolveMeUserId(Tasks.GetById(taskId)!).Should().Be(OwnerId);
    }

    // ─── отказы: членство и роль ──────────────────────────────────────────────

    [Fact]
    public async Task НазначитьНеУчастника_400_assignee_not_member_ИЗаписьНеМеняется()
    {
        var (_, taskId) = await SharedProjectAsync(null);

        var (status, body) = await AssignAsync(Owner(), taskId, MemberId);

        ((int)status).Should().Be(400);
        CodeOf(body).Should().Be(TaskManager.AssigneeNotMemberCode);
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();
    }

    [Fact]
    public async Task НазначитьЧитателя_400_reader_not_assignable_ИЗаписьНеМеняется()
    {
        var (_, taskId) = await SharedProjectAsync("reader");

        var (status, body) = await AssignAsync(Owner(), taskId, MemberId);

        ((int)status).Should().Be(400);
        CodeOf(body).Should().Be(TaskManager.ReaderNotAssignableCode);
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();
    }

    // Задача с персоной-исполнителем человеку не отдаётся: код несёт ответ 400 (тот же
    // словарь, что у проверок двери), персонная запись остаётся нетронутой.
    [Fact]
    public async Task ЗадачаСПерсоной_НазначитьУчастника_400_person_task_not_assignable_ИПерсонаЦела()
    {
        var (projectId, _) = await SharedProjectAsync("editor");
        var personaId = await CreatePersonaAsync(Owner(),
            new { name = "Персона назначения", scope = "project", projectId });
        var created = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Персонная задача", assignee = "claude", personaId });
        created.EnsureSuccessStatusCode();
        var taskId = IdOf(await created.Content.ReadAsStringAsync());

        var (status, body) = await AssignAsync(Owner(), taskId, MemberId);

        ((int)status).Should().Be(400);
        CodeOf(body).Should().Be(TaskManager.PersonTaskNotAssignableCode);
        var stored = Tasks.GetById(taskId)!;
        stored.PersonaId.Should().Be(personaId);
        stored.Assignee.Should().Be(TaskItemAssignee.Claude);
        stored.AssigneeUserId.Should().BeNull();
    }

    // Личная задача: чужой id — тот же отказ (ей нечего быть участником); свой id допустим
    // и равен «снять» (для правки задачи вызывающий и есть её владелец).
    [Fact]
    public async Task ЛичнаяЗадача_ЧужомуId_400_АСвоему_Сброс()
    {
        var created = await Owner().PostAsJsonAsync("/api/tasks", new { title = "Личная задача" });
        created.EnsureSuccessStatusCode();
        var taskId = IdOf(await created.Content.ReadAsStringAsync());

        var (status, body) = await AssignAsync(Owner(), taskId, MemberId);
        ((int)status).Should().Be(400);
        CodeOf(body).Should().Be(TaskManager.AssigneeNotMemberCode);
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();

        var (ownStatus, ownBody) = await AssignAsync(Owner(), taskId, OwnerId);
        ownStatus.Should().Be(HttpStatusCode.OK);
        ownBody.TryGetProperty("assigneeUserId", out _).Should().BeFalse();
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();
    }

    // ─── право назначать — у того, кто вправе править задачи проекта ───────────

    // Write участнику не открыт (матрица ролей и запрет в ProjectAccessService на месте):
    // участник не может назначить исполнителя, даже самого себя.
    [Fact]
    public async Task УчастникНазначаетИсполнителя_403_project_scope_ИЗаписьНеМеняется()
    {
        var (_, taskId) = await SharedProjectAsync("editor");

        var (status, body) = await AssignAsync(Member(), taskId, MemberId);

        status.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(body).Should().Be("project_scope");
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();
    }
}
