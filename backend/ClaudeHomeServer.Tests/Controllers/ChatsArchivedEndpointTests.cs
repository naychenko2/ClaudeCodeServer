using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

// PUT /api/chats/{id}/archived и GET /api/chats/archive-preview — шаг 2 плана «Архив
// чатов» (v4): гейт живости (409 на ходе в полёте и живых фоновых агентах), выдача
// архивного из GET /api/chats/{id} (не фильтруется никогда) и счётчик превью,
// совпадающий с отбором тика правила (одной функцией GetArchiveRuleCandidates).
public class ChatsArchivedEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<string> CreateChatAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    private static SessionManager SessionsOf(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<SessionManager>();

    // Подмена адаптера чата занятым (образец — Update_ХодВПолёте_ОтказПоЖивомуПрогону в
    // SessionManagerTests): гейт архивации смотрит на живой прогон, а не Status
    private static void SetAdapter(TestWebApplicationFactory factory, string sessionId,
        bool hasLiveTurn, bool hasTrackedBg)
    {
        var sessions = SessionsOf(factory);
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var map = (System.Collections.IDictionary)field.GetValue(sessions)!;
        var entry = map[sessionId]!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(hasLiveTurn);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.SetupGet(a => a.HasTrackedBg).Returns(hasTrackedBg);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
    }

    // --- Гейт живости: 409 ---

    [Fact]
    public async Task Архивация_ХодВПолёте_409()
    {
        var id = await CreateChatAsync();
        SetAdapter(factory, id, hasLiveTurn: true, hasTrackedBg: false);

        var resp = await _client.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = true });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("идёт ход");
        SessionsOf(factory).GetById(id)!.IsArchived.Should().BeFalse("состояние не изменилось");
    }

    [Fact]
    public async Task Архивация_ЖивыеФоновыеАгенты_409()
    {
        var id = await CreateChatAsync();
        SetAdapter(factory, id, hasLiveTurn: false, hasTrackedBg: true);

        var resp = await _client.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = true });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("фоновые агенты");
        SessionsOf(factory).GetById(id)!.IsArchived.Should().BeFalse();
    }

    // --- Штатная архивация/возврат ---

    [Fact]
    public async Task Архивация_СпокойныйЧат_200_иGetОтдаётАрхивный()
    {
        var id = await CreateChatAsync();

        var put = await _client.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = true });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        body.GetProperty("archivedAt").ValueKind.Should().Be(JsonValueKind.String,
            "ответ — полная Session с полями архива");
        body.GetProperty("archivedBy").GetString().Should().Be("user");

        // GET /api/chats/{id} по архиву не фильтруется никогда (план v4, шаг 2)
        var get = await _client.GetAsync($"/api/chats/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        fetched.GetProperty("isArchived").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ВозвратИзАрхива_200_ПоляСброшены()
    {
        var id = await CreateChatAsync();
        await _client.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = true });

        var put = await _client.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = false });

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var chat = SessionsOf(factory).GetById(id)!;
        chat.ArchivedAt.Should().BeNull();
        chat.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Архивация_ЧужойЧат_404()
    {
        var id = await CreateChatAsync();
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await stranger.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = true });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Архивация_НесуществующийЧат_404()
    {
        var resp = await _client.PutAsJsonAsync("/api/chats/no-such-chat/archived", new { archived = true });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- Превью автоправила ---

    [Fact]
    public async Task Превью_СчитаетТолькоКандидатов_иСовпадаетСОтборомТика()
    {
        var sessions = SessionsOf(factory);
        // Два старых кандидата; остальные — исключения по каждому правилу отбора
        var old1 = await CreateChatAsync();
        var old2 = await CreateChatAsync();
        sessions.GetById(old1)!.UpdatedAt = DateTime.UtcNow.AddDays(-100);
        sessions.GetById(old2)!.UpdatedAt = DateTime.UtcNow.AddDays(-100);

        var fresh = await CreateChatAsync(); // свежий — порог не пройден

        var pinnedOld = await CreateChatAsync(); // закреплённый
        sessions.GetById(pinnedOld)!.UpdatedAt = DateTime.UtcNow.AddDays(-100);
        sessions.GetById(pinnedOld)!.IsPinned = true;

        var temporaryOld = await CreateChatAsync(); // временный
        sessions.SetExpiry(temporaryOld, 60);
        sessions.GetById(temporaryOld)!.UpdatedAt = DateTime.UtcNow.AddDays(-100);

        var archivedOld = await CreateChatAsync(); // уже в архиве
        sessions.GetById(archivedOld)!.UpdatedAt = DateTime.UtcNow.AddDays(-100);
        sessions.SetArchived(archivedOld, archived: true, by: "user");

        var resp = await _client.GetAsync("/api/chats/archive-preview?days=30");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("count").GetInt32().Should().Be(2);

        // Сторож расхождения (критерий 9 плана v4): превью и тик считают ОДНОЙ функцией —
        // прямой вызов с тем же порогом обязан дать то же число (пороги в тесте с запасом
        // в обе стороны, секундный дрейф nowUtc между вызовами ничего не меняет)
        var ownerId = sessions.GetById(old1)!.OwnerId!;
        sessions.GetArchiveRuleCandidates(ownerId, projectId: null, days: 30, DateTime.UtcNow)
            .Should().HaveCount(2);

        // Превью read-only: повторный вызов — то же число, ничего не заархивировалось
        var again = await _client.GetAsync("/api/chats/archive-preview?days=30");
        (JsonSerializer.Deserialize<JsonElement>(await again.Content.ReadAsStringAsync())
            .GetProperty("count").GetInt32()).Should().Be(2);
        sessions.GetById(old1)!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Превью_ЧужойПроект_404()
    {
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_arch_" + Guid.NewGuid().ToString("N"))).FullName;
        var projectResp = await stranger.PostAsJsonAsync("/api/projects",
            new { name = "Чужой проект архива", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var resp = await _client.GetAsync($"/api/chats/archive-preview?days=30&projectId={projectId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Превью_НеположительныйПорог_400(int days)
    {
        var resp = await _client.GetAsync($"/api/chats/archive-preview?days={days}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Чтение настройки автоправила ---

    // Контракт initialDays/hasFirstRun экрана настройки: личный порог и признак
    // первого прохода (производный от User.ArchiveRuleFirstRunAt)
    [Fact]
    public async Task НастройкаАвтоправила_Чтение_ОтдаётПорогИПризнакПервогоПрохода()
    {
        // Исходно правило не настроено и первый проход не запускался
        var resp = await _client.GetAsync("/api/chats/archive-settings");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("archiveAfterDays").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("hasFirstRun").GetBoolean().Should().BeFalse();

        // Запись порога (за флагом) отражается в чтении
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.ChatAutoArchive}",
            new { enabled = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PutAsJsonAsync("/api/chats/archive-days", new { days = 30 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        resp = await _client.GetAsync("/api/chats/archive-settings");
        body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("archiveAfterDays").GetInt32().Should().Be(30);
        body.GetProperty("hasFirstRun").GetBoolean().Should().BeFalse("первый проход ещё не запускался");

        // Признак первого прохода — производный от User.ArchiveRuleFirstRunAt
        var me = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        var userId = me.GetProperty("userId").GetString()!;
        factory.Services.GetRequiredService<UserStore>()
            .SetArchiveRuleFirstRunAt(userId, DateTime.UtcNow);

        resp = await _client.GetAsync("/api/chats/archive-settings");
        body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("archiveAfterDays").GetInt32().Should().Be(30);
        body.GetProperty("hasFirstRun").GetBoolean().Should().BeTrue();
    }
}
