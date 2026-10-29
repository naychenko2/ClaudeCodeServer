using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// B4 (волна 6 «Обзор фич», разрез п.2): параметр featureNote у tasks_update и права по единому
// правилу членства FeatureChatMembership. Голос фичи пишут оркестратор (curator/orchestrator)
// или человек; исполнитель шага — нет; чат без членства — нет (fail-closed). Не-эпик — отказ
// тем же текстом, что REST PUT note. Мутации:
// • убрать гейт membership в case "tasks_update" — тесты «СессияИсполнителя»/«БезЧленства» краснеют;
// • убрать вызов SetFeatureNote — тесты «Curator»/«Оркестратор» краснеют;
// • убрать проверку role=feature — тест «НеЭпик» краснеет;
// • заменить белый список CanWriteFeatureVoice на Kind != "executor" — строка «key» в тесте
//   «БелыйСписок» краснеет (R-B4 минор 1);
// • откатить гейт к voiceLink.Kind == "executor" (предикат не вызывается) — тест
//   «ГейтЗовётПредикат» краснеет: «key»-связь через шов была бы пущена (R-DB B4);
// • вернуть чтение Now.FirstOrDefault()?.Stage в штампе TasksToolset — тест
//   «БезИдущихЗадачей» краснеет (B14 возврат FIN: штамп — FeatureBrief.ActiveStageOf,
//   та же база, что у StaleOf; R-B4 минор 2 закрыт).
[Trait("Category", "Integration")]
public class TasksToolsetFeatureNoteTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksToolsetFeatureNoteTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private FeatureProjector Features => _factory.Services.GetRequiredService<FeatureProjector>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync(string tag)
    {
        var dir = Path.Combine(_factory.TempDir, "mcp_note_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "McpNote" + tag, rootPath = dir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var sessionId = await CreateSessionInProjectAsync(projectId);
        return (projectId, sessionId);
    }

    private async Task<string> CreateSessionInProjectAsync(string projectId)
    {
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Вызов MCP-инструмента через /mcp/tasks/{sessionId}: {content[0].text, isError} —
    // text несёт JSON задачи при успехе или текст Deny при отказе (паттерн TasksToolsetDefectTests).
    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "MCP отвечает 200 даже на отказ — isError=true в теле, а не HTTP-код");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var text = result.GetProperty("content").EnumerateArray()
            .First().GetProperty("text").GetString()!;
        return (text, isError);
    }

    // Эпик + шаг, заведённые из чата-штаба: штаб — curator фичи (SourceSessionId эпика).
    private async Task<(string CuratorSessionId, string EpicId, string StepId)> CreateFeatureFromCuratorAsync(
        string projectId, string tag)
    {
        var curatorSessionId = await CreateSessionInProjectAsync(projectId);
        var (createText, createErr) = await CallToolAsync(curatorSessionId, "tasks_create",
            new { title = "Фича " + tag, role = "feature" });
        createErr.Should().BeFalse();
        var epicId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;
        var (stepText, stepErr) = await CallToolAsync(curatorSessionId, "tasks_create",
            new { title = "Шаг " + tag, role = "step", featureId = epicId });
        stepErr.Should().BeFalse();
        var stepId = JsonSerializer.Deserialize<JsonElement>(stepText).GetProperty("id").GetString()!;
        return (curatorSessionId, epicId, stepId);
    }

    [Fact]
    public void ToolsList_TasksUpdate_СодержитFeatureNote()
    {
        // Параметр статичен: в tools/list всегда, в проектном и личном составе — иначе
        // сигнатура запуска мерцала бы между ходами (инвариант McpToolsetStabilityTests).
        foreach (var (label, tools) in new[] { ("project", TasksToolset.ProjectChatTools), ("personal", TasksToolset.PersonalTools) })
        {
            var props = tools.Single(t => t.Name == "tasks_update").InputSchema["properties"] is JsonObject p
                ? p : throw new InvalidOperationException($"{label}: у tasks_update нет properties");
            props.ContainsKey("featureNote").Should().BeTrue($"{label}: featureNote — аддитивный параметр tasks_update");
        }
    }

    // c2: curator (чат, создавший эпик) пишет голос; автор в сторе — чат-вызыватель, не человек;
    // At/StateAtWrite штампованы сервером; bySessionId наружу не отдаётся (FeatureNoteJsonConverter).
    [Fact]
    public async Task FeatureNote_Curator_ПишетГолос()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("curator");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "curator");

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = epicId, featureNote = "фича идёт по плану, ждём ревью" });
        isError.Should().BeFalse();

        var note = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("note");
        note.GetProperty("text").GetString().Should().Be("фича идёт по плану, ждём ревью");
        note.TryGetProperty("bySessionId", out _).Should().BeFalse(
            "чат автора наружу не отдаётся — тот же конвертер, что у REST/SignalR");

        var stored = Tasks.GetById(epicId)!;
        stored.Note!.Text.Should().Be("фича идёт по плану, ждём ревью");
        stored.Note.BySessionId.Should().Be(curatorId);
        stored.Note.ByUser.Should().BeFalse("MCP-путь — агентский, не человеческий");
        stored.Note.At.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1),
            "At штампует сервер, агент его не передаёт");
        // Штампы — из той же проекции, что читает фронт (разрез п.1: «из FeatureRun»).
        // B15: StateAtWrite — КАТЕГОРИЯ фичи (brief.kind), а не сырое состояние проекции.
        stored.Note.StateAtWrite.Should().Be(FeatureBrief.KindAtWriteOf(Features.Project(epicId, OwnerId)!));
        // B14 (возврат FIN): шаг в todo — идущих задач нет (Now пуст), но активная стадия конвейера
        // есть («prod», первая незаполненная стадия с задачами). Штамп — активная стадия, а не Now[0]:
        // тот же «prod», что в тесте «АктивнаяСтадия» (там шаг идёт).
        stored.Note.StageAtWrite.Should().Be("prod");
    }

    // Пустой текст снимает голос (та же семантика, что у REST PUT note).
    [Fact]
    public async Task FeatureNote_ПустойТекст_СнимаетГолос()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("remove");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "remove");
        (await CallToolAsync(curatorId, "tasks_update", new { id = epicId, featureNote = "голос" }))
            .IsError.Should().BeFalse();

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update", new { id = epicId, featureNote = "" });
        isError.Should().BeFalse();
        JsonSerializer.Deserialize<JsonElement>(text).GetProperty("note")
            .ValueKind.Should().Be(JsonValueKind.Null, "пустой текст снял голос");
        Tasks.GetById(epicId)!.Note.Should().BeNull();
    }

    // c2: сессия-исполнитель (chat.TaskId → шаг фичи) голос не пишет; обычный update без
    // голоса при этом остаётся разрешённым — гейт не разрастается на другие поля.
    [Fact]
    public async Task FeatureNote_СессияИсполнителя_Отказ()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("executor");
        var (_, epicId, stepId) = await CreateFeatureFromCuratorAsync(projectId, "executor");
        var execChat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec",
            taskId: stepId);

        var (text, isError) = await CallToolAsync(execChat.Id, "tasks_update",
            new { id = epicId, featureNote = "голос из чата исполнителя" });
        isError.Should().BeTrue("executor — не право на голос, fail-closed");
        text.Should().Contain("Голос фичи пишет оркестратор или человек");
        Tasks.GetById(epicId)!.Note.Should().BeNull("отказ ничего не записал");

        var (_, plainErr) = await CallToolAsync(execChat.Id, "tasks_update",
            new { id = stepId, title = "правка шага из чата исполнителя" });
        plainErr.Should().BeFalse("обычный update без featureNote не затронут");
    }

    // c2: orchestrator (чат, запустивший исполнителя задачи фичи) пишет голос.
    [Fact]
    public async Task FeatureNote_Оркестратор_ПишетГолос()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("orch");
        var (_, epicId, stepId) = await CreateFeatureFromCuratorAsync(projectId, "orch");
        var orchestratorId = await CreateSessionInProjectAsync(projectId);
        // Знак запуска исполнителя — как его ставит tasks_run_executor: на задаче фичи.
        Tasks.Update(stepId, new UpdateTaskRequest(ExecutorLaunchedBySessionId: orchestratorId))
            .Should().NotBeNull();

        var (_, isError) = await CallToolAsync(orchestratorId, "tasks_update",
            new { id = epicId, featureNote = "голос оркестратора" });
        isError.Should().BeFalse();
        Tasks.GetById(epicId)!.Note!.Text.Should().Be("голос оркестратора");
        Tasks.GetById(epicId)!.Note!.BySessionId.Should().Be(orchestratorId);
    }

    // c2: чат без членства в фиче (тот же проект, но ни executor, ни curator, ни orchestrator)
    // — отказ тем же текстом: право только через FeatureChatMembership.
    [Fact]
    public async Task FeatureNote_БезЧленства_Отказ()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("nomember");
        var epic = Tasks.Create(projectId, OwnerId, new CreateTaskRequest("Фича без члена", Role: "feature"));

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = epic.Id, featureNote = "чужой чат" });
        isError.Should().BeTrue();
        text.Should().Contain("Голос фичи пишет оркестратор или человек");
        Tasks.GetById(epic.Id)!.Note.Should().BeNull();
    }

    // c2: параметр у задачи не-эпика → отказ (400-семантика: текст о запросе, как у REST
    // «Голос пишется только у фичи (role=feature)»), даже если вызыватель — curator фичи.
    [Fact]
    public async Task FeatureNote_НеЭпик_Отказ()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("nonepic");
        var (curatorId, _, stepId) = await CreateFeatureFromCuratorAsync(projectId, "nonepic");

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = stepId, featureNote = "голос у шага" });
        isError.Should().BeTrue();
        text.Should().Contain("Голос пишется только у фичи (role=feature)");
    }

    // c2: чужой владелец — «не найдена» (первый гейт case не меняется).
    [Fact]
    public async Task FeatureNote_ЧужойВладелец_НеНайдена()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("foreign");
        var foreign = Tasks.Create(projectId, "other-owner-" + Guid.NewGuid().ToString("N"),
            new CreateTaskRequest("Чужая фича", Role: "feature"));

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = foreign.Id, featureNote = "чужой голос" });
        isError.Should().BeTrue();
        text.Should().Contain("не найдена");
        Tasks.GetById(foreign.Id)!.Note.Should().BeNull();
    }

    // c1 (R-B4 минор 1): право голоса — белый список видов {curator, orchestrator}, а не
    // «не executor». Kind="key" BuildDetailed ещё не производит (добавит Б2, цепочка ключевого
    // чата) — тест моделирует связь: новый вид права не получает. Мутация: заменить тело
    // CanWriteFeatureVoice на link.Kind != "executor" — строка «key» краснеет.
    [Fact]
    public void FeatureNote_Право_БелыйСписок_НовыйKindНеДаётПраво()
    {
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("curator", 1, null, null, null))
            .Should().BeTrue();
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("orchestrator", 2, null, null, null))
            .Should().BeTrue();
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("executor", 3, null, null, null))
            .Should().BeFalse();
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("key", 0, null, null, null))
            .Should().BeFalse("ключевой чат (Б2) права не получает: только явный вход в список");
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("future-kind", 3, null, null, null))
            .Should().BeFalse();
    }

    // c1 (R-DB B4, минор 1): решение принимает ГЕЙТ, а не изолированный предикат — тест идёт через
    // боевой путь tasks_update. Связь (session, epic) подменена тестовым шовом TestVoiceLinkOverride
    // (одна связь, не вся карта членства — сузён R-B11B12 минор 2) на фикстуру со связью вида вне
    // белого списка и не «executor» («key», вид Б2): прежний гейт («не executor») пустил бы её,
    // текущий — отказывает через CanWriteFeatureVoice. Мутация:
    // откатить гейт к voiceLink.Kind == "executor" (предикат не вызывается) — «key» пущена, тест краснеет.
    [Fact]
    public async Task FeatureNote_Право_ГейтЗовётПредикат_НовыйKindОтказ()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("keykind");
        var epic = Tasks.Create(projectId, OwnerId, new CreateTaskRequest("Фича под ключом", Role: "feature"));

        var toolset = _factory.Services.GetServices<IMcpToolset>().OfType<TasksToolset>().Single();
        toolset.TestVoiceLinkOverride = (sid, fid) =>
            sid == sessionId && fid == epic.Id
                ? new FeatureChatMembership.Link("key", 0, null, null, null)
                : null;
        try
        {
            var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
                new { id = epic.Id, featureNote = "ключевой чат пишет голос" });
            isError.Should().BeTrue("решает гейт: «key» вне белого списка — предикат отказывает");
            text.Should().Contain("Голос фичи пишет оркестратор или человек");
            Tasks.GetById(epic.Id)!.Note.Should().BeNull("отказ ничего не записал");
        }
        finally
        {
            toolset.TestVoiceLinkOverride = null;
        }
    }

    // c2 (R-B4 минор 2): у фичи ЕСТЬ активная стадия (idущий шаг) — StageAtWrite в MCP-пути
    // равен ей. Мутация: заменить чтение Now.FirstOrDefault()?.Stage в штампе TasksToolset на
    // "" — последний Should краснеет.
    [Fact]
    public async Task FeatureNote_АктивнаяСтадия_ШтампуетсяСтадия()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("stage");
        var (curatorId, epicId, stepId) = await CreateFeatureFromCuratorAsync(projectId, "stage");
        // Шаг идёт: Now непустой, активная стадия есть.
        Tasks.Update(stepId, new UpdateTaskRequest(Status: TaskItemStatus.InProgress))
            .Should().NotBeNull();

        var (_, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = epicId, featureNote = "голос на активной стадии" });
        isError.Should().BeFalse();

        var run = Features.Project(epicId, OwnerId)!;
        run.Now.Should().NotBeEmpty("фикстура: у фичи идёт задача — активная стадия есть");
        run.Now[0].Stage.Should().Be("prod", "стадия шага (role=step) по таблице RoleToStage");

        var stored = Tasks.GetById(epicId)!;
        stored.Note!.StageAtWrite.Should().Be("prod",
            "StageAtWrite в MCP-пути — активная стадия на момент записи (FeatureBrief.StaleOf сверяет с ней)");
    }

    // B14 (возврат FIN 74722f13): голос, записанный у фичи БЕЗ идущих задач (шаг в todo — Now пуст),
    // не устаревший с момента записи: штамп и StaleOf берут одну базу — активную стадию конвейера
    // («prod»), а не Now[0]. До правки штамп был "" (Now пуст), StaleOf сверял с "prod" →
    // brief.note.stale = true с первой секунды. Мутация: вернуть в штампе TasksToolset чтение
    // Now.FirstOrDefault()?.Stage — StageAtWrite станет "", последний Should краснеет.
    [Fact]
    public async Task FeatureNote_БезИдущихЗадачей_ГолосНеУстаревший()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("idle");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "idle");

        var (_, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = epicId, featureNote = "голос на ожидании" });
        isError.Should().BeFalse();

        var run = Features.Project(epicId, OwnerId)!;
        run.Now.Should().BeEmpty("фикстура: идущих задач нет (шаг todo)");

        var brief = FeatureBrief.Build(run, null, null, DateTime.UtcNow);
        brief.Note!.StageAtWrite.Should().Be("prod",
            "штамп — активная стадия конвейера, не Now[0] (здесь пустой)");
        brief.Note.Stale.Should().BeFalse(
            "голос записан только что на активной стадии того же состояния — ни стадия, ни категория не менялись");
    }

    // B15 (дефект 415c1c80): голос, записанный агентом у фичи В ОЧЕРЕДИ, не «устаревший» сразу.
    // Штамп голоса — КАТЕГОРИЯ (brief.kind): у фичи в очереди состояние «paused», категория —
    // «queued». До правки штамп был run.State («paused») ⇒ stale с первой секунды.
    // Мутация: вернуть в штампе TasksToolset run.State — штамп станет «paused», тест красный.
    [Fact]
    public async Task FeatureNote_FichaVOcheredi_GolosNeUstarevshiSrazu()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("queue");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "queue");
        Tasks.SetHold(epicId, FeatureHoldReasons.Queue);

        var (_, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = epicId, featureNote = "в очереди" });
        isError.Should().BeFalse();

        var stored = Tasks.GetById(epicId)!;
        stored.Note!.StateAtWrite.Should().Be("queued",
            "штамп голоса — категория фичи (brief.kind), а не состояние «paused»");

        var run = Features.Project(epicId, OwnerId)!;
        var brief = FeatureBrief.Build(run, null, null, DateTime.UtcNow);
        brief.Kind.Should().Be("queued", "фикстура: удержание с причиной queue");
        brief.Note!.Stale.Should().BeFalse("голос записан только что в очереди — категория не менялась");
    }

    // ─── оценка оркестратора (featureEstimate): пишется вместе с голосом ───────────────────────

    [Fact]
    public void ToolsList_FeatureEstimate_СтатичныйПараметрСОписанием()
    {
        foreach (var (label, tools) in new[] { ("project", TasksToolset.ProjectChatTools), ("personal", TasksToolset.PersonalTools) })
        {
            var props = (JsonObject)tools.Single(t => t.Name == "tasks_update").InputSchema["properties"]!;
            props.ContainsKey("featureEstimate").Should().BeTrue($"{label}: featureEstimate в tools/list всегда");
            var est = (JsonObject)props["featureEstimate"]!;
            ((JsonObject)est["properties"]!).Select(p => p.Key).Should()
                .BeEquivalentTo("done", "inProgress", "total", "percent", "remaining", "etaMinutes", "why");
            est["description"]!.GetValue<string>().Should().Contain("в каждом ходе").And.Contain("why");
            props["featureNote"]!["description"]!.GetValue<string>().Should().Contain("в каждом ходе");
        }
    }

    [Fact]
    public async Task FeatureEstimate_Curator_ПишетсяВместеСГолосомИИнициальныйСрокОдинРаз()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("est");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "est");

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update", new
        {
            id = epicId, featureNote = "идём",
            featureEstimate = new { done = 1, total = 6, inProgress = 2, remaining = new[] { " ревью ", "финал" }, etaMinutes = 150, why = " фронт " },
        });
        isError.Should().BeFalse(text);

        var est = Tasks.GetById(epicId)!.Note!.Estimate!;
        (est.Done, est.Total, est.InProgress, est.Percent, est.EtaMinutes, est.Why).Should().Be((1, 6, 2, null, 150, "фронт"));
        est.Remaining.Should().Equal("ревью", "финал");
        JsonSerializer.Deserialize<JsonElement>(text).GetProperty("note").GetProperty("estimate")
            .GetProperty("total").GetInt32().Should().Be(6);

        // Новый вызов без оценки заменяет голос целиком: оценка не «залипает».
        (await CallToolAsync(curatorId, "tasks_update", new { id = epicId, featureNote = "без оценки" }))
            .IsError.Should().BeFalse();
        Tasks.GetById(epicId)!.Note!.Estimate.Should().BeNull();
    }

    [Fact]
    public async Task FeatureEstimate_БезГолоса_Отказ()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("estnonote");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "estnonote");
        var est = new { done = 1, total = 6, inProgress = 0 };

        var (noNote, err1) = await CallToolAsync(curatorId, "tasks_update", new { id = epicId, featureEstimate = est });
        err1.Should().BeTrue();
        noNote.Should().Contain("вместе с голосом");
        var (emptyNote, err2) = await CallToolAsync(curatorId, "tasks_update", new { id = epicId, featureNote = "", featureEstimate = est });
        err2.Should().BeTrue();
        emptyNote.Should().Contain("вместе с голосом");
        Tasks.GetById(epicId)!.Note.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0, 0, null, null, "total")]      // total < 1
    [InlineData(0, 51, 0, null, null, "total")]     // total > 50
    [InlineData(4, 3, 0, null, null, "done")]       // done > total
    [InlineData(-1, 3, 0, null, null, "done")]      // done < 0
    [InlineData(1, 3, 3, null, null, "inProgress")] // inProgress > total − done
    [InlineData(1, 3, 0, 101, null, "percent")]
    [InlineData(1, 3, 0, -1, null, "percent")]
    [InlineData(1, 3, 0, null, 0, "etaMinutes")]    // etaMinutes < 1
    public async Task FeatureEstimate_НарушениеОграничений_Отказ(
        int done, int total, int inProgress, int? percent, int? eta, string field)
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("estbad" + field);
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "estbad" + field);

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update", new
        {
            id = epicId, featureNote = "г",
            featureEstimate = new { done, total, inProgress, percent, etaMinutes = eta },
        });

        isError.Should().BeTrue();
        text.Should().Contain("featureEstimate." + field);
        Tasks.GetById(epicId)!.Note.Should().BeNull("отказ — голос тоже не записан");
    }

    [Fact]
    public async Task FeatureEstimate_ДлинныеRemainingИWhy_Отказ()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("estlong");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "estlong");

        var (t1, e1) = await CallToolAsync(curatorId, "tasks_update", new
        {
            id = epicId, featureNote = "г",
            featureEstimate = new { done = 0, total = 3, inProgress = 0, remaining = new[] { new string('я', 41) } },
        });
        e1.Should().BeTrue(); t1.Should().Contain("remaining");
        var (t2, e2) = await CallToolAsync(curatorId, "tasks_update", new
        {
            id = epicId, featureNote = "г",
            featureEstimate = new { done = 0, total = 3, inProgress = 0, remaining = Enumerable.Repeat("п", 9).ToArray() },
        });
        e2.Should().BeTrue(); t2.Should().Contain("remaining");
        var (t3, e3) = await CallToolAsync(curatorId, "tasks_update", new
        {
            id = epicId, featureNote = "г",
            featureEstimate = new { done = 0, total = 3, inProgress = 0, why = new string('я', 161) },
        });
        e3.Should().BeTrue(); t3.Should().Contain("why");
        // Ровно на границе — принимается.
        var (_, ok) = await CallToolAsync(curatorId, "tasks_update", new
        {
            id = epicId, featureNote = "г",
            featureEstimate = new
            {
                done = 3, total = 3, inProgress = 0, why = new string('я', 160),
                remaining = Enumerable.Repeat(new string('п', 40), 8).ToArray(),
            },
        });
        ok.Should().BeFalse();
    }

    // Права те же, что у голоса: исполнитель шага оценку не пишет.
    [Fact]
    public async Task FeatureEstimate_СессияИсполнителя_Отказ()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("estexec");
        var (_, epicId, stepId) = await CreateFeatureFromCuratorAsync(projectId, "estexec");
        var execChat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec", taskId: stepId);

        var (text, isError) = await CallToolAsync(execChat.Id, "tasks_update", new
        {
            id = epicId, featureNote = "г", featureEstimate = new { done = 0, total = 3, inProgress = 0 },
        });

        isError.Should().BeTrue();
        text.Should().Contain("Голос фичи пишет оркестратор или человек");
        Tasks.GetById(epicId)!.Note.Should().BeNull();
    }
}
