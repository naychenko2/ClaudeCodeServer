using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Гейт анти-рекурсии делегирования на http-пути задач (ADR-012, фаза 2 волна 2 — главный
/// пункт приёмки). [DenyOnDelegatedTurn] — MVC-атрибут, а McpTransportController его не
/// применяет вовсе: тулсет зовёт TaskExecutionService через DI, минуя конвейер фильтров.
/// Поэтому гейт обязан жить в TasksToolset (DelegatedTurnGate), и его потеря означала бы
/// платный цикл «доклад → запуск → доклад» — тот самый, ради которого гейт писали.
///
/// fail-closed проверяется и с другой стороны: гейт идёт по сессии из ХВОСТА маршрута
/// (изолирована GetOwned по владельцу токена), а не по заголовку от клиента.
/// </summary>
public class TasksHttpDelegationGateTests : IDisposable
{
    // Фабрика адаптеров с настраиваемой глубиной хода: GetActiveTurnDelegation спрашивает
    // ЖИВОЙ адаптер (env и заголовки протухают), поэтому глубину меняем на лету — один
    // и тот же чат то обычный, то делегированный
    private sealed class DepthAdapterFactory : ILlmSessionAdapterFactory
    {
        public volatile int AgentDepth;
        public volatile bool SuppressTasksExecute;

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context) =>
            new DepthAdapter(session, this);
    }

    private sealed class DepthAdapter(Session info, DepthAdapterFactory owner) : ILlmSessionAdapter
    {
        public Session Info => info;
        public int CurrentTurnAgentDepth => owner.AgentDepth;
        public bool CurrentTurnSuppressTasksExecute => owner.SuppressTasksExecute;
        public bool HasLiveTurn => false;
        public bool HasQueuedTurn => false;
        public bool OrchestrationActive => false;
        public bool HasPendingBg => false;
        public bool HasTrackedBg => false;
        public bool HasTrackedCommandBg => false;
        public bool IsContinuationInFlight => false;
        public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;

        public Task StartAsync() => Task.CompletedTask;
        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false, string? turnModelOverride = null) => Task.CompletedTask;
        public Task CompactAsync() => Task.CompletedTask;
        public void RespondPermission(string requestId, string behavior) { }
        public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
        public void RespondPlan(string requestId, bool approve, string? feedback) { }
        public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
        public bool TrySetModelLive(string model) => false;
        public void Interrupt() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly DepthAdapterFactory _adapters = new();
    private readonly TestWebApplicationFactory _factory;

    public TasksHttpDelegationGateTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync()
    {
        var project = await Client.PostAsJsonAsync("/api/projects", new { name = $"gate-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var session = await Client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    private async Task<JsonElement> CallToolAsync(HttpClient client, string sessionId, string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/tasks/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "отказ гейта — content-ошибка, не протокольная");
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string ToolText(JsonElement answer)
    {
        var result = answer.GetProperty("result");
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    /// <summary>
    /// Потолок глубины: ход с agentDepth=2 (цепочка делегирования ушла глубже допустимого)
    /// не может запустить исполнителя задачи. Отказ — текстом для модели, тем же, что
    /// отдал бы REST-гейт [DenyOnDelegatedTurn].
    /// </summary>
    [Fact]
    public async Task ДелегированныйХод_НеМожетЗапуститьИсполнителя()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        _adapters.AgentDepth = 2;

        var answer = await CallToolAsync(Client, sessionId, "tasks_run_executor", new { taskId = "any" });

        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("недоступно на делегированном ходу",
            "тот же текст, что у REST-гейта — модель должна понять, что цепочка дальше не идёт");
    }

    /// <summary>
    /// Решение владельца 17.10: на глубине 1 (чат позван из другого чата) запуск исполнителя
    /// РАЗРЕШЁН — анти-рекурсия стала потолком глубины, а не запретом. Гейт пропускает, и
    /// отказ дальше даёт только отсутствие задачи — это доказывает, что прошёл именно гейт.
    /// </summary>
    [Fact]
    public async Task Глубина1_ЗапускРазрешён()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        _adapters.AgentDepth = 1;

        var answer = await CallToolAsync(Client, sessionId, "tasks_run_executor", new { taskId = "нет-такой" });

        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("не найдена")
            .And.NotContain("делегированном", "на глубине 1 запуск разрешён — гейт не режет");
    }

    /// <summary>
    /// Контроль: тот же чат на ОБЫЧНОМ ходу (agentDepth=0) проходит гейт и падает уже на
    /// отсутствии задачи — это доказывает, что отказ выше дал именно гейт, а не что-то ещё.
    /// </summary>
    [Fact]
    public async Task ОбычныйХод_ГейтПропускает_ОтказТолькоПоЗадаче()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        _adapters.AgentDepth = 0;

        var answer = await CallToolAsync(Client, sessionId, "tasks_run_executor", new { taskId = "нет-такой" });

        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("не найдена")
            .And.NotContain("делегированном", "гейт обычный ход пропускает");
    }

    /// <summary>
    /// Реакционный авто-ход постановщика (агентная глубина 0, ход — ответ на доклад
    /// исполнителя): запуск разрешён — запрет снят решением владельца, он мешал ставить
    /// следующую задачу. Гейт пропускает, отказ дальше даёт только отсутствие задачи.
    /// </summary>
    [Fact]
    public async Task ХодДокладаИсполнителя_ГейтПропускает()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        _adapters.AgentDepth = 0;
        _adapters.SuppressTasksExecute = true;

        var answer = await CallToolAsync(Client, sessionId, "tasks_run_executor", new { taskId = "нет-такой" });

        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("не найдена")
            .And.NotContain("недоступно", "ход-реакция на доклад запуск больше не запрещает");
    }

    /// <summary>
    /// Делегированный ход глубины 2, пришедший ещё и докладом, по-прежнему запрещён: снятый
    /// запрет касается только реакционного хода, а на глубине 2+ цепочка делегирования дальше
    /// не идёт.
    /// </summary>
    [Fact]
    public async Task ДелегированныйХодДоклада_ПоПрежнемуЗапрещён()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        _adapters.AgentDepth = 2;
        _adapters.SuppressTasksExecute = true;

        var answer = await CallToolAsync(Client, sessionId, "tasks_run_executor", new { taskId = "any" });

        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("недоступно на делегированном ходу");
    }

    /// <summary>
    /// Изоляция: сессия в хвосте обязана принадлежать владельцу ТОКЕНА — токен B с хвостом
    /// сессии A не получает ни инструментов, ни вызова: доступ к задачам закрывается целиком.
    /// </summary>
    [Fact]
    public async Task ЧужаяСессияВХвосте_НиСоставаНиВызова()
    {
        var (_, sessionIdA) = await CreateProjectWithSessionAsync();
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var list = await clientB.PostAsJsonAsync($"/mcp/tasks/{sessionIdA}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        list.EnsureSuccessStatusCode();
        var tools = JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools");
        tools.GetArrayLength().Should().Be(0,
            "чужая сессия — пустой состав (fail-closed), а не «сервер без прав на всякий случай»");

        var call = await CallToolAsync(clientB, sessionIdA, "tasks_list", new { });
        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(call).Should().Contain("другому владельцу");
    }

    /// <summary>
    /// Живой сценарий приёмки «создание задачи работает»: tools/call tasks_create на обычном
    /// ходу создаёт задачу в проекте чата, и она видна через REST того же владельца.
    /// </summary>
    [Fact]
    public async Task СозданиеЗадачи_ЖивымВызовом_Работает()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        _adapters.AgentDepth = 0;

        var answer = await CallToolAsync(Client, sessionId, "tasks_create",
            new { title = "Задача из http-тулсета", priority = "high" });

        answer.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();
        var task = JsonSerializer.Deserialize<JsonElement>(ToolText(answer));
        task.GetProperty("title").GetString().Should().Be("Задача из http-тулсета");
        task.GetProperty("projectId").GetString().Should().Be(projectId,
            "проект по умолчанию — проект чата-вызывателя");
        // Происхождение: задача из чата помнит его id (sourceSessionId)
        task.GetProperty("sourceSessionId").GetString().Should().Be(sessionId);
    }

    // ── Закрытие эпика фичи — только человеком ──────────────────────────────

    private const string FeatureCloseDenied = "Фичу закрывает человек";

    private async Task<string> CreateEpicAsync(string projectId)
    {
        var resp = await Client.PostAsJsonAsync("/api/tasks",
            new { title = "эпик", role = "feature", projectId });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private async Task<string> StatusOfAsync(string id) =>
        JsonSerializer.Deserialize<JsonElement>(await (await Client.GetAsync($"/api/tasks/{id}"))
            .Content.ReadAsStringAsync()).GetProperty("status").GetString()!;

    /// <summary>Ответ — content-ошибка; при не-отказе падает с текстом ответа, а не KeyNotFoundException.</summary>
    private static void AssertIsError(JsonElement answer) =>
        (answer.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean())
            .Should().BeTrue($"ожидался отказ, пришло: {ToolText(answer)}");

    /// <summary>Чат человека на глубине 0 (не исполнитель этого эпика) закрывает фичу — проходит.</summary>
    [Theory]
    [InlineData("tasks_complete")]
    [InlineData("tasks_update")]
    public async Task ЗакрытиеЭпика_ЧатЧеловекаГлубина0_Проходит(string tool)
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        _adapters.AgentDepth = 0;

        var answer = await CallToolAsync(Client, sessionId, tool, new { id = epicId, status = "done" });

        answer.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(ToolText(answer));
        (await StatusOfAsync(epicId)).Should().Be("done");
    }

    /// <summary>Делегированный ход (глубина 1) эпик закрыть не может — потолок 0, не 1, как у запуска.</summary>
    [Theory]
    [InlineData("tasks_complete")]
    [InlineData("tasks_update")]
    public async Task ЗакрытиеЭпика_ДелегированныйХод_Отказ(string tool)
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        _adapters.AgentDepth = 1;

        var answer = await CallToolAsync(Client, sessionId, tool, new { id = epicId, status = "done" });

        AssertIsError(answer);
        ToolText(answer).Should().Contain(FeatureCloseDenied);
        (await StatusOfAsync(epicId)).Should().NotBe("done");
    }

    /// <summary>Чат-исполнитель ЛЮБОЙ задачи фичи (Session.TaskId задан, не эпик) закрыть эпик не может даже на глубине 0.</summary>
    [Theory]
    [InlineData("tasks_complete")]
    [InlineData("tasks_update")]
    public async Task ЗакрытиеЭпика_ЧатИсполнительЭпика_Отказ(string tool)
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        _adapters.AgentDepth = 0;
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId = "шаг-фичи";

        var answer = await CallToolAsync(Client, sessionId, tool, new { id = epicId, status = "done" });

        AssertIsError(answer);
        ToolText(answer).Should().Contain(FeatureCloseDenied);
        (await StatusOfAsync(epicId)).Should().NotBe("done");
    }

    /// <summary>Перевод карточки эпика в колонку категории «Готово» тем же исполнителем — отказ: и в MCP, и в REST (403).</summary>
    [Fact]
    public async Task ЗакрытиеЭпика_ЧерезКолонкуГотово_ИсполнительОтказ()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        _adapters.AgentDepth = 0;
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId = "шаг-фичи";

        var mcp = await CallToolAsync(Client, sessionId, "tasks_update", new { id = epicId, columnId = "done" });
        AssertIsError(mcp);
        ToolText(mcp).Should().Contain(FeatureCloseDenied);

        var rest = await ServiceClient(sessionId).PutAsJsonAsync($"/api/tasks/{epicId}", new { columnId = "done" });
        rest.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rest.Content.ReadAsStringAsync()).Should().Contain(FeatureCloseDenied);
        (await StatusOfAsync(epicId)).Should().NotBe("done");
    }

    /// <summary>Обычная задача (не эпик) закрывается исполнителем как раньше — запрет только на role=feature.</summary>
    [Fact]
    public async Task ЗакрытиеОбычнойЗадачи_ЧатИсполнитель_Проходит()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var created = await Client.PostAsJsonAsync("/api/tasks", new { title = "шаг", projectId });
        var id = JsonSerializer.Deserialize<JsonElement>(
            await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        _adapters.AgentDepth = 0;
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId = id;

        var answer = await CallToolAsync(Client, sessionId, "tasks_complete", new { id });

        answer.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(ToolText(answer));
    }

    private HttpClient ServiceClient(string? callerSessionId)
    {
        var userId = new JwtSecurityTokenHandler()
            .ReadJwtToken(Client.DefaultRequestHeaders.Authorization!.Parameter).Subject;
        var token = _factory.Services.GetRequiredService<JwtService>().IssueServiceToken(userId);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (callerSessionId is not null) client.DefaultRequestHeaders.Add("X-Caller-Session-Id", callerSessionId);
        return client;
    }

    /// <summary>
    /// REST PUT: сервисный токен — из чата-исполнителя и с делегированного хода отказ (400),
    /// из чата человека на глубине 0 и пользовательский JWT (кнопка в UI) — проходит.
    /// </summary>
    [Fact]
    public async Task Rest_ЗакрытиеЭпика_СервисныйТокен()
    {
        var (projectId, humanSession) = await CreateProjectWithSessionAsync();
        var (_, executorSession) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        _factory.Services.GetRequiredService<SessionManager>().GetById(executorSession)!.TaskId = "шаг-фичи";
        var body = new { status = "done" };

        _adapters.AgentDepth = 0;
        var byExecutor = await ServiceClient(executorSession).PutAsJsonAsync($"/api/tasks/{epicId}", body);
        byExecutor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await byExecutor.Content.ReadAsStringAsync()).Should().Contain(FeatureCloseDenied);

        var noSession = await ServiceClient(null).PutAsJsonAsync($"/api/tasks/{epicId}", body);
        noSession.StatusCode.Should().Be(HttpStatusCode.Forbidden, "fail-closed: сервисный токен без сессии");

        _adapters.AgentDepth = 1;
        var delegated = await ServiceClient(humanSession).PutAsJsonAsync($"/api/tasks/{epicId}", body);
        delegated.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusOfAsync(epicId)).Should().NotBe("done");

        _adapters.AgentDepth = 0;
        (await ServiceClient(humanSession).PutAsJsonAsync($"/api/tasks/{epicId}", body))
            .StatusCode.Should().Be(HttpStatusCode.OK, "чат человека на глубине 0");
    }

    /// <summary>Пользовательский JWT (кнопка «Закрыть фичу») закрывает эпик всегда.</summary>
    [Fact]
    public async Task Rest_ЗакрытиеЭпика_ПользовательскийJwt_Проходит()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);

        (await Client.PutAsJsonAsync($"/api/tasks/{epicId}", new { status = "done" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Возобновление эпика фичи — только человеком (дефект df85c799) ───────────
    //
    // Симметрия закрытию: тот же предикат вызывающего, обратный переход Done → Todo.
    // Без этого гарда чат-исполнитель молча отменял решение человека о закрытии.

    private const string FeatureReopenDenied = "Фичу возобновляет человек";

    /// <summary>Закрываем эпик пользовательским JWT (путь кнопки «Закрыть») — человек проходит.</summary>
    private async Task CloseEpicAsync(string epicId)
    {
        var resp = await Client.PutAsJsonAsync($"/api/tasks/{epicId}", new { status = "done" });
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Чат-исполнитель ЛЮБОЙ задачи (Session.TaskId задан) не может возобновить закрытый эпик: отказ в MCP и REST (403), статус остаётся done.</summary>
    [Fact]
    public async Task ВозобновлениеЭпика_ЧатИсполнитель_Отказ_McpИRest()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        await CloseEpicAsync(epicId);
        _adapters.AgentDepth = 0;
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId = "шаг-фичи";

        var mcp = await CallToolAsync(Client, sessionId, "tasks_update", new { id = epicId, status = "todo" });
        AssertIsError(mcp);
        ToolText(mcp).Should().Contain(FeatureReopenDenied);

        var rest = await ServiceClient(sessionId).PutAsJsonAsync($"/api/tasks/{epicId}", new { status = "todo" });
        rest.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rest.Content.ReadAsStringAsync()).Should().Contain(FeatureReopenDenied);
        (await StatusOfAsync(epicId)).Should().Be("done", "отказ не должен трогать статус");
    }

    /// <summary>Перевод карточки эпика в колонку категории «Работа» (columnId=todo) тем же исполнителем — отказ: и в MCP, и в REST (403). Статус из колонки попадает в гард так же, как у закрытия.</summary>
    [Fact]
    public async Task ВозобновлениеЭпика_ЧерезКолонкуВРаботе_ИсполнительОтказ()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        await CloseEpicAsync(epicId);
        _adapters.AgentDepth = 0;
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId = "шаг-фичи";

        var mcp = await CallToolAsync(Client, sessionId, "tasks_update", new { id = epicId, columnId = "todo" });
        AssertIsError(mcp);
        ToolText(mcp).Should().Contain(FeatureReopenDenied);

        var rest = await ServiceClient(sessionId).PutAsJsonAsync($"/api/tasks/{epicId}", new { columnId = "todo" });
        rest.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rest.Content.ReadAsStringAsync()).Should().Contain(FeatureReopenDenied);
        (await StatusOfAsync(epicId)).Should().Be("done", "отказ не должен трогать статус");
    }

    /// <summary>Делегированный ход (глубина 1) эпик возобновить не может — потолок 0, по образцу отказа закрытия. tasks_complete возобновить не может в принципе (всегда Status=Done), поэтому инструмент один.</summary>
    [Fact]
    public async Task ВозобновлениеЭпика_ДелегированныйХод_Отказ()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        await CloseEpicAsync(epicId);
        _adapters.AgentDepth = 1;

        var answer = await CallToolAsync(Client, sessionId, "tasks_update", new { id = epicId, status = "todo" });

        AssertIsError(answer);
        ToolText(answer).Should().Contain(FeatureReopenDenied);
        (await StatusOfAsync(epicId)).Should().Be("done", "отказ не должен трогать статус");
    }

    /// <summary>Чат человека на глубине 0 (не исполнитель) возобновляет эпик — проходит (MCP), статус становится todo.</summary>
    [Fact]
    public async Task ВозобновлениеЭпика_ЧатЧеловекаГлубина0_Проходит()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        await CloseEpicAsync(epicId);
        _adapters.AgentDepth = 0;

        var answer = await CallToolAsync(Client, sessionId, "tasks_update", new { id = epicId, status = "todo" });

        answer.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(ToolText(answer));
        (await StatusOfAsync(epicId)).Should().Be("todo");
    }

    /// <summary>
    /// REST PUT: сервисный токен без сессии и делегированный ход — отказ (403); чат человека
    /// на глубине 0 и пользовательский JWT (кнопка «Возобновить» в UI) — проходят.
    /// </summary>
    [Fact]
    public async Task Rest_ВозобновлениеЭпика_СервисныйТокен()
    {
        var (projectId, humanSession) = await CreateProjectWithSessionAsync();
        var epicId = await CreateEpicAsync(projectId);
        await CloseEpicAsync(epicId);
        var body = new { status = "todo" };

        var noSession = await ServiceClient(null).PutAsJsonAsync($"/api/tasks/{epicId}", body);
        noSession.StatusCode.Should().Be(HttpStatusCode.Forbidden, "fail-closed: сервисный токен без сессии");

        _adapters.AgentDepth = 1;
        var delegated = await ServiceClient(humanSession).PutAsJsonAsync($"/api/tasks/{epicId}", body);
        delegated.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusOfAsync(epicId)).Should().Be("done", "отказ не трогает статус");

        _adapters.AgentDepth = 0;
        (await ServiceClient(humanSession).PutAsJsonAsync($"/api/tasks/{epicId}", body))
            .StatusCode.Should().Be(HttpStatusCode.OK, "чат человека на глубине 0");

        (await Client.PutAsJsonAsync($"/api/tasks/{epicId}", body))
            .StatusCode.Should().Be(HttpStatusCode.OK, "пользовательский JWT — кнопка «Возобновить»");
    }

    /// <summary>Обычная задача (не эпик) возвращается исполнителем как раньше — запрет только на role=feature.</summary>
    [Fact]
    public async Task ВозобновлениеОбычнойЗадачи_ЧатИсполнитель_Проходит()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var created = await Client.PostAsJsonAsync("/api/tasks", new { title = "шаг", projectId });
        var id = JsonSerializer.Deserialize<JsonElement>(
            await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        (await Client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" })).EnsureSuccessStatusCode();

        _adapters.AgentDepth = 0;
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId = id;

        var answer = await CallToolAsync(Client, sessionId, "tasks_update", new { id, status = "todo" });

        answer.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(ToolText(answer));
        (await StatusOfAsync(id)).Should().Be("todo");
    }
}
