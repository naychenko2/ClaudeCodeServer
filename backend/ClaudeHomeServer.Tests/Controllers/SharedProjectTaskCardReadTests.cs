using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2c-9 (решение Андрея 01.11): полная карточка чужой задачи открыта участнику общего проекта
/// по праву чтения. GET /api/tasks/{id}: редактор и читатель получают 200 с Description и
/// ResultMarkdown; посторонний, отозванный, участник с выключенным флагом, личная задача владельца —
/// 404 («нет» и «чужое» не различаем). Правка остаётся отказом читателю (403 project_scope);
/// редактору дверь правки открыл F2-T1 — её матрица в SharedProjectTaskWriteRightsTests.
///
/// Список и карточка идут через одну проекцию для участника (TaskView): id заметки личного vault
/// владельца участнику не уходит (он раскрывает путь и название личной заметки), id заметки этого
/// проекта остаётся; ClaudeResult — как сейчас (Q2). Ключ хранилища не меняется: задача лежит у
/// арендатора.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectTaskCardReadTests : IClassFixture<TestWebApplicationFactory>
{
    private const string DescriptionText = "Описание задачи проекта — полный текст для карточки.";
    private const string ResultText = "Итог выполнения — ResultMarkdown для карточки.";

    private readonly TestWebApplicationFactory _factory;

    public SharedProjectTaskCardReadTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string MemberId() =>
        _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    // Тот же формат id заметки, что кодирует NotesService.EncodeId: base64url «source|путь».
    private static string NoteId(string sourceKey, string relPath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sourceKey}|{relPath}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Проект владельца с участником заданной роли (role = null — участника нет, второй
    /// пользователь остаётся посторонним).</summary>
    private async Task<string> ProjectWithMemberAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        if (role is not null) await SetFlagAsync(Member(), true);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Карточка" + Guid.NewGuid().ToString("N")[..6] });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    /// <summary>Задача с описанием, итогом и ссылкой на заметку (seed через TaskManager: SourceNoteId —
    /// не REST-поле). sourceNoteId — id заметки-источника; null — ссылки нет.</summary>
    private string SeedTaskAsync(string projectId, string? sourceNoteId = null)
    {
        var ownerUserId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        return _factory.Services.GetRequiredService<TaskManager>().Create(projectId, ownerUserId,
            new CreateTaskRequest("Задача общего проекта", Description: DescriptionText,
                ResultMarkdown: ResultText, SourceNoteId: sourceNoteId)).Id;
    }

    private async Task<JsonElement> GetCardAsync(HttpClient client, string taskId)
    {
        var r = await client.GetAsync($"/api/tasks/{taskId}");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var r = await client.GetAsync(url);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
    }

    // Значения в поле нет: отсутствует или null. Маска проекции обнуляет значение, не меняя
    // форму ответа: TaskItem уже сериализует nullable-поля как null (WhenWritingNull на них нет),
    // «не уходит» здесь — отсутствие значения, а не отсутствие ключа.
    private static bool NoValue(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    // ── карточка: редактор и читатель получают 200 с полным текстом ─────────────────

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ЧитаетПолнуюКарточку_200_СDescriptionИResultMarkdown(string role)
    {
        var projectId = await ProjectWithMemberAsync(role);
        var taskId = SeedTaskAsync(projectId);

        var card = await GetCardAsync(Member(), taskId);

        card.GetProperty("id").GetString().Should().Be(taskId);
        card.GetProperty("title").GetString().Should().Be("Задача общего проекта");
        card.GetProperty("description").GetString().Should().Be(DescriptionText);
        card.GetProperty("resultMarkdown").GetString().Should().Be(ResultText);
        // Q2 (решение Андрея): ClaudeResult участнику показывается как сейчас
        card.TryGetProperty("claudeResult", out _).Should().BeTrue();
    }

    // ── 404: «нет» и «чужое» не различаем ────────────────────────────────────────────

    [Fact]
    public async Task Посторонний_Карточка_404()
    {
        var projectId = await ProjectWithMemberAsync(role: null);
        var taskId = SeedTaskAsync(projectId);

        (await Member().GetAsync($"/api/tasks/{taskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ОтозванныйУчастник_Карточка_404()
    {
        var projectId = await ProjectWithMemberAsync("reader");
        var taskId = SeedTaskAsync(projectId);
        var memberId = MemberId();

        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{memberId}")).EnsureSuccessStatusCode();

        (await Member().GetAsync($"/api/tasks/{taskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ФлагУчастникаВыключен_Карточка_404()
    {
        var projectId = await ProjectWithMemberAsync("reader");
        var taskId = SeedTaskAsync(projectId);
        await SetFlagAsync(Member(), false);
        try
        {
            (await Member().GetAsync($"/api/tasks/{taskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await SetFlagAsync(Member(), true); }
    }

    [Fact]
    public async Task ЛичнаяЗадачаВладельца_Участнику_404()
    {
        _ = await ProjectWithMemberAsync("reader");
        // личная задача владельца (без проекта) — тот же ключ хранилища, но не проектная
        var personal = await Owner().PostAsJsonAsync("/api/tasks", new { title = "Личная задача владельца" });
        personal.EnsureSuccessStatusCode();
        var personalId = IdOf(await personal.Content.ReadAsStringAsync());

        (await Member().GetAsync($"/api/tasks/{personalId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "личная задача владельца — не задача проекта: всегда 404");
        // для владельца она осталась своей
        (await Owner().GetAsync($"/api/tasks/{personalId}")).EnsureSuccessStatusCode();
    }

    // ── правка читателю по-прежнему отказ (редактору её открыл F2-T1: SharedProjectTaskWriteRightsTests) ──

    [Fact]
    public async Task Читатель_ПравитКарточку_403()
    {
        var projectId = await ProjectWithMemberAsync("reader");
        var taskId = SeedTaskAsync(projectId);

        var r = await Member().PutAsJsonAsync($"/api/tasks/{taskId}", new { title = "перебил участник" });

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync())
            .GetProperty("code").GetString().Should().Be("project_scope");
    }

    // ── одна проекция списка и карточки: личной заметки владельца в видe участника нет ─

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task SourceNoteIdЛичногоVaultа_УчастникуНеУходит_ВладельцуОстаётся(string role)
    {
        var projectId = await ProjectWithMemberAsync(role);
        var taskId = SeedTaskAsync(projectId, NoteId("personal", "Личная/заметка.md"));

        // участник: ни карточка, ни оба списка не несут значения id личной заметки
        var memberCard = await GetCardAsync(Member(), taskId);
        NoValue(memberCard, "sourceNoteId").Should().BeTrue(
            "id личного vault раскрывает участнику путь и название личной заметки владельца");
        foreach (var url in new[] { $"/api/projects/{projectId}/tasks", "/api/tasks" })
        {
            var list = await GetJsonAsync(Member(), url);
            NoValue(list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == taskId), "sourceNoteId")
                .Should().BeTrue($"{url}: список идёт той же проекцией, что карточка");
        }

        // владелец: своя личная заметка остаётся и в карточке, и в списке
        (await GetCardAsync(Owner(), taskId)).TryGetProperty("sourceNoteId", out _).Should().BeTrue();
        var ownerList = await GetJsonAsync(Owner(), $"/api/projects/{projectId}/tasks");
        ownerList.EnumerateArray().Single(t => t.GetProperty("id").GetString() == taskId)
            .TryGetProperty("sourceNoteId", out _).Should().BeTrue();
    }

    /// <summary>Маска бьёт только чужой источник — и в карточке, и в обоих списках (п.3 вердикта):
    /// рядом с задачей этого проекта стоит задача с id заметки личного vault, её id участнику не
    /// уходит. Мутация «маска списка безусловная» (TaskView.ForMemberListItem: обнулять всегда) —
    /// этот тест красный: заметка своего проекта пропала бы из списка, оставшись в карточке.</summary>
    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task SourceNoteIdЗаметкиЭтогоПроекта_УчастникуОстаётся_ЧужогоVaultаНетВСписках(string role)
    {
        var projectId = await ProjectWithMemberAsync(role);
        var noteId = NoteId(projectId, "заметка-проекта.md");
        var taskId = SeedTaskAsync(projectId, noteId);
        // рядом — задача того же проекта с id заметки личного vault: её маска и должна бить
        var foreignId = SeedTaskAsync(projectId, NoteId("personal", "Личная/заметка.md"));

        var card = await GetCardAsync(Member(), taskId);

        card.GetProperty("sourceNoteId").GetString().Should().Be(noteId,
            "заметки этого проекта участник читает и так: маска бьёт только чужой источник");

        // оба списка идут той же проекцией, что карточка: своя заметка видна, чужой vault закрыт
        foreach (var url in new[] { $"/api/projects/{projectId}/tasks", "/api/tasks" })
        {
            var list = await GetJsonAsync(Member(), url);
            list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == taskId)
                .GetProperty("sourceNoteId").GetString().Should().Be(noteId,
                    $"{url}: список — та же проекция, что карточка");
            NoValue(list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == foreignId), "sourceNoteId")
                .Should().BeTrue($"{url}: id личного vault закрыт и в списке");
        }
    }

    [Fact]
    public async Task ПроекцияСпискаИКарточкиУчастника_Совпадает()
    {
        var projectId = await ProjectWithMemberAsync("reader");
        var taskId = SeedTaskAsync(projectId, NoteId("personal", "Личная/заметка.md"));

        var list = (await GetJsonAsync(Member(), $"/api/projects/{projectId}/tasks"))
            .EnumerateArray().Single(t => t.GetProperty("id").GetString() == taskId);
        var card = await GetCardAsync(Member(), taskId);

        var listKeys = list.EnumerateObject().Select(p => p.Name).ToHashSet();
        var cardKeys = card.EnumerateObject().Select(p => p.Name).ToHashSet();
        // карточка — список плюс тяжёлые поля; маркеры облегчённого элемента — только в списке
        cardKeys.Except(listKeys).Should().BeEmpty("карточка не добавляет полей, кроме тяжёлого текста");
        listKeys.Except(cardKeys).Should().BeEquivalentTo(
            new[] { "listItem", "hasDescription", "hasResult", "descriptionPreview" },
            "список отличается только маркерами облегчённого элемента TaskListItem");
        foreach (var name in listKeys.Intersect(cardKeys))
        {
            if (name is "description" or "resultMarkdown") continue; // здесь список маскирует, карточка нет
            list.GetProperty(name).ToString().Should().Be(card.GetProperty(name).ToString(),
                $"поле {name}: список и карточка — одна проекция, значения обязаны совпадать");
        }
        card.GetProperty("description").GetString().Should().Be(DescriptionText);
        card.GetProperty("resultMarkdown").GetString().Should().Be(ResultText);
        list.GetProperty("hasDescription").GetBoolean().Should().BeTrue();
        list.GetProperty("hasResult").GetBoolean().Should().BeTrue();
        // маска в обеих точках одна: личной заметки нет ни в списке, ни в карточке
        NoValue(list, "sourceNoteId").Should().BeTrue();
        NoValue(card, "sourceNoteId").Should().BeTrue();
    }

    // ── конкурентность: отзыв участника против чтения карточки ──────────────────────

    /// <summary>Чтение не меняется, запись не меняется; отзыв и чтение стартуют по одному
    /// сигналу TaskCompletionSource (без Task.Delay). До отзыва — 200 полной карточки, после —
    /// 404; в самой гонке допустимы ровно два согласованных исхода: 200 с полным текстом или 404,
    /// рваного состояния (200 без полей, 500, полутела) нет ни в одном переплетении.</summary>
    [Fact]
    public async Task ГонкаОтзываУчастникаИЧтенияКарточки_ДоОтзыва200_После404_БезРваногоСостояния()
    {
        var projectId = await ProjectWithMemberAsync("reader");
        var taskId = SeedTaskAsync(projectId);
        var memberId = MemberId();
        var cardUrl = $"/api/tasks/{taskId}";

        // до отзыва — 200 полная карточка
        var before = await GetCardAsync(Member(), taskId);
        before.GetProperty("description").GetString().Should().Be(DescriptionText);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readTask = Task.Run(async () => { await start.Task; return await Member().GetAsync(cardUrl); });
        var revokeTask = Task.Run(async () =>
        {
            await start.Task;
            return await Owner().DeleteAsync($"/api/projects/{projectId}/members/{memberId}");
        });
        start.SetResult();
        var read = await readTask;
        var revoke = await revokeTask;
        revoke.EnsureSuccessStatusCode();

        if (read.StatusCode == HttpStatusCode.OK)
        {
            // гонка закончилась до отзыва — только полная карточка, полутела быть не может
            var body = JsonSerializer.Deserialize<JsonElement>(await read.Content.ReadAsStringAsync());
            body.GetProperty("description").GetString().Should().Be(DescriptionText);
            body.GetProperty("resultMarkdown").GetString().Should().Be(ResultText);
        }
        else
        {
            read.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "исходов гонки ровно два: 200 полной карточки или 404; третьего (рваного) состояния нет");
        }

        // после отзыва — 404
        (await Member().GetAsync(cardUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── MCP tasks_get агента участника: та же проекция для актора-участника ─────────

    private async Task<(string Text, bool IsError)> McpCallAsync(HttpClient client, string chatId, string tool, object args)
    {
        var resp = await client.AsCaller(chatId).PostAsJsonAsync($"/mcp/tasks/{chatId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
    }

    private async Task<(string ProjectId, string TaskId, string ChatId)> SharedProjectWithChatAndTaskAsync()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var taskId = SeedTaskAsync(projectId, NoteId("personal", "Личная/заметка.md"));
        var chat = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        return (projectId, taskId, IdOf(await chat.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task АгентУчастника_tasks_get_ПолнаяКарточкаБезSourceNoteIdЛичногоVaultа()
    {
        var (_, taskId, chatId) = await SharedProjectWithChatAndTaskAsync();

        // ход запускает редактор: актор пишется в реестр хода (не из заголовка берём его дальше)
        (await Member().PostAsJsonAsync($"/api/sessions/{chatId}/messages",
            new { text = "вопрос редактора", wait = "none" })).EnsureSuccessStatusCode();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var ownerUserId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        sessions.GetTurnActor(chatId, ownerUserId).Should().Be(MemberId(), "актор идущего хода — редактор");

        // вызов из чата — токеном арендатора (так ход участника и вызывает платформенный MCP)
        var (text, isError) = await McpCallAsync(Owner(), chatId, "tasks_get", new { id = taskId });

        isError.Should().BeFalse(text);
        var json = JsonSerializer.Deserialize<JsonElement>(text);
        json.GetProperty("description").GetString().Should().Be(DescriptionText,
            "tasks_get остаётся полной карточкой — та же проекция, что REST для участника");
        json.GetProperty("resultMarkdown").GetString().Should().Be(ResultText);
        NoValue(json, "sourceNoteId").Should().BeTrue();
    }

    [Fact]
    public async Task БезХодаУчастника_tasks_get_ПраваАрендатора_SourceNoteIdВиден()
    {
        var (_, taskId, chatId) = await SharedProjectWithChatAndTaskAsync();
        // хода нет: актор неизвестен. После О2 (892deb00) безымянному открыто только то, что не
        // требует права: tasks_get — чтение, оно открыто; проекция актора-участника не применяется.

        var (text, isError) = await McpCallAsync(Owner(), chatId, "tasks_get", new { id = taskId });

        isError.Should().BeFalse(text);
        JsonSerializer.Deserialize<JsonElement>(text)
            .GetProperty("sourceNoteId").GetString().Should().Be(NoteId("personal", "Личная/заметка.md"));
    }

    /// <summary>S2c-9 доработка (п.2 вердикта): ответ записи — та же проекция, что tasks_get и REST.
    /// Актор-участник не получает в ответе ни одного из четырёх пишущих инструментов id заметки
    /// личного vault. Запись и гейты не меняются: после всех четырёх вызовов карточка владельца
    /// остаётся с sourceNoteId — проецируется ответ, не хранимая задача.</summary>
    [Fact]
    public async Task АгентУчастника_ОтветыЗаписи_БезSourceNoteIdЛичногоVaultа_ЗаписьНеМеняется()
    {
        var (_, taskId, chatId) = await SharedProjectWithChatAndTaskAsync();
        // ход редактора активен: актор пишется в реестр хода (тот же путь, что у теста tasks_get выше)
        (await Member().PostAsJsonAsync($"/api/sessions/{chatId}/messages",
            new { text = "вопрос редактора", wait = "none" })).EnsureSuccessStatusCode();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var ownerUserId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        sessions.GetTurnActor(chatId, ownerUserId).Should().Be(MemberId(), "актор идущего хода — редактор");

        var calls = new (string Tool, object Args)[]
        {
            ("tasks_add_subtask", new { taskId, title = "подзадача редактора" }),
            ("tasks_toggle_subtask", new { taskId, subtaskTitle = "подзадача редактора", isDone = true }),
            ("tasks_update", new { id = taskId, title = "переписал редактор" }),
            ("tasks_complete", new { id = taskId, resultMarkdown = ResultText }),
        };
        foreach (var (tool, args) in calls)
        {
            var (text, isError) = await McpCallAsync(Owner(), chatId, tool, args);
            isError.Should().BeFalse($"{tool}: {text}");
            var json = JsonSerializer.Deserialize<JsonElement>(text);
            json.GetProperty("id").GetString().Should().Be(taskId, $"{tool}: ответ — карточка задачи");
            NoValue(json, "sourceNoteId").Should().BeTrue(
                $"{tool}: ответ записи идёт той же проекцией, что tasks_get");
        }

        // хранимая карточка не правилась: своя заметка остаётся у владельца и в карточке, и в списке
        (await GetCardAsync(Owner(), taskId)).GetProperty("sourceNoteId").GetString()
            .Should().Be(NoteId("personal", "Личная/заметка.md"), "проецируется ответ, не запись");
    }

    /// <summary>Без хода участника (актор неизвестен — фон) пишущий вызов общего проекта ОТКАЗАН
    /// гейтом актора: после О2 финального ревью «Общих проектов» (892deb00) безымянный ход общего
    /// проекта не имеет ни одного права — открыто только не требующее права (пара:
    /// БезХодаУчастника_tasks_get… — чтение тому же актору открыто, и оно остаётся полным).
    /// Отказ не трогает запись: карточка владельца всё так же полна.</summary>
    [Fact]
    public async Task БезХодаУчастника_ОтветыЗаписи_ОтказГейта_ЗаписьНеМеняется()
    {
        var (_, taskId, chatId) = await SharedProjectWithChatAndTaskAsync();

        var (text, isError) = await McpCallAsync(Owner(), chatId, "tasks_update", new { id = taskId, title = "переписал штаб" });

        isError.Should().BeTrue(text);
        text.Should().Contain(ActorRightGate.DeniedText,
            "безымянный ход общего проекта: права не даются ни одному пишущему инструменту");
        (await GetCardAsync(Owner(), taskId)).GetProperty("sourceNoteId").GetString()
            .Should().Be(NoteId("personal", "Личная/заметка.md"), "отказ был до мутации: запись не тронута");
    }
}
