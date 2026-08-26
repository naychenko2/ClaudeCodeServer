using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// MCP-over-HTTP для памяти (ADR-012, фаза 2 волна 1): POST /mcp/memory/{personaId}/{projectId}.
/// Один тулсет обслуживает «memory» и все pmem_&lt;handle&gt; — персона и проект едут хвостом
/// маршрута, а URL виден модели в конфиге хода. Поэтому здесь проверяется то, чем эта
/// поверхность отличается от widgets: ИЗОЛЯЦИЯ (хвост — не источник прав, владелец — только
/// из claim sub), состав по осям хвоста (personal/team/dossier) и живые вызовы сервисов.
/// </summary>
[Trait("Category", "Slow")]
public class MemoryHttpTransportTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MemoryHttpTransportTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private static string MemoryUrl(string persona, string project) =>
        $"/mcp/memory/{persona}/{project}";

    private async Task<JsonElement> RpcAsync(string url, string method, object? @params = null, int id = 1)
    {
        var body = @params is null
            ? new { jsonrpc = "2.0", id, method }
            : (object)new { jsonrpc = "2.0", id, method, @params };
        var resp = await _client.PostAsJsonAsync(url, body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private async Task<List<string>> ToolNamesAsync(string url)
    {
        var tools = (await RpcAsync(url, "tools/list")).GetProperty("result").GetProperty("tools");
        return tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    private static async Task<List<string>> ToolNamesAsync(HttpClient client, string url)
    {
        var resp = await client.PostAsJsonAsync(url, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.EnsureSuccessStatusCode();
        var answer = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return answer.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    private async Task<(string PersonaId, string ProjectId)> SeedAsync(bool projectScopedPersona = true)
    {
        var dir = Path.Combine(_factory.TempDir, "mcpmem_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "McpMemory", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var personaResp = await _client.PostAsJsonAsync("/api/personas", new
        {
            name = "Тестовая персона",
            role = "Роль",
            scope = projectScopedPersona ? "Project" : "Global",
            projectId = projectScopedPersona ? projectId : null,
        });
        personaResp.EnsureSuccessStatusCode();
        var personaId = JsonSerializer.Deserialize<JsonElement>(
            await personaResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        return (personaId, projectId);
    }

    [Fact]
    public async Task Initialize_ОтдаётИмяСервера()
    {
        var answer = await RpcAsync(MemoryUrl("-", "-"), "initialize", new { protocolVersion = "2025-06-18" });
        answer.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()
            .Should().Be("memory");
    }

    [Fact]
    public async Task ToolsList_ПерсональныйПроектныйЧат_PersonalПлюсTeam()
    {
        var (personaId, projectId) = await SeedAsync();
        var tools = await ToolNamesAsync(MemoryUrl(personaId, projectId));

        tools.Should().Contain("memory_remember").And.Contain("memory_recall");
        tools.Should().Contain("team_memory_remember").And.Contain("team_memory_list");
        // Флаг change-dossiers-recall по умолчанию выключен — секции паспортов нет
        tools.Should().NotContain("dossier_lookup").And.NotContain("dossier_get");
        tools.Count.Should().Be(15);
    }

    /// <summary>
    /// Приёмка: чат БЕЗ персоны (обычный проектный чат) получает только командные
    /// инструменты — как сегодня при пустом MEMORY_PERSONA_ID у stdio-сервера.
    /// </summary>
    [Fact]
    public async Task ToolsList_ЧатБезПерсоны_ТолькоИнструментыКоманды()
    {
        var (_, projectId) = await SeedAsync();
        var tools = await ToolNamesAsync(MemoryUrl("-", projectId));

        tools.Should().OnlyContain(t => t.StartsWith("team_memory_", StringComparison.Ordinal));
        tools.Count.Should().Be(5, "пять командных инструментов, ни одного personal");
    }

    [Fact]
    public async Task ToolsList_ЧатВнеПроекта_ТолькоЛичнаяПамять()
    {
        var (personaId, _) = await SeedAsync(projectScopedPersona: false);
        var tools = await ToolNamesAsync(MemoryUrl(personaId, "-"));

        tools.Should().OnlyContain(t => t.StartsWith("memory_", StringComparison.Ordinal));
        tools.Count.Should().Be(10);
    }

    /// <summary>
    /// Секция dossier_* — по флагу ВЛАДЕЛЬЦА change-dossiers-recall (этап 2, ADR-004 §5):
    /// решается по владельцу токена, а не по ходу — инвариант стабильности состава.
    /// </summary>
    [Fact]
    public async Task ToolsList_DossierСекция_ПоФлагуВладельца()
    {
        var (personaId, projectId) = await SeedAsync();
        var owner = _factory.Services.GetRequiredService<ClaudeHomeServer.Services.UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!;
        owner.FeatureFlags = new Dictionary<string, bool> { ["change-dossiers-recall"] = true };

        var tools = await ToolNamesAsync(MemoryUrl(personaId, projectId));
        tools.Should().Contain("dossier_lookup").And.Contain("dossier_get");
        tools.Count.Should().Be(17);

        // Флаг погашен — секция исчезает (состав зависит от владельца, не от хода)
        owner.FeatureFlags = new Dictionary<string, bool> { ["change-dossiers-recall"] = false };
        (await ToolNamesAsync(MemoryUrl(personaId, projectId))).Count.Should().Be(15);
    }

    /// <summary>
    /// ПРИЁМКА изоляции: токен владельца A + personaId персоны владельца B — отказ на ВЫЗОВЕ
    /// (content-ошибка, не данные) и пустой состав на tools/list. Хвост маршрута виден модели
    /// в конфиге хода и не является источником прав.
    /// </summary>
    [Fact]
    public async Task ЧужаяПерсона_ОтказНаВызовеИПустойСостав()
    {
        var (ownPersona, _) = await SeedAsync(projectScopedPersona: false);

        // Персона второго владельца
        using var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var resp = await other.PostAsJsonAsync("/api/personas", new { name = "Чужая персона", role = "Роль" });
        resp.EnsureSuccessStatusCode();
        var strangerId = JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        // tools/list: personal-инструментов нет
        var tools = await ToolNamesAsync(MemoryUrl(strangerId, "-"));
        tools.Should().BeEmpty("чужая персона не существует для владельца токена — сервер без инструментов");

        // tools/call: content-ошибка, не данные и не разрыв протокола
        var answer = await RpcAsync(MemoryUrl(strangerId, "-"), "tools/call", new
        {
            name = "memory_remember",
            arguments = new { type = "semantic", text = "не должно сохраниться" },
        });
        var result = answer.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!
            .Should().Contain("другому владельцу");

        // Своей персоне память по-прежнему доступна — отказ не задел её
        (await ToolNamesAsync(MemoryUrl(ownPersona, "-"))).Should().Contain("memory_remember");
    }

    [Fact]
    public async Task ЧужойПроект_ОтказНаКоманднуюПамять()
    {
        var (_, ownProject) = await SeedAsync();
        using var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var dir = Path.Combine(_factory.TempDir, "mcpmem_other_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var resp = await other.PostAsJsonAsync("/api/projects", new { name = "Чужой проект", rootPath = dir });
        resp.EnsureSuccessStatusCode();
        var strangerProject = JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var answer = await RpcAsync(MemoryUrl("-", strangerProject), "tools/call", new
        {
            name = "team_memory_list",
            arguments = new { },
        });
        var result = answer.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!
            .Should().Contain("другому владельцу");

        // Свой проект продолжает работать
        (await ToolNamesAsync(MemoryUrl("-", ownProject))).Should().Contain("team_memory_list");
    }

    [Fact]
    public async Task MemoryRemember_ИRecall_РаботаютЧерезТулсет()
    {
        var (personaId, _) = await SeedAsync(projectScopedPersona: false);
        var remember = await RpcAsync(MemoryUrl(personaId, "-"), "tools/call", new
        {
            name = "memory_remember",
            arguments = new { type = "semantic", text = "Мой рабочий стек — ASP.NET Core" },
        });
        remember.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        var recall = await RpcAsync(MemoryUrl(personaId, "-"), "tools/call", new
        {
            name = "memory_recall",
            arguments = new { query = "какой у меня рабочий стек" },
        });
        var text = recall.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        text.Should().Contain("ASP.NET Core", "запись сохранилась и находится recall'ом");
    }

    /// <summary>
    /// Гейт записи команды (③-3.4): пишет «свой» вызов без персоны либо персона САМОГО
    /// проекта; глобальная персона и консультант другого проекта — read-only. Правило
    /// переехало из ProjectsController (заголовок X-Caller-Persona-Id у stdio-сервера);
    /// теперь вызывающая персона — та, что в хвосте маршрута.
    /// </summary>
    [Fact]
    public async Task TeamWrite_ГейтитсяПерсонойПроекта()
    {
        var (projectPersona, projectId) = await SeedAsync(); // проектная персона = «своя»
        var (globalPersona, _) = await SeedAsync(projectScopedPersona: false); // глобальная

        // Глобальная персона — отказ с пояснением
        var denied = await RpcAsync(MemoryUrl(globalPersona, projectId), "tools/call", new
        {
            name = "team_memory_remember",
            arguments = new { text = "не должно записаться" },
        });
        denied.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        denied.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!
            .Should().Contain("только персоне ЭТОГО проекта");

        // Чат без персоны (обычный проектный чат) — пишет
        var own = await RpcAsync(MemoryUrl("-", projectId), "tools/call", new
        {
            name = "team_memory_remember",
            arguments = new { text = "Договорённость: сборка в dev-контейнере" },
        });
        own.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        // Персона проекта — пишет
        var member = await RpcAsync(MemoryUrl(projectPersona, projectId), "tools/call", new
        {
            name = "team_memory_remember",
            arguments = new { text = "Договорённость: тесты платформонезависимые" },
        });
        member.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        // Итог виден списком — обе записи на месте, отказ ничего не записал
        var list = await RpcAsync(MemoryUrl("-", projectId), "tools/call", new
        {
            name = "team_memory_list",
            arguments = new { },
        });
        var payload = list.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        payload.Should().Contain("dev-контейнере").And.Contain("платформонезависимые");
        payload.Should().NotContain("не должно записаться");
    }

    [Fact]
    public async Task TeamList_ПагинируетИУсекаетТекст()
    {
        var (_, projectId) = await SeedAsync();
        for (var i = 1; i <= 3; i++)
            await RpcAsync(MemoryUrl("-", projectId), "tools/call", new
            {
                name = "team_memory_remember",
                arguments = new { text = $"Факт {i} " + new string('х', 400) },
            }, id: i);

        var answer = await RpcAsync(MemoryUrl("-", projectId), "tools/call", new
        {
            name = "team_memory_list",
            arguments = new { limit = 2, offset = 0 },
        });
        var text = answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var page = JsonSerializer.Deserialize<JsonElement>(text);
        page.GetProperty("total").GetInt32().Should().Be(3);
        var items = page.GetProperty("items");
        items.GetArrayLength().Should().Be(2, "лимит страницы");
        foreach (var item in items.EnumerateArray())
        {
            item.GetProperty("text").GetString()!.Length.Should().BeLessThanOrEqualTo(202,
                "длинные записи усечены до 200 символов + многоточие");
            item.GetProperty("text").GetString()!.Should().EndWith("…");
        }
    }

    /// <summary>
    /// Тот же инвариант, что у widgets (McpHttpTransportTests.СоставИнструментов_…): хвост и
    /// владелец — легитимные входы состава (закреплены конфигом хода), а заголовки/тело
    /// хода на состав влиять не смеют — иначе процесс CLI перезапускается между ходами.
    /// </summary>
    [Fact]
    public async Task СоставНаФиксированномХвосте_НеЗависитОтЗаголовковИТела()
    {
        var (personaId, projectId) = await SeedAsync();

        static string Fingerprint(List<string> tools) => string.Join(",", tools);

        var plain = Fingerprint(await ToolNamesAsync(MemoryUrl(personaId, projectId)));

        using var withContext = _factory.CreateAuthenticatedClient();
        withContext.DefaultRequestHeaders.Add("X-Caller-Session-Id", Guid.NewGuid().ToString());
        var resp = await withContext.PostAsJsonAsync(MemoryUrl(personaId, projectId), new
        {
            jsonrpc = "2.0", id = 7, method = "tools/list", @params = new { cursor = "чушь" },
        });
        var loaded = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var withHeaders = Fingerprint(loaded.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList());

        withHeaders.Should().Be(plain);
    }

    /// <summary>
    /// Хвост — наш конфиг, но проверяем форму и на входе: мусорный хвост (не два сегмента,
    /// посторонние символы) — fail-closed отказ вызова и пустой tools/list, а не данные.
    /// «..» сюда не включён: HttpClient нормализует путь ДО отправки, и dots-сегменты
    /// до маршрута просто не доезжают.
    /// </summary>
    [Fact]
    public async Task МусорныйХвост_ОтказВызова_АНеShortcutВДанные()
    {
        var (personaId, _) = await SeedAsync(projectScopedPersona: false);

        foreach (var tail in new[] { personaId, $"{personaId}/{personaId}/{personaId}", $"{personaId}/проект:1" })
        {
            (await ToolNamesAsync($"/mcp/memory/{tail}")).Should().BeEmpty($"хвост «{tail}» не разбирается");
            var answer = await RpcAsync($"/mcp/memory/{tail}", "tools/call", new
            {
                name = "memory_list",
                arguments = new { },
            });
            answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        }
    }

    /// <summary>
    /// Вызов без хвоста — не маршрут параметризованного тулсета: честный 404 (клиент
    /// JSON-RPC не должен молча получить «сервер без инструментов»).
    /// </summary>
    [Fact]
    public async Task ВызовБезХвоста_Отказ404()
    {
        var resp = await _client.PostAsJsonAsync("/mcp/memory",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("mcp_route_not_found");
    }

    /// <summary>
    /// Обратная сторона того же правила: простому (одно-сегментному) тулсету хвост не
    /// принадлежит — /mcp/widgets/extra это промах, а не «виджеты с параметром».
    /// </summary>
    [Fact]
    public async Task ПростомуТулсетуХвостНеПринадлежит_404()
    {
        var resp = await _client.PostAsJsonAsync("/mcp/widgets/extra",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
