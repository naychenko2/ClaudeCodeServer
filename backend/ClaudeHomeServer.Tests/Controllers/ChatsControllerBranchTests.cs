using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/chats/{id}/branch — шаг 4 плана «Ветвление чата» (docs/research/chat-branching-2026-09.md
// §6): эндпоинт-обёртка над SessionManager.BranchAsync (шаг 3, гейты §9 и резак покрыты
// SessionManagerBranchTests). Здесь проверяются только контроллерные аспекты: резолв владельца
// (404 — как у /archived и /migrate-provider) и форма успешного ответа (200).
public class ChatsControllerBranchTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private static SessionManager SessionsOf(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<SessionManager>();

    private async Task<string> CreateProjectAsync(string dir)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name = "BranchProject", rootPath = dir });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Синтетический транскрипт CLI — тот же формат, что SessionManagerBranchTests.WriteTranscript:
    // два хода, находимые BranchAsync по соглашению FlattenCwd(project.RootPath) внутри
    // UserProfileDir (провайдер по умолчанию не настроен в тестовом конфиге → ConfigRootFor
    // отдаёт UserProfileDir).
    private static void WriteTranscript(TestWebApplicationFactory factory, string csid, string cwd,
        string userText1, string userText2)
    {
        var llmProviders = factory.Services.GetRequiredService<LlmProviderRegistry>();
        var flat = TranscriptMigrator.FlattenCwd(cwd);
        var dir = Path.Combine(llmProviders.UserProfileDir, "projects", flat);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, csid + ".jsonl");
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"system\",\"subtype\":\"init\",\"sessionId\":\"" + csid + "\",\"uuid\":\"s0\"}\n");
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(userText1) + "}}\n");
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid + "\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":\"ответ 1\"}}\n");
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u2\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(userText2) + "}}\n");
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid + "\",\"uuid\":\"a2\",\"message\":{\"role\":\"assistant\",\"content\":\"ответ 2\"}}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    [Fact]
    public async Task Ветвление_ЧужойЧат_404()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_branch_" + Guid.NewGuid().ToString("N"))).FullName;
        var projectId = await CreateProjectAsync(dir);
        var sessionId = await CreateSessionAsync(projectId);

        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await stranger.PostAsJsonAsync($"/api/chats/{sessionId}/branch", new
        {
            userMessageIndex = 0,
            anchorText = "что угодно",
            include = "turn",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ветвление_НесуществующийЧат_404()
    {
        var resp = await _client.PostAsJsonAsync("/api/chats/no-such-chat/branch", new
        {
            userMessageIndex = 0,
            anchorText = "что угодно",
            include = "turn",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ветвление_УспешныйПуть_200_ФормаОтвета()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_branch_" + Guid.NewGuid().ToString("N"))).FullName;
        var projectId = await CreateProjectAsync(dir);
        var sessionId = await CreateSessionAsync(projectId);

        var sessions = SessionsOf(factory);
        var history = factory.Services.GetRequiredService<ChatHistoryService>();
        var csid = "csid-" + Guid.NewGuid().ToString("N")[..12];
        var live = sessions.GetById(sessionId)!;
        live.ClaudeSessionId = csid;

        const string text1 = "первый вопрос разговора с запасом символов для якоря теста";
        const string text2 = "второй вопрос разговора с запасом символов для якоря теста";
        await history.SaveAsync(csid,
        [
            new StoredUserMessage(text1),
            new StoredTextMessage("ответ 1"),
            new StoredResultMessage("success", 100, 1),
            new StoredUserMessage(text2),
            new StoredTextMessage("ответ 2"),
            new StoredResultMessage("success", 100, 1),
        ]);
        WriteTranscript(factory, csid, dir, text1, text2);

        var resp = await _client.PostAsJsonAsync($"/api/chats/{sessionId}/branch", new
        {
            userMessageIndex = 0,
            anchorText = text1,
            include = "turn",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("chatId").GetString().Should().NotBeNullOrEmpty().And.NotBe(sessionId);
        body.GetProperty("projectId").GetString().Should().Be(projectId);
        body.GetProperty("claudeSessionId").GetString().Should().NotBeNullOrEmpty().And.NotBe(csid);
        body.GetProperty("draft").ValueKind.Should().Be(JsonValueKind.Null, "include=turn черновик не возвращает");
    }
}
