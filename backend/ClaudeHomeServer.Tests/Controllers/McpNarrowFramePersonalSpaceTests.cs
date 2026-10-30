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
/// Узкая рамка хода в платформенном MCP (S2a-3b, решение 29.10): в чате ОБЩЕГО проекта личное
/// пространство владельца закрыто всем, включая самого владельца. Здесь — память (личная память
/// глобальной персоны, полка сферы), персоны (глобальные персоны и управление персонами),
/// уведомления (личный ящик), Higgsfield (аккаунт: деньги и история генераций), плагины
/// (черновики владельца), Dify (датасеты вне проекта чата). Контекст проекта остаётся:
/// командная память, проектная команда чата, уведомление о работе проекта, база этого проекта.
/// Вердикт читается из ITurnFrameSource по заголовку X-Caller-Session-Id; «нет заголовка /
/// чат не найден» — узкая рамка (fail-closed). В отказе нет id и названий из личных данных
/// (маркеры PERSONAL-*). Dify здесь ФАКТИКА: свой стаб-хендлер, тесты не ходят в сеть.
/// </summary>
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

        var sharedProject = await CreateProjectAsync($"Общий-{tag}");
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(sharedProject,
            [new ProjectMember { UserId = "участник-" + tag, Role = ProjectRole.Editor }]);
        var sharedChat = await CreateSessionAsync(sharedProject);
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
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(sharedInSphere,
            [new ProjectMember { UserId = "участник-" + f.Tag, Role = ProjectRole.Editor }]);
        var sharedSphereChat = await CreateSessionAsync(sharedInSphere);
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
    [InlineData(null)] // вызов без заголовка
    [InlineData("нет-такого-чата")] // чат не найден
    public async Task Memory_БезЗаголовкаИлиЧужойЧат_УзкаяРамка_FailClosed(string? chatId)
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/memory/{f.GlobalPersona}/-", "memory_remember",
            new { type = "semantic", text = "x" }, chatId);
        isError.Should().BeTrue(text);
        text.Should().Contain("Личная память глобальной персоны закрыта");
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Memory_ЛичныйПроект_ШирокаяРамка_ЛичнаяПамятьОткрыта()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync($"/mcp/memory/{f.GlobalPersona}/-", "memory_search",
            new { query = $"PERSONAL-секрет-{f.Tag}" }, f.PersonalChat);
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
    /// Стаб «Dify» для этого класса: два датасета тестового пользователя — личный
    /// (ds-personal-1, маркер PERSONAL) и тот, что будет привязан к общему проекту
    /// (ds-project-1). Остальное — пустые страницы. Свой, не общий TestKit-стаб: список
    /// датасетов здесь — часть проверки фильтрации.
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
                    + "{\"id\":\"ds-project-1\",\"name\":\"testuser:kb:Проект-база\",\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0}"
                    + "],\"has_more\":false}",
                var (p, _) when p.EndsWith("/segments") => "{\"data\":[]}",
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
