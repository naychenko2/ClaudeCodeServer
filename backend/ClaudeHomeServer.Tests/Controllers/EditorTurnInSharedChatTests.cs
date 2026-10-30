using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2a-10a, сквозной: редактор общего проекта отправляет сообщение REST-ручкой, ход принадлежит
/// ему (актор хода — он, расходы и ключ чата — владельца), рамка хода узкая, а ответы платформенного
/// MCP из этого чата и текст, ушедший в ход, не несут личных маркеров владельца (образец —
/// ParticipantTurnFrameGuardTests: сторож по всему tools/list держит полный состав, здесь — сквозная
/// связка «REST-отправка редактора → актор → рамка → MCP»).
/// Усиление S2a-10b: кроме «личного в ответе нет», D-инструменты трёх тулсетов, которые в узкой
/// рамке остаются в составе и обязаны отказать на вызове (wsp search_unified, tasks
/// list_projects/search, notes semantic_search), проверяются как отказ — ослабление сужения в любом
/// из тулсетов красит этот тест (проведено мутациями в отчёте задачи).
/// </summary>
public class EditorTurnInSharedChatTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _owner;
    private readonly HttpClient _editor;

    public EditorTurnInSharedChatTests()
    {
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _owner = _factory.CreateAuthenticatedClient();
        _editor = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public void Dispose() => _factory.Dispose();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    [Fact]
    public async Task ХодРедактора_АкторРедактор_РамкаУзкая_МаркерыВладельцаНеПротекают()
    {
        foreach (var c in new[] { _owner, _editor })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();
        var tag = Guid.NewGuid().ToString("N")[..8];

        // личное владельца: проект, задача, заметка личного vault — с маркерами
        var personal = await _owner.PostAsJsonAsync("/api/projects", new { name = $"PERSONAL-проект-{tag}" });
        personal.EnsureSuccessStatusCode();
        var personalId = IdOf(await personal.Content.ReadFromJsonAsync<JsonElement>());
        var task = await _owner.PostAsJsonAsync($"/api/projects/{personalId}/tasks", new { title = $"PERSONAL-задача-{tag}" });
        task.EnsureSuccessStatusCode();
        var taskId = IdOf(await task.Content.ReadFromJsonAsync<JsonElement>());

        // общий проект владельца: редактор — второй пользователь
        var shared = await _owner.PostAsJsonAsync("/api/projects", new { name = $"Общий-{tag}" });
        shared.EnsureSuccessStatusCode();
        var sharedId = IdOf(await shared.Content.ReadFromJsonAsync<JsonElement>());
        var editorUser = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        var ownerUser = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!;
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(sharedId,
            [new ProjectMember { UserId = editorUser.Id, Role = ProjectRole.Editor, AddedBy = ownerUser.Id }]);
        var chat = await _owner.PostAsJsonAsync($"/api/projects/{sharedId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        var chatId = IdOf(await chat.Content.ReadFromJsonAsync<JsonElement>());

        var response = await _editor.PostAsJsonAsync($"/api/sessions/{chatId}/messages",
            new { text = "вопрос редактора", wait = "none" });

        response.IsSuccessStatusCode.Should().BeTrue($"редактор отправляет в чат общего проекта: {(int)response.StatusCode}");
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        sessions.GetTurnActor(chatId, ownerUser.Id).Should().Be(editorUser.Id,
            "ход запущен редактором — актор хода он, а не владелец чата (ключ расходов и хранилищ)");
        sessions.GetTurnFrame(chatId, ownerUser.Id).Should().Be(TurnFrameKind.Narrow);
        var adapter = _factory.LlmAdapters.Adapters[chatId];
        string sent;
        lock (adapter.SentMessages) sent = string.Join("\n", adapter.SentMessages);
        sent.Should().Contain("вопрос редактора").And.NotContain("PERSONAL").And.NotContain(personalId).And.NotContain(taskId);

        // MCP из чата редактора (токен — владельца-арендатора, заголовок — чат): личное недостижимо
        foreach (var (server, tool, args) in new (string, string, object)[]
        {
            ("wsp", "search_unified", new { query = tag }),
            ("wsp", "files_tree", new { projectId = personalId }),
            ("wsp", "chats_list", new { }),
            ("tasks", "tasks_list", new { scope = "all" }),
            ("tasks", "tasks_get", new { id = taskId }),
            ("notes", "notes_search", new { query = "PERSONAL" }),
        })
        {
            var mcp = await _owner.AsCaller(chatId).PostAsJsonAsync($"/mcp/{server}/{chatId}", new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
            });
            mcp.EnsureSuccessStatusCode();
            var body = await mcp.Content.ReadAsStringAsync();
            // id в аргументах вызова отказ может повторить сам — проверяем маркеры данных и id личного проекта в ответе
            body.Should().NotContain("PERSONAL", $"{server}/{tool}");
            if (tool != "tasks_get" && tool != "files_tree") body.Should().NotContain(personalId).And.NotContain(taskId);
        }

        // Сила сквозного теста (находка M ревью R-S2a-10a): «маркеров в ответе» мало — ослабление
        // сужения в отдельном тулсете их не даёт. Проверяется конкретный отказ каждого тулсета в
        // узкой рамке. Список — из манифеста ParticipantTurnFrameGuardTests (класс D: «отказывает
        // в узкой рамке»), он живёт там, здесь только то, чем ловится сужение трёх тулсетов.
        // Сначала состав tools/list этого чата: без него отказ был бы вакуумным — несуществующий
        // инструмент тоже отвечает isError.
        // Состав проверяется намеренно: часть D-инструментов в узкой рамке снимается из тулсета
        // целиком (notes_graph, notes_daily, notes_resolve — их нет в tools/list, и их пометку
        // держит состав ParticipantTurnFrameGuardTests, а не вызов). Здесь те, что остаются
        // offered и обязаны отказать на вызове.
        foreach (var (server, denied) in new (string, string[])[]
        {
            ("wsp", ["search_unified"]),
            ("tasks", ["tasks_list_projects", "tasks_search"]),
            ("notes", ["notes_semantic_search"]),
        })
        {
            var list = await _owner.AsCaller(chatId).PostAsJsonAsync($"/mcp/{server}/{chatId}",
                new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } });
            list.EnsureSuccessStatusCode();
            var offered = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement
                .GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!).ToHashSet();
            foreach (var tool in denied)
                offered.Should().Contain(tool,
                    $"«{server}/{tool}» обязан быть в составе этого чата: иначе проверка отказа ниже вакуумна");
        }

        foreach (var (server, tool, args) in new (string, string, object)[]
        {
            ("wsp", "search_unified", new { query = tag }), // поиск по всему пространству владельца
            ("tasks", "tasks_list_projects", new { }), // список проектов владельца
            ("tasks", "tasks_search", new { query = tag }), // поиск по всем задачам владельца
            ("notes", "notes_semantic_search", new { query = tag }), // поиск по всем источникам, включая личный vault
        })
        {
            var mcp = await _owner.AsCaller(chatId).PostAsJsonAsync($"/mcp/{server}/{chatId}", new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
            });
            mcp.EnsureSuccessStatusCode();
            var result = JsonDocument.Parse(await mcp.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
            (result.TryGetProperty("isError", out var isError) && isError.GetBoolean()).Should().BeTrue(
                $"[{server}/{tool}] обязан отказывать в узкой рамке (D-строка манифеста "
                + $"ParticipantTurnFrameGuardTests); получил: {result.GetRawText()}");
        }
    }
}
