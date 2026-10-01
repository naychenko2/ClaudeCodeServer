using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Опция персоны «Облегчённый контекст»: единая точка резолва профиля
/// (<c>LlmProviderRegistry.LightProfileFor</c>), карта без серверного дефолта и разовая миграция.
/// </summary>
public class PersonaLightContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-light-" + Guid.NewGuid().ToString("N")[..8]);

    public PersonaLightContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
    }

    private static LlmProviderRegistry Registry() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:BareMode"] = "true",
            ["LlmProviders:local-qwen:TrimMcpServers"] = "true",
            ["LlmProviders:local-qwen:KeepMcpServers:0"] = "tasks",
            ["LlmProviders:local-qwen:SystemPromptFile"] = "SystemPrompts/CLAUDE-local.md",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:remote-bare:AnthropicBaseUrl"] = "https://example.invalid",
            ["LlmProviders:remote-bare:ApiKey"] = "sk-test",
            ["LlmProviders:remote-bare:BareMode"] = "true",
            ["LlmProviders:remote-bare:Models:0:Id"] = "remote-bare-model",
            ["LlmProviders:LightProfile:KeepMcpServers:0"] = "memory",
            ["LlmProviders:LightProfile:SystemPromptFile"] = "SystemPrompts/CLAUDE-local.md",
        }).Build());

    private static Persona Persona(bool? light) => new() { OwnerId = "u", Name = "Р", LightContext = light };

    [Fact]
    public void БезПерсоны_РешаетПровайдер()
    {
        var registry = Registry();

        registry.LightProfileFor("qwen3.8-27b", null)!.Source.Should().Be("local-qwen");
        registry.LightProfileFor("opus", null).Should().BeNull();
        registry.LightProfileFor("deepseek-v4-pro", null).Should().BeNull();
    }

    [Fact]
    public void ЛокальнаяМодельСоСвоимПрофилем_ПрофильВсегда_НезависимоОтОпции()
    {
        var registry = Registry();

        registry.LightProfileFor("qwen3.8-27b", Persona(false))!.Source.Should().Be("local-qwen",
            "полный контекст не влезает в окно локальной модели — опция персоны тут не решает");
        registry.LightProfileFor("qwen3.8-27b", Persona(null))!.Source.Should().Be("local-qwen");
        registry.LightProfileFor("qwen3.8-27b", Persona(true))!.Source.Should().Be("local-qwen");
    }

    [Fact]
    public void Фолбэк_СменаМоделиСOpusНаQwen_ПриВыключеннойОпции_ДаётЛокальныйПрофиль()
    {
        // Фолбэк по цепочке меняет модель хода мимо персоны: профиль резолвится заново по
        // новой модели и обязан стать профилем local-qwen, а не остаться «полным» от Opus
        var registry = Registry();
        var persona = Persona(false);

        registry.LightProfileFor("opus", persona).Should().BeNull();
        registry.LightProfileFor("qwen3.8-27b", persona)!.Source.Should().Be("local-qwen");
    }

    [Fact]
    public void НелокальныйПровайдерСоСвоимПрофилем_РешаетОпцияПерсоны()
    {
        var registry = Registry();

        registry.LightProfileFor("remote-bare-model", Persona(false)).Should().BeNull();
        registry.LightProfileFor("remote-bare-model", Persona(true))!.Source.Should().Be("remote-bare");
    }

    [Fact]
    public void ПерсонаНаOpus_ВключённаяОпция_БерётОбщийПрофиль()
    {
        var profile = Registry().LightProfileFor("opus", Persona(true));

        profile.Should().NotBeNull();
        profile!.Source.Should().Be("light");
        profile.KeepMcpServers.Should().Equal("memory");
        profile.ServerMapFallback.Should().BeFalse("серверная карта — про наш репозиторий");
        Registry().LightProfileFor("opus", Persona(false)).Should().BeNull();
    }

    [Fact]
    public void КлючLightProfile_НеСтановитсяПровайдером()
    {
        Registry().GetByKey("LightProfile").Should().BeNull();
    }

    [Fact]
    public void КартаБезПроектной_СервернуюНеПодставляет_НоОблегчениеСохраняет()
    {
        var project = Path.Combine(_root, "foreign");
        Directory.CreateDirectory(project);
        var server = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(server, "SystemPrompts"));
        File.WriteAllText(Path.Combine(server, "SystemPrompts", "CLAUDE-local.md"), "# карта нашего репо");

        var args = ClaudeSession.BuildBareModeArgs(project, server, "SystemPrompts/CLAUDE-local.md",
            ["Bash", "Read"], IdentityPathMapper.Instance, null, out _, out var effective, out var bytes,
            serverMapFallback: false);

        effective.Should().BeTrue("автозагрузка CLAUDE.md отключается и без карты");
        bytes.Should().Be(0);
        args.Should().Equal("--tools", "Bash Read");
        args.Should().NotContain("--system-prompt-file");

        // Провайдерный профиль (fallback=true) серверную карту по-прежнему подставляет
        ClaudeSession.BuildBareModeArgs(project, server, "SystemPrompts/CLAUDE-local.md",
            ["Bash", "Read"], IdentityPathMapper.Instance, null, out _, out _, out _,
            serverMapFallback: true).Should().Contain("--system-prompt-file");

        // Проектная карта подхватывается и без fallback
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        File.WriteAllText(Path.Combine(project, "docs", "CLAUDE-local.md"), "# карта проекта");
        ClaudeSession.BuildBareModeArgs(project, server, "SystemPrompts/CLAUDE-local.md",
            ["Bash", "Read"], IdentityPathMapper.Instance, null, out _, out _, out var withMap,
            serverMapFallback: false).Should().Contain("--system-prompt-file");
        withMap.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Миграция_ВыставляетРешениеТолькоПерсонамБезРешения()
    {
        var path = Path.Combine(_root, "personas.json");
        var manager = new PersonaManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PersonasPath"] = path }).Build());
        var old = manager.Create("u", "Старая", null, null, null, null, null, PersonaScope.Global, null,
            null, null, true);
        old.LightContext = null;
        var decided = manager.Create("u", "Решённая", null, null, null, null, null, PersonaScope.Global,
            null, null, null, true, lightContext: false);

        var count = manager.MigrateLightContext(p => true);

        count.Should().Be(1);
        old.LightContext.Should().BeTrue();
        decided.LightContext.Should().BeFalse("уже принятое решение миграция не трогает");
        manager.MigrateLightContext(p => true).Should().Be(0, "идемпотентна");
    }

    private sealed class NoTierModels : ITierModelResolver
    {
        public string? TierModel(ModelTier tier) => null;
    }

    [Fact]
    public async Task Миграция_ПерсонеНаЛокальнойМодели_ВключаетОпцию_ОстальнымВыключает()
    {
        var path = Path.Combine(_root, "personas-migration.json");
        var manager = new PersonaManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PersonasPath"] = path }).Build());
        var onQwen = manager.Create("u", "Кузьма", null, null, null, null, null, PersonaScope.Global,
            null, null, null, true);
        onQwen.Model = "qwen3.8-27b";
        onQwen.LightContext = null;
        var onOpus = manager.Create("u", "Александр", null, null, null, null, null, PersonaScope.Global,
            null, null, null, true);
        onOpus.Model = "opus";
        onOpus.LightContext = null;

        var migration = new PersonaLightContextMigration(manager,
            new ModelAssignmentResolver(new NoTierModels()), Registry(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PersonaLightContextMigration>.Instance);
        await migration.StartAsync(CancellationToken.None);

        onQwen.LightContext.Should().BeTrue("провайдер модели персоны со своим облегчённым профилем");
        onOpus.LightContext.Should().BeFalse();
    }
}

