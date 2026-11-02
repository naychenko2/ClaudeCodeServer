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
/// S2d-4i (находка ревью R-S2d-4b, Глеб): write-ответы TasksController отдавали хранимый TaskItem
/// без маски чужих приватных чатов, хотя GET той же задачи её ставил. Задача общего проекта лежит
/// под владельцем-арендатором, а её чат-источник — приватный чат участника (его автор создал
/// задачу из своего чата под токеном арендатора). Инвариант S2d: ни один путь не отдаёт чужой
/// приватный чат (id, название, производные) никому, кроме автора — включая владельца проекта.
///
/// Правка — копия с маской (TaskView.CopyOf + MaskForeignChats, та же функция, что в GET); запись
/// стора не тронута. Автор своего чата и общий чат проекта — как раньше: id в своём поле, формы нет.
///
/// Мутация (критерий c2): в PUT вернуть WithWarning(updated) без MaskForeignChats →
/// «ПравкаЗадачиУчастника…» красный: в JSON владельца снова стоит id и название чужого чата.
/// </summary>
[Trait("Category", "Host")]
public class TaskWriteResponseChatMaskTests : IClassFixture<TestWebApplicationFactory>
{
    private const string TaskTitle = "Задача из приватного чата участника";
    private const string ChatTitle = "Рабочий разговор редактора";

    private readonly TestWebApplicationFactory _factory;

    public TaskWriteResponseChatMaskTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string OwnerId() => UserOf(TestWebApplicationFactory.TestUsername).Id;
    private string MemberId() => UserOf(TestWebApplicationFactory.SecondUsername).Id;

    private User UserOf(string username) =>
        _factory.Services.GetRequiredService<UserStore>().FindByUsername(username)!;

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Проект владельца с участником-редактором: редактор — чтобы чат проекта можно было
    /// сделать приватным (читатель видимость чата не меняет).</summary>
    private async Task<string> ProjectWithEditorAsync()
    {
        await SetFlagAsync(Owner(), true);
        await SetFlagAsync(Member(), true);

        var created = await Owner().PostAsJsonAsync("/api/projects",
            new { name = "Записи" + Guid.NewGuid().ToString("N")[..6] });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        (await Owner().PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();
        return projectId;
    }

    private async Task<string> CreateChatAsync(HttpClient client, string projectId)
    {
        var chat = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", name = ChatTitle });
        chat.EnsureSuccessStatusCode();
        return IdOf(await chat.Content.ReadAsStringAsync());
    }

    /// <summary>Приватный чат автора в общем проекте — тот же путь, что в бою: автор создаёт чат
    /// проекта и делает его приватным (PUT /api/chats/{id}/visibility).</summary>
    private async Task<string> PrivateChatAsync(HttpClient author, string projectId)
    {
        var chatId = await CreateChatAsync(author, projectId);
        var priv = await author.PutAsJsonAsync($"/api/chats/{chatId}/visibility", new { @private = true });
        priv.StatusCode.Should().Be(HttpStatusCode.OK, "приватность ставится её автору");
        Sessions.GetById(chatId)!.Name.Should().Be(ChatTitle, "название чата в сторе: «в JSON нет названия» не тривиально");
        return chatId;
    }

    /// <summary>Задача общего проекта под арендатором с чатом-источником участника — тот же
    /// результат, что MCP-путь участника: tasks_create идёт под токеном арендатора, и TaskManager
    /// кладёт OwnerId арендатора, оставив источником id чата, из которого задачу заводили.</summary>
    private string SeedTaskAsync(string projectId, string sourceSessionId, bool forClaude = false) =>
        Tasks.Create(projectId, OwnerId(), new CreateTaskRequest(TaskTitle,
            SourceSessionId: sourceSessionId,
            Assignee: forClaude ? TaskItemAssignee.Claude : null)).Id;

    private static async Task<JsonElement> OkBodyAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    // Значения в поле нет: отсутствует или null (маска обнуляет значение, не меняя форму ответа:
    // nullable-поля TaskItem сериализуются без null).
    private static bool NoValue(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    private void AssertNoForeignChat(JsonElement body, string chatId, string chatName, string where)
    {
        var json = body.ToString();
        json.Should().NotContain(chatId, $"{where}: id чужого приватного чата не уходит владельцу проекта");
        json.Should().NotContain(chatName, $"{where}: название чужого приватного чата не уходит владельцу проекта");
        var form = body.GetProperty("sourceChat");
        form.GetProperty("private").GetBoolean>().Should().BeTrue($"{where}: рядом форма-заглушка");
        form.GetProperty("title").GetString().Should().Be(ChatRef.PrivateFormTitle, $"{where}: форма не несёт названия чата");
        form.GetProperty("authorUserId").GetString().Should().Be(MemberId(), $"{where}: форма говорит, чей чат");
    }

    // ─── PUT /api/tasks/{id} — тот же вектор, что закрытый в GET ───────────────────────

    [Fact]
    public async Task ПравкаЗадачиУчастникаВладельцем_ВJSONНетНиIdНиНазвания_ЗаписьНеМеняется()
    {
        var projectId = await ProjectWithEditorAsync();
        var chatId = await PrivateChatAsync(Member(), projectId);
        var taskId = SeedTaskAsync(projectId, chatId);

        // Предпосылка та же, что в бою: задача лежит у арендатора, её источник — чат участника.
        Tasks.GetById(taskId)!.OwnerId.Should().Be(OwnerId());
        Tasks.GetById(taskId)!.SourceSessionId.Should().Be(chatId);

        var body = await OkBodyAsync(await Owner().PutAsJsonAsync($"/api/tasks/{taskId}", new { title = "переписал владелец" }));

        AssertNoForeignChat(body, chatId, ChatTitle, "PUT");
        // Состав ответа не изменился ничем, кроме маски: та же карточка, правка применена.
        body.GetProperty("id").GetString().Should().Be(taskId);
        body.GetProperty("title").GetString().Should().Be("переписал владелец");

        // Проецируется копия ответа, не запись: хранимая задача осталась со своим источником.
        Tasks.GetById(taskId)!.SourceSessionId.Should().Be(chatId);
    }

    [Theory]
    [InlineData("ownPrivate")]
    [InlineData("open")]
    public async Task ПравкаЗадачиСвоимИлиОбщимЧатом_ИдНаМесте_ФормыНет(string kind)
    {
        var projectId = await ProjectWithEditorAsync();
        var chatId = kind == "open"
            ? await CreateChatAsync(Owner(), projectId)
            : await PrivateChatAsync(Owner(), projectId);
        var taskId = SeedTaskAsync(projectId, chatId);

        var body = await OkBodyAsync(await Owner().PutAsJsonAsync($"/api/tasks/{taskId}", new { title = "переписал владелец" }));

        body.GetProperty("sourceSessionId").GetString().Should().Be(chatId,
            kind == "open" ? "общий чат проекта виден всем участникам" : "автор своего приватного чата видит его id");
        NoValue(body, "sourceChat").Should().BeTrue("формы-заглушки нет там, где id на месте");
    }

    // ─── POST создания задачи: тот же маска-ответ, что PUT и GET ───────────────────────

    [Fact]
    public async Task СозданиеПроектнойЗадачиСЧужимПриватнымЧатомВТеле_ОтветБезId_ЗаписьСId()
    {
        var projectId = await ProjectWithEditorAsync();
        var chatId = await PrivateChatAsync(Member(), projectId);

        var body = await OkBodyAsync(await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "задача проекта с чужим источником", sourceSessionId = chatId }));

        AssertNoForeignChat(body, chatId, ChatTitle, "POST /api/projects/{id}/tasks");
        Tasks.GetById(body.GetProperty("id").GetString()!)!.SourceSessionId
            .Should().Be(chatId, "ответ проецируется, запись стора — нет");
    }

