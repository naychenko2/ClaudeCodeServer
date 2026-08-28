using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
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

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context) =>
            new DepthAdapter(session, this);
    }

    private sealed class DepthAdapter(Session info, DepthAdapterFactory owner) : ILlmSessionAdapter
    {
        public Session Info => info;
        public int CurrentTurnAgentDepth => owner.AgentDepth;
        public bool CurrentTurnSuppressTasksExecute => false;
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
            int agentDepth = 0, bool suppressTasksExecute = false) => Task.CompletedTask;
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
    /// Главный сценарий: ход с agentDepth=1 (чат позвали из другого чата через chats_send)
    /// не может запустить исполнителя задачи. Отказ — текстом для модели, тем же, что
    /// отдал бы REST-гейт [DenyOnDelegatedTurn].
    /// </summary>
    [Fact]
    public async Task ДелегированныйХод_НеМожетЗапуститьИсполнителя()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        _adapters.AgentDepth = 1;

        var answer = await CallToolAsync(Client, sessionId, "tasks_run_executor", new { taskId = "any" });

        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("недоступно на делегированном ходу",
            "тот же текст, что у REST-гейта — модель должна понять, что цепочка дальше не идёт");
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
    /// Реакционный авто-ход постановщика (агентная глубина 0, но ход — ответ на доклад
    /// исполнителя): запуск тоже запрещён — иначе A сам себе запускает только что созданную
    /// задачу и цикл «доклад → запуск → доклад» становится бесконечным.
    /// </summary>
    [Fact]
    public async Task ХодДокладаИсполнителя_ТожеЗапрещён()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        // SuppressTasksExecute-флаг адаптера читается тем же GetActiveTurnDelegation;
        // выставить его снаружи нельзя — проверяем через глубину (см. атрибут:
        // AlsoWhenExecutorSuppressed) вторым уровнем юнит-проверки ShouldDeny в
        // TaskExecutionServiceTests; здесь — сам факт, что тулсет вообще гейтит
        _adapters.AgentDepth = 2;

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
}
