using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

// Ф-2.48 (разбор Ф-2.47): ожидание человека гасится действием АДРЕСАТА блокера.
// Ожидание (TaskItem.AwaitingHumanSince/Reason) — единственная точка правды «жду вас», и
// блокер адресован тому, кому ушёл доклад: SessionTaskLinks.ParentSessionId(чата-исполнителя).
// До правки ожидание снимали только ход человека в чате задачи (AwaitingHumanAnswerSubscriber),
// старт исполнителя, переход в Done и явный клир из MCP tasks_update; снятие блокера по факту
// жило лишь в режиме штаба. Живой случай: координатор ответил исполнителю сообщением в его
// чат, а плашка «ждёт вас» висела ещё 1 ч 54 мин.
//
// Точка правки — SessionMessagingService.SendAsync: общая для MCP chats_send, REST
// POST /sessions/{id}/messages и http-тулсета wsp. Положительный путь проверяется сквозным
// вызовом через MCP (тот же вход, что в бою), остальные оси — прявым вызовом сервиса: там,
// где важна сама граница правила, а не обвязка тулсета.
//
// Мутации (каждая краснит ровно названное):
// • убрать вызов TryClearAwaitingForAddresseeAsync в SendAsync — краснеет
//   «АдресатСнимаетОжидание…» (ожидание стоит) и «АдресатВещаетTaskChanged…» (нет события);
// • заменить ClearAwaitingHuman(id, at) на ClearAwaitingHuman(id) (без сторожа) — краснеет
//   «Сторож…» (свежее ожидание снято чужим действием);
// • снять проверку ParentSessionId (снимать от любого caller) — краснеет «НеАдресат…»;
// • снять отсечку по пустому callerSessionId — краснеет «АвтоСообщениеСервера…»;
// • убрать вещание task_changed — краснеет «АдресатВещаетTaskChanged…»;
// • переставить взятие acceptedAt после await SendMessageAndWaitAsync — краснеет «Гонка…»
//   (свежее ожидание, поднятое внутри хода адресата, снято прежним моментом);
// • допустить дубль в условии снятия (убрать { Duplicate: false }) — краснеет «Дубль…»
//   (новое ожидание снято rejected-ретраем, а не принятым сообщением).
[Trait("Category", "Host")]
public class ChatsSendAwaitingHumanClearTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ChatsSendAwaitingHumanClearTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();
    private FeatureJournalStore Journal => _factory.Services.GetRequiredService<FeatureJournalStore>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<string> CreateProjectAsync(string tag)
    {
        var dir = Path.Combine(_factory.TempDir, "chatsend_await_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "ChatsSendAwait" + tag, rootPath = dir, createDirectory = true });
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

    // Сценарий живой ситуации: фича (журнал пишется по featureId — у эпика это его id), её
    // чат-исполнитель и родитель = чат, где фичу создали (SessionTaskLinks.ParentSessionId →
    // TaskItem.SourceSessionId). Привязка исполнителя — тот же MarkClaudeStarted, что ставит
    // сервер при старте исполнителя.
    private async Task<(string TaskId, string ProjectId, string StaffChat, string ExecChat)> ExecutorScenarioAsync(string tag)
    {
        var projectId = await CreateProjectAsync(tag);
        var staffChat = await CreateSessionAsync(projectId);
        var (text, isError) = await CallMcpAsync("tasks", staffChat, "tasks_create",
            new { title = "Ф-2.48 " + tag, role = "feature" });
        isError.Should().BeFalse(text);
        var taskId = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;
        var exec = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec-" + tag, taskId: taskId);
        Tasks.MarkClaudeStarted(taskId, exec.Id, DateTime.UtcNow);
        return (taskId, projectId, staffChat, exec.Id);
    }

    // Ожидание стоит — тот же продюсер, что в бою: доклад-блокер из чата-исполнителя
    // (SessionMessagingService.ReportUpAsync → SetAwaitingHuman).
    private async Task SetAwaitingByBlockerAsync(string taskId, string execChat)
    {
        var (text, isError) = await CallMcpAsync("wsp", execChat, "chats_report_up",
            new { text = "Застрял на доступе к репозиторию. Работа дальше не идёт.", blocker = true });
        isError.Should().BeFalse(text);
        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().NotBeNull(
            "сценарий без ожидания проверку не проверяет");
    }

    // Занятый получатель: Working + подставной адаптер на месте процесса — сообщение
    // принимается в очередь без живого CLI-хода (тот же приём, что у
    // WorkspaceHttpChatsSendPreemptTests). Принятие в очередь — принятое сообщение:
    // для снятия ожидания момент и исход принятия те же, что у Running/Completed.
    private void SetWorkingAdapter(string sessionId)
    {
        var field = typeof(SessionManager).GetProperty("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var map = (System.Collections.IDictionary)field.GetValue(Sessions)!;
        var entry = map[sessionId]!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
        Sessions.GetById(sessionId)!.Status = SessionStatus.Working;
    }

    // Доступ к SessionEntry (white-box): тесту на гонку нужны внутренности подачи — подменный
    // Process и TurnWaiter ожидаемого пути (тот же рефлексивный приём, что у SetWorkingAdapter).
    private object GetEntry(string sessionId)
    {
        var field = typeof(SessionManager).GetProperty("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var map = (System.Collections.IDictionary)field.GetValue(Sessions)!;
        return map[sessionId]!;
    }

    // Живой ход на месте процесса: мокадтер (тот же приём, что SetWorkingAdapter, но ход не
    // «висит», а доходит до конца). Когда сообщение подано «ходу»: сначала тестовый хук
    // (raiseNewAwaiting — тот же продюсер нового ожидания, что боевой report_up:
    // TaskManager.SetAwaitingHuman; доставка доклада здесь не проверяется), затем разрешение
    // TurnWaiter подачи — тот же исход, что резолв OnMessageAsync по result: ожидаемый путь
    // (TurnIntake.SendMessageAndWaitAsync) возвращается Completed, не дожидаясь таймаута.
    private void SetTurnAdapter(object entry, Action? raiseNewAwaiting)
    {
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(info);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .Callback(() =>
            {
                raiseNewAwaiting?.Invoke();
                var tcs = (TaskCompletionSource<TurnResult>?)entry.GetType()
                    .GetField("TurnWaiter")!.GetValue(entry);
                tcs?.TrySetResult(new TurnResult("Правки внесены.", 1, null, true));
            })
            .Returns(Task.CompletedTask);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
    }

    // Сервис с тестовым вещателем: DI-вещатель в этой проверке не видим (тот же приём, что
    // ReportUpAwaitingHumanTests.БлокерВещаетTaskChanged).
    private SessionMessagingService Messaging(TestSessionBroadcaster bc) => new(Sessions, Projects, Tasks, null, bc);

    // Снятия не было — события «updated» без изменения не шлём. (Предикат FluentAssertions
    // берёт как expression tree: «is» с объявлением переменной в нём недопустим (CS8122),
    // поэтому приведение до Where.)
    private void AssertNoTaskChanged(TestSessionBroadcaster bc, string taskId) =>
        bc.Owner.Select(m => m.Message as Protocol.TaskChangedMessage)
            .Where(t => t != null && t.Task.Id == taskId)
            .Should().BeEmpty();

    // --- положительный путь: сквозной вызов через MCP (тот же вход, что в бою) ---

    [Fact]
    public async Task АдресатСнимаетОжидание_ЖурналФичиПолучаетWaitingCleared()
    {
        var (taskId, _, staff, exec) = await ExecutorScenarioAsync("clear");
        await SetAwaitingByBlockerAsync(taskId, exec);

        SetWorkingAdapter(exec);
        var (text, isError) = await CallMcpAsync("wsp", staff, "chats_send",
            new { sessionId = exec, text = "Разобрался: доступ есть, правь по мастеру.", wait = "none" });
        isError.Should().BeFalse(text);
        text.Should().Contain("queued", "сообщение адресата принято — это и есть его действие");

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanSince.Should().BeNull("действие адресата блокера снимает ожидание");
        stored.AwaitingHumanReason.Should().BeNull("пара Since/Reason снимается целиком");

        // Журнал фичи: пара waiting → waiting_cleared (по ней таймлайн считает время ожидания;
        // снятие без записи оставило бы «ждёт вас» в статистике фичи)
        var entries = Journal.Read(OwnerId, taskId).Entries;
        entries.Should().Contain(e => e.Kind == "waiting" && e.TaskId == taskId);
        entries.Should().Contain(e => e.Kind == "waiting_cleared" && e.TaskId == taskId);
    }

    // --- вещание (доска и страница фичи живут только по task_changed) ---

    [Fact]
    public async Task АдресатВещаетTaskChanged_УжеСнятымОжиданием()
    {
        var (taskId, _, staff, exec) = await ExecutorScenarioAsync("broadcast");
        await SetAwaitingByBlockerAsync(taskId, exec);
        SetWorkingAdapter(exec);

        var bc = new TestSessionBroadcaster();
        var messaging = Messaging(bc);

        var caught = await bc.AssertBroadcasts("task_changed", taskId, OwnerId,
            async () => (await messaging.SendAsync(OwnerId, exec, "Разобрался: правь по мастеру.",
                    callerSessionId: staff, senderSessionId: staff, agentDepthFallback: 0,
                    wait: "none", timeoutSec: null))
                .Should().BeOfType<SessionMessagingService.SendOutcome.Queued>());

        var msg = caught.Should().ContainSingle().Subject
            .Should().BeAssignableTo<Protocol.TaskChangedMessage>().Subject;
        msg.Action.Should().Be("updated");
        msg.Task.Id.Should().Be(taskId);
        msg.Task.AwaitingHumanSince.Should().BeNull("событие несёт снятое состояние, а не до ручки");
        msg.Task.AwaitingHumanReason.Should().BeNull();
    }

    // --- что ожидания НЕ снимает ---

    // Правило «только адресат»: чат, не являющийся родителем получателя. Соседний чат того же
    // проекта и дочерний исполнитель (свой чат-исполнитель своей задачи) — оба не адресат
    // блокера нашей задачи, хотя оба пишут в тот же чат.
    [Fact]
    public async Task НеАдресатОжиданияНеСнимает_СоседнийЧатИДочернийИсполнитель()
    {
        var (taskId, projectId, staff, exec) = await ExecutorScenarioAsync("notaddressee");
        await SetAwaitingByBlockerAsync(taskId, exec);

        var sibling = await CreateSessionAsync(projectId);
        // Дочерний исполнитель: своя задача того же штаба, свой чат-исполнитель
        var (otherText, otherErr) = await CallMcpAsync("tasks", staff, "tasks_create",
            new { title = "Ф-2.48 соседняя задача" });
        otherErr.Should().BeFalse(otherText);
        var otherTaskId = JsonSerializer.Deserialize<JsonElement>(otherText).GetProperty("id").GetString()!;
        var otherExec = (await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits,
            name: "exec-other", taskId: otherTaskId)).Id;
        Tasks.MarkClaudeStarted(otherTaskId, otherExec, DateTime.UtcNow);

        SetWorkingAdapter(exec);
        var bc = new TestSessionBroadcaster();
        var messaging = Messaging(bc);

        var asSibling = await messaging.SendAsync(OwnerId, exec, "Соседний вопрос.",
            callerSessionId: sibling, senderSessionId: sibling, agentDepthFallback: 0, wait: "none", timeoutSec: null);
        asSibling.Should().BeOfType<SessionMessagingService.SendOutcome.Queued>("проверка не вакуумна: сообщение принято");

        var asChild = await messaging.SendAsync(OwnerId, exec, "Доклад дочернего исполнителя.",
            callerSessionId: otherExec, senderSessionId: otherExec, agentDepthFallback: 0, wait: "none", timeoutSec: null);
        asChild.Should().BeOfType<SessionMessagingService.SendOutcome.Queued>();

        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().NotBeNull(
            "ожидание снимает действие адресата — родителя получателя, а не любого чата");
        AssertNoTaskChanged(bc, taskId);
    }

    // Авто-сообщения сервера (будильник сторожа, доылка после рестарта) приходят без
    // вызывателя (WatchdogAlarm: callerSessionId = null): их отрезает правило «только
    // адресат» — особого списка «служебных» сообщений нет. Ответ человека в чате задачи этим
    // путём не проходит и не должен: его снимает AwaitingHumanAnswerSubscriber (FS-E3b).
    [Fact]
    public async Task АвтоСообщениеСервераБезВызывателя_ОжиданияНеСнимает()
    {
        var (taskId, _, _, exec) = await ExecutorScenarioAsync("auto");
        await SetAwaitingByBlockerAsync(taskId, exec);
        SetWorkingAdapter(exec);

        var bc = new TestSessionBroadcaster();
        var messaging = Messaging(bc);

        var outcome = await messaging.SendAsync(OwnerId, exec, "[СИСТЕМНАЯ ДИРЕКТИВА — доылка прошлого хода]",
            callerSessionId: null, senderSessionId: null, agentDepthFallback: 0,
            wait: "none", timeoutSec: null, preempt: false);
        outcome.Should().BeOfType<SessionMessagingService.SendOutcome.Queued>(
            "сообщение принято — иначе «не сняло» значит лишь «ничего не произошло»");

        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().NotBeNull();
        AssertNoTaskChanged(bc, taskId);
    }

    // Сторож «Since <= at» (тот же, что у ответа человека): ожидание, поставленное ПОСЛЕ
    // принятия сообщения, создано не этим действием — гонка «ответ адресата / новый блокер»
    // не должна снимать свежее. Момент действия в бою — время принятия; здесь он задан явно,
    // флейки в проверке нет.
    [Fact]
    public async Task СторожОжиданиеПозжеПринятияНеСнимается_РаннееСнимается()
    {
        var (taskId, _, staff, exec) = await ExecutorScenarioAsync("guard");
        await SetAwaitingByBlockerAsync(taskId, exec);
        var since = Tasks.GetById(taskId)!.AwaitingHumanSince!.Value;

        var target = Sessions.GetById(exec)!;
        // Предусловие сценария: родитель чата-исполнителя — чат, где создали задачу
        target.TaskId.Should().Be(taskId);
        SessionTaskLinks.ParentSessionId(target,
            new ClaudeHomeServer.Services.Composition.TaskLookupAdapter(Tasks)).Should().Be(staff);
        var bc = new TestSessionBroadcaster();
        var messaging = Messaging(bc);

        // Действие «до» постановки ожидания — его часовой свежий set не трогает
        await messaging.TryClearAwaitingForAddresseeAsync(target, staff, since - TimeSpan.FromSeconds(10));
        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().NotBeNull("сторож Since <= at: ожидание новее действия");
        AssertNoTaskChanged(bc, taskId);

        // Тот же вызов адресата, момент действия ПОСЛЕ постановки — снимает
        await messaging.TryClearAwaitingForAddresseeAsync(target, staff, since + TimeSpan.FromSeconds(10));
        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().BeNull();
        Tasks.GetById(taskId)!.AwaitingHumanReason.Should().BeNull();
    }

    // Тест на гонку (R-Ф-2.48, major): предыдущий тест проверял стор с явным «at», но не источник
    // acceptedAt в SendAsync. Дефект был именно там: acceptedAt брался после await
    // SendMessageAndWaitAsync — это время конца ожидания, а не начала отправки. При wait=… ход
    // адресата идёт внутри вызова (таймаут 5–240 с), и поднятый в нём новый блокер (Since позже
    // ответа) ответу не адресован — «прежний» at прошёл бы сторож Since<=at и снял бы его.
    // Путь — боевой ожидаемый: чат свободен, сообщение само стартует ход; мокадтер поднимает
    // новое ожидание внутри хода, до ответа. Два момента одной подачи: поставленное до отправки
    // снимается (этап 1), поднятое внутри хода остаётся (этап 2). Мутация «acceptedAt после
    // await» краснит этап 2; этап 1 при мутации зелёный — он фиксирует инвариант, не дефект.
    [Fact]
    public async Task ГонкаНовыйБлокерВнутриОжиданияХода_НеСнимается_РаннееОжиданиеСнимается()
    {
        var (taskId, _, staff, exec) = await ExecutorScenarioAsync("race");
        await SetAwaitingByBlockerAsync(taskId, exec);
        var entry = GetEntry(exec);
        var bc = new TestSessionBroadcaster();
        var messaging = Messaging(bc);

        // Этап 1: ожидание, поставленное ДО отправки, — действие адресата его снимает
        // (ожидаемый путь, тот же clear, что в бою).
        SetTurnAdapter(entry, raiseNewAwaiting: null);
        var first = await messaging.SendAsync(OwnerId, exec, "Разобрался: правь по мастеру.",
            callerSessionId: staff, senderSessionId: staff, agentDepthFallback: 0,
            wait: null, timeoutSec: null);
        first.Should().BeOfType<SessionMessagingService.SendOutcome.Completed>(
            "сообщение подняло ход адресата и дождалось его конца — то окно, где живёт гонка");
        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().BeNull(
            "ожидание, поставленное до начала отправки, снимается действием адресата");

        // Этап 2: новый блокер внутри хода адресата (Since позже начала отправки) — не снят.
        // Предыдущий ход оставил чат Working с живым TurnWaiter — возвращаем состояние до
        // подачи (тот же шов, что WorkspaceDeployDelegationTests:140), чтобы второй вызов шёл
        // ожидаемым путём, а не через очередь.
        Sessions.GetById(exec)!.Status = SessionStatus.Active;
        entry.GetType().GetField("TurnWaiter")!.SetValue(entry, null);
        var newReason = "Уткнулся в новый блок: нет доступа к прод-конфигу.";
        SetTurnAdapter(entry, () => Tasks.SetAwaitingHuman(taskId, newReason));

        var second = await messaging.SendAsync(OwnerId, exec, "Понял, жду доступ — держи блок.",
            callerSessionId: staff, senderSessionId: staff, agentDepthFallback: 0,
            wait: null, timeoutSec: null);
        second.Should().BeOfType<SessionMessagingService.SendOutcome.Completed>(
            "проверка не вакуумна: второй ход тоже состоялся");

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanSince.Should().NotBeNull(
            "ожидание, поднятое внутри хода адресата, этим ответом не снимается");
        stored.AwaitingHumanReason.Should().Be(newReason);
    }

    // Дубль в очереди (R-Ф-2.48, минор): дубль — не действие адресата. Тот же текст того же
    // отправителя уже стоит в очереди и будет доставлен; первое его принятие уже учлось как
    // действие (то же чтение, что у рефанда в SendAsync — «не дошло»). Опасность прежнего
    // условия: дублем можно ошибочно снять НОВОЕ ожидание, поднятое между первой копией и
    // ретраем, — хронологически оно раньше дубля, и сторож по acceptedAt тут бессилен.
    [Fact]
    public async Task ДубльОжиданияПоднятогоМеждуПервойКопиейИРетраемНеСнимает()
    {
        var (taskId, _, staff, exec) = await ExecutorScenarioAsync("duplicate");
        await SetAwaitingByBlockerAsync(taskId, exec);
        SetWorkingAdapter(exec);

        var bc = new TestSessionBroadcaster();
        var messaging = Messaging(bc);

        // Первая копия принята — её действие снимает ожидание (положительный путь уже покрыт
        // «АдресатСнимаетОжидание…»; здесь она нужна, чтобы создать состояние дубля).
        var first = await messaging.SendAsync(OwnerId, exec, "Разобрался: правь по мастеру.",
            callerSessionId: staff, senderSessionId: staff, agentDepthFallback: 0, wait: "none", timeoutSec: null);
        first.Should().BeOfType<SessionMessagingService.SendOutcome.Queued>();
        Tasks.GetById(taskId)!.AwaitingHumanSince.Should().BeNull();

        // Между первой копией (ещё в очереди) и ретраем поднят новый блокер — новое ожидание.
        // Первая копия уже родила своё task_changed (сняла исходное ожидание) — события дубля
        // считаем от её границы.
        var newReason = "Уткнулся в новый блок: нет доступа к прод-конфигу.";
        Tasks.SetAwaitingHuman(taskId, newReason);
        var beforeRetry = bc.Owner.Count;

        var second = await messaging.SendAsync(OwnerId, exec, "Разобрался: правь по мастеру.",
            callerSessionId: staff, senderSessionId: staff, agentDepthFallback: 0, wait: "none", timeoutSec: null);
        var dup = second.Should().BeOfType<SessionMessagingService.SendOutcome.Queued>().Subject;
        dup.Duplicate.Should().BeTrue("тот же текст того же отправителя уже в очереди — ретрей отклонён");

        var stored = Tasks.GetById(taskId)!;
        stored.AwaitingHumanSince.Should().NotBeNull(
            "дубль — не принятое действие: новое ожидание, поднятое после первой копии, он не снимает");
        stored.AwaitingHumanReason.Should().Be(newReason);
        bc.Owner.Skip(beforeRetry)
            .Select(m => m.Message as Protocol.TaskChangedMessage)
            .Where(t => t != null && t.Task.Id == taskId)
            .Should().BeEmpty("снятия не было — «updated» без изменения не шлём");
    }
}
