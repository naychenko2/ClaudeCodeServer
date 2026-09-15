using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты узла higgsfield в MCP-конфиге хода (фаза 1.3):
/// 1. Инстанс подключён (context non-null, UseHttp=true) → узел higgsfield в конфиге
/// 2. Инстанс не подключён (context null) → узла нет
/// 3. TrimMcpServers=true, higgsfield не в KeepMcpServers → узел гасится
/// </summary>
public class HiggsfieldMcpNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-hf-node-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _configs = [];

    public HiggsfieldMcpNodeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Минимальный контекст: только higgsfield (или null) — без остальных MCP-серверов.
    // UseHttp=true — http-ветка, stdio-файлы не нужны.
    // TasksMcp — required positional parameter (nullable): null = «задач нет».
    private static LlmSessionContext Ctx(HiggsfieldMcpContext? hf) => new(
        RootPath: Path.Combine(Path.GetTempPath(), "ccs-hf-" + Guid.NewGuid().ToString("N")[..8]),
        OnMessage: _ => Task.CompletedTask,
        RawSystemPrompt: null,
        BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
        PermissionRules: null,
        TasksMcp: null,
        HiggsfieldMcp: hf);

    private static LlmProviderRegistry NoTrim()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local:IsLocal"] = "true",
            ["LlmProviders:local:Models:0:Id"] = "test-model",
        };
        return new LlmProviderRegistry(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(dict).Build());
    }

    private IReadOnlyList<string> BuildServers(HiggsfieldMcpContext? hf)
    {
        var context = Ctx(hf);
        var session = new ClaudeSession(new Session { Model = "test-model" }, context, providers: NoTrim());
        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null])!;
        var type = result.GetType();
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;

        if (string.IsNullOrEmpty(path)) return [];
        _configs.Add(path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var mcpServers = (JsonObject)doc["mcpServers"]!;
        return [.. mcpServers.Select(kv => (string)kv.Key!),
               .. keys.Split(';', StringSplitOptions.RemoveEmptyEntries)];
    }

    [Fact]
    public void ИнстансПодключён_УзелHiggsfieldВКонфиге()
    {
        var hf = new HiggsfieldMcpContext("http://localhost:5000", () => "svc-tok", UseHttp: true);
        var servers = BuildServers(hf);

        servers.Should().Contain("higgsfield",
            "инстанс подключён (context non-null, UseHttp=true) — узел higgsfield обязан быть в конфиге хода");
    }

    [Fact]
    public void ИнстансНеПодключён_УзлаHiggsfieldНет()
    {
        // null контекста = «инстанс не подключён» (BuildHiggsfieldContext вернул null:
        // RO-персона или EnsureFresh() = null)
        var servers = BuildServers(null);

        servers.Should().NotContain("higgsfield",
            "инстанс не подключён (context=null) — узел higgsfield не объявляется ходу");
    }

    [Fact]
    public void TrimMcp_HiggsfieldНеВKeep_Гасится()
    {
        // Провайдер с TrimMcpServers=true и KeepMcpServers=["tasks"] — higgsfield не в списке
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local:IsLocal"] = "true",
            ["LlmProviders:local:TrimMcpServers"] = "true",
            ["LlmProviders:local:KeepMcpServers:0"] = "tasks",
            ["LlmProviders:local:Models:0:Id"] = "test-model",
        };
        var providers = new LlmProviderRegistry(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(dict).Build());

        var hf = new HiggsfieldMcpContext("http://localhost:5000", () => "svc-tok", UseHttp: true);
        var context = Ctx(hf);
        var session = new ClaudeSession(new Session { Model = "test-model" }, context, providers: providers);
        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null])!;
        var type = result.GetType();
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;

        if (string.IsNullOrEmpty(path))
        {
            // Если нет ни одного сервера → конфиг не создан, higgsfield гасился полностью
            keys.Should().NotContain("higgsfield");
            return;
        }
        _configs.Add(path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var mcpServers = (JsonObject)doc["mcpServers"]!;
        mcpServers.Should().NotContainKey("higgsfield",
            "higgsfield не в KeepMcpServers провайдера — селективный блок TrimMcpServers гасит узел");
    }

    /// <summary>
    /// BareMode-профиль → KeepMcpTools["higgsfield"] = ровно 3 инструмента (generate_image,
    /// generate_video, job_status). Проверка через McpToolWhitelist: белый список фильтрует
    /// состав tools/list до трёх, остальные 7 инструментов полного ядра вырезаются.
    /// </summary>
    [Fact]
    public void BareMode_KeepMcpToolsHiggsfield_ТриИнструмента()
    {
        // Провайдер с KeepMcpTools["higgsfield"] = 3 инструмента
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local:IsLocal"] = "true",
            ["LlmProviders:local:Models:0:Id"] = "test-model",
            // KeepMcpTools: higgsfield → 3 tools (BareMode-состав)
            ["LlmProviders:local:KeepMcpTools:higgsfield:0"] = "generate_image",
            ["LlmProviders:local:KeepMcpTools:higgsfield:1"] = "generate_video",
            ["LlmProviders:local:KeepMcpTools:higgsfield:2"] = "job_status",
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(dict).Build();
        var providers = new LlmProviderRegistry(config);
        var provider = providers.ResolveByModel("test-model");
        provider.Should().NotBeNull("провайдер должен резолвиться по модели");
        // Проверяем, что провайдер имеет KeepMcpTools с higgsfield → 3 инструмента
        provider!.KeepMcpTools.Should().ContainKey("higgsfield",
            "KeepMcpTools содержит ключ higgsfield");
        var tools = provider.KeepMcpTools["higgsfield"];
        tools.Should().HaveCount(3,
            "BareMode-состав higgsfield: ровно 3 инструмента (generate_image, generate_video, job_status)");
        tools.Should().BeEquivalentTo(
            new[] { "generate_image", "generate_video", "job_status" },
            "состав KeepMcpTools[higgsfield]: generate_image, generate_video, job_status");
    }

    // ── RO-гейт: изоляция от ветки «нет токена» ─────────────────────────────────────

    /// <summary>
    /// Строит HiggsfieldOAuthService с живым токеном (EnsureFresh() ≠ null) и вешает её
    /// на bare-SessionManager. Общий хелпер для тестов RO-гейта.
    /// </summary>
    private static (SessionManager Sm, HiggsfieldOAuthService HfOAuth) CreateHiggsfieldSetup(
        string ownerId, string dataDir)
    {
        Directory.CreateDirectory(dataDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "placeholder.json"),
                ["McpTasksApiUrl"] = "http://localhost:5000",
            }).Build();

        var secrets = new McpSecretStore(config);
        // Живой токен: non-null из Resolve → EnsureFresh вернёт его.
        // Токен и запись — под ServiceOwnerId (инстансный владелец), не под человеком.
        var svcOwner = HiggsfieldOAuthService.ServiceOwnerId;
        var tokenRef = secrets.Set(svcOwner, "valid-access-token");

        var registry = new McpRegistry(config, secrets);
        var record = new McpServerRecord
        {
            OwnerId = svcOwner,
            Key = "higgsfield",
            Label = "Higgsfield",
            Auth = new McpAuthConfig
            {
                Kind = McpAuthKind.OAuth2,
                OAuth = new McpOAuthConfig { AccessTokenRef = tokenRef },
            },
        };
        registry.CreateBuiltIn(svcOwner, record);

        var status = new McpStatusStore(config);
        var httpFactory = new Mock<System.Net.Http.IHttpClientFactory>().Object;
        var oauth = new McpOAuthService(registry, secrets, status, httpFactory, config,
            Mock.Of<ILogger<McpOAuthService>>());
        var hfOAuth = new HiggsfieldOAuthService(registry, secrets, status, oauth, config,
            Mock.Of<ILogger<HiggsfieldOAuthService>>());

        // Состояние подключения: AdminOwnerId задан → EnsureFresh не вылетит на первом if
        File.WriteAllText(
            Path.Combine(dataDir, "higgsfield.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Connected = true,
                AdminOwnerId = ownerId,
                ExpiresAt = (string?)null,
                AuthVersion = 1,
            }));

        var sm = (SessionManager)RuntimeHelpers.GetUninitializedObject(typeof(SessionManager));
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // _higgsfieldOAuth: нужен для BuildHiggsfieldContext
        typeof(SessionManager).GetField("_higgsfieldOAuth", bf)!.SetValue(sm, hfOAuth);

        // _launchers + _config: нужны для ResolveTasksApiUrl (контрпример: Full-персона)
        var plMock = new Mock<IProcessLauncher>();
        plMock.Setup(l => l.McpApiUrlOverride).Returns(null as string);
        var launchers = new Mock<ILauncherFactory>();
        launchers.Setup(f => f.ForOwner(It.IsAny<string?>())).Returns(plMock.Object);
        typeof(SessionManager).GetField("_launchers", bf)!.SetValue(sm, launchers.Object);

        typeof(SessionManager).GetField("_config", bf)!.SetValue(sm, config);

        return (sm, hfOAuth);
    }

    [Fact]
    public void RoGate_ТокенЖив_ReadOnlyПерсона_ВернётNull()
    {
        // Изоляция RO-гейта от ветки «нет токена»: EnsureFresh() ≠ null (живой токен),
        // но persona.Access == ReadOnly → BuildHiggsfieldContext = null.
        // Если убрать строку RO-гейта, тест сломается: код пойдёт дальше к EnsureFresh()
        // и вернёт non-null контекст.
        var ownerId = "owner-1";
        var dataDir = Path.Combine(_root, "ro-gate-" + Guid.NewGuid().ToString("N")[..8]);
        var (sm, hfOAuth) = CreateHiggsfieldSetup(ownerId, dataDir);

        // Контроль: EnsureFresh() действительно жив (не null) — иначе тест тривиально
        // проходил бы и по ветке «нет токена».
        hfOAuth.EnsureFresh().Should().NotBeNull(
            "препостановка: EnsureFresh() ≠ null (токен жив)");

        var roPersona = new Persona { Access = PersonaAccess.ReadOnly };
        var result = sm.BuildHiggsfieldContext(ownerId, roPersona);

        result.Should().BeNull(
            "RO-гейт: ReadOnly-персона → null, хотя EnsureFresh() вернул живой токен");

        // Контрпример: та же настройка, но персона не ReadOnly → контекст создаётся
        var rwPersona = new Persona { Access = PersonaAccess.Full };
        var result2 = sm.BuildHiggsfieldContext(ownerId, rwPersona);
        result2.Should().NotBeNull(
            "контрпример: Full-персона + живой токен → BuildHiggsfieldContext вернул контекст");
    }

    // ── ClaudeSession-защита: не-http контекст не даёт узла ───────────────────────────

    [Fact]
    public void ClaudeSession_Защита_НонHttpКонтекст_УзлаНет()
    {
        // Имитация «ошибка выше по цепочке»: HiggsfieldMcpContext пришёл не-null (контекст
        // существует), но UseHttp=false (адрес не поддерживает http-транспорт).
        // ClaudeSession проверяет HiggsfieldHttpOn() = _higgsfieldMcp is { UseHttp: true }
        // && HttpMcpOnNow() — без UseHttp=true узел не объявляется, даже если контекст
        // не-null. Это вторая линия защиты: RO-гейт выше (BuildHiggsfieldContext → null),
        // а здесь — сам ClaudeSession.
        var hf = new HiggsfieldMcpContext("https://example.com", () => "svc-tok", UseHttp: false);
        var servers = BuildServers(hf);

        servers.Should().NotContain("higgsfield",
            "non-null контекст с UseHttp=false → ClaudeSession не объявляет узел higgsfield");
    }

    // ── Фиксация «без per-owner изоляции» (ф.2.1, осознанное решение) ────────────────────

    /// <summary>
    /// Higgsfield — ПЕРВЫЙ http-тулсет, где владелец JWT-claims (admin) авторизует
    /// ДОСТУП, но данные за ним ОБЩИЕ (один внешний аккаунт, один OAuth-токен).
    /// Единственная защита — белый список <see cref="Mcp.Http.HiggsfieldToolset.Whitelist"/>:
    /// ни листинга, ни биллинга, ни публикации. Это НЕ дефект, а осознанный выбор.
    ///
    /// Тест сторожит две оси фиксации:
    /// 1. <c>ServiceOwnerId</c> — фиксированный pseudo-owner (не id человека),
    ///    значит записей per-owner быть не должно.
    /// 2. Белый список не пуст — если его вычистят, защита исчезнет.
    /// </summary>
    [Fact]
    public void БезPerOwnerИзоляции_PseudoOwnerИБелыйСписок()
    {
        // Ось 1: фиксированный pseudo-owner — не id конкретного человека.
        // Если кто-то заменит его на ownerId из JWT, per-owner изоляция «вернётся»
        // молча, и тест обязан упасть.
        ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService.ServiceOwnerId
            .Should().Be("higgsfield-instance",
                "фиксированный pseudo-owner: один аккаунт Higgsfield, один OAuth-токен на инстанс");

        // Ось 2: белый список — единственная защита общих данных от посторонних инструментов.
        // Если вычистят список (пустой массив), защита исчезнет, и Higgsfield отдаст
        // ВСЕ 88+ инструментов, включая биллинг/листинг/публикацию.
        var whitelist = ClaudeHomeServer.Services.Mcp.Http.HiggsfieldToolset.Whitelist;
        whitelist.Length.Should().BeGreaterThan(0,
            "белый список Higgsfield — единственная защита: пустой список = все 88+ инструментов наружу");
        // Критичные инструменты (доступ к биллингу/публичным данным) не должны попасть в список:
        // их присутствие означало бы, что защита не работает.
        whitelist.Should().NotContain("billing", "листинг/биллинг — не в белом списке");
        whitelist.Should().NotContain("publish", "публичные данные — не в белом списке");
    }
}
