using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// S1-7: матрица «роль × эндпоинт проектной группы» на настоящем сервере. Четыре роли — владелец,
/// редактор, читатель, посторонний — против чтения, записи, настроек, хода и личных отметок чата.
/// Правила S1: посторонний получает 404 на всё (существование не раскрываем); читатель и редактор
/// читают, а запись проекта и настройки им закрыты — 403 (редактору запись откроют S2a/S2b);
/// ход и его управление (право RunTurn) — только владельцу, остальным 404, как чужому чату;
/// личные отметки (прочитано, закреплён) доступны всем, кто чат видит, и у каждого свои.
/// Каждая строка матрицы — отдельный случай теории: снятая проверка в одном эндпоинте
/// красит только его строки.
/// </summary>
public class SharedProjectRoleMatrixTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Owner = "owner", Editor = "editor", Reader = "reader", Outsider = "outsider";

    // 200 в ожиданиях значит «любой 2xx»: владелец получает 200/201/202/204 по ручке
    private const int Ok2xx = 200;

    private readonly TestWebApplicationFactory _factory;

    public SharedProjectRoleMatrixTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    /// <summary>Эндпоинт матрицы: подпись, метод, путь ({p} — проект, {s} — чат), тело и коды по ролям.</summary>
    public sealed record Endpoint(string Name, string Method, string Path, object? Body,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider)
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
        // запись проекта: владельцу; участник — 403, посторонний — 404
        new("задача: создание", "POST", "/api/projects/{p}/tasks", new { title = "Задача матрицы" }, Ok2xx, 403, 403, 404),
        new("память команды: запись", "POST", "/api/projects/{p}/team-memory", new { text = "Факт матрицы" }, Ok2xx, 403, 403, 404),
        new("чат: удаление", "DELETE", "/api/projects/{p}/sessions/{s}", null, Ok2xx, 403, 403, 404),
        // настройки и удаление проекта — Configure, только владелец
        new("проект: настройки", "PUT", "/api/projects/{p}", new { name = "Имя матрицы" }, Ok2xx, 403, 403, 404),
        new("проект: удаление", "DELETE", "/api/projects/{p}", null, Ok2xx, 403, 403, 404),
        // ход и управление им — RunTurn: у участника чат «чужой», 404
        new("ход: сообщение", "POST", "/api/sessions/{s}/messages", new { text = "привет", wait = "none" }, Ok2xx, 404, 404, 404),
        new("ход: режим прав", "PUT", "/api/chats/{s}/mode", new { mode = "auto" }, Ok2xx, 404, 404, 404),
        new("ход: цикл до готово", "PUT", "/api/chats/{s}/loop", new { enabled = false }, Ok2xx, 404, 404, 404),
        new("ход: очередь, перебить", "POST", "/api/sessions/{s}/pending/preempt", null, 409, 404, 404, 404),
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
        if (expected == Ok2xx)
            response.IsSuccessStatusCode.Should().BeTrue($"{role}: {endpointName} → {(int)response.StatusCode}");
        else
            ((int)response.StatusCode).Should().Be(expected, $"{role}: {endpointName}");
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
