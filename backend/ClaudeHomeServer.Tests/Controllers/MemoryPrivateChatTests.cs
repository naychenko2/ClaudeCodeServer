using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2d-5a (разрез 4bc3d04f §1 «S2d-5a», путь 17): запись в общую память из приватного чата —
/// отказ, В ТОМ ЧИСЛЕ автору-владельцу в его собственном приватном чате (гейт актора S2c-6
/// этого не даёт: автор своего приватного проходит его как актор). Проверяются три полки:
/// память команды (team_memory_remember/update/forget), полка сферы (sphere_memory_*) и память
/// проектной персоны (memory_* при проектной персоне). Граница правила: закрыта запись,
/// чтение общих полок и личная память (глобальной персоны) — как раньше; вызов без
/// заголовка X-Caller-Session-Id — не чат (прямой запрос, sidecar), не приватный.
/// Плюс «Задачи из чата» (ChatTaskExtractionService): по приватному чату не запускается
/// вовсе — ни транскрипта, ни LLM (счётчик вызовов).
///
/// Рамка хода зафиксирована широкой (WideTurnFrame): тесты изолируют гейт приватного чата —
/// он от рамки не зависит. Сам вердикт рамки — в TurnFrameTests и McpNarrowFrame*; в бою
/// приватный чат владельца при акторе-владельце широк (S2d-1b), и именно эта конфигурация
/// здесь и нужна: отказ обязан быть у владельца, а не только у редактора.
/// </summary>
[Trait("Category", "Host")]
public class MemoryPrivateChatTests : IDisposable
{
    private readonly CountingCheap _cheap = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MemoryPrivateChatTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services =>
            {
                // Свой стаб раннера с счётчиком — на нём проверяется «LLM не вызван»
                services.AddSingleton<ICheapTextRunner>(_cheap);
                services.AddSingleton<ITurnFrameSource>(new WideTurnFrame());
            },
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task<string> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    /// <summary>Общий проект (с участником-редактором) — приватным чат бывает только в нём.</summary>
    private async Task<string> SharedProjectAsync()
    {
        var projectId = await IdOf(await _client.PostAsJsonAsync("/api/projects",
            new { name = $"priv-{Guid.NewGuid():N}" }));
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
            [new ProjectMember { UserId = "reader-" + Guid.NewGuid().ToString("N")[..8], Role = ProjectRole.Editor }]);
        return projectId;
    }

    private async Task<string> ChatAsync(string projectId) =>
        await IdOf(await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" }));

    /// <summary>Тумблер S2d-7a: чат автора-владельца без сообщений становится приватным.</summary>
    private async Task MakePrivateAsync(string chatId) =>
        (await _client.PutAsJsonAsync($"/api/chats/{chatId}/visibility", new { @private = true }))
            .EnsureSuccessStatusCode();

    private async Task<string> PersonaAsync(string name, string scope, string? projectId) =>
        await IdOf(await _client.PostAsJsonAsync("/api/personas", new { name, role = "Роль", scope, projectId }));

    /// <summary>
    /// Вызов инструмента от лица чата (заголовок X-Caller-Session-Id — его в бою ставит конфиг
    /// хода). chatId=null — вызов без заголовка: не чат, приватности нет.
    /// </summary>
    private async Task<JsonElement> CallAsync(string? personaId, string projectId, string tool,
        object args, string? chatId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/memory/{personaId ?? "-"}/{projectId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
            }), Encoding.UTF8, "application/json"),
        };
        if (chatId is not null) req.Headers.Add("X-Caller-Session-Id", chatId);
        var resp = await _client.SendAsync(req);
        // MCP отвечает 200 даже на отказ — isError в теле
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out var e) && e.GetBoolean();

    private static string TextOf(JsonElement result) =>
        result.GetProperty("content")[0].GetProperty("text").GetString()!;

    private async Task<(string ProjectId, string OpenChat, string PrivateChat)> FixtureAsync()
    {
        var projectId = await SharedProjectAsync();
        var openChat = await ChatAsync(projectId);
        var privateChat = await ChatAsync(projectId);
        await MakePrivateAsync(privateChat);
        return (projectId, openChat, privateChat);
    }

    // ─── Память команды ─────────────────────────────────────────────────────

    [Fact]
    public async Task TeamMemoryRemember_ИзПриватногоЧата_Отказ_И_ПамятьКомандыНеИзменена()
    {
        var (projectId, openChat, privateChat) = await FixtureAsync();

        // Посев из общего чата — «не изменена» значит «остался ровно посеянный»
        var seeded = await CallAsync(null, projectId, "team_memory_remember",
            new { text = "ОБЩАЯ-запись-" + Guid.NewGuid().ToString("N")[..8] }, openChat);
        IsError(seeded).Should().BeFalse(TextOf(seeded));
        var seededId = JsonDocument.Parse(TextOf(seeded)).RootElement.GetProperty("id").GetString()!;

        var denied = await CallAsync(null, projectId, "team_memory_remember",
            new { text = "ПРИВАТНАЯ-запись-" + Guid.NewGuid().ToString("N")[..8] }, privateChat);
        IsError(denied).Should().BeTrue("автор-владелец в своём приватном чате — тоже отказ");
        TextOf(denied).Should().Contain("приватн");

        var list = await CallAsync(null, projectId, "team_memory_list", new { }, privateChat);
        IsError(list).Should().BeFalse("чтение памяти команды в приватном чате разрешено");
        var body = JsonDocument.Parse(TextOf(list)).RootElement;
        body.GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("items")[0].GetProperty("id").GetString().Should().Be(seededId);
        TextOf(list).Should().NotContain("ПРИВАТНАЯ");
    }

    [Fact]
    public async Task TeamMemoryUpdateИForget_ИзПриватногоЧата_Отказ()
    {
        var (projectId, openChat, privateChat) = await FixtureAsync();
        var seeded = await CallAsync(null, projectId, "team_memory_remember",
            new { text = "ОБЩАЯ-" + Guid.NewGuid().ToString("N")[..8] }, openChat);
        var id = JsonDocument.Parse(TextOf(seeded)).RootElement.GetProperty("id").GetString()!;

        var deniedUpdate = await CallAsync(null, projectId, "team_memory_update",
            new { id, text = "переписано из приватного" }, privateChat);
        IsError(deniedUpdate).Should().BeTrue();
        TextOf(deniedUpdate).Should().Contain("приватн");

        var deniedForget = await CallAsync(null, projectId, "team_memory_forget", new { id }, privateChat);
        IsError(deniedForget).Should().BeTrue();
        TextOf(deniedForget).Should().Contain("приватн");

        // ни update, ни forget не прошли: запись цела
        var list = await CallAsync(null, projectId, "team_memory_list", new { id }, privateChat);
        IsError(list).Should().BeFalse();
        TextOf(list).Should().NotContain("переписано");
    }

    // ─── Память проектной персоны ───────────────────────────────────────────

    [Fact]
    public async Task ПроектнаяПерсона_ЗаписьИзПриватногоЧата_Отказ_ЧтениеКакРаньше()
    {
        var (projectId, openChat, privateChat) = await FixtureAsync();
        var personaId = await PersonaAsync("КОМАНДА-" + Guid.NewGuid().ToString("N")[..8], "project", projectId);

        // общий чат — как раньше
        var ok = await CallAsync(personaId, projectId, "memory_remember",
            new { type = "semantic", text = "ОБЩЕЕ-" + Guid.NewGuid().ToString("N")[..8] }, openChat);
        IsError(ok).Should().BeFalse(TextOf(ok));

        // приватный чат автора-владельца — отказ: память проектной персоны видна всем чатам проекта
        var denied = await CallAsync(personaId, projectId, "memory_remember",
            new { type = "semantic", text = "ПРИВАТНОЕ-" + Guid.NewGuid().ToString("N")[..8] }, privateChat);
        IsError(denied).Should().BeTrue();
        TextOf(denied).Should().Contain("приватн");

        var list = await CallAsync(personaId, projectId, "memory_list", new { }, privateChat);
        IsError(list).Should().BeFalse("чтение своей памяти не выносит фактов наружу");
        TextOf(list).Should().Contain("ОБЩЕЕ").And.NotContain("ПРИВАТНОЕ");
    }

    [Fact]
    public async Task ЛичнаяПамятьГлобальнойПерсоны_ВПриватномЧатеВладельца_КакРаньше()
    {
        var (projectId, _, privateChat) = await FixtureAsync();
        var globalPersona = await PersonaAsync("PERSONAL-" + Guid.NewGuid().ToString("N")[..8], "global", null);

        var ok = await CallAsync(globalPersona, projectId, "memory_remember",
            new { type = "semantic", text = "ЛИЧНОЕ-" + Guid.NewGuid().ToString("N")[..8] }, privateChat);
        IsError(ok).Should().BeFalse(
            "личная память — вне правила S2d-5a: она принадлежит владельцу, а не полкам проекта "
            + "(в бою приватный чат владельца при акторе-владельце — широкая рамка, S2d-1b)");
    }

    // ─── Память сферы ───────────────────────────────────────────────────────

    [Fact]
    public async Task ПамятьСферы_ЗаписьИзПриватногоЧата_Отказ_ЧтениеДоступно()
    {
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var sphereId = await IdOf(await _client.PostAsJsonAsync("/api/project-groups",
            new { name = $"sg-{Guid.NewGuid():N}", color = "#D97757" }));
        var projectId = await IdOf(await _client.PostAsJsonAsync("/api/projects",
            new { name = $"sp-{Guid.NewGuid():N}", groupId = sphereId }));
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
            [new ProjectMember { UserId = "editor-" + Guid.NewGuid().ToString("N")[..8], Role = ProjectRole.Editor }]);
        var privateChat = await ChatAsync(projectId);
        await MakePrivateAsync(privateChat);

        var denied = await CallAsync(null, projectId, "sphere_memory_remember",
            new { text = "ПРИВАТНАЯ-" + Guid.NewGuid().ToString("N")[..8] }, privateChat);
        IsError(denied).Should().BeTrue();
        TextOf(denied).Should().Contain("приватн");

        var list = await CallAsync(null, projectId, "sphere_memory_list", new { }, privateChat);
        IsError(list).Should().BeFalse("чтение полки сферы разрешено");
        TextOf(list).Should().NotContain("ПРИВАТНАЯ");
    }

    // ─── Вызов вне чата ─────────────────────────────────────────────────────

    [Fact]
    public async Task ВызовБезЗаголовкаВызывателя_НеПриватныйЧат_КакРаньше()
    {
        var (projectId, _, _) = await FixtureAsync();
        var ok = await CallAsync(null, projectId, "team_memory_remember",
            new { text = "ВНЕ-ЧАТА-" + Guid.NewGuid().ToString("N")[..8] }, chatId: null);
        IsError(ok).Should().BeFalse(
            "без X-Caller-Session-Id это не чат (прямой запрос, sidecar): гейт приватности к нему не применяется");
    }

    // ─── «Задачи из чата» (ChatTaskExtractionService) ──────────────────────

    [Fact]
    public async Task ИзвлечениеЗадач_ПриватныйЧат_НеЗапускается_LlmНольВызовов()
    {
        var (projectId, _, privateChat) = await FixtureAsync();
        var extraction = _factory.Services.GetRequiredService<ChatTaskExtractionService>();

        var before = _cheap.Calls;
        var act = () => extraction.ExtractAsync(OwnerId, privateChat, CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*приватн*");
        _cheap.Calls.Should().Be(before,
            "по приватному чату извлечение не запускается вовсе: ни транскрипта, ни платного LLM");
    }

    [Fact]
    public async Task ИзвлечениеЗадач_ОбщийЧат_РаботаетКакРаньше_LlmОдинВызов()
    {
        var projectId = await SharedProjectAsync();

        // история до создания чата — по образцу NewProjectChatWithHistory
        var csid = "priv-ctrl-" + Guid.NewGuid().ToString("N");
        await _factory.Services.GetRequiredService<ChatHistoryService>().SaveAsync(csid,
            [new StoredUserMessage("Обсуди план"), new StoredTextMessage("Разобрали, вот шаги…")]);
        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(projectId, ClaudeMode.Auto, resumeSessionId: csid);

        var extraction = _factory.Services.GetRequiredService<ChatTaskExtractionService>();
        var before = _cheap.Calls;
        var result = await extraction.ExtractAsync(OwnerId, session.Id, CancellationToken.None);
        _cheap.Calls.Should().Be(before + 1,
            "контроль non-vacuousness: в общем чате LLM зовётся — иначе предыдущий тест ничего не проверяет");
        result.Tasks.Should().BeEmpty(); // стаб отвечает «[]»
    }

    // Стаб раннера с счётчиком: «[]» — все парсеры читают как «ничего не извлечено».
    private sealed class CountingCheap : ICheapTextRunner
    {
        public int Calls;
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult("[]");
        }
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<string?>("[]");
        }
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new OneShotResult("[]", null, 0));
        }
    }
}
