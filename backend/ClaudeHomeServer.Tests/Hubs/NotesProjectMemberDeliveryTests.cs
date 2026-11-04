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
/// S2c-4: события изменения заметки проекта (notes_changed) идут группе проекта — участник
/// (reader и editor) получает «заметка изменилась», посторонний и отозванный — нет; владелец
/// получает своё событие в собственной группе, даже не состоя в группе проекта; личная
/// заметка — только владельцу. Полезная нагрузка не шире Read-доступа: id и действие,
/// текста в событии нет. Рассылка не меняет запись проекта. Отрицательные проверки без
/// Task.Delay: проба в группу и адресная проба тому же соединению идут одним FIFO-каналом,
/// ждём адресную через TCS. Тест гонки (доработка S2c-4): записи заметок последовательны,
/// параллельна им только отзывка участника — гонка параллельных POST /api/notes (кэш моделей
/// NotesService) проверятся отдельным дефектом 1886eba9, не здесь.
/// </summary>
[Trait("Category", "Host")]
public class NotesProjectMemberDeliveryTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private const string ProbeEvent = "test_notes_probe";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TestWebApplicationFactory _factory;
    private readonly List<HubConnection> _connections = [];

    public NotesProjectMemberDeliveryTests(TestWebApplicationFactory factory)
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
        var dir = Path.Combine(_factory.TempDir, "notes_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>Приёмник: события notes_changed (первый payload по action) и сигнал на адресную пробу.</summary>
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
                if (!m.TryGetProperty("type", out var t) || t.GetString() != "notes_changed") return;
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

    private async Task<string> CreateNoteAsync(HttpClient client, string? source, string title)
    {
        var response = await client.PostAsJsonAsync("/api/notes", new { title, content = "тело", source });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // Адресная проба именно этому соединению: если бы событие заметки дошло до приёмника,
    // оно встало бы перед пробой в тот же FIFO-канал — счёт после пробы нижний не будет.
    private async Task ProbeAsync(HubConnection conn)
    {
        var hub = _factory.Services.GetRequiredService<IHubContext<SessionHub>>();
        await hub.Clients.Client(conn.ConnectionId!).SendAsync(ProbeEvent, new { });
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ПолучаетСобытияИзмененияЗаметкиПроекта(string role)
    {
        var projectId = await CreateProjectAsync(role);
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        var id = await CreateNoteAsync(Owner(), projectId, "Заметка проекта");

        var created = await sink.Await("created").WaitAsync(Wait);
        created.GetProperty("noteId").GetString().Should().Be(id);
        // Полезная нагрузка не шире Read-доступа: текста в событии нет, участник читает заметку через REST.
        created.TryGetProperty("content", out _).Should().BeFalse();

        (await Owner().PutAsJsonAsync($"/api/notes/{id}", new { content = "правка" })).EnsureSuccessStatusCode();
        (await sink.Await("updated").WaitAsync(Wait)).GetProperty("noteId").GetString().Should().Be(id);

        (await Owner().DeleteAsync($"/api/notes/{id}")).EnsureSuccessStatusCode();
        (await sink.Await("deleted").WaitAsync(Wait)).GetProperty("noteId").GetString().Should().Be(id);
    }

    [Fact]
    public async Task Владелец_ПолучаетСобытиеСвоейПроектнойЗаметки_НеСостояВГруппеПроекта()
    {
        var projectId = await CreateProjectAsync("none");
        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var sink = new Sink();
        sink.Attach(conn);
        // Только своя группа, без JoinProject: другие вкладки владельца и общий список
        // заметок подписаны на user_{id} и должны получать событие, даже когда группы проекта нет.
        await conn.InvokeAsync("JoinUser", OwnerId);

        var id = await CreateNoteAsync(Owner(), projectId, "Заметка владельца");
        (await sink.Await("created").WaitAsync(Wait)).GetProperty("noteId").GetString().Should().Be(id);
    }

    [Fact]
    public async Task Посторонний_НеМожетПодписатьсяНаГруппуПроекта()
    {
        var projectId = await CreateProjectAsync("none");
        var conn = await ConnectSecondAsync();

        var act = () => conn.InvokeAsync("JoinProject", projectId);

        await act.Should().ThrowAsync<HubException>("не участник не попадает в группу проекта (fail-closed)");
    }

    [Fact]
    public async Task ОтозванныйУчастник_СобытийЗаметокБольшеНеПолучает()
    {
        var projectId = await CreateProjectAsync("editor");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);
        // project_access_revoked шлётся в группу user_{id} (ProjectMembershipService) —
        // своё соединение нужно, чтобы дождаться сигнала завершённого выкидывания.
        await conn.InvokeAsync("JoinUser", SecondId);
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("project_access_revoked", _ => revoked.TrySetResult());

        // Сначала — живая доставка (иначе «не получил после отзыва» ничего не доказывает)
        await CreateNoteAsync(Owner(), projectId, "До отзыва");
        await sink.Await("created").WaitAsync(Wait);

        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();
        await revoked.Task.WaitAsync(Wait);

        await CreateNoteAsync(Owner(), projectId, "После отзыва");
        await ProbeAsync(conn);
        await sink.Probe.Task.WaitAsync(Wait);

        sink.Actions().Should().HaveCount(1, "отозванное соединение выброшено из группы проекта до рассылки");
    }

    [Fact]
    public async Task ЛичнаяЗаметка_УчастникуНеПриходит()
    {
        var projectId = await CreateProjectAsync("editor");
        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        await CreateNoteAsync(Owner(), null, "Личная");
        await ProbeAsync(conn);
        await sink.Probe.Task.WaitAsync(Wait);

        sink.Actions().Should().BeEmpty("личные заметки идут только в группу владельца");
    }

    [Fact]
    public async Task Конкурентность_РассылкаЗаметокПриОтзыве_НеМеняетЗаписиПроектаИНеДоставляетПослеОтзыва()
    {
        var projectId = await CreateProjectAsync("editor");
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var membersBefore = projects.GetById(projectId)!.Members!.Select(m => (m.UserId, m.Role)).ToList();

        var conn = await ConnectSecondAsync();
        var sink = new Sink();
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);
        await conn.InvokeAsync("JoinUser", SecondId); // сигнал отзыва живёт в группе user_{id}
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("project_access_revoked", _ => revoked.TrySetResult());

        // Записи идут последовательно, параллельна им только отзывка: гонка параллельных
        // POST /api/notes (кэш моделей NotesService) — отдельный дефект 1886eba9, здесь она
        // не проверяется. Отзывка стартует по сигналу до цепочки и перекрывается с ней;
        // рассылка каждой записи завершается до ответа той записи (NotesController.Create
        // ждёт шов до ответа). Вердикт теста одинаков при любом исходе гонки — отзывка до
        // цепочки, в середине или после: после завершённого отзыва выкинутому соединению
        // ничего не доставляется, поэтому счёт после него финален при любом порядке записей.
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoke = Task.Run(async () =>
        {
            await go.Task;
            (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();
        });
        go.SetResult();
        foreach (var i in Enumerable.Range(0, 5))
            await CreateNoteAsync(Owner(), projectId, $"Гонка {i}");
        await revoke.WaitAsync(Wait);
        await revoked.Task.WaitAsync(Wait);

        // После завершённого отзыва ни одна новая рассылка не доходит
        var receivedAtRevoke = sink.Actions().Length;
        await CreateNoteAsync(Owner(), projectId, "После гонки");
        await ProbeAsync(conn);
        await sink.Probe.Task.WaitAsync(Wait);

        sink.Actions().Length.Should().Be(receivedAtRevoke);
        // Рассылка не писала в проект: все заметки гонки на месте, состав участников
        // изменил только отзыв.
        var list = await Owner().GetFromJsonAsync<JsonElement>($"/api/notes?source={projectId}");
        var titles = list.EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToList();
        foreach (var i in Enumerable.Range(0, 5))
            titles.Should().Contain($"Гонка {i}", "рассылка не меняет записи проекта");
        projects.GetById(projectId)!.Members.Should().BeNullOrEmpty();
        membersBefore.Should().ContainSingle(m => m.UserId == SecondId);
    }
}
