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

// FS-E2 (§6.5 п.2): report_up(blocker: true) из чата-исполнителя задачи ставит её ожидание
// человека через TaskManager.SetAwaitingHuman (продюсер FS-E1). Причина — первая фраза доклада,
// обрезанная до 120 символов по границе слова. Обычный доклад (blocker=false), чат без задачи
// и несостоявшийся доклад (no_parent) ожидания не ставят; Done-задача — устаревший сигнал,
// пропуск. Оба пути к инварианту — MCP chats_report_up (/mcp/wsp) и REST POST /report —
// идут через SessionMessagingService.ReportUpAsync, по тесту на каждый.
// Мутации:
// • убрать вызов SetAwaitingHuman в TrySetAwaitingHumanForBlocker — краснеют тесты путей
//   (MCP, REST) и «БлокерВещаетTaskChanged» (нет состояния — нечего и вещать);
// • убрать вещание task_changed в TrySetAwaitingHumanForBlocker — краснеет ровно
//   «БлокерВещаетTaskChanged_ПоЗадаче» (хелпер AssertBroadcasts: «не вызвало task_changed»);
// • снять проверку Done в TrySetAwaitingHumanForBlocker — краснеет «БлокерПослеDone»
//   (переход в Done выполняется до доклада — без проверки expectation поставился бы на
//   закрытой задаче);
// • снять условие blocker — краснеет «ОбычныйДоклад»;
// • перенести постановку до доклада — краснеет «NoParent» (несостоявшийся доклад не ставит);
// • правка FirstPhrase (граница, лимит, жёсткая граница, точка внутри URL) — краснеют
//   юнит-тесты FirstPhrase.
[Trait("Category", "Host")]
public class ReportUpAwaitingHumanTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ReportUpAwaitingHumanTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    // Первая фраза этого текста длиннее 120 — на нём проверяется и лимит, и граница слова:
    // хвост «согласия человека» в причину попасть не должен
    private const string LongBlockerText =
        "Работа стоит: нужен доступ к репозиторию задачи и решение о ветке, в которой править "
        + "файлы, потому что правила проекта запрещают мне менять чужие вертикали без явного "
        + "согласия человека. Дальше сам не продолжу.";

    private async Task<string> CreateProjectAsync(string tag)
    {
        var dir = Path.Combine(_factory.TempDir, "reportup_await_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "ReportUpAwait" + tag, rootPath = dir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // tasks_* — http-тулсет tasks, chats_report_up — http-тулсет wsp: разные серверы, один приём вызова
    private async Task<(string Text, bool IsError)> CallMcpAsync(string server, string sessionId, string tool, object args)
    {
        var resp = await _client.AsCaller(sessionId).PostAsync($"/mcp/{server}/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "MCP отвечает 200 даже на отказ — isError в теле");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var text = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        return (text, isError);
    }

    // Задача из чата-штаба (SourceSessionId = штаб — он и родитель по SessionTaskLinks),
    // чат-исполнитель с этой задачей, привязка LinkedSessionId — тот же MarkClaudeStarted,
    // который ставит сервер при старте исполнителя
    private async Task<(string TaskId, string ExecChat)> ExecutorScenarioAsync(string tag)
    {
        var projectId = await CreateProjectAsync(tag);
        var staffChat = await CreateSessionAsync(projectId);
        var (text, isError) = await CallMcpAsync("tasks", staffChat, "tasks_create", new { title = "FS-E2 " + tag });
        isError.Should().BeFalse(text);
        var taskId = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;
        var exec = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec-" + tag, taskId: taskId);
        Tasks.MarkClaudeStarted(taskId, exec.Id, DateTime.UtcNow);
        return (taskId, exec.Id);
    }

    private async Task<HttpResponseMessage> ReportRestAsync(string sessionId, string text, bool blocker) =>
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/report", new { text, blocker });

    // --- путь MCP: chats_report_up http-тулсета wsp ---

    [Fact]
    public async Task БлокерЧерезMcp_СтавитОжиданиеЗадаче_ПоследнийПобеждает()
    {
        var (taskId, exec) = await ExecutorScenarioAsync("mcp");
        var t0 = DateTime.UtcNow;

        var (text1, err1) = await CallMcpAsync("wsp", exec, "chats_report_up",
            new { text = "Застрял на доступе к репозиторию. Работа дальше не идёт.", blocker = true });
        err1.Should().BeFalse(text1);
        text1.Should().Contain("\"status\":\"delivered\"");

        var first = Tasks.GetById(taskId)!;
        first.AwaitingHumanReason.Should().Be("Застрял на доступе к репозиторию",
            "причина — первая фраза доклада, без конца предложения");
        first.AwaitingHumanSince.Should().NotBeNull();
        first.AwaitingHumanSince!.Value.Should().BeOnOrAfter(t0, "Since штампует сервер (FS-E1)");

        // Второй блокер: побеждает последний, Since — от последнего; его первая фраза длиннее
        // лимита → причина обрезана по границе слова и не длиннее 120
        var t1 = DateTime.UtcNow;
        var (text2, err2) = await CallMcpAsync("wsp", exec, "chats_report_up", new { text = LongBlockerText, blocker = true });
        err2.Should().BeFalse(text2);

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanReason.Should().NotBeNull();
        stored.AwaitingHumanReason!.Should().StartWith("Работа стоит: нужен доступ",
            "последний вызов побеждает: причина — фраза второго доклада");
        stored.AwaitingHumanReason!.Length.Should().BeLessThanOrEqualTo(120);
        stored.AwaitingHumanReason.Should().NotContain("согласия человека", "хвост фразы за границей отрезан");
        stored.AwaitingHumanSince!.Value.Should().BeOnOrAfter(t1);
    }

    // --- путь REST: POST /api/sessions/{sid}/report (SessionMessagesController) ---

    [Fact]
    public async Task БлокерЧерезRest_СтавитОжиданиеЗадаче_Лимит120()
    {
        var (taskId, exec) = await ExecutorScenarioAsync("rest");
        var t0 = DateTime.UtcNow;

        var resp = await ReportRestAsync(exec, LongBlockerText, blocker: true);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanReason.Should().NotBeNull();
        stored.AwaitingHumanReason!.Should().StartWith("Работа стоит: нужен доступ");
        stored.AwaitingHumanReason!.Length.Should().BeLessThanOrEqualTo(120);
        stored.AwaitingHumanReason.Should().NotContain("Дальше сам", "вторая фраза в причину не берётся");
        stored.AwaitingHumanSince.Should().NotBeNull();
        stored.AwaitingHumanSince!.Value.Should().BeOnOrAfter(t0);
    }

    // --- вещание (FS-E2 ревью, Major 1) ---

    // Стор фронта живёт только по task_changed (lib/tasks.ts, lib/featurePage.ts): молча поставленное
    // ожидание оставляло доску устаревшей — тот же класс дефекта, что линия паузы W4. Сервис собран
    // с тестовым вещателем (тот же приём, что TaskChangedBroadcastContractTests): DI-вещатель здесь
    // не видим. Мутация «убрать вещание» краснит ровно этот тест.
    [Fact]
    public async Task БлокерВещаетTaskChanged_ПоЗадаче()
    {
        var (taskId, exec) = await ExecutorScenarioAsync("broadcast");
        var bc = new TestSessionBroadcaster();
        var messaging = new SessionMessagingService(Sessions,
            _factory.Services.GetRequiredService<ProjectManager>(), Tasks, null, bc);

        var caught = await bc.AssertBroadcasts("task_changed", taskId, OwnerId,
            async () => (await messaging.ReportUpAsync(OwnerId, exec,
                    "Застрял на доступе к репозиторию. Работа дальше не идёт.", blocker: true))
                .Should().BeOfType<SessionMessagingService.ReportOutcome.Delivered>());

        var msg = caught.Should().ContainSingle().Subject
            .Should().BeAssignableTo<Protocol.TaskChangedMessage>().Subject;
        msg.Action.Should().Be("updated");
        msg.Task.Id.Should().Be(taskId);
        msg.Task.AwaitingHumanReason.Should().Be("Застрял на доступе к репозиторию",
            "событие несёт уже поставленное состояние, а не состояние до ручки");
    }

    // --- что ожидания НЕ ставит ---

    [Fact]
    public async Task ОбычныйДокладИзЧатаИсполнителя_ОжиданияНеСтавит()
    {
        var (taskId, exec) = await ExecutorScenarioAsync("plain");

        var resp = await ReportRestAsync(exec, "Готово наполовину. Продолжаю.", blocker: false);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanSince.Should().BeNull("промежуточная сводка по ходу работы — не «жду тебя»");
        stored.AwaitingHumanReason.Should().BeNull();
    }

    // Задача есть, но не привязана к чату (нет LinkedSessionId) — поиск ровно по GetBySession:
    // чужая задача штаба ожидания не получает, доклад уходит как раньше
    [Fact]
    public async Task ЧатБезЗадачи_ОжиданияНет_ДокладКакРаньше()
    {
        var projectId = await CreateProjectAsync("nolink");
        var staffChat = await CreateSessionAsync(projectId);
        var (text, isError) = await CallMcpAsync("tasks", staffChat, "tasks_create", new { title = "FS-E2 nolink" });
        isError.Should().BeFalse(text);
        var taskId = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;
        var child = await CreateSessionAsync(projectId);
        Sessions.SetParent(child, staffChat, OwnerId).Should().NotBeNull();

        var resp = await ReportRestAsync(child, "Застрял на доступе к репозиторию.", blocker: true);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().BeNull(
            "задача ищется по LinkedSessionId чата, а не по source: чужая не получает");
    }

    // Порядок прежний: сначала доклад, ожидание — только после состоявшегося доклада.
    // NoParent (задача без чата-создателя → у чата-исполнителя нет родителя) — 400, ожидания нет.
    [Fact]
    public async Task НесостоявшийсяДокладNoParent_ОжиданияНет()
    {
        var created = await _client.PostAsJsonAsync("/api/tasks", new { title = "FS-E2 no-parent" });
        created.EnsureSuccessStatusCode();
        var taskId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        // Источник — null (REST без сессии-создателя), значит и родителя у чата нет
        var exec = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec-no-parent", taskId: taskId);
        Tasks.MarkClaudeStarted(taskId, exec.Id, DateTime.UtcNow);

        var resp = await ReportRestAsync(exec.Id, "Застрял на доступе к репозиторию.", blocker: true);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("no_parent");

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanSince.Should().BeNull("ожидание ставится только после доставленного доклада");
        stored.AwaitingHumanReason.Should().BeNull();
    }

    // --- устаревший сигнал ---

    // Блокер после закрытия: переход в Done выполняется ДО доклада (находка ревью: прежняя
    // «параллельная» версия была порядково-зависимой — её итог зависел от скорости прямого
    // Update против HTTP-пути доклада). Финал тот же: закрытая задача не «ждёт вас» — пропуск по
    // статусу в TrySetAwaitingHumanForBlocker (переход в Done сам снимает ожидание:
    // TaskManager.Update вызывает ClearAwaitingHuman). Мутация «снять проверку Done» краснит
    // этот тест: без проверки ожидание поставилось бы на закрытой задаче.
    [Fact]
    public async Task БлокерПослеDone_ОжиданияНет()
    {
        var (taskId, exec) = await ExecutorScenarioAsync("race");

        var closed = Tasks.Update(taskId, new UpdateTaskRequest { Status = TaskItemStatus.Done });
        closed.Should().NotBeNull("переход в Done выполнен");

        var reported = await ReportRestAsync(exec, "Застрял на доступе к репозиторию.", blocker: true);
        reported.StatusCode.Should().Be(HttpStatusCode.OK, "закрытие не роняет сам доклад");
        var stored = Tasks.GetById(taskId)!;
        stored.Status.Should().Be(TaskItemStatus.Done);
        stored.AwaitingHumanSince.Should().BeNull("блокер после закрытия — устаревший сигнал");
        stored.AwaitingHumanReason.Should().BeNull();
    }

    // --- FirstPhrase: граница фразы и лимит (юнит, без поднятия приложения) ---

    [Fact]
    public void FirstPhrase_ОтрезаетПоКонцуПредложенияИлиСтроки_ДвоеточиеНеГраница()
    {
        SessionMessagingService.FirstPhrase("Первая фраза. Вторая фраза. Третья")
            .Should().Be("Первая фраза");
        SessionMessagingService.FirstPhrase("Двухстрочный текст\nвторая строка")
            .Should().Be("Двухстрочный текст");
        SessionMessagingService.FirstPhrase("Вопрос? Нет. Точка!")
            .Should().Be("Вопрос");
        // двоеточие — не конец предложения: «Застрял: …» остаётся одной фразой (формулировка
        // блокера из task-executor-rules пишется именно так)
        SessionMessagingService.FirstPhrase("Застрял: нет доступа к БД. Помоги")
            .Should().Be("Застрял: нет доступа к БД");
    }

    [Fact]
    public void FirstPhrase_ПустыеОтрезкиПропускает()
    {
        // текст начинается с разделителей — берётся следующий непустой отрезок
        SessionMessagingService.FirstPhrase(". ! ? начало").Should().Be("начало");
        SessionMessagingService.FirstPhrase("\n\nЗастрял. Дальше сам").Should().Be("Застрял");
        // только разделители — несущего ничего нет, берётся весь текст (причина не пустеет)
        SessionMessagingService.FirstPhrase("...").Should().Be("...");
    }

    [Fact]
    public void FirstPhrase_ОбрезаетПоГраницеСлова_БезМноготочия()
    {
        var phrase = string.Join(' ', Enumerable.Repeat("слово", 40)); // 239 символов
        var result = SessionMessagingService.FirstPhrase(phrase + ".");
        result.Length.Should().BeLessThanOrEqualTo(120);
        // 20 целых слов по 5 + 19 пробелов = 119: слово не разрезано, хвост-пробел снят
        result.Should().Be(string.Join(' ', Enumerable.Repeat("слово", 20)));
    }

    // FS-E2 ревью (минор): точка внутри URL/домена — не конец предложения, иначе причина
    // блокера, ссылающегося на документ, резалась по первой точке ссылки.
    [Fact]
    public void FirstPhrase_ТочкаВнутриUrl_НеГраница()
    {
        SessionMessagingService.FirstPhrase("Детали в docs/adr/012.md. Помоги")
            .Should().Be("Детали в docs/adr/012.md");
        SessionMessagingService.FirstPhrase("Документ: https://example.com/page. Дальше")
            .Should().Be("Документ: https://example.com/page");
        // точка с пробелом после неё — граница, точки внутри ссылки — нет
        SessionMessagingService.FirstPhrase("См https://example.com/page. Дальше")
            .Should().Be("См https://example.com/page");
    }

    [Fact]
    public void FirstPhrase_ЖёсткаяГраницаКогдаПервоеСловоДлиннееЛимита()
    {
        // пробела нет вовсе — жёсткий срез ровно по лимиту (SetAwaitingHuman reject'ит длиннее)
        SessionMessagingService.FirstPhrase(new string('ы', 200)).Should().Be(new string('ы', 120));
        // ровно лимит — без изменения
        SessionMessagingService.FirstPhrase(new string('а', 120)).Should().Be(new string('а', 120));
    }
}
