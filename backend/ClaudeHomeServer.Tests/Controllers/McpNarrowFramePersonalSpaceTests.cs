using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Узкая рамка хода в платформенном MCP (S2a-3b, решение 29.10; дополнения по ревью R-S2a-3b
/// от 30.10): в чате ОБЩЕГО проекта личное пространство владельца закрыто всем, включая самого
/// владельца. Здесь — память (личная память глобальной и сферной персон, полка сферы), персоны
/// (глобальные и сферные персоны, управление персонами, AI-подбор привязок), knowledge_search
/// (датасеты вне проекта чата — той же формулой, что Dify), уведомления (личный ящик),
/// Higgsfield (аккаунт: деньги и история генераций), плагины (черновики владельца),
/// Dify (датасеты вне проекта чата). Контекст проекта остаётся:
/// командная память, проектная команда чата, уведомление о работе проекта, база этого проекта.
/// Вердикт читается из ITurnFrameSource по заголовку X-Caller-Session-Id; «нет заголовка /
/// чат не найден» — узкая рамка (fail-closed). В отказе нет id и названий из личных данных
/// (маркеры PERSONAL-*). Dify здесь ФАКТИКА: свой стаб-хендлер, тесты не ходят в сеть.
/// Пишущие вызовы общего проекта идут под названным актором — ход в чате проекта начинает
/// владелец (<see cref="StartOwnerTurnAsync"/>): после О2 финального ревью «Общих проектов»
/// (1af1d702a) безымянный вызов отрезан ГЕЙТОМ актора в транспортном контроллере и не доходит
/// до текста отказа рамки, который здесь сверяется.
/// </summary>
[Trait("Category", "Host")]
public class McpNarrowFramePersonalSpaceTests : IDisposable
{
    private readonly FrameDifyHandler Dify = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public McpNarrowFramePersonalSpaceTests()
    {
        _factory = new TestWebApplicationFactory
        {
            // IsConfigured=true без сети: тулсет и каталог ходят в записывающий хендлер
            ExtraServices = services => services.AddSingleton<KnowledgeService>(sp =>
                new KnowledgeService(
                    new StubHttpClientFactory(Dify),
                    Options.Create(new DifyOptions { ApiUrl = "http://dify-fake.test", ApiKey = "test-key" }),
                    sp.GetRequiredService<WorkspaceKnowledgeStore>())),
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<string> CreateProjectAsync(string name, string? groupId = null)
    {
        // groupId при создании — та же форма, что у SphereMemoryToolsetTests: проект сразу
        // входит в сферу
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name, groupId });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreatePersonaAsync(string name, string scope, string? projectId = null)
    {
        var resp = await _client.PostAsJsonAsync("/api/personas",
            new { name, role = "Роль", scope, projectId });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// Вызов инструмента от лица чата: заголовок X-Caller-Session-Id — то, по которому рамка
    /// читает вердикт (в бою его ставит конфиг хода). chatId=null — вызов без заголовка.
    /// </summary>
    private async Task<(string Text, bool IsError)> CallAsync(string url, string tool, object args, string? chatId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), Encoding.UTF8, "application/json"),
        };
        if (chatId is not null) req.Headers.Add("X-Caller-Session-Id", chatId);
        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "MCP отвечает 200 даже на отказ — isError в теле");
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
    }

    /// <summary>
    /// Начать в чате хода владельца — явный инициатор подачи (S2a-1), без заголовка вызывателя:
    /// подача должна быть прямым ходом человека, а не продолжением чужого чата. Нужен пишущим
    /// вызовам общего проекта: после О2 финального ревью «Общих проектов» (1af1d702a) безымянный
    /// ход общего проекта не имеет ни одного права и отрезан ГЕЙТОМ актора в транспортном
    /// контроллере (McpTransportController) — ещё до того текста отказа рамки, который эти тесты
    /// и сверяют. Рамка при этом не меняется: общий чат общего проекта остаётся Narrow и
    /// владельцу (решение 29.10), отказывает ровно она же.
    /// </summary>
    private async Task StartOwnerTurnAsync(string chatId) =>
        (await _client.PostAsJsonAsync($"/api/sessions/{chatId}/messages",
            new { text = "начни работу", wait = "none" })).EnsureSuccessStatusCode();

    /// <summary>
    /// Личный проект с маркерами (глобальная персона «PERSONAL-…» с записью в личной памяти)
    /// и общий проект (участник-редактор) с чатом. Плюс проектная персона общего проекта.
    /// </summary>
    private async Task<Fixture> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var personalProject = await CreateProjectAsync($"PERSONAL-проект-{tag}");
        var personalChat = await CreateSessionAsync(personalProject);

        var globalPersona = await CreatePersonaAsync($"PERSONAL-персона-{tag}", "global");
        // Запись в личной памяти — от лица чата личного проекта (широкая рамка): она маркер
        var (seedText, seedErr) = await CallAsync($"/mcp/memory/{globalPersona}/-", "memory_remember",
            new { type = "semantic", text = $"PERSONAL-секрет-{tag}" }, personalChat);
        seedErr.Should().BeFalse(seedText);

        // Чат до участников: в общем проекта без руководителя провижна владельца нет (W4-B1a
        // доработка 2, финал ed64271c); состояние фикстуры прежнее.
        var sharedProject = await CreateProjectAsync($"Общий-{tag}");
        var sharedChat = await CreateSessionAsync(sharedProject);
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(sharedProject,
            [new ProjectMember { UserId = "участник-" + tag, Role = ProjectRole.Editor }]);
        // Пишущие вызовы этого чата идут под названным актором (см. StartOwnerTurnAsync)
        await StartOwnerTurnAsync(sharedChat);
        var projectPersona = await CreatePersonaAsync($"КОМАНДА-{tag}", "project", sharedProject);

        return new Fixture(tag, personalProject, personalChat, globalPersona,
            sharedProject, sharedChat, projectPersona);
    }

    private sealed record Fixture(string Tag, string PersonalProject, string PersonalChat, string GlobalPersona,
        string SharedProject, string SharedChat, string ProjectPersona)
    {
        public void AssertNoPersonal(string text) => text.Should()
            .NotContain("PERSONAL").And.NotContain(GlobalPersona)
            .And.NotContain(PersonalProject).And.NotContain(PersonalChat);
    }

    /// <summary>
    /// Сфера с проектами владельца: общий проект сферы (участник-редактор) с чатом, личный
    /// проект сферы с чатом и персона этой сферы (scope=sphere через REST — через MCP сферная
    /// зона не создаётся). Фикстура решения «сфера — личное пространство владельца» (R-S2a-3b,
    /// п.4): её полка была закрыта и раньше, теперь — память и persona_ask её персон.
    /// </summary>
    private async Task<(string SharedInSphere, string SharedSphereChat, string PersonalInSphere,
        string PersonalSphereChat, string SpherePersona)> ArrangeSphereAsync(Fixture f)
    {
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var sphereResp = await _client.PostAsJsonAsync("/api/project-groups", new { name = $"сфера-{f.Tag}" });
        sphereResp.EnsureSuccessStatusCode();
        var sphere = IdOf(JsonSerializer.Deserialize<JsonElement>(await sphereResp.Content.ReadAsStringAsync()));
        var sharedInSphere = await CreateProjectAsync($"Общий-сфера-{f.Tag}", sphere);
        var sharedSphereChat = await CreateSessionAsync(sharedInSphere);
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(sharedInSphere,
            [new ProjectMember { UserId = "участник-" + f.Tag, Role = ProjectRole.Editor }]);
        var personalInSphere = await CreateProjectAsync($"Личный-сфера-{f.Tag}", sphere);
        var personalSphereChat = await CreateSessionAsync(personalInSphere);
        // Тот же порядок, что в ArrangeAsync: общий чат сферы — под названным актором
        await StartOwnerTurnAsync(sharedSphereChat);
        var personaResp = await _client.PostAsJsonAsync("/api/personas",
            new { name = $"СФЕРА-персона-{f.Tag}", role = "Роль", scope = "sphere", sphereId = sphere });
        personaResp.EnsureSuccessStatusCode();
        var spherePersona = IdOf(JsonSerializer.Deserialize<JsonElement>(await personaResp.Content.ReadAsStringAsync()));
        return (sharedInSphere, sharedSphereChat, personalInSphere, personalSphereChat, spherePersona);
    }

    // --- memory ---

    [Theory]
    [InlineData("memory_remember")]
    [InlineData("memory_search")]
    [InlineData("memory_list")]
    public async Task Memory_УзкаяРамка_ЛичнаяПамятьГлобальнойПерсоны_Отказ(string tool)
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/memory/{f.GlobalPersona}/{f.SharedProject}", tool,
            new { query = f.Tag, type = "semantic", text = "x" }, f.SharedChat);
        isError.Should().BeTrue(text);
        text.Should().Contain("Личная память глобальной персоны закрыта");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Memory_УзкаяРамка_ПамятьСферы_Отказ_ВЛичномЧатеРаботает()
    {
        var f = await ArrangeAsync();
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var sphereResp = await _client.PostAsJsonAsync("/api/project-groups", new { name = $"сфера-{f.Tag}" });
        sphereResp.EnsureSuccessStatusCode();
        var sphere = IdOf(JsonSerializer.Deserialize<JsonElement>(await sphereResp.Content.ReadAsStringAsync()));
        var sharedInSphere = await CreateProjectAsync($"Общий-сфера-{f.Tag}", sphere);
        var sharedSphereChat = await CreateSessionAsync(sharedInSphere);
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(sharedInSphere,
            [new ProjectMember { UserId = "участник-" + f.Tag, Role = ProjectRole.Editor }]);
        var personalInSphere = await CreateProjectAsync($"Личный-сфера-{f.Tag}", sphere);
        var personalSphereChat = await CreateSessionAsync(personalInSphere);

        var (denied, deniedErr) = await CallAsync($"/mcp/memory/-/{sharedInSphere}", "sphere_memory_list",
            new { }, sharedSphereChat);
        deniedErr.Should().BeTrue(denied);
        denied.Should().Contain("Память сферы закрыта");
        f.AssertNoPersonal(denied);

        // Анти-тест: та же полка в личном чате (широкая рамка) работает — отказывала рамка,
        // не флаг и не членство проекта в сфере
        var (ok, okErr) = await CallAsync($"/mcp/memory/-/{personalInSphere}", "sphere_memory_list",
            new { }, personalSphereChat);
        okErr.Should().BeFalse(ok);
    }

    [Fact]
    public async Task Memory_УзкаяРамка_КоманднаяПамятьПроекта_НеПоломана()
    {
        var f = await ArrangeAsync();
        var (write, writeErr) = await CallAsync($"/mcp/memory/-/{f.SharedProject}", "team_memory_remember",
            new { text = $"Договорённость проекта {f.Tag}" }, f.SharedChat);
        writeErr.Should().BeFalse(write);
        var (list, listErr) = await CallAsync($"/mcp/memory/-/{f.SharedProject}", "team_memory_list",
            new { }, f.SharedChat);
        listErr.Should().BeFalse(list);
        list.Should().Contain(f.Tag);
    }

    [Fact]
    public async Task Memory_УзкаяРамка_ПамятьПроектнойПерсоны_Доступна()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/memory/{f.ProjectPersona}/{f.SharedProject}",
            "memory_remember", new { type = "semantic", text = "рабочий факт проекта" }, f.SharedChat);
        isError.Should().BeFalse(text);
    }

    [Theory]
    [InlineData("memory_list")]
    [InlineData("memory_search")]
    [InlineData("memory_remember")]
    public async Task Memory_УзкаяРамка_ПамятьСфернойПерсоны_Отказ(string tool)
    {
        // Сферная персона — личная группировка владельца (решение 30.10, R-S2a-3b п.4): полка
        // памяти сферы была закрыта, её персона — нет; теперь закрыты обе
        var f = await ArrangeAsync();
        var s = await ArrangeSphereAsync(f);
        var (text, isError) = await CallAsync($"/mcp/memory/{s.SpherePersona}/{s.SharedInSphere}", tool,
            new { query = f.Tag, type = "semantic", text = "x" }, s.SharedSphereChat);
        isError.Should().BeTrue($"{tool}: {text}");
        text.Should().Contain("Личная память сферной персоны закрыта");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Memory_ПамятьСфернойПерсоны_ВЛичномЧатеРаботает()
    {
        // Анти-тест: та же персона и та же сфера в личном чате (широкая рамка) работают —
        // отказывала рамка, а не зона персоны и не флаг сфер
        var f = await ArrangeAsync();
        var s = await ArrangeSphereAsync(f);
        var (seed, seedErr) = await CallAsync($"/mcp/memory/{s.SpherePersona}/{s.PersonalInSphere}",
            "memory_remember", new { type = "semantic", text = $"СФЕРА-факт-{f.Tag}" }, s.PersonalSphereChat);
        seedErr.Should().BeFalse(seed);
        var (list, listErr) = await CallAsync($"/mcp/memory/{s.SpherePersona}/{s.PersonalInSphere}",
            "memory_list", new { }, s.PersonalSphereChat);
        listErr.Should().BeFalse(list);
        list.Should().Contain($"СФЕРА-факт-{f.Tag}");
    }

    [Theory]
    [InlineData(null)] // вызов без заголовка
    [InlineData("нет-такого-чата")] // чат не найден
    public async Task Memory_БезЗаголовкаИлиЧужойЧат_УзкаяРамка_FailClosed(string? chatId)
    {
        var f = await ArrangeAsync();
        // Вердикт рамки меряется читающим инструментом: после О2 (1af1d702a) пишущий вызов
        // с неизвестного чата отрезан раньше — гейтом актора, и текста отказа рамки в нём уже нет
        // (сам отказ — строкой ниже).
        var (text, isError) = await CallAsync($"/mcp/memory/{f.GlobalPersona}/-", "memory_search",
            new { query = f.Tag }, chatId);
        isError.Should().BeTrue(text);
        text.Should().Contain("Личная память глобальной персоны закрыта");
        f.AssertNoPersonal(text);
        var (_, writeErr) = await CallAsync($"/mcp/memory/{f.GlobalPersona}/-", "memory_remember",
            new { type = "semantic", text = "x" }, chatId);
        writeErr.Should().BeTrue("личное пространство закрыто и для пишущего вызова");
    }

    [Theory]
    // Wide — и у личного проекта, и у чата вне проекта: личное пространство и так своё,
    // рамка его не режет ни при каком вызове (с2)
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wide_ЛичныйПроектИЧатВнеПроекта_ЛичнаяПамятьОткрыта(bool viaProject)
    {
        var f = await ArrangeAsync();
        string chatId;
        if (viaProject)
        {
            chatId = f.PersonalChat;
        }
        else
        {
            var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
            resp.EnsureSuccessStatusCode();
            chatId = IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
        }
        var (text, isError) = await CallAsync($"/mcp/memory/{f.GlobalPersona}/-", "memory_search",
            new { query = $"PERSONAL-секрет-{f.Tag}" }, chatId);
        isError.Should().BeFalse(text);
        text.Should().Contain($"PERSONAL-секрет-{f.Tag}");
    }

    // --- personas ---

    [Theory]
    [InlineData("personas_list")]
    [InlineData("personas_mcp_list")]
    [InlineData("personas_create")]
    [InlineData("personas_update")]
    [InlineData("personas_delete")]
    [InlineData("personas_automation_list")]
    public async Task Personas_УзкаяРамка_ГлобальныеИУправление_Отказ(string tool)
    {
        var f = await ArrangeAsync();
        var args = tool switch
        {
            "personas_list" => (object)new { scope = "all" },
            "personas_create" => new { name = "новая", scope = "global" },
            "personas_update" or "personas_delete" or "personas_automation_list" => new { id = f.GlobalPersona },
            _ => new { },
        };
        var (text, isError) = await CallAsync($"/mcp/personas/{f.SharedChat}", tool, args, f.SharedChat);
        isError.Should().BeTrue($"{tool}: {text}");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Personas_УзкаяРамка_ScopeGlobalИGetГлобальной_Отказ()
    {
        var f = await ArrangeAsync();
        var (list, listErr) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_list",
            new { scope = "global" }, f.SharedChat);
        listErr.Should().BeTrue(list);
        f.AssertNoPersonal(list);

        var (get, getErr) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_get",
            new { id = f.GlobalPersona }, f.SharedChat);
        getErr.Should().BeTrue(get);
        f.AssertNoPersonal(get);
    }

    [Fact]
    public async Task Personas_УзкаяРамка_ВопросГлобальнойПерсоне_Отказ()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/personas/{f.SharedChat}", "persona_ask",
            new { personaId = f.GlobalPersona, question = "что ты помнишь?" }, f.SharedChat);
        isError.Should().BeTrue(text);
        text.Should().Contain("Глобальные персоны");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Personas_УзкаяРамка_ПроектнаяКомандаДоступна_ГлобальныхВСпискеНет()
    {
        var f = await ArrangeAsync();
        var (context, contextErr) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_list",
            new { scope = "context" }, f.SharedChat);
        contextErr.Should().BeFalse(context);
        context.Should().Contain($"КОМАНДА-{f.Tag}");
        context.Should().NotContain("PERSONAL", "ambient-глобальные персоны из контекста общего проекта убраны");

        var (project, projectErr) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_list",
            new { scope = "project" }, f.SharedChat);
        projectErr.Should().BeFalse(project);
        project.Should().Contain($"КОМАНДА-{f.Tag}");
    }

    [Fact]
    public async Task Personas_ЛичныйПроект_ШирокаяРамка_GlobalAllMcpListРаботают()
    {
        var f = await ArrangeAsync();
        var (all, allErr) = await CallAsync($"/mcp/personas/{f.PersonalChat}", "personas_list",
            new { scope = "all" }, f.PersonalChat);
        allErr.Should().BeFalse(all);
        all.Should().Contain($"PERSONAL-персона-{f.Tag}");

        var (mcp, mcpErr) = await CallAsync($"/mcp/personas/{f.PersonalChat}", "personas_mcp_list",
            new { }, f.PersonalChat);
        mcpErr.Should().BeFalse(mcp);
    }

    [Fact]
    public async Task Personas_УзкаяРамка_ВопросСфернойПерсоне_Отказ()
    {
        // Ответ сферной персоны приносит её личную память — тот же класс, что закрытая
        // полка памяти сферы (решение 30.10, R-S2a-3b п.4)
        var f = await ArrangeAsync();
        var s = await ArrangeSphereAsync(f);
        var (text, isError) = await CallAsync($"/mcp/personas/{s.SharedSphereChat}", "persona_ask",
            new { personaId = s.SpherePersona, question = "что ты помнишь?" }, s.SharedSphereChat);
        isError.Should().BeTrue(text);
        text.Should().Contain("Персоны сферы");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Personas_УзкаяРамка_СфернаяПерсонаВСпискеКонтекстаНет_ПроектнаяДоступна()
    {
        // Дефект 837bb1c8: сферная персона была закрыта по памяти и persona_ask, но оставалась
        // в personas_list scope=context (GetForContext отдаёт команду сферы проекта чата) —
        // список, из которого нельзя спросить. Личная группировка убирается целиком;
        // проектная команда этого чата не тронута
        var f = await ArrangeAsync();
        var s = await ArrangeSphereAsync(f);
        var sphereTeamPersona = await CreatePersonaAsync($"СФЕРА-КОМАНДА-{f.Tag}", "project", s.SharedInSphere);

        var (list, listErr) = await CallAsync($"/mcp/personas/{s.SharedSphereChat}", "personas_list",
            new { scope = "context" }, s.SharedSphereChat);
        listErr.Should().BeFalse(list);
        list.Should().Contain(sphereTeamPersona, "проектная персона этого чата — в списке");
        list.Should().NotContain(s.SpherePersona, "сферная персона — личная группировка владельца");
        list.Should().NotContain($"СФЕРА-персона-{f.Tag}", "и по имени: персоны нет в составе");
    }

    [Fact]
    public async Task Personas_ШирокаяРамка_СфернаяПерсонаВСпискеКонтекстаЕсть()
    {
        // Анти-тест: та же персона и та же сфера в личном чате (широкая рамка) — в списке;
        // проектная персона тоже в обеих рамках (Wide-путь не тронут)
        var f = await ArrangeAsync();
        var s = await ArrangeSphereAsync(f);
        var personalTeamPersona = await CreatePersonaAsync($"СФЕРА-КОМАНДА-личный-{f.Tag}", "project", s.PersonalInSphere);

        var (list, listErr) = await CallAsync($"/mcp/personas/{s.PersonalSphereChat}", "personas_list",
            new { scope = "context" }, s.PersonalSphereChat);
        listErr.Should().BeFalse(list);
        list.Should().Contain(s.SpherePersona, "в личном чате личное пространство владельца — своё");
        list.Should().Contain(personalTeamPersona);
    }

    [Fact]
    public async Task Personas_УзкаяРамка_GetИПривязкиСфернойПерсоны_Отказ_ВЛичномЧатеРаботают()
    {
        // Другие пути отдачи данных персоны (перечислены грепом по PersonasToolset): профиль
        // и привязки сферной персоны закрыты тем же отказом; в широкой рамке читаются
        var f = await ArrangeAsync();
        var s = await ArrangeSphereAsync(f);
        foreach (var tool in new[] { "personas_get", "personas_bindings_list" })
        {
            var (text, isError) = await CallAsync($"/mcp/personas/{s.SharedSphereChat}", tool,
                new { id = s.SpherePersona }, s.SharedSphereChat);
            isError.Should().BeTrue($"{tool}: {text}");
            text.Should().Contain("Персоны сферы");
            f.AssertNoPersonal(text);
        }
        foreach (var tool in new[] { "personas_get", "personas_bindings_list" })
        {
            var (text, isError) = await CallAsync($"/mcp/personas/{s.PersonalSphereChat}", tool,
                new { id = s.SpherePersona }, s.PersonalSphereChat);
            isError.Should().BeFalse($"{tool}: {text}");
        }
    }

    [Fact]
    public async Task PersonasSuggestBindings_УзкаяРамка_Отказ_ВЛичномЧатеРаботает()
    {
        // Каталог подбора — всё пространство владельца (проекты, заметки, базы), а не только
        // датасеты: сузить его до проекта чата означало бы пересобрать промпт подбора;
        // инструмент закрыт целиком (R-S2a-3b п.2)
        var f = await ArrangeAsync();
        var (denied, deniedErr) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_suggest_bindings",
            new { id = f.ProjectPersona }, f.SharedChat);
        deniedErr.Should().BeTrue(denied);
        denied.Should().Contain("подбор привязок закрыт");
        f.AssertNoPersonal(denied);

        // Анти-тест: та же подсказка в личном чате (широкая рамка) проходит — TestKit-стаб
        // дешёвого LLM отвечает «[]»: кандидатов нет, но и отказа по рамке нет
        var (ok, okErr) = await CallAsync($"/mcp/personas/{f.PersonalChat}", "personas_suggest_bindings",
            new { id = f.GlobalPersona }, f.PersonalChat);
        okErr.Should().BeFalse(ok);
        ok.Should().NotContain("подбор привязок закрыт");
    }

    [Fact]
    public async Task PersonasBindingsSet_УзкаяРамка_ЛичнаяБазаИСфернаяЦель_Отказ_ЦелиПроектаПроходят()
    {
        // Дефект 67dcf4c2 (находка ревью R-S2a-5): рамка фильтровала только Global-цель вызова;
        // SetBindingsAsync применял проектной персоне привязку к личной базе (id, известный из
        // подсказки) и цели чужих проектов. Якорь — проект чата, тот же, что у knowledge_search
        // выше: его база и его папка проходят, всё остальное — отказ фиксированным текстом без id
        var f = await ArrangeAsync();
        var s = await ArrangeSphereAsync(f);
        GiveProjectDataset(f.SharedProject, "ds-project-1");
        await GiveManageModuleAsync(f.ProjectPersona); // вызов обязан дойти до гейта рамки, а не до «модуль выключен»

        // knowledge-цель личной базы
        var (personal, errPersonal) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_bindings_set",
            new { id = f.ProjectPersona, bindings = new[] { new { type = "knowledge", target = "ds-personal-1" } } },
            f.SharedChat);
        errPersonal.Should().BeTrue(personal);
        personal.Should().Contain("Привязки персоны в общем проекте — только к данным этого проекта");
        f.AssertNoPersonal(personal);

        // сферная цель: ProjectPersonas с path = id сферной персоны. Прежний путь отвергал её
        // только валидацией цели («Персона не найдена в этом проекте»); теперь — отказом рамки
        // с общим текстом (тот же класс, что чужие проекты и personal-цели)
        var (sphere, errSphere) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_bindings_set",
            new { id = f.ProjectPersona, bindings = new[] { new { type = "projectPersonas", target = s.SharedInSphere, path = s.SpherePersona } } },
            f.SharedChat);
        errSphere.Should().BeTrue(sphere);
        sphere.Should().Contain("не данные этого проекта");
        f.AssertNoPersonal(sphere);

        // цель вызова — сферная персона: тот же отказ, что у personas_get/bindings_list (дефект:
        // прежняя проверка знала только Global)
        var (target, errTarget) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_bindings_set",
            new { id = s.SpherePersona, bindings = Array.Empty<object>() }, f.SharedChat);
        errTarget.Should().BeTrue(target);
        target.Should().Contain("Персоны сферы");
        f.AssertNoPersonal(target);

        // проектные цели — база и папка этого чата проекта: проходят (путь валидации Wide не тронут)
        var (ok, okErr) = await CallAsync($"/mcp/personas/{f.SharedChat}", "personas_bindings_set",
            new { id = f.ProjectPersona, bindings = new[]
            {
                new { type = "knowledge", target = "ds-project-1", path = (string?)null },
                new { type = "projectPath", target = f.SharedProject, path = (string?)"docs" },
            } }, f.SharedChat);
        okErr.Should().BeFalse(ok);
        ok.Should().Contain("ds-project-1");
    }

    [Fact]
    public async Task PersonasBindingsSet_ЛичныйПроект_ШирокаяРамка_ЛичнаяБазаПроходит()
    {
        // Анти-тест: та же цель в личном чате (широкая рамка) применяется — отказывала рамка,
        // не валидация цели (Wide-путь не тронут)
        var f = await ArrangeAsync();
        var (ok, err) = await CallAsync($"/mcp/personas/{f.PersonalChat}", "personas_bindings_set",
            new { id = f.GlobalPersona, bindings = new[] { new { type = "knowledge", target = "ds-personal-1" } } },
            f.PersonalChat);
        err.Should().BeFalse(ok);
        ok.Should().Contain("ds-personal-1");
    }

    /// <summary>
    /// Модуль manage проектной персоне — та же выдача, что делает человек в настройках
    /// (tool:personas-manage через REST, вне рамки): без неё personas_bindings_set отклоняется
    /// шагом «модуль manage выключен» и не доходит до гейта рамки.
    /// </summary>
    private async Task GiveManageModuleAsync(string personaId)
    {
        var resp = await _client.PutAsJsonAsync($"/api/personas/{personaId}/bindings",
            new { bindings = new[] { new { type = "tool", target = "personas-manage" } } });
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PersonasKnowledgeSearch_УзкаяРамка_БазаПроектаОткрыта_ЛичнаяОбщаяИЧужаяРезаны()
    {
        // Ровно то, что шаг закрыл в DifyToolset: в общем чате доступна только база этого
        // проекта; прежняя проверка по полному каталогу владельца пропускала в общий чат
        // содержимое личной базы, чей id модель знала из подсказки привязок (R-S2a-3b п.1)
        var f = await ArrangeAsync();
        GiveProjectDataset(f.SharedProject, "ds-project-1");

        var (own, ownErr) = await CallAsync($"/mcp/personas/{f.SharedChat}", "knowledge_search",
            new { datasetId = "ds-project-1", query = f.Tag }, f.SharedChat);
        ownErr.Should().BeFalse(own);

        foreach (var (id, label) in new[]
        {
            ("ds-personal-1", "личная база"), ("ds-common-1", "общая ничья база"), ("ds-foreign-1", "чужая база"),
        })
        {
            var (text, isError) = await CallAsync($"/mcp/personas/{f.SharedChat}", "knowledge_search",
                new { datasetId = id, query = f.Tag }, f.SharedChat);
            isError.Should().BeTrue($"{label} {id}: {text}");
            text.Should().Contain("только база знаний этого проекта");
            f.AssertNoPersonal(text);
        }
    }

    [Fact]
    public async Task PersonasKnowledgeSearch_УзкаяРамка_БезБазыУПроекта_Отказ()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/personas/{f.SharedChat}", "knowledge_search",
            new { datasetId = "ds-personal-1", query = f.Tag }, f.SharedChat);
        isError.Should().BeTrue(text);
        text.Should().Contain("у этого проекта её нет");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task PersonasKnowledgeSearch_ЛичныйПроект_ШирокаяРамка_КаталогВладельцаОткрыт()
    {
        // Анти-тест: те же id в личном чате (широкая рамка) доступны — отказывала рамка,
        // а не проверка каталога (Wide-путь не тронут)
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/personas/{f.PersonalChat}", "knowledge_search",
            new { datasetId = "ds-personal-1", query = f.Tag }, f.PersonalChat);
        isError.Should().BeFalse(text);
        text.Should().NotContain("не найдена или недоступна");
    }

    // --- notifications ---

    [Theory]
    [InlineData("notifications_list")]
    [InlineData("notifications_mark_read")]
    [InlineData("notifications_delete")]
    public async Task Notifications_УзкаяРамка_ЛичныйЯщик_Отказ(string tool)
    {
        var f = await ArrangeAsync();
        var args = tool switch
        {
            "notifications_mark_read" or "notifications_delete" => (object)new { id = "нет-такого" },
            _ => new { },
        };
        var (text, isError) = await CallAsync($"/mcp/notifications/{f.SharedChat}", tool, args, f.SharedChat);
        isError.Should().BeTrue($"{tool}: {text}");
        text.Should().Contain("Уведомления владельца закрыты");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Notifications_УзкаяРамка_СоздатьОПроекте_Можно_ЯщикЧитаетсяВЛичномЧате()
    {
        var f = await ArrangeAsync();
        var (created, createdErr) = await CallAsync($"/mcp/notifications/{f.SharedChat}", "notifications_create",
            new { title = $"SHARED-итог-{f.Tag}", body = "работа проекта завершена" }, f.SharedChat);
        createdErr.Should().BeFalse(created);

        // Созданное видно в ящике из личного чата (широкая рамка) — ящик не сломан рамкой
        var (list, listErr) = await CallAsync($"/mcp/notifications/{f.PersonalChat}", "notifications_list",
            new { }, f.PersonalChat);
        listErr.Should().BeFalse(list);
        list.Should().Contain($"SHARED-итог-{f.Tag}");
    }

    // --- higgsfield ---

    [Theory]
    [InlineData("generate_image")]
    [InlineData("generate_video")]
    [InlineData("job_status")]
    [InlineData("show_generation_by_ids")]
    public async Task Higgsfield_УзкаяРамка_ГенерацияИИсторияАккаунта_Отказ(string tool)
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/higgsfield/{f.SharedChat}", tool,
            new { prompt = "x", id = "x" }, f.SharedChat);
        isError.Should().BeTrue($"{tool}: {text}");
        text.Should().Contain("закрыт в общем проекте");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Higgsfield_УзкаяРамка_ModelsExplore_НеРезан_ВЛичномЧатеРамкаНеМешает()
    {
        var f = await ArrangeAsync();
        var (models, _) = await CallAsync($"/mcp/higgsfield/{f.SharedChat}", "models_explore",
            new { }, f.SharedChat);
        models.Should().NotContain("закрыт в общем проекте",
            "публичный справочник моделей не читает аккаунт и не тратит деньги");

        var (gen, _) = await CallAsync($"/mcp/higgsfield/{f.PersonalChat}", "generate_image",
            new { prompt = "x" }, f.PersonalChat);
        gen.Should().NotContain("закрыт в общем проекте", "в широкой рамке генерация не резана рамкой");
    }

    // --- plugins ---

    [Fact]
    public async Task Plugins_УзкаяРамка_ЧерновикиВладельца_Отказ_ВЛичномЧатеЧерновикРаботает()
    {
        var f = await ArrangeAsync();
        foreach (var key in new[] { FeatureFlagKeys.PluginsMvp, FeatureFlagKeys.PluginsBuildYourself })
            (await _client.PutAsJsonAsync($"/api/feature-flags/{key}", new { enabled = true }))
                .EnsureSuccessStatusCode();

        var (denied, deniedErr) = await CallAsync($"/mcp/plugins/{f.SharedChat}", "draft_create",
            new { slug = $"frame-{f.Tag}" }, f.SharedChat);
        deniedErr.Should().BeTrue(denied);
        denied.Should().Contain("Плагины закрыты");
        f.AssertNoPersonal(denied);

        var (draft, draftErr) = await CallAsync($"/mcp/plugins/{f.PersonalChat}", "draft_create",
            new { slug = $"frame-{f.Tag}" }, f.PersonalChat);
        draftErr.Should().BeFalse(draft);
    }

    // --- dify ---

    [Theory]
    [InlineData("search_knowledge")]
    [InlineData("list_datasets")]
    [InlineData("create_dataset")]
    public async Task Dify_УзкаяРамка_БезБазыУПроекта_ВсёВнеПроекта_Отказ(string tool)
    {
        var f = await ArrangeAsync();
        var args = tool switch
        {
            "search_knowledge" => (object)new { query = f.Tag, dataset_id = "ds-personal-1" },
            "create_dataset" => new { name = "новая-база" },
            _ => new { },
        };
        var (text, isError) = await CallAsync($"/mcp/dify/{f.SharedChat}", tool, args, f.SharedChat);
        isError.Should().BeTrue($"{tool}: {text}");
        text.Should().Contain("у этого проекта её нет");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Dify_УзкаяРамка_СвояБазаДоступна_ЧужиеДатасетыРезаны_СписокОграничен()
    {
        var f = await ArrangeAsync();
        GiveProjectDataset(f.SharedProject, "ds-project-1");

        var (docs, docsErr) = await CallAsync($"/mcp/dify/{f.SharedChat}", "list_documents",
            new { }, f.SharedChat);
        docsErr.Should().BeFalse(docs);

        var (foreign, foreignErr) = await CallAsync($"/mcp/dify/{f.SharedChat}", "search_knowledge",
            new { query = f.Tag, dataset_id = "ds-personal-1" }, f.SharedChat);
        foreignErr.Should().BeTrue(foreign);
        foreign.Should().Contain("только база знаний этого проекта");
        f.AssertNoPersonal(foreign);

        var (list, listErr) = await CallAsync($"/mcp/dify/{f.SharedChat}", "list_datasets",
            new { }, f.SharedChat);
        listErr.Should().BeFalse(list);
        list.Should().Contain("ds-project-1").And.NotContain("ds-personal-1");
    }

    [Fact]
    public async Task Dify_ЛичныйПроект_ШирокаяРамка_ДатасетыВладельцаОткрыты()
    {
        var f = await ArrangeAsync();
        var (list, listErr) = await CallAsync($"/mcp/dify/{f.PersonalChat}", "list_datasets",
            new { }, f.PersonalChat);
        listErr.Should().BeFalse(list);
        list.Should().Contain("ds-personal-1");
    }

    // --- фикстура Dify ---

    /// <summary>
    /// Привязать датасет к проекту той же записью стора, что делает живая связка
    /// (WorkspaceKnowledgeStore по rootPath) — формулу дефолтного датасета тулсет читает живьём.
    /// </summary>
    private void GiveProjectDataset(string projectId, string datasetId)
    {
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var store = _factory.Services.GetRequiredService<WorkspaceKnowledgeStore>();
        var root = projects.GetById(projectId)!.RootPath;
        var wk = store.GetOrCreate(root);
        wk.DifyDatasetId = datasetId;
        store.Save(wk);
    }

    /// <summary>
    /// Стаб «Dify» для этого класса: датасеты тестового пользователя — личный (ds-personal-1,
    /// маркер PERSONAL) и тот, что будет привязан к общему проекту (ds-project-1); плюс общая
    /// ничья база (ds-common-1, без префикса — видна каталогу и обязана быть резана
    /// узкой рамкой) и чужая (ds-foreign-1, префикс otheruser — скрыта каталогом). Остальное —
    /// пустые страницы. Свой, не общий TestKit-стаб: список датасетов здесь — часть проверки
    /// фильтрации.
    /// </summary>
    private sealed class FrameDifyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = (path, request.Method.Method) switch
            {
                ("/datasets", "POST") => "{\"id\":\"ds-created-1\"}",
                ("/datasets", _) => "{\"data\":["
                    + "{\"id\":\"ds-personal-1\",\"name\":\"testuser:kb:PERSONAL-база\",\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0},"
                    + "{\"id\":\"ds-project-1\",\"name\":\"testuser:kb:Проект-база\",\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0},"
                    + "{\"id\":\"ds-common-1\",\"name\":\"ОБЩАЯ-база\",\"permission\":\"all_team_members\",\"document_count\":0,\"word_count\":0,\"created_at\":0},"
                    + "{\"id\":\"ds-foreign-1\",\"name\":\"otheruser:kb:чужая-база\",\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0}"
                    + "],\"has_more\":false}",
                var (p, _) when p.EndsWith("/segments") => "{\"data\":[]}",
                // Dify retrieve: KnowledgeService читает records[] — пустой, но валидный ответ
                var (p, _) when p.EndsWith("/retrieve") => "{\"query\":\"q\",\"records\":[],\"document_search_count\":0}",
                var (p, _) when p.StartsWith("/datasets/") && p.Contains("/documents") =>
                    "{\"data\":[],\"has_more\":false,\"total\":0}",
                _ => "{\"data\":[],\"has_more\":false}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
