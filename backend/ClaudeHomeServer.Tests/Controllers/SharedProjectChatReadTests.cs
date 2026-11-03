using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Чаты общего проекта для участника (shared-projects, S1-3a2). В S1 участник ТОЛЬКО ЧИТАЕТ:
/// список чатов проекта (включая созданные ДО приглашения), карточку чата и историю — 200;
/// переименование, клон, продолжение, передача и значок —
/// 403/404 (отправка сообщения редактору открыта в S2a-10, читателю 403) (а чат владельца при этом не меняется). Посторонний, выключенный флаг и выключенный
/// рубильник — 404: существование не раскрываем. Владелец идёт прежним путём.
/// QA-P1 (решение Андрея): переименование и AI-переименование — право записи контента чата:
/// редактору открыты (200 / тело ручки), читателю — прежний отказ.
/// Создание чата — право редактора с S2d-1c, здесь не проверяется: см. SharedProjectChatCreateTests.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectChatReadTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectChatReadTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Проект владельца с чатом, созданным ДО приглашения; второй пользователь —
    /// в заданной роли (role = null — не приглашён, остаётся посторонним).</summary>
    private async Task<(string ProjectId, string SessionId)> SharedChatAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(Member(), true);
        var dir = Path.Combine(_factory.TempDir, "sc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Чаты" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        var chat = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", name = "Чат владельца" });
        chat.EnsureSuccessStatusCode();
        var sessionId = IdOf(await chat.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return (projectId, sessionId);
    }

    private static async Task<List<string>> ChatIdsAsync(HttpClient client, string projectId)
    {
        var r = await client.GetAsync($"/api/projects/{projectId}/sessions");
        r.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync())
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();
    }

    private static void ShouldBeRefused(HttpResponseMessage r, string what) =>
        r.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.NotFound], what);

    // ── чтение участника ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ВидитСписокЧатов_ВключаяСозданныйДоПриглашения(string role)
    {
        var (projectId, sessionId) = await SharedChatAsync(role);

        (await ChatIdsAsync(Member(), projectId)).Should().Contain(sessionId);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ОткрываетЧатИИсторию_200(string role)
    {
        var (projectId, sessionId) = await SharedChatAsync(role);
        var member = Member();

        var card = await member.GetAsync($"/api/chats/{sessionId}");
        card.StatusCode.Should().Be(HttpStatusCode.OK, "карточка чата — чтение, видимость по точке доступа");
        IdOf(await card.Content.ReadAsStringAsync()).Should().Be(sessionId);

        (await member.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/history"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.GetAsync($"/api/sessions/{sessionId}/history"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.GetAsync($"/api/sessions/{sessionId}/pending"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Участник_СчётчикЧатовПроекта_ВидитЧатВладельца()
    {
        var (projectId, _) = await SharedChatAsync("reader");

        var project = await Member().GetAsync($"/api/projects/{projectId}");
        project.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("sessionCount").GetInt32().Should().BeGreaterThan(0);
    }

    // ── запись и ходы — не участника ─────────────────────────────────────────────

    // Читатель после QA-P1 — как и прежде, ничего: и переименование, и AI-переименование для него
    // 403 project_scope (права записи контента у читателя нет), клон/продолжение/передача/значок/
    // контекст/настройки — прежние 403/404.
    [Fact]
    public async Task Читатель_НеПереименовываетНеКлонируетНеПродолжаетНеПишет()
    {
        var (projectId, sessionId) = await SharedChatAsync("reader");
        var member = Member();
        var before = await ChatIdsAsync(Owner(), projectId);

        // Создание чата здесь нет: с S2d-1c редактор создаёт (и становится автором), читатель — 403;
        // обе ветки проверяет SharedProjectChatCreateTests.
        ShouldBeRefused(await member.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}",
            new { name = "Перебил участник" }), "переименование");
        ShouldBeRefused(await member.PostAsync($"/api/projects/{projectId}/sessions/icon-batch", null),
            "смена значков");
        ShouldBeRefused(await member.PostAsJsonAsync($"/api/chats/{sessionId}/branch",
            new { userMessageIndex = 0, anchorText = "x", include = "none" }), "клон");
        ShouldBeRefused(await member.PostAsJsonAsync($"/api/chats/{sessionId}/continue", new { }), "продолжение");
        ShouldBeRefused(await member.PostAsync($"/api/chats/{sessionId}/handoff", null), "передача");
        ShouldBeRefused(await member.PostAsync($"/api/chats/{sessionId}/retitle", null), "AI-переименование");
        ShouldBeRefused(await member.PostAsJsonAsync($"/api/sessions/{sessionId}/messages",
            new { text = "привет", wait = "none" }), "отправка сообщения");
        ShouldBeRefused(await member.GetAsync($"/api/chats/{sessionId}/context"), "стор контекста чата");
        ShouldBeRefused(await member.PutAsJsonAsync($"/api/chats/{sessionId}/loop", new { enabled = true }),
            "запись настроек чата");

        // Отказ не оставил следа: ни нового чата (ни у владельца, ни у участника), ни правки имени
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before);
        var card = await Owner().GetAsync($"/api/chats/{sessionId}");
        JsonSerializer.Deserialize<JsonElement>(await card.Content.ReadAsStringAsync())
            .GetProperty("name").GetString().Should().Be("Чат владельца");
        (await member.GetAsync("/api/chats")).StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement>(await (await member.GetAsync("/api/chats")).Content.ReadAsStringAsync())
            .EnumerateArray().Should().BeEmpty("в личное пространство участника ничего не утекло");
    }

    // QA-P1 (решение Андрея, продуктовый вопрос 2 плана v2): редактор переименовывает чат владельца —
    // право записи контента, а не авторство. AI-переименование тоже его (гейт пройден: ручка отвечает
    // по делу — в чате нет сообщений, 502, а не 403/404). Прочее — по-прежнему не он: клон,
    // продолжение, передача, значки, контекст, настройки чата.
    [Fact]
    public async Task Редактор_ПереименовываетЧатВладельца_ПрочегоЕмуНельзя()
    {
        var (projectId, sessionId) = await SharedChatAsync("editor");
        var member = Member();
        var before = await ChatIdsAsync(Owner(), projectId);

        ShouldBeRefused(await member.PostAsync($"/api/projects/{projectId}/sessions/icon-batch", null),
            "смена значков");
        ShouldBeRefused(await member.PostAsJsonAsync($"/api/chats/{sessionId}/branch",
            new { userMessageIndex = 0, anchorText = "x", include = "none" }), "клон");
        ShouldBeRefused(await member.PostAsJsonAsync($"/api/chats/{sessionId}/continue", new { }), "продолжение");
        ShouldBeRefused(await member.PostAsync($"/api/chats/{sessionId}/handoff", null), "передача");
        // Право записи пройдено: до тела ручки дошло (там «в чате ещё нет сообщений», 502) —
        // отказа права (403) или видимости (404) нет; фейковый раннер TestWebApplicationFactory
        // network не делает
        var retitle = await member.PostAsync($"/api/chats/{sessionId}/retitle", null);
        var retitleCode = (int)retitle.StatusCode;
        retitleCode.Should().NotBe(403, $"retitle редактора отказан правом: {retitleCode}")
            .And.NotBe(404, $"retitle редактора не видим: {retitleCode}");

        // Ручное переименование чужого общего чата — 200, имя пишется
        (await member.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}",
            new { name = "Перебил редактор" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var card = await Owner().GetAsync($"/api/chats/{sessionId}");
        JsonSerializer.Deserialize<JsonElement>(await card.Content.ReadAsStringAsync())
            .GetProperty("name").GetString().Should().Be("Перебил редактор");

        // Отправка в чат — право RunTurn (S2a-10), здесь не меняется
        (await member.PostAsJsonAsync($"/api/sessions/{sessionId}/messages",
            new { text = "привет", wait = "none" })).IsSuccessStatusCode.Should().BeTrue();
        ShouldBeRefused(await member.GetAsync($"/api/chats/{sessionId}/context"), "стор контекста чата");
        ShouldBeRefused(await member.PutAsJsonAsync($"/api/chats/{sessionId}/loop", new { enabled = true }),
            "запись настроек чата");

        // Отказанные ручки не оставили следа: ни нового чата, ничего в личном пространстве
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before);
        (await member.GetAsync("/api/chats")).StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement>(await (await member.GetAsync("/api/chats")).Content.ReadAsStringAsync())
            .EnumerateArray().Should().BeEmpty("в личное пространство редактора ничего не утекло");
    }

    // ── «сужение, безопасно»: внутри снова GetOwned → участнику 404 (кроме архива и ход-входов) ──

    [Fact]
    public async Task Участник_ДействияНадЧатомСвнутреннимGetOwned_404()
    {
        var (_, sessionId) = await SharedChatAsync("editor");
        var member = Member();

        (await member.PostAsync($"/api/chats/{sessionId}/window-1m/drop", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        // архив чата здесь нет: с QA-P1 это право записи контента (строка «чат: архив» матрицы
        // ролей и ChatWriteRightsEndpointTests), а не действий внутреннего GetOwned
        // ход-входы (RunTurn, S1-7): чат участнику виден, вести ход ему нельзя — 403, а не 404
        (await member.PutAsJsonAsync($"/api/chats/{sessionId}/team-implement", new { })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await member.PutAsync($"/api/chats/{sessionId}/team-implement/stop", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync($"/api/chats/{sessionId}/files/upload", new { })).StatusCode
            .Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.UnsupportedMediaType, HttpStatusCode.BadRequest]);
    }

    // ── невидимому — 404 ─────────────────────────────────────────────────────────

    private static async Task ReadsAreNotFoundAsync(HttpClient client, string projectId, string sessionId)
    {
        (await client.GetAsync($"/api/projects/{projectId}/sessions")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/history")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/chats/{sessionId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/sessions/{sessionId}/history")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/sessions/{sessionId}/pending")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Посторонний_НеВидитЧатов_404()
    {
        var (projectId, sessionId) = await SharedChatAsync(role: null);
        await ReadsAreNotFoundAsync(Member(), projectId, sessionId);
    }

    [Fact]
    public async Task ФлагУчастникаВыключен_404()
    {
        var (projectId, sessionId) = await SharedChatAsync("editor");
        await SetFlagAsync(Member(), false);
        try { await ReadsAreNotFoundAsync(Member(), projectId, sessionId); }
        finally { await SetFlagAsync(Member(), true); }
    }

    [Fact]
    public async Task РубильникВыключен_УчастникНеВидит_404()
    {
        var (projectId, sessionId) = await SharedChatAsync("editor");
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        config[SharedProjectsGate.SwitchKey] = "false";
        try { await ReadsAreNotFoundAsync(Member(), projectId, sessionId); }
        finally { config[SharedProjectsGate.SwitchKey] = "true"; }
    }

    // ── владелец: прежний путь ───────────────────────────────────────────────────

    [Fact]
    public async Task Владелец_ЧитаетИПереименовываетКакРаньше()
    {
        var (projectId, sessionId) = await SharedChatAsync("reader");
        var owner = Owner();

        (await ChatIdsAsync(owner, projectId)).Should().Contain(sessionId);
        (await owner.GetAsync($"/api/chats/{sessionId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.GetAsync($"/api/sessions/{sessionId}/history")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}", new { name = "Новое имя" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ЧатВнеПроекта_ВиденТолькоСвоемуВладельцу()
    {
        var owner = Owner();
        var chat = await owner.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        var id = IdOf(await chat.Content.ReadAsStringAsync());

        (await owner.GetAsync($"/api/chats/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Member().GetAsync($"/api/chats/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
