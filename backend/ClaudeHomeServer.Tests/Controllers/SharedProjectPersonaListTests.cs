using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// W3-B2 (разрез docs/research/shared-projects-personas-cut-2026-11.md §3.1 строки «GET /api/personas
/// без scope», «GET ?scope=project», «GET ?scope=context», «POST /match»; И-2, И-3): списки персон
/// и /match по проекту и ключу арендатора.
///
/// Участник общего проекта (редактор и читатель): в scope=project и scope=context — только команда
/// проекта; глобальная и сферная персона владельца не видна ни в одном списке (И-3). Без scope —
/// свои персоны + проектные персоны проектов, где вызывающий участник, без личных персон владельца.
/// POST /match с projectId — кандидаты команды проекта под ключом арендатора. Нет доступа к проекту
/// (в том числе «проекта нет») — 404, как несуществующему.
/// scope=context и /match без projectId — личное пространство вызывающего, поведение не изменилось.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaListTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    // Стаб дешёвой модели для /match: ответ — id, который «модель» назвала (управляется на вызов).
    // Встроенный в хост стаб отвечает «[]» — /match давал бы null при любом пуле кандидатов,
    // здесь пул проверяется именно ответом. STATIC: экземпляр тестового класса создаётся на каждый
    // тест, а хост строится один раз — на первом экземпляре; раннер должен быть тем же объектом,
    // что зарегистрирован в хосте (методы одного класса xUnit гоняет последовательно).
    private static readonly StubMatchRunner _match = new();

    public SharedProjectPersonaListTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _factory.ExtraServices = services => services.AddSingleton<ICheapTextRunner>(_match);
    }

    private sealed class StubMatchRunner : ICheapTextRunner
    {
        public string Answer { get; set; } = "";
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(Answer);
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(Answer);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private async Task<string> CreateProjectDirAsync(string tag)
    {
        var dir = Path.Combine(_factory.TempDir, tag + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<string> CreateProjectAsync(HttpClient client, string tag)
    {
        var created = await client.PostAsJsonAsync("/api/projects",
            new { name = tag + Guid.NewGuid().ToString("N")[..6], rootPath = await CreateProjectDirAsync(tag) });
        created.EnsureSuccessStatusCode();
        return IdOf(await created.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateGlobalPersonaAsync(HttpClient client, string name)
    {
        var r = await client.PostAsJsonAsync("/api/personas", new { name, scope = "global" });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateProjectPersonaAsync(HttpClient client, string name, string projectId)
    {
        var r = await client.PostAsJsonAsync("/api/personas", new { name, scope = "project", projectId });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    /// <summary>Сида: проект владельца с 4 проектными и 2 глобальными персонами; role —
    /// приглашённый участник этой роли (null — участников нет, проект личный).</summary>
    private async Task<(string ProjectId, List<string> Team, List<string> OwnerGlobals)> FixtureAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        if (role is not null) await SetFlagAsync(Member());
        var projectId = await CreateProjectAsync(owner, "Списки");
        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var uniq = Guid.NewGuid().ToString("N")[..6];
        var team = new List<string>();
        for (var i = 0; i < 4; i++)
            team.Add(await CreateProjectPersonaAsync(owner, $"Бортовой-{i}-{uniq}", projectId));
        var globals = new List<string>();
        for (var i = 0; i < 2; i++)
            globals.Add(await CreateGlobalPersonaAsync(owner, $"Глобальная-Владельца-{i}-{uniq}"));
        return (projectId, team, globals);
    }

    private static async Task<List<string>> IdsAsync(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return (await BodyAsync(r)).EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()!).ToList();
    }

    // ─── scope=project / scope=context: участник видит команду проекта, не личное владельца ───

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task УчастникПоScopeProject_ПолучаетВсюКомандуПроекта_БезГлобальныхВладельца(string role)
    {
        var (projectId, team, ownerGlobals) = await FixtureAsync(role);

        var ids = await IdsAsync(await Member().GetAsync($"/api/personas?scope=project&projectId={projectId}"));

        ids.Should().BeEquivalentTo(team, $"участник ({role}) получает все проектные персоны проекта (W3-B2)");
        ids.Intersect(ownerGlobals).Should().BeEmpty("личные персоны владельца не в списке проекта (И-3)");
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task УчастникПоScopeContext_ПолучаетТолькоКомандуПроекта(string role)
    {
        var (projectId, team, _) = await FixtureAsync(role);

        var ids = await IdsAsync(await Member().GetAsync($"/api/personas?scope=context&projectId={projectId}"));

        // Ровно команда: ни глобальных, ни сферных владельца (И-3). Если участник ведётся через
        // GetForContextWithExtras(T) с глобальными — этот тест красный (мутация c3 плана).
        ids.Should().BeEquivalentTo(team,
            $"участнику ({role}) в контексте проекта — только команда проекта, без глобальных/сферных владельца (И-3)");
    }

    [Theory]
    [InlineData("project")]
    [InlineData("context")]
    public async Task ПостороннийПоПроектнымСкоупам_404(string scope)
    {
        var (projectId, _, _) = await FixtureAsync(role: null);

        var r = await Member().GetAsync($"/api/personas?scope={scope}&projectId={projectId}");

        r.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"нет доступа к проекту — существование не раскрываем ({scope})");
    }

    // ─── без scope: свои + проектные персоны проектов участника ───

    [Fact]
    public async Task БезScope_Участник_СвоиПлюсПроектныеОбщегоПроекта_БезЛичныхПерсонВладельца()
    {
        var (_, team, ownerGlobals) = await FixtureAsync("editor");
        var ownGlobal = await CreateGlobalPersonaAsync(Member(), "Своя-Глобальная-" + Guid.NewGuid().ToString("N")[..6]);
        // своя проекная персона — «свои» в том же списке (свой проект участника)
        var ownProjectId = await CreateProjectAsync(Member(), "ЛичныйПроектУчастника");
        var ownProjectPersona = await CreateProjectPersonaAsync(Member(),
            "Своя-Проектная-" + Guid.NewGuid().ToString("N")[..6], ownProjectId);

        var ids = await IdsAsync(await Member().GetAsync("/api/personas"));

        ids.Should().Contain(ownGlobal, "свои глобальные — как раньше");
        ids.Should().Contain(ownProjectPersona, "свои проектные — как раньше");
        ids.Should().Contain(team,
            "проектные персоны проектов, где вызывающий участник, — в списке (W3-B2)");
        ids.Intersect(ownerGlobals).Should().BeEmpty(
            "личные (глобальные/сферные) персоны владельца участнику не видны (И-3)");
    }

    // ─── POST /match: кандидаты по проекту — команда проекта под ключом арендатора ───

    [Fact]
    public async Task MatchУчастникаПоПроекту_КандидатыТолькоИзКомандыПроекта()
    {
        var (projectId, team, ownerGlobals) = await FixtureAsync("editor");

        // «Модель» называет глобальную персону владельца: её нет среди кандидатов — ответ none
        _match.Answer = ownerGlobals[0];
        var r1 = await Member().PostAsJsonAsync("/api/personas/match", new { task = "Задача борту", projectId });
        r1.EnsureSuccessStatusCode();
        (await BodyAsync(r1)).GetProperty("personaId").ValueKind.Should().Be(JsonValueKind.Null,
            "глобальная персона владельца — не кандидат участника (И-3)");

        // «Модель» называет проектную персону: она кандидат
        _match.Answer = team[0];
        var r2 = await Member().PostAsJsonAsync("/api/personas/match", new { task = "Задача борту", projectId });
        r2.EnsureSuccessStatusCode();
        (await BodyAsync(r2)).GetProperty("personaId").GetString().Should().Be(team[0],
            "кандидаты /match с projectId — команда проекта (разрез §3.1)");
    }

    [Fact]
    public async Task MatchВладельцаПоСвоемуПроекту_КандидатыКомандаПроекта()
    {
        // решение разреза §3.1 строки «POST /match»: с projectId кандидаты — команда проекта
        // и для владельца («в общем проекте их рамка всё равно режет»); без projectId — прежнее.
        var (projectId, team, ownerGlobals) = await FixtureAsync(role: null);

        _match.Answer = ownerGlobals[0];
        var r1 = await Owner().PostAsJsonAsync("/api/personas/match", new { task = "Задача борту", projectId });
        r1.EnsureSuccessStatusCode();
        (await BodyAsync(r1)).GetProperty("personaId").ValueKind.Should().Be(JsonValueKind.Null,
            "с projectId кандидат — только команда проекта, глобальные в /match по проекту не участвуют");

        _match.Answer = team[1];
        var r2 = await Owner().PostAsJsonAsync("/api/personas/match", new { task = "Задача борту", projectId });
        r2.EnsureSuccessStatusCode();
        (await BodyAsync(r2)).GetProperty("personaId").GetString().Should().Be(team[1]);
    }

    [Fact]
    public async Task MatchБезПроекта_СвоиГлобальныеКакРаньше()
    {
        var (projectId, _, _) = await FixtureAsync("editor");
        var ownGlobal = await CreateGlobalPersonaAsync(Member(), "Своя-Глобальная-" + Guid.NewGuid().ToString("N")[..6]);

        _match.Answer = ownGlobal;
        var r = await Member().PostAsJsonAsync("/api/personas/match", new { task = "Задача вне проекта" });
        r.EnsureSuccessStatusCode();
        (await BodyAsync(r)).GetProperty("personaId").GetString().Should().Be(ownGlobal,
            "/match без projectId — личное пространство вызывающего, путь не тронут");

        _match.Answer = projectId;
        var r2 = await Member().PostAsJsonAsync("/api/personas/match", new { task = "Задача вне проекта" });
        r2.EnsureSuccessStatusCode();
        (await BodyAsync(r2)).GetProperty("personaId").ValueKind.Should().Be(JsonValueKind.Null,
            "без проекта чужие проекные персоны кандидатами не становятся");
    }

    [Fact]
    public async Task КомпозерБезПроекта_Context_СвоиГлобальныеКакРаньше()
    {
        var (_, team, ownerGlobals) = await FixtureAsync("editor");
        var ownGlobal = await CreateGlobalPersonaAsync(Member(), "Своя-Глобальная-" + Guid.NewGuid().ToString("N")[..6]);

        var ids = await IdsAsync(await Member().GetAsync("/api/personas?scope=context"));

        ids.Should().Contain(ownGlobal, "context без projectId — свои глобальные, путь не тронут");
        ids.Should().NotContain(team, "без проекта команда чужого проекта в контекст не входит");
        ids.Intersect(ownerGlobals).Should().BeEmpty("личных персон владельца здесь тоже нет (И-3)");
    }
}
