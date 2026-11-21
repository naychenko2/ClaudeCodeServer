using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Хвост ревью R-Б8e (дельта 2): при переезде проекта с сервера на устройство событие
/// «датасет удалён» уходит владельцу проекта, а не вызывающему:
/// ProjectsController.SetDevice → DropKnowledgeIfOrphanAsync(oldKnowledgeRoot, TenantId) (:467)
/// → группа user_{notifyOwnerId} (ProjectDeletionService.cs:126).
///
/// О вызывающем: ручка под [ProjectScope(Right=Configure)], а Configure редактору не отдаёт
/// матрица прав (ProjectAccess.cs:127 → 403, ProjectScopeAttribute.cs:102) — единственный
/// живой вызывающий ручки это сам владелец. Проверяется адресат: группа владельца, а не группа
/// редактора-участника того же общего проекта.
///
/// Отрицательная проверка без Task.Delay: групповая проба в группу редактора идёт тем же
/// исходящим каналом, что и событие; получил пробу и не получил события — до переезда в его
/// группе не было ничего.
/// </summary>
[Trait("Category", "Host")]
public sealed class ProjectsDeviceMoveKnowledgeEventTests : IClassFixture<ProjectsDeviceMoveKnowledgeEventTests.Host>, IAsyncLifetime
{
    private const string ProbeEvent = "test_move_probe";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // Фиктивное устройство: только Exec — папку на нём никто не проверяет (старый агент,
    // DeviceFolderCheck.Skipped), ретранслятор и выдача папки тесту не нужны вовсе
    private const string DeviceId = "dev-move";

    internal sealed class FakeDeviceExec : IDeviceExecChannel
    {
        public Dictionary<string, DeviceExecStatus> Devices { get; } = [];
        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) => Devices.GetValueOrDefault(deviceId);
        public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    public sealed class Host : TestWebApplicationFactory
    {
        internal FakeDeviceExec DeviceStatuses { get; } = new();

        public Host()
        {
            // Рубильник общих проектов включён до первого запроса: без него участник не резолвится
            ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
            ExtraServices = s => s.AddSingleton<IDeviceExecChannel>(DeviceStatuses);
        }
    }

    private readonly Host _factory;
    private readonly List<HubConnection> _connections = [];

