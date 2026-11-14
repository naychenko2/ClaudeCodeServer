using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// W3-B6 (разрез shared-projects-personas-cut-2026-11.md §3.1, И-8): событие проектной персоны
/// (personas_changed) идёт группе проекта И её владельцу — участник после JoinProject получает
/// создание и правку владельца, владелец и без группы проекта получает события своих правок;
/// событие личной (глобальной) персоны не получает ни один участник. Покрыты обе точки
/// рассылки: сервис (создание и PUT) и контроллер (добавление привязки, запись памяти). Полезная нагрузка —
/// тот же PersonasChangedMessage: type, action, personaId, без новых полей. Отрицательные
/// проверки без Task.Delay: проба в группу и адресная проба тому же соединению идут одним
/// FIFO-каналом, ждём адресную через TCS.
/// </summary>
[Trait("Category", "Host")]
public class PersonaChangedMemberDeliveryTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private const string ProbeEvent = "test_persona_probe";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TestWebApplicationFactory _factory;
    private readonly List<HubConnection> _connections = [];

    public PersonaChangedMemberDeliveryTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string SecondId => _factory.Services
        .GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private string OwnerId => _factory.Services
        .GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private async Task<string> CreateProjectAsync(string role)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        await SetFlagAsync(Member());
        var dir = Path.Combine(_factory.TempDir, "personas_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await owner.PostAsJsonAsync("/api/projects", new { name = "Общий", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        if (role != "none")
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    private async Task<HubConnection> ConnectAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.HandshakeTimeout = TimeSpan.FromSeconds(60);
        connection.ServerTimeout = TimeSpan.FromSeconds(60);
        await connection.StartAsync();
        _connections.Add(connection);
        return connection;
    }

    private async Task<HubConnection> ConnectSecondAsync() =>
        await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    /// <summary>Приёмник: события personas_changed (первый payload по action) и сигнал на адресную пробу.</summary>
    private sealed class Sink
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _byAction = new();
        private readonly List<string> _actions = [];

        public TaskCompletionSource Probe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Attach(HubConnection conn)
        {
            conn.On<JsonElement>("message", m =>
            {
                if (!m.TryGetProperty("type", out var t) || t.GetString() != "personas_changed") return;
                var action = m.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                TaskCompletionSource<JsonElement>? tcs = null;
                lock (_gate)
                {
                    _actions.Add(action);
                    if (!_byAction.TryGetValue(action, out tcs))
                    {
                        tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _byAction[action] = tcs;
                    }
                }
                tcs!.TrySetResult(m.Clone());
            });
            conn.On<JsonElement>(ProbeEvent, _ => Probe.TrySetResult());
        }

        public Task<JsonElement> Await(string action)
        {
            TaskCompletionSource<JsonElement>? tcs = null;
            lock (_gate)
            {
                if (!_byAction.TryGetValue(action, out tcs))
                {
                    tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _byAction[action] = tcs;
                }
            }
            return tcs!.Task;
        }

        public string[] Actions() { lock (_gate) return [.. _actions]; }
    }

    private async Task ProbeAsync(HubConnection conn)
    {
        var hub = _factory.Services.GetRequiredService<IHubContext<SessionHub>>();
        await hub.Clients.Client(conn.ConnectionId!).SendAsync(ProbeEvent, new { });
    }

    // voice: {} — явный «без голоса»: создание не доходит до LLM-подбора голоса и остаётся
    // детерминированным; без specialty/autoBindings/autoAvatar прочих one-shot вызовов нет.
    private async Task<string> CreatePersonaAsync(HttpClient client, string? projectId, string name)
    {
        var response = await client.PostAsJsonAsync("/api/personas",
            new { name, scope = projectId is null ? null : "project", projectId, voice = new { } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ПолучаетСобыниеСозданияПроектнойПерсоны(string role)
    {
        var projectId = await CreateProjectAsync(role);
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        var id = await CreatePersonaAsync(Owner(), projectId, "Руководитель");

        var created = await sink.Await("created").WaitAsync(Wait);
        created.GetProperty("personaId").GetString().Should().Be(id);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ПолучаетСобыниеПравкиПроектнойПерсоны_ПравкаВладельцем(string role)
    {
        var projectId = await CreateProjectAsync(role);
        // Персона создана до подписки участника: «created» сюда не доходит, «updated» однозначен
        var id = await CreatePersonaAsync(Owner(), projectId, "Персона проекта");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        (await Owner().PutAsJsonAsync($"/api/personas/{id}", new { name = "Новое имя" })).EnsureSuccessStatusCode();

        var updated = await sink.Await("updated").WaitAsync(Wait);
        updated.GetProperty("personaId").GetString().Should().Be(id);
    }

    // С 12.11 правка проектной персоны — только владелец (Configure): редактор получает 403,
    // события правки от него быть не может (раньше этот тест ловил событие владельцу).
    [Fact]
    public async Task ПравкаПроектнойПерсоныРедактором_403()
    {
        var projectId = await CreateProjectAsync("editor");
        var id = await CreatePersonaAsync(Owner(), projectId, "Персона проекта");

        (await Member().PutAsJsonAsync($"/api/personas/{id}", new { description = "правка редактора" }))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    // Владелец правит свою проектную персону, не состоя в группе проекта: ToOwner доносит
    // событие вкладкам владельца (раздел «Персоны» вне проекта). Проба: убрать ToOwner из
    // PersonasCrudService.Broadcast — тест краснеет (событие не приходит).
    [Fact]
    public async Task Владелец_ПолучаетСобытиеПравкиСвоейПроектнойПерсоны_НеСостояВГруппеПроекта()
    {
        var projectId = await CreateProjectAsync("editor");
        var id = await CreatePersonaAsync(Owner(), projectId, "Персона проекта");

        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var sink = new Sink();
        sink.Attach(conn);
        // Только своя группа, без JoinProject
        await conn.InvokeAsync("JoinUser", OwnerId);

        (await Owner().PutAsJsonAsync($"/api/personas/{id}", new { description = "правка владельца" }))
            .EnsureSuccessStatusCode();

        var updated = await sink.Await("updated").WaitAsync(Wait);
        updated.GetProperty("personaId").GetString().Should().Be(id);
    }

    // Точка контроллера: POST {id}/bindings вещает сам PersonasController, не сервис.
    // Цель «tool/web» — статический ключ ToolCatalog: валидна и не пересекается с посевными
    // привязками проекта (те — Project/Notes/Knowledge).
    [Fact]
    public async Task Участник_ПолучаетСобыниеПривязкиПроектнойПерсоны()
    {
        var projectId = await CreateProjectAsync("editor");
        var id = await CreatePersonaAsync(Owner(), projectId, "Персона проекта");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        (await Owner().PostAsJsonAsync($"/api/personas/{id}/bindings",
            new { type = "tool", target = "web" })).EnsureSuccessStatusCode();

        (await sink.Await("updated").WaitAsync(Wait)).GetProperty("personaId").GetString().Should().Be(id);
    }

    // Точка контроллера: POST {id}/memory вещает сам PersonasController, не сервис.
    // Доработка возврата R-W3-B6: событие памяти проектной персоны — по той же зоне, что
    // created/updated (ревью: шесть memory-рассылок остались owner-only).
    [Fact]
    public async Task Участник_ПолучаетСобыниеПамятиПроектнойПерсоны()
    {
        var projectId = await CreateProjectAsync("editor");
        var id = await CreatePersonaAsync(Owner(), projectId, "Персона проекта");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        (await Owner().PostAsJsonAsync($"/api/personas/{id}/memory",
            new { type = "semantic", text = "факт о проекте" })).EnsureSuccessStatusCode();

        (await sink.Await("memory").WaitAsync(Wait)).GetProperty("personaId").GetString().Should().Be(id);
    }

    [Fact]
    public async Task ЛичнаяПерсона_УчастникуНеПриходит()
    {
        var projectId = await CreateProjectAsync("editor");
        var id = await CreatePersonaAsync(Owner(), null, "Личная");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        (await Owner().PutAsJsonAsync($"/api/personas/{id}", new { name = "Правка" })).EnsureSuccessStatusCode();
        await ProbeAsync(conn);
        await sink.Probe.Task.WaitAsync(Wait);

        sink.Actions().Should().BeEmpty("событие личной персоны идёт только в группу владельца");
    }
}
