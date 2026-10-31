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
/// S2b-3b: события доски (task_changed) общего проекта идут группе проекта — участник получает
/// обновления доски, посторонний и отозванный участник — нет; личные задачи остаются владельцу.
/// Рассылка не меняет запись проекта. Отрицательные проверки без Task.Delay: проба в группу и
/// адресная проба тому же соединению идут одним FIFO-каналом, ждём адресную через TCS.
/// </summary>
public class TaskBoardMemberDeliveryTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private const string ProbeEvent = "test_board_probe";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TestWebApplicationFactory _factory;
    private readonly List<HubConnection> _connections = [];

    public TaskBoardMemberDeliveryTests(TestWebApplicationFactory factory)
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

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private async Task<string> CreateProjectAsync(string role)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        await SetFlagAsync(Member());
        var dir = Path.Combine(_factory.TempDir, "board_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await owner.PostAsJsonAsync("/api/projects", new { name = "Общий", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        if (role != "none")
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    private async Task<HubConnection> ConnectSecondAsync()
    {
        var token = _factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
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

    /// <summary>Приёмник: заголовки задач из task_changed (в порядке прихода) и сигнал на адресную пробу.</summary>
    private sealed class Sink
    {
        private readonly object _gate = new();
        private readonly List<string> _titles = [];
        public TaskCompletionSource Probe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> FirstTitle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Attach(HubConnection conn)
        {
            conn.On<JsonElement>("message", m =>
            {
                if (m.TryGetProperty("type", out var t) && t.GetString() == "task_changed")
                {
                    var title = m.GetProperty("task").GetProperty("title").GetString() ?? "";
                    lock (_gate) _titles.Add(title);
                    FirstTitle.TrySetResult(title);
                }
            });
            conn.On<JsonElement>(ProbeEvent, _ => Probe.TrySetResult());
        }

        public string[] Titles() { lock (_gate) return [.. _titles]; }
    }

    private async Task CreateTaskAsync(string projectId, string title) =>
        (await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title })).EnsureSuccessStatusCode();

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ПолучаетСобытиеДоскиПроекта(string role)
    {
        var projectId = await CreateProjectAsync(role);
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        await CreateTaskAsync(projectId, "Задача доски");

        (await sink.FirstTitle.Task.WaitAsync(Wait)).Should().Be("Задача доски");
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_СобытиеДоски_БезОписанияИИтога_ВладельцуПолное(string role)
    {
        var projectId = await CreateProjectAsync(role);
        var conn = await ConnectSecondAsync();
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("message", m =>
        {
            if (!m.TryGetProperty("type", out var t) || t.GetString() != "task_changed") return;
            if (m.GetProperty("action").GetString() == "updated") updated.TrySetResult(m.Clone());
            else received.TrySetResult(m.Clone());
        });
        await conn.InvokeAsync("JoinProject", projectId);

        (await Owner().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Тяжёлая", description = new string('д', 250) })).EnsureSuccessStatusCode();

        var task = (await received.Task.WaitAsync(Wait)).GetProperty("task");
        task.GetProperty("title").GetString().Should().Be("Тяжёлая");
        task.GetProperty("description").GetString().Should().BeNullOrEmpty("участник не получает по WS то, чего REST ему не отдаёт");
        (task.TryGetProperty("resultMarkdown", out var res) ? res.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null);

        // Паритет с REST-элементом списка: поля облегчённого элемента едут в payload
        task.GetProperty("listItem").GetBoolean().Should().BeTrue();
        task.GetProperty("hasDescription").GetBoolean().Should().BeTrue();
        task.GetProperty("hasResult").GetBoolean().Should().BeFalse();
        task.GetProperty("descriptionPreview").GetString().Should().Be(new string('д', 200) + "…");

        // С заполненным итогом превью-событие несёт hasResult=true, сам итог не едет
        (await Owner().PutAsJsonAsync($"/api/tasks/{task.GetProperty("id").GetString()}",
            new { resultMarkdown = "итог работы" })).EnsureSuccessStatusCode();
        var upd = (await updated.Task.WaitAsync(Wait)).GetProperty("task");
        upd.GetProperty("hasResult").GetBoolean().Should().BeTrue();
        (upd.TryGetProperty("resultMarkdown", out var res2) ? res2.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null);

        // Владелец в БД/REST видит полную карточку
        var list = await Owner().GetFromJsonAsync<JsonElement>($"/api/tasks/{task.GetProperty("id").GetString()}");
        list.GetProperty("description").GetString().Should().Be(new string('д', 250));
    }

    [Fact]
    public async Task Посторонний_НеМожетПодписатьсяНаДоскуПроекта()
    {
        var projectId = await CreateProjectAsync("none");
        var conn = await ConnectSecondAsync();

        var act = () => conn.InvokeAsync("JoinProject", projectId);

        await act.Should().ThrowAsync<HubException>("не участник не попадает в группу проекта (fail-closed)");
    }

    [Fact]
    public async Task ОтозванныйУчастник_СобытийДоскиБольшеНеПолучает()
    {
        var projectId = await CreateProjectAsync("editor");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);
        await conn.InvokeAsync("JoinUser", SecondId);
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("project_access_revoked", _ => revoked.TrySetResult());

        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();
        await revoked.Task.WaitAsync(Wait);

        await CreateTaskAsync(projectId, "После отзыва");
        var hub = _factory.Services.GetRequiredService<IHubContext<SessionHub>>();
        await hub.Clients.Client(conn.ConnectionId!).SendAsync(ProbeEvent, new { });
        await sink.Probe.Task.WaitAsync(Wait);

        sink.Titles().Should().BeEmpty("отозванное соединение выброшено из группы проекта до рассылки");
    }

    [Fact]
    public async Task ЛичнаяЗадачаВладельца_УчастникуНеПриходит()
    {
        var projectId = await CreateProjectAsync("editor");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        (await Owner().PostAsJsonAsync("/api/tasks", new { title = "Личная" })).EnsureSuccessStatusCode();
        var hub = _factory.Services.GetRequiredService<IHubContext<SessionHub>>();
        await hub.Clients.Client(conn.ConnectionId!).SendAsync(ProbeEvent, new { });
        await sink.Probe.Task.WaitAsync(Wait);

        sink.Titles().Should().BeEmpty("личные задачи идут только в группу владельца");
    }

    [Fact]
    public async Task Конкурентность_РассылкаДоскиПриОтзыве_НеМеняетЗаписьПроектаИНеДоставляетПослеОтзыва()
    {
        var projectId = await CreateProjectAsync("editor");
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var membersBefore = projects.GetById(projectId)!.Members!.Select(m => (m.UserId, m.Role)).ToList();

        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);
        await conn.InvokeAsync("JoinUser", SecondId);
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("project_access_revoked", _ => revoked.TrySetResult());

        // Старт отзыва и рассылок по сигналу, чтобы они действительно пересекались
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoke = Task.Run(async () =>
        {
            await go.Task;
            (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();
        });
        var broadcasts = Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
        {
            await go.Task;
            await CreateTaskAsync(projectId, $"Гонка {i}");
        })).ToList();
        go.SetResult();
        await Task.WhenAll(broadcasts.Append(revoke)).WaitAsync(Wait);
        await revoked.Task.WaitAsync(Wait);

        // После завершённого отзыва ни одна новая рассылка не доходит
        var receivedAtRevoke = sink.Titles().Length;
        await CreateTaskAsync(projectId, "После гонки");
        var hub = _factory.Services.GetRequiredService<IHubContext<SessionHub>>();
        await hub.Clients.Client(conn.ConnectionId!).SendAsync(ProbeEvent, new { });
        await sink.Probe.Task.WaitAsync(Wait);

        sink.Titles().Should().NotContain("После гонки");
        sink.Titles().Length.Should().Be(receivedAtRevoke);
        // Рассылка не писала в проект: состав участников изменил только отзыв
        projects.GetById(projectId)!.Members.Should().BeNullOrEmpty();
        membersBefore.Should().ContainSingle(m => m.UserId == SecondId);
    }
}