/// <summary>
/// Фильтр инструментов (McpToolWhitelist) и ClaudeSession резолвят профиль одной функцией —
/// здесь доказываем со стороны фильтра: чат персоны на модели БЕЗ собственного профиля
/// (Opus-подобной) получает урезанный набор только при LightContext=true.
/// </summary>
public class PersonaLightContextWhitelistTests : IDisposable
{
    private const string Server = "test-light";
    private const string PlainModel = "light-plain-model";

    private sealed class DemoToolset : IMcpStaticToolset
    {
        public string Name => Server;
        public string Version => "0.0.1";
        private static McpToolSchema Schema(string n) => new(n, n,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        public IReadOnlyList<McpToolSchema> Tools { get; } = [Schema("demo_read"), Schema("demo_delete")];
        public Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
            McpToolCallContext context, CancellationToken ct) =>
            Task.FromResult(new McpToolCallResult("ok"));
    }

    private readonly TestWebApplicationFactory _factory;

    public PersonaLightContextWhitelistTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraConfig =
            {
                ["LlmProviders:light-plain:AnthropicBaseUrl"] = "https://example.invalid",
                ["LlmProviders:light-plain:ApiKey"] = "sk-test",
                ["LlmProviders:light-plain:Models:0:Id"] = PlainModel,
                [$"LlmProviders:LightProfile:KeepMcpTools:{Server}:0"] = "demo_read",
            },
        };
        _factory.ExtraServices = s => s.AddSingleton<IMcpToolset>(new DemoToolset());
    }

    public void Dispose() => _factory.Dispose();

    private async Task<IReadOnlyList<string>> ToolsForPersonaChatAsync(bool light, bool perturbTurn = false)
    {
        var client = _factory.CreateAuthenticatedClient();
        var persona = await client.PostAsJsonAsync("/api/personas",
            new { name = "Облегчённая", lightContext = light });
        persona.EnsureSuccessStatusCode();
        var personaJson = JsonSerializer.Deserialize<JsonElement>(await persona.Content.ReadAsStringAsync());
        var chat = await client.PostAsJsonAsync("/api/chats",
            new { mode = "auto", model = PlainModel, personaId = personaJson.GetProperty("id").GetString() });
        chat.EnsureSuccessStatusCode();
        var chatJson = JsonSerializer.Deserialize<JsonElement>(await chat.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-Caller-Session-Id", chatJson.GetProperty("id").GetString()!);
        // Признак персоны — свойство СЕССИИ: заголовки и тело «хода» состав не двигают
        // (инвариант McpToolsetStabilityTests; мерцание перезапускало бы CLI со всеми MCP)
        if (perturbTurn) client.DefaultRequestHeaders.Add("X-Mcp-Tool", "demo_delete");
        var resp = await client.PostAsJsonAsync($"/mcp/{Server}",
            perturbTurn
                ? new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = (object?)new { agentDepth = 3 } }
                : new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = (object?)null });
        resp.EnsureSuccessStatusCode();
        var list = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return [.. list.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)];
    }

    [Fact]
    public async Task ПерсонаСВключённымРежимом_НаМоделиБезПрофиля_ВидитУрезанныйНабор()
    {
        (await ToolsForPersonaChatAsync(light: true)).Should().Equal("demo_read");
    }

    [Fact]
    public async Task СоставСОблегчённымРежимом_НеЗависитОтЗаголовковИТелаХода()
    {
        (await ToolsForPersonaChatAsync(light: true, perturbTurn: true)).Should().Equal("demo_read");
    }

    [Fact]
    public async Task ПерсонаСВыключеннымРежимом_ВидитСерверЦеликом()
    {
        (await ToolsForPersonaChatAsync(light: false)).Should().BeEquivalentTo("demo_read", "demo_delete");
    }
}
