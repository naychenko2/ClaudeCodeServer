using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// S1-7: матрица «роль × эндпоинт проектной группы» на настоящем сервере. Четыре роли — владелец,
/// редактор, читатель, посторонний — против чтения, записи, настроек, хода и личных отметок чата.
/// Правила S1: посторонний получает 404 на всё (существование не раскрываем); читатель и редактор
/// читают, а запись проекта и настройки им закрыты — 403 (Write редактору откроют S2b/S2c; S2a-10 открыл только RunTurn);
/// отправка в ход (RunTurn) — владельцу и редактору, читателю 403; личные отметки (прочитано,
/// закреплён) — всем, кто чат видит, и у каждого свои.
/// Правила S2a-10b различаются машинным кодом отказа (строка DenyCode): перебой чужого хода —
/// автору идущего хода или владельцу, прочему участнику not_chat_author; «не доставлять» сообщение
/// из очереди — его отправителю или владельцу, прочему — not_sender; настройки чата (режим прав,
/// цикл, штаб, модель, провайдер, продолжение) — только владельцу, участнику owner_settings.
/// Каждая строка матрицы — отдельный случай теории: снятая проверка в одном эндпоинте
/// красит только его строки.
/// </summary>
public class SharedProjectRoleMatrixTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Owner = "owner", Editor = "editor", Reader = "reader", Outsider = "outsider";

    // 200 в ожиданиях значит «любой 2xx»: владелец получает 200/201/202/204 по ручке
    private const int Ok2xx = 200;

    // -1 в ожиданиях значит «не 403»: гейт права пройден, а дальше ручка вправе ответить по делу
    // (409 «нечего перезапускать», 400 «нет координатора» и т. п.) — для владельца важно только, что его не отшили правом
    private const int PassesGate = -1;

    // Машинные коды отказа (строки контракта фронта): не автор чата, не отправитель сообщения
    // из очереди, настройки чата принадлежат владельцу
    private const string NotAuthor = "not_chat_author";
    private const string NotSender = "not_sender";
    private const string OwnerSettings = "owner_settings";

    private readonly TestWebApplicationFactory _factory;

    public SharedProjectRoleMatrixTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    /// <summary>
    /// Эндпоинт матрицы: подпись, метод, путь ({p} — проект, {s} — чат), тело и коды по ролям.
    /// DenyCode — машинный code, которым эндпоинт обязан ответить на свой 403: «не автор чата»,
    /// «не отправитель сообщения» и «настройки чата выбирает владелец» различаются для фронта
    /// (текст подсказки и выключенная строка меню), поэтому проверяются строкой матрицы, а не
    /// догадкой по имени эндпоинта.
    /// </summary>
    public sealed record Endpoint(string Name, string Method, string Path, object? Body,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider, string? DenyCode = null)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };
    }

    private static readonly Endpoint[] Endpoints =
    [
        // чтение: видят все, кроме постороннего
        new("проект: чтение", "GET", "/api/projects/{p}", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("чаты проекта: список", "GET", "/api/projects/{p}/sessions", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("чат: чтение", "GET", "/api/chats/{s}", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("чат: история", "GET", "/api/projects/{p}/sessions/{s}/history", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("задачи: список", "GET", "/api/projects/{p}/tasks", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("память команды: чтение", "GET", "/api/projects/{p}/team-memory", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("участники: список", "GET", "/api/projects/{p}/members", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("журнал событий: чтение", "GET", "/api/projects/{p}/events", null, Ok2xx, Ok2xx, Ok2xx, 404),
        // запись проекта: владельцу; участник — 403 (Write редактору S2a-10 не открывал), посторонний — 404
        new("задача: создание", "POST", "/api/projects/{p}/tasks", new { title = "Задача матрицы" }, Ok2xx, 403, 403, 404),
        new("память команды: запись", "POST", "/api/projects/{p}/team-memory", new { text = "Факт матрицы" }, Ok2xx, 403, 403, 404),
        new("чат: удаление", "DELETE", "/api/projects/{p}/sessions/{s}", null, Ok2xx, 403, 403, 404, NotAuthor),
        new("чат: переименование", "PUT", "/api/projects/{p}/sessions/{s}", new { name = "Имя матрицы" }, Ok2xx, 403, 403, 404, NotAuthor),
        // модель и её настройки — не «автор чата», а «настройки владельца» (S2a-10b): тот же
        // гейт, что у режима прав и провайдера
        new("чат: смена модели", "PUT", "/api/projects/{p}/sessions/{s}", new { model = "sonnet" }, PassesGate, 403, 403, 404, OwnerSettings),
        new("чат: архив", "PUT", "/api/chats/{s}/archived", new { archived = true }, Ok2xx, 403, 403, 404, NotAuthor),
        new("чат: удаление по /api/chats", "DELETE", "/api/chats/{s}", null, 404, 403, 403, 404, NotAuthor),
        new("чат: переименование по /api/chats", "PUT", "/api/chats/{s}", new { name = "Имя матрицы" }, 404, 403, 403, 404, NotAuthor),
        // настройки и удаление проекта — Configure, только владелец
        new("проект: настройки", "PUT", "/api/projects/{p}", new { name = "Имя матрицы" }, Ok2xx, 403, 403, 404),
        new("проект: удаление", "DELETE", "/api/projects/{p}", null, Ok2xx, 403, 403, 404),
        // ход: отправка — владелец и редактор (RunTurn, S2a-10), читатель 403 как отказ права;
        // перебой чужого хода (S2a-10b) — автору идущего хода или владельцу, прочему участнику
        // not_chat_author; «не доставлять» сообщение из очереди — его отправителю или владельцу,
        // прочему — not_sender; настройки хода (режим, цикл, штаб, модель, провайдер,
        // продолжение) — только владельцу, участнику owner_settings
        new("ход: сообщение", "POST", "/api/sessions/{s}/messages", new { text = "привет", wait = "none" }, Ok2xx, Ok2xx, 403, 404, "project_scope"),
        new("ход: очередь, снять чужое", "DELETE", "/api/sessions/{s}/pending/nope", null, 404, 403, 403, 404, NotSender),
        new("ход: очередь, перебить", "POST", "/api/sessions/{s}/pending/preempt", null, 409, 403, 403, 404, NotAuthor),
        new("ход: режим прав", "PUT", "/api/chats/{s}/mode", new { mode = "auto" }, Ok2xx, 403, 403, 404, OwnerSettings),
        new("ход: цикл до готово", "PUT", "/api/chats/{s}/loop", new { enabled = false }, Ok2xx, 403, 403, 404, OwnerSettings),
        new("ход: штаб, остановить", "PUT", "/api/chats/{s}/team-implement/stop", null, PassesGate, 403, 403, 404, OwnerSettings),
        new("ход: штаб, авто-волны", "PUT", "/api/chats/{s}/team-implement/auto", new { autoWaves = false }, PassesGate, 403, 403, 404, OwnerSettings),
        new("ход: штаб, перезапуск волны", "POST", "/api/chats/{s}/team-wave/restart", null, PassesGate, 403, 403, 404, OwnerSettings),
        new("ход: штаб, перезапуск хода", "POST", "/api/chats/{s}/team-wave/restart-turn", null, PassesGate, 403, 403, 404, OwnerSettings),
        new("ход: смена провайдера", "POST", "/api/chats/{s}/migrate-provider", new { model = "sonnet" }, PassesGate, 403, 403, 404, OwnerSettings),
        new("ход: продолжить в новом чате", "POST", "/api/chats/{s}/continue", null, PassesGate, 403, 403, 404, OwnerSettings),
        // личное состояние: у каждого своё, достаточно видеть чат
        new("личное: прочитано", "PUT", "/api/chats/{s}/read", null, Ok2xx, Ok2xx, Ok2xx, 404),
        new("личное: закрепить", "PUT", "/api/chats/{s}/user-state", new { pinned = true }, Ok2xx, Ok2xx, Ok2xx, 404),
    ];

    public static IEnumerable<object[]> Matrix() =>
        from e in Endpoints
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { e.Name, role };

    private HttpClient OwnerClient() => _factory.CreateAuthenticatedClient();

    private HttpClient SecondClient() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private HttpClient ClientOf(string role) => role == Owner ? OwnerClient() : SecondClient();

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    /// <summary>Проект владельца с одним чатом; второй пользователь — участник роли role
    /// (для owner и outsider участника нет: owner ходит сам, outsider остаётся посторонним).</summary>
    private async Task<(string ProjectId, string SessionId)> ProjectWithChatAsync(string role)
    {
        var owner = OwnerClient();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(SecondClient(), true);
        var dir = Path.Combine(_factory.TempDir, "mx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Матрица" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is Editor or Reader)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var chat = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        return (projectId, IdOf(await chat.Content.ReadAsStringAsync()));
    }

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task РольНаЭндпоинте_ДаётОжидаемыйКод(string endpointName, string role)
    {
        var endpoint = Endpoints.Single(e => e.Name == endpointName);
        var (projectId, sessionId) = await ProjectWithChatAsync(role);
        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method),
            endpoint.Path.Replace("{p}", projectId).Replace("{s}", sessionId));
        if (endpoint.Body is not null) request.Content = JsonContent.Create(endpoint.Body);

        var response = await ClientOf(role).SendAsync(request);

        var expected = endpoint.For(role);
        // Отказ различается машинным кодом, а не только статусом: «не автор чата», «не отправитель»
        // и «настройки чата выбирает владелец» — не общий project_scope (S2a-10b): фронт по коду
        // решает и текст подсказки, и выключенную строку меню
        if (expected == 403 && endpoint.DenyCode is not null)
            (await response.Content.ReadAsStringAsync()).Should().Contain(endpoint.DenyCode, $"{role}: {endpointName}");
        if (expected == Ok2xx)
            response.IsSuccessStatusCode.Should().BeTrue($"{role}: {endpointName} → {(int)response.StatusCode}");
        else if (expected == PassesGate)
            response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, $"{role}: {endpointName}");
        else
            ((int)response.StatusCode).Should().Be(expected, $"{role}: {endpointName}");
    }

    // ── перенос личной задачи в чужой проект: запись в проект, а не чтение ─────────────────────

    [Theory]
    [InlineData(Owner, 200)]
    [InlineData(Editor, 403)]
    [InlineData(Reader, 403)]
    [InlineData(Outsider, 400)]
    public async Task ПереносЛичнойЗадачиВПроект_ТребуетЗаписи(string role, int expected)
    {
        var (projectId, _) = await ProjectWithChatAsync(role);
        var client = ClientOf(role);
        var created = await client.PostAsJsonAsync("/api/tasks", new { title = "Личная задача переноса" });
        created.EnsureSuccessStatusCode();
        var taskId = IdOf(await created.Content.ReadAsStringAsync());

        var response = await client.PutAsJsonAsync($"/api/tasks/{taskId}", new { projectId });

        ((int)response.StatusCode).Should().Be(expected, $"{role}: перенос личной задачи в проект");
        var moved = _factory.Services.GetRequiredService<TaskManager>().GetById(taskId)!;
        if (expected == 200) moved.ProjectId.Should().Be(projectId);
        else moved.ProjectId.Should().BeNull("отказ не должен переносить задачу");
    }

    // ── личное состояние: отметки участника не меняют отметок владельца и наоборот ────────────────

    private static async Task<JsonElement> ChatOfAsync(HttpClient client, string projectId, string sessionId)
    {
        var response = await client.GetAsync($"/api/projects/{projectId}/sessions");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .EnumerateArray().Single(s => s.GetProperty("id").GetString() == sessionId);
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(Reader)]
    public async Task Прочитанность_УчастникаНеМеняетПрочитанностьВладельца(string role)
    {
        var (projectId, sessionId) = await ProjectWithChatAsync(role);
        var owner = OwnerClient();
        var member = SecondClient();
        (await owner.PutAsync($"/api/chats/{sessionId}/read", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var ownerReadAt = (await ChatOfAsync(owner, projectId, sessionId)).GetProperty("lastReadAt").GetString();
        ownerReadAt.Should().NotBeNull("владелец отметил чат прочитанным");

        (await ChatOfAsync(member, projectId, sessionId)).GetProperty("lastReadAt").ValueKind
            .Should().Be(JsonValueKind.Null, "отметка владельца участнику не достаётся");
        (await member.PutAsync($"/api/chats/{sessionId}/read", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ChatOfAsync(member, projectId, sessionId)).GetProperty("lastReadAt").GetString()
            .Should().NotBeNull("участник видит свою отметку");
        (await ChatOfAsync(owner, projectId, sessionId)).GetProperty("lastReadAt").GetString()
            .Should().Be(ownerReadAt, "чтение участника не двигает прочитанность владельца");
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(Reader)]
    public async Task ЗакрепИЗаглушение_УчастникаВидитТолькоОн_ВладельцаТолькоВладелец(string role)
    {
        var (projectId, sessionId) = await ProjectWithChatAsync(role);
        var owner = OwnerClient();
        var member = SecondClient();

        (await owner.PutAsJsonAsync($"/api/chats/{sessionId}/user-state", new { pinned = true }))
            .EnsureSuccessStatusCode();
        var forMember = await ChatOfAsync(member, projectId, sessionId);
        forMember.GetProperty("isPinned").GetBoolean().Should().BeFalse("закрепление владельца участнику не достаётся");

        (await member.PutAsJsonAsync($"/api/chats/{sessionId}/user-state", new { notificationsMuted = true }))
            .EnsureSuccessStatusCode();

        var memberView = await ChatOfAsync(member, projectId, sessionId);
        memberView.GetProperty("notificationsMuted").GetBoolean().Should().BeTrue("свою отметку участник видит");
        memberView.GetProperty("isPinned").GetBoolean().Should().BeFalse();
        var ownerView = await ChatOfAsync(owner, projectId, sessionId);
        ownerView.GetProperty("notificationsMuted").GetBoolean().Should().BeFalse("заглушение участника владельцу не мешает");
        ownerView.GetProperty("isPinned").GetBoolean().Should().BeTrue();
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.NotificationsMuted
            .Should().BeFalse("поля Session остаются значениями владельца");
    }

    [Fact]
    public async Task РубильникВыключен_УчастникуЛичныеОтметки404()
    {
        var (_, sessionId) = await ProjectWithChatAsync(Editor);
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        config[SharedProjectsGate.SwitchKey] = "false";
        try
        {
            (await SecondClient().PutAsync($"/api/chats/{sessionId}/read", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await SecondClient().PutAsJsonAsync($"/api/chats/{sessionId}/user-state", new { pinned = true }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { config[SharedProjectsGate.SwitchKey] = "true"; }
    }
}