    public ProjectsDeviceMoveKnowledgeEventTests(Host factory)
    {
        _factory = factory;
        _factory.DeviceStatuses.Devices[DeviceId] = new DeviceExecStatus(
            DeviceId, "move-host", true, "linux-x64", "1.0.0", "2.0.0", "2.0.0",
            [DeviceCapabilities.Exec], true, null);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private string UserId(string username) => _factory.Services
        .GetRequiredService<UserStore>().FindByUsername(username)!.Id;

    private async Task<HubConnection> ConnectAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                // Под TestServer WebSocket'а нет, только LongPolling (как в образцах хаб-тестов)
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

    private static TaskCompletionSource<JsonElement> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Личный флаг «Общие проекты» у обоих: без него приглашение участника — 409
    // (ProjectMembershipService.cs:75), рубильника инстанса для этого не достаточно
    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    /// <summary>
    /// Общий проект: владелец testuser + участник-editor seconduser, без чатов (иначе 409).
    /// Наружу — id проекта и rootPath: папка нужна тесту события, чтобы записать в её
    /// WorkspaceKnowledge тот самый датасет, который переезд обязан снять.
    /// </summary>
    private async Task<(string ProjectId, string Dir)> CreateSharedProjectWithEditorAsync(string name)
    {
        await SetFlagAsync(Owner());
        await SetFlagAsync(_factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        var dir = Path.Combine(_factory.TempDir, "kmv_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await Owner().PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await Owner().PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();
        return (projectId, dir);
    }

    /// <summary>Приёмник knowledge_changed: payload первого полученного и флаг «получил любой».</summary>
    private static void AttachKnowledgeSink(HubConnection conn, TaskCompletionSource<JsonElement>? first,
        Action? any = null) =>
        conn.On<JsonElement>("message", m =>
        {
            if (!m.TryGetProperty("type", out var t) || t.GetString() != "knowledge_changed") return;
            any?.Invoke();
            first?.TrySetResult(m.Clone());
        });

    [Fact]
    public async Task ПереездНаУстройство_СобытиеУдаленияДатасета_УходитВладельцу_НеРедактору()
    {
        var ownerId = UserId(TestWebApplicationFactory.TestUsername);
        var editorId = UserId(TestWebApplicationFactory.SecondUsername);

        // Общий проект: владелец testuser + участник-editor seconduser (хелпер ниже)
        var (projectId, dir) = await CreateSharedProjectWithEditorAsync("KnowledgeMove");

        // Запись знаний серверной папки с датасетом — ровно та, что переезд обязан снять
        var datasetId = "dataset-" + Guid.NewGuid().ToString("N");
        var wkStore = _factory.Services.GetRequiredService<WorkspaceKnowledgeStore>();
        var wk = wkStore.GetOrCreate(dir);
        wk.DifyDatasetId = datasetId;
        wkStore.Save(wk);

        // Оба — в своих личных группах: именно в них и идёт событие (user_{id})
        var ownerConn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await ownerConn.InvokeAsync("JoinUser", ownerId);
        var ownerEvent = NewSignal();
        AttachKnowledgeSink(ownerConn, ownerEvent);

        var editorConn = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await editorConn.InvokeAsync("JoinUser", editorId);
        var editorGot = false;
        AttachKnowledgeSink(editorConn, null, () => editorGot = true);

        // Действие: владелец переезжает проект на устройство (путь на фиктивном устройстве
        // никто не проверяет — старый агент). Вызывающий — владелец: редактору ручка закрыта
        // матрицей прав (Configure), см. шапку класса
        var movedPath = "/agent/moved_" + Guid.NewGuid().ToString("N")[..8];
        var r = await Owner().PutAsJsonAsync($"/api/projects/{projectId}/device",
            new { deviceId = DeviceId, rootPath = movedPath });
        r.EnsureSuccessStatusCode();

        // Владелец получил: «датасет удалён», ровно тот id
        var payload = await ownerEvent.Task.WaitAsync(Wait);
        payload.GetProperty("action").GetString().Should().Be("deleted");
        payload.GetProperty("datasetId").GetString().Should().Be(datasetId);

        // Редактор — нет. Групповая проба идёт тем же исходящим каналом, что и событие:
        // получил пробу и не получил события — до переезда в его группе не было ничего.
        // Подписка — ДО отправки: иначе рано пришедшее сообщение теряется и тест висит до таймаута
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editorConn.On<JsonElement>(ProbeEvent, _ => probe.TrySetResult());
        await _factory.Services.GetRequiredService<IHubContext<SessionHub>>()
            .Clients.Group("user_" + editorId).SendAsync(ProbeEvent, new { });
        await probe.Task.WaitAsync(Wait);
        editorGot.Should().BeFalse("событие уходит арендатору проекта (TenantId), а не вызывающему и не участникам");

        // Тот же каскад снимает и запись стора: переезд не оставляет сироту
        wkStore.GetByPath(dir).Should().BeNull();
    }

    [Fact]
    public async Task Редактор_НеМожетВызватьПереезд_РучкаТолькоВладельца()
    {
        var (projectId, _) = await CreateSharedProjectWithEditorAsync("KnowledgeMove403");

        var editor = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var r = await editor.PutAsJsonAsync($"/api/projects/{projectId}/device",
            new { deviceId = DeviceId, rootPath = "/agent/moved_" + Guid.NewGuid().ToString("N")[..8] });

        // 403, а не 404: существование проекта редактору известно, права на переезд нет
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }
}