    [Fact]
    public async Task СозданиеЛичнойЗадачиСЧужимПриватнымЧатомВТеле_ОтветБезId()
    {
        var projectId = await ProjectWithEditorAsync();
        var chatId = await PrivateChatAsync(Member(), projectId);

        var body = await OkBodyAsync(await Owner().PostAsJsonAsync("/api/tasks",
            new { title = "личная задача с чужим источником", sourceSessionId = chatId }));

        AssertNoForeignChat(body, chatId, ChatTitle, "POST /api/tasks");
        Tasks.GetById(body.GetProperty("id").GetString()!)!.SourceSessionId
            .Should().Be(chatId, "приватность держит автора, а не проект задачи");
    }

    // ─── POST /api/tasks/{id}/execute — ответом уходит хранимая задача после запуска ───

    [Fact]
    public async Task ЗапускИсполнителяНаЗадачеУчастника_ОтветБезIdЧужогоПриватногоЧата()
    {
        var projectId = await ProjectWithEditorAsync();
        var chatId = await PrivateChatAsync(Member(), projectId);
        var taskId = SeedTaskAsync(projectId, chatId, forClaude: true);

        var body = await OkBodyAsync(await Owner().PostAsync($"/api/tasks/{taskId}/execute", null));

        AssertNoForeignChat(body, chatId, ChatTitle, "POST /api/tasks/{id}/execute");
        var stored = Tasks.GetById(taskId)!;
        stored.ClaudeStartedAt.Should().NotBeNull("исполнитель реально стартовал — иначе проверяется не этот путь");
        body.GetProperty("linkedSessionId").GetString().Should().Be(stored.LinkedSessionId,
            "ответ — та же задача после запуска: свой чат исполнителя виден владельцу как раньше");
        stored.SourceSessionId.Should().Be(chatId, "запуск не правит чат-источник задачи");
    }
}
