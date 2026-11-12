using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Навыки персоны — библиотека владельца (shared-projects, W3-B5c, разрез
/// docs/research/shared-projects-personas-cut-2026-11.md §3.2, строка SkillsController).
/// До шага оба пути читали персону по sub вызывающего: участник общего проекта не мог ни
/// поставить навык проектной персоне (404 на `personas.Get`), ни попросить подбор под неё
/// (404 в `SkillSuggestService`) — при том, что карточку персоны и ход с ней он ведёт.
///
/// После: допуск решает право `Configure` по персоне — `[ProjectScope(From = Persona)]` для
/// POST /api/personas/{id}/skills и явный `ResolvePersona` в теле для POST /api/skills/suggest
/// (id в DTO — фильтр его не видит); ключ записи и подборки — арендатор персоны. Грань отказа
/// та же, что у фильтра: редактору и читателю проектной персоны 403 project_scope (её
/// существование им и так известно), постороннему 404 (существование чужой персоны не
/// подтверждаем). Своя глобальная персона — прежний путь по владению; чужая глобальная — 404.
///
/// Установка навыка и каталог реестра — заглушка CLI (`Skills:Command` с нулевым кодом, тот же
/// приём, что `Markitdown:Command` в FilesControllerEditorDenialsTests): тест меряет ДОПУСК, а
/// не настоящий «npx skills add» из реестра. Поэтому `bound` после заглушки не проверяем —
/// она зависит от глобального каталога машины (`SkillsService.cs:35-36` — `~/.claude/skills`
/// хоста), а не от этого контракта. «Без записи» в тесте отказа меряется по телу ответа:
/// у отказа нет поля `installed` (до установки дело не дошло); привязку по персоне не меряем
/// — при навыке, которого нет в каталоге, `ValidateAsync` не пишет её даже при снятом гейте
/// (`PersonaBindingsService.cs:1186-1188`), и такая проверка была бы зелёной впустую.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaSkillsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectPersonaSkillsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        // Заглушка навыков: на Windows — батч с нулевым кодом (SkillsCliService на Windows
        // обвязывает DirectCommand как cmd.exe /c &lt;команда&gt;, поэтому это должен быть один
        // токен), на Linux — /bin/true. Аргументы CLI заглушка игнорирует.
        _factory.ExtraConfig["Skills:Command"] = OperatingSystem.IsWindows()
            ? WindowsSkillStub()
            : "/bin/true";
    }

    private string WindowsSkillStub()
    {
        var file = Path.Combine(_factory.TempDir, "skills-stub.bat");
        Directory.CreateDirectory(_factory.TempDir);
        File.WriteAllText(file, "@echo off\r\nexit 0\r\n");
        return file;
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Проект владельца с проектной персоной этого проекта. memberRole = null —
    /// участников нет: второй пользователь для такого проекта посторонний.</summary>
    private async Task<string> ProjectWithPersonaAsync(string? memberRole)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        if (memberRole is not null) await SetFlagAsync(Member());

        var dir = Path.Combine(_factory.TempDir, "skp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Навыки" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (memberRole is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role = memberRole }))
                .EnsureSuccessStatusCode();

        var persona = await owner.PostAsJsonAsync("/api/personas",
            new { name = "Проектная-" + Guid.NewGuid().ToString("N")[..6], scope = "project", projectId });
        persona.EnsureSuccessStatusCode();
        return IdOf(await persona.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateGlobalPersonaAsync(HttpClient client)
    {
        var r = await client.PostAsJsonAsync("/api/personas",
            new { name = "Глобальная-" + Guid.NewGuid().ToString("N")[..6], scope = "global" });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> InstallSkillAsync(HttpClient client, string personaId) =>
        client.PostAsJsonAsync($"/api/personas/{personaId}/skills",
            new { source = "anthropics/skills", skill = "pdf" });

    private static Task<HttpResponseMessage> SuggestForPersonaAsync(HttpClient client, string personaId) =>
        client.PostAsJsonAsync("/api/skills/suggest", new { personaId });

    // ── POST /api/personas/{id}/skills ───────────────────────────────────────────────

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ПоставитьНавыкПроектнойПерсоне_403СКодомИБезЗаписи(string role)
    {
        var personaId = await ProjectWithPersonaAsync(role);

        var r = await InstallSkillAsync(Member(), personaId);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();

        // «Без записи» — по телу ответа, детерминированно и независимо от глобального каталога
        // навыков машины: до установки дело не дошло — в отказе нет поля installed (у 200
        // владельца оно есть). Привязку по персоне здесь не меряем: при навыке, которого нет
        // в каталоге, ValidateAsync не пишет её даже при снятом гейте
        // (PersonaBindingsService.cs:1186-1188), и проверка была бы зелёной впустую. Зубы у
        // этой — с целевой поломки: ослабленное право (Configure→Read) даёт 200
        // {installed, bound:false, warning} (SkillsController.cs:211-213) — и краснеет именно
        // она, а не только код ответа.
        body.TryGetProperty("installed", out _).Should().BeFalse(
            "отказ не должен нести installed: до установки дело не дошло");

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"{role} видит персону своего проекта, но право Configure — только у владельца");
        body.GetProperty("code").GetString()
            .Should().Be(ProjectScopeAttribute.ErrorCode, "тот же машинный код, что у [ProjectScope]");
    }

    [Fact]
    public async Task Владелец_ПоставитьНавыкПроектнойПерсоне_Разрешён()
    {
        var personaId = await ProjectWithPersonaAsync("editor");

        var r = await InstallSkillAsync(Owner(), personaId);

        // 200 — путь владельца жив (с bound заглушка CLI не мерится: она не ставит файл)
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("installed", out _).Should()
            .BeTrue("ответ владельца — прежней формы");
    }

    [Fact]
    public async Task Посторонний_ПоставитьНавыкПроектнойПерсоне_404()
    {
        var personaId = await ProjectWithPersonaAsync(memberRole: null);

        var r = await InstallSkillAsync(Member(), personaId);

        r.StatusCode.Should().Be(HttpStatusCode.NotFound, "чужая персона: существования не подтверждаем");
    }

    // ── POST /api/skills/suggest с personaId (id в DTO — резолв в теле) ──────────────

    [Fact]
    public async Task Редактор_ПодборНавыковПоПроектнойПерсоне_403СКодом()
    {
        var personaId = await ProjectWithPersonaAsync("editor");

        var r = await SuggestForPersonaAsync(Member(), personaId);

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "до шага был 404 (персона не нашлась по sub вызывающего); теперь — тот же 403, что у фильтра");
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()
            .Should().Be(ProjectScopeAttribute.ErrorCode);
    }

    [Fact]
    public async Task Владелец_ПодборНавыковПоПроектнойПерсоне_Разрешён()
    {
        var personaId = await ProjectWithPersonaAsync("editor");

        var r = await SuggestForPersonaAsync(Owner(), personaId);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidates").ValueKind
            .Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Посторонний_ПодборНавыковПоПерсоне_404()
    {
        var personaId = await ProjectWithPersonaAsync(memberRole: null);

        var r = await SuggestForPersonaAsync(Member(), personaId);

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── прежнее поведение: своя и чужая глобальная персона ───────────────────────────

    [Fact]
    public async Task СвояГлобальнаяПерсона_НавыкИПодбор_КакПрежде()
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        var personaId = await CreateGlobalPersonaAsync(owner);

        var install = await InstallSkillAsync(owner, personaId);
        install.StatusCode.Should().Be(HttpStatusCode.OK, "своя глобальная персона — по владению");

        var suggest = await SuggestForPersonaAsync(owner, personaId);
        suggest.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ЧужаяГлобальнаяПерсона_404аНе403()
    {
        var personaId = await ProjectWithPersonaAsync("editor");
        var global = await CreateGlobalPersonaAsync(Owner());

        // Глобальная персона владельца участнику не видна никогда (И-3): здесь нет грани
        // «виден, но права нет» — отказ тот же 404, что был до шага.
        (await InstallSkillAsync(Member(), global)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SuggestForPersonaAsync(Member(), global)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await InstallSkillAsync(Member(), personaId)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "проектная персона того же проекта — 403, чужая глобальная — 404: грань разная");
    }
}
