using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// S2a-10b на ЖИВОМ ходу чата общего проекта: матрица SharedProjectRoleMatrixTests берёт отказ
/// там, где идущего хода нет, а правило «автор или владелец» решает именно идущий ход. Здесь ход
/// реально ведётся: его автор — тот, кто подал сообщение (реестр актора S2a-1), а не владелец
/// чата. Перебой чужого хода и «не доставлять» сообщение из очереди отвечают по авторству и
/// отправительству машинным кодом (not_chat_author, not_sender); автору хода и владельцу путь
/// открыт. Хаб (SessionHub.OwnerOrTurnAuthor) решает тем же helper'ом, что проверен здесь и на
/// уровне стора (SessionManagerTests.TurnActor).
/// </summary>
[Trait("Category", "Host")]
public class SharedChatTurnControlTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _owner;
    private readonly HttpClient _editor;

    public SharedChatTurnControlTests()
    {
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _owner = _factory.CreateAuthenticatedClient();
        _editor = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public void Dispose() => _factory.Dispose();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    /// <summary>Проект владельца с чатом; второй пользователь — редактор.</summary>
    private async Task<string> SharedChatAsync()
    {
        foreach (var c in new[] { _owner, _editor })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var project = await _owner.PostAsJsonAsync("/api/projects", new { name = $"Общий-{tag}" });
        project.EnsureSuccessStatusCode();
        var projectId = IdOf(await project.Content.ReadFromJsonAsync<JsonElement>());
        // Чат до участников: пока проект личный, провижна владельца нет; в общем проекта без
        // руководителя она не запускается никому (W4-B1a доработка 2, финал ed64271c)
        var chat = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        var chatId = IdOf(await chat.Content.ReadFromJsonAsync<JsonElement>());

        var ownerUser = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!;
        var editorUser = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
            [new ProjectMember { UserId = editorUser.Id, Role = ProjectRole.Editor, AddedBy = ownerUser.Id }]);

        return chatId;
    }

    /// <summary>Арендатор чата — владелец его проекта: тот, чьим ключом живут хранилища и чей
    /// токен платит ход.</summary>
    private string OwnerOf(string chatId)
    {
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        return sessions.ResolveOwnerId(sessions.GetById(chatId)!)!; // чат этого теста всегда с арендатором
    }

    /// <summary>Послать сообщение (wait=none): первый — начинает ход, следующий в занятом чате
    /// встаёт в очередь (202). Стаб LLM-адаптера ход не завершает — он остаётся идущим.</summary>
    private static async Task SendAsync(HttpClient client, string chatId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/sessions/{chatId}/messages", new { text, wait = "none" });
        ((int)response.StatusCode).Should().BeOneOf(new[] { 200, 202 }, $"подача «{text}»");
    }

    /// <summary>Сообщения, стоящие в очереди этого чата: id по тексту подачи.</summary>
    private static async Task<string> PendingIdAsync(HttpClient client, string chatId, string text)
    {
        var response = await client.GetAsync($"/api/sessions/{chatId}/pending");
        response.EnsureSuccessStatusCode();
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return items.EnumerateArray().Single(m => m.GetProperty("text").GetString() == text)
            .GetProperty("id").GetString()!;
    }

    private static async Task Assert403WithCodeAsync(HttpResponseMessage response, string code)
    {
        ((int)response.StatusCode).Should().Be(403);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    // ── перебой: чужой ход не для чужого участника, свой — для автора, любой — для владельца ──

    [Fact]
    public async Task ПеребойЧужогоХода_УчастникНеАвтор_403NotChatAuthor()
    {
        var chatId = await SharedChatAsync();
        await SendAsync(_owner, chatId, "ход ведёт владелец");

        var response = await _editor.PostAsync($"/api/sessions/{chatId}/pending/preempt", null);

        await Assert403WithCodeAsync(response, ChatAuthorGate.ErrorCode);
    }

    // Автор своего хода и владелец чата — оба проходят гейт (это разные случаи: человек,
    // ведущий ход, и его арендатор). Ответ — 409 «прерывать нечего», а не 403: в тестовом хосте
    // 204 принципиально не получить, потому что стаб-адаптер не объявляет живой прогон
    // (FakeLlmSessionAdapter.HasLiveTurn = false → SessionManager.PreemptForPending, :6129
    // «живого прогона нет — убивать нечего»). Правомерность автора и владельца подтверждается
    // ниже по цепочке: актор записан (GetTurnActor) и IsOwnerOrTurnAuthor даёт им обоим true
    // (тот же helper, что проверяет ручка), а на уровне стора это SessionManagerTests
    // «АвторХода_ВладелецИАвторДа_ДругойУчастникИНеизвестныйНет» и гонка «ГонкаДвухПодач…».
    [Fact]
    public async Task ПеребойСвоегоХода_АвторИВладелецГейтПроходят_ЧужойУчастникНет()
    {
        var chatId = await SharedChatAsync();
        await SendAsync(_editor, chatId, "ход ведёт редактор"); // актор идущего хода — редактор
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var editorUser = _factory.Services.GetRequiredService<UserStore>().FindByUsername(
            TestWebApplicationFactory.SecondUsername)!;

        sessions.GetTurnActor(chatId, OwnerOf(chatId)).Should().Be(editorUser.Id,
            "реестр актора: ход принадлежит подавшему его участнику");
        sessions.IsOwnerOrTurnAuthor(chatId, editorUser.Id).Should().BeTrue("автор хода");
        sessions.IsOwnerOrTurnAuthor(chatId, OwnerOf(chatId)).Should().BeTrue("владелец чата");

        var asAuthor = await _editor.PostAsync($"/api/sessions/{chatId}/pending/preempt", null);
        ((int)asAuthor.StatusCode).Should().Be(409, "автор идущего хода не отшит правом");
        var asOwner = await _owner.PostAsync($"/api/sessions/{chatId}/pending/preempt", null);
        ((int)asOwner.StatusCode).Should().Be(409, "владелец останавливает любой ход чата");
    }

    // ── «не доставлять»: отправитель и владелец, не чужой участник ───────────────────────────

    [Fact]
    public async Task НеДоставлять_ЧужоеУчастнику403NotSender_СвоёИВладелецДа()
    {
        var chatId = await SharedChatAsync();
        await SendAsync(_owner, chatId, "ход ведёт владелец"); // занятый чат: следующие подачи встанут в очередь
        await SendAsync(_editor, chatId, "очередь редактора");
        await SendAsync(_owner, chatId, "очередь владельца");

        var editorMsg = await PendingIdAsync(_editor, chatId, "очередь редактора");
        var ownerMsg = await PendingIdAsync(_owner, chatId, "очередь владельца");

        var foreign = await _editor.DeleteAsync($"/api/sessions/{chatId}/pending/{ownerMsg}");
        await Assert403WithCodeAsync(foreign, "not_sender");

        var own = await _editor.DeleteAsync($"/api/sessions/{chatId}/pending/{editorMsg}");
        own.StatusCode.Should().Be(HttpStatusCode.NoContent, "свою очередь снимает её отправитель");

        var byOwner = await _owner.DeleteAsync($"/api/sessions/{chatId}/pending/{ownerMsg}");
        byOwner.StatusCode.Should().Be(HttpStatusCode.NoContent, "владелец снимает чужое сообщение");
    }

    // ── смена модели: тот же гейт, что у режима прав, — код owner_settings ────────────────────

    [Fact]
    public async Task СменаМоделиУчастником_403OwnerSettings_ВладелецПроходит()
    {
        var chatId = await SharedChatAsync();
        var projectId = _factory.Services.GetRequiredService<SessionManager>().GetById(chatId)!.ProjectId!;

        var denied = await _editor.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{chatId}", new { model = "sonnet" });
        await Assert403WithCodeAsync(denied, OwnerSettingsGate.ErrorCode);

        var allowed = await _owner.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{chatId}", new { model = "sonnet" });
        allowed.EnsureSuccessStatusCode();
        IdOf(JsonDocument.Parse(await allowed.Content.ReadAsStringAsync()).RootElement).Should().Be(chatId);
    }
}
