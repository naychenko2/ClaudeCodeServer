using System.Reflection;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож на задачу 8628bbf4: у провайдера с TrimMcpServers=true состав MCP-серверов в
/// конфиге хода совпадает с KeepMcpServers — каким бы путём сервер ни приехал. Базовый
/// KeepMcpServersTests проверяет селективный сброс флагов hasX (точечный Keep для
/// каждого сервера до сборки), но оставляет дыру: сервер, проскочивший мимо hasX
/// (через базовый McpConfigPath, через personal registry externalMcp, через блок,
/// добавленный без индивидуальной проверки Keep) ехал в окно локальной модели.
/// Финальный рубеж в BuildTurnMcpConfig (фильтр уже собранного словаря servers по
/// KeepMcpServers) эту дыру закрывает. Эти тесты фиксируют контракт:
/// в конфиге хода у trim-провайдера — только разрешённые ключи, источник не важен.
/// </summary>
public class TrimMcpServersKeepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-trim-keep-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _configs = [];
    private readonly List<string> _files = [];

    public TrimMcpServersKeepTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { /* уборка best-effort */ }
        foreach (var path in _files)
            try { File.Delete(path); } catch { /* уборка best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
    }

    // Один локальный провайдер с TrimMcpServers=true. Состав KeepMcpServers задаётся
    // параметром — тесты проверяют, что только он оказывается в конфиге хода.
    private static LlmProviderRegistry BuildTrimProvider(params string[] keep)
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:TrimMcpServers"] = "true",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
        };
        for (var i = 0; i < keep.Length; i++)
            dict[$"LlmProviders:local-qwen:KeepMcpServers:{i}"] = keep[i];
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Готовая сессия с активными HTTP-контекстами для tasks/notes/memory/etc., чтобы
    // они прошли первый рубеж (hasX) и попали в servers; дальше в игру вступает
    // второй рубеж — финальная фильтрация по KeepMcpServers.
    private static LlmSessionContext AllHttpMcp(ExternalMcpContext? external = null,
        DifyMcpContext? dify = null) => new(
        RootPath: Path.Combine(Path.GetTempPath(), "ccs-trim-keep-" + Guid.NewGuid().ToString("N")[..8]),
        OnMessage: _ => Task.CompletedTask,
        RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
        PermissionRules: null,
        TasksMcp: new TasksMcpContext("http://localhost:5999", () => "tok", ProjectId: null,
            UseHttp: true),
        NotesMcp: new NotesMcpContext("http://localhost:5999", () => "tok", ProjectId: null,
            AnnotationsEnabled: false, UseHttp: true),
        // Memory требует PersonaId (не nullable). Берём тестовую заглушку — реальный
        // сервер в этом тесте не дёргается, нас интересует только гейт hasMemory.
        MemoryMcp: new MemoryMcpContext("http://localhost:5999", () => "tok", PersonaId: "p1",
            ProjectId: "proj1", UseHttp: true),
        PersonasMcp: new PersonasMcpContext("http://localhost:5999", () => "tok", ProjectId: "proj1",
            MentionsToolsEnabled: false),
        WorkspaceMcp: new WorkspaceMcpContext("http://localhost:5999", () => "tok", "proj1",
            Sections: []),
        NotificationsMcp: new NotificationsMcpContext("http://localhost:5999", () => "tok"),
        ModulesMcp: new ModulesMcpContext(new List<ModuleMcpServer>()),
        WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", () => "tok", UseHttp: true),
        CodeGraphMcp: new CodeGraphMcpContext("http://localhost:5999", () => "tok", "proj1"),
        DifyMcp: dify,
        DesktopMcp: new DesktopMcpContext("http://localhost:5999", "tok", "sess-test"),
        ExternalMcpProvider: () => external);

    // Вызов приватного BuildTurnMcpConfig + разбор temp-конфига. Возвращает СПИСОК
    // КЛЮЧЕЙ серверов (порядок не важен).
    private IReadOnlyList<string> BuildFor(LlmSessionContext context, LlmProviderRegistry providers,
        string model = "qwen3.8-27b", string? mcpConfigPath = null)
    {
        var session = new ClaudeSession(new Session { Model = model }, context,
            mcpConfigPath: mcpConfigPath, providers: providers);
        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null])!;
        var type = result.GetType();
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;

        if (string.IsNullOrEmpty(path)) return [];
        _configs.Add(path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var mcpServers = (JsonObject)doc["mcpServers"]!;
        return [.. mcpServers.Select(kv => (string)kv.Key!), .. keys.Split(';', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Серверы из личного реестра владельца (ExternalMcpProvider) гасятся финальным
    /// рубежом, если они не в KeepMcpServers — даже если первый рубеж (hasX &=
    /// Keep(...) выше) не сработал (externalMcp гасится скопом через Keep("external"),
    /// но если разработчик забудет добавить эту строку для нового пути, сервер пройдёт).
    /// Воспроизводит кейс из задачи 8628bbf4: запись miro в личном реестре попала в
    /// конфиг хода, хотя KeepMcpServers её не разрешает.
    /// </summary>
    [Fact]
    public void ExternalРеестр_СерверыВнеKeep_ГасятсяФинальнымРубежом()
    {
        // Нарочно НЕ ставим "external" в KeepMcpServers — но даже если бы поставили,
        // конкретный ключ "miro" всё равно обязан отсеяться: внешний канал режется
        // по СВОИМ ключам, а не скопом "весь external".
        var external = new ExternalMcpContext(new List<ExternalMcpServer>
        {
            new("miro", "http", null, [],
                new Dictionary<string, string>(), "https://mcp.miro.com",
                new Dictionary<string, string>(), AlwaysLoad: false, AuthVersion: 1),
        });

        var servers = BuildFor(AllHttpMcp(external: external),
            BuildTrimProvider("tasks", "memory"));

        servers.Should().Contain("tasks");
        servers.Should().Contain("memory");
        servers.Should().NotContain("miro",
            "miro не в KeepMcpServers — финальный рубеж обязан его отрезать, "
            + "даже если разработчик забудет про скоп external");
    }

    /// <summary>
    /// Продуктовый http-сервер dify (DifyMcp) гасится финальным рубежом, если он
    /// не в KeepMcpServers. На trim-провайдере dify часто не нужен (модель работает
    /// с memory + codegraph, а поиск по знаниям делается иначе) — он ехал в окно
    /// в обход селективного hasDify &= Keep("dify"), когда trimMcp почему-то был
    /// выключен (например, EffectiveModel не резолвился в trim-провайдера).
    /// Финальный рубеж этот случай закрывает.
    /// </summary>
    [Fact]
    public void DifyMcp_КогдаНеВKeep_ГаситсяФинальнымРубежом()
    {
        var dify = new DifyMcpContext(
            ApiUrl: "http://localhost:5999",
            DifyUrl: "http://localhost:5999/dify",
            DifyKey: "test-key",
            TokenFactory: () => "tok",
            UseHttp: true);

        var servers = BuildFor(AllHttpMcp(dify: dify),
            BuildTrimProvider("tasks", "memory"));

        servers.Should().Contain("tasks");
        servers.Should().Contain("memory");
        servers.Should().NotContain("dify",
            "dify не в KeepMcpServers — финальный рубеж гасит его независимо от того, "
            + "как пришёл hasDify (true через _difyMcp, false через скоп)");
    }

    /// <summary>
    /// Серверы из McpConfigPath (внешний .mcp.json) тоже подчиняются финальному
    /// рубежу. Раньше этот путь был в обход TrimMcpServers: блок чтения базового
    /// конфига не имел проверки Keep и добавлял всё содержимое в servers скопом.
    /// Регрессия задачи 8628bbf4: если разработчик забудет индивидуальный Keep
    /// для нового блока, чужой конфиг поедет в окно локальной модели.
    /// </summary>
    [Fact]
    public void McpConfigPath_СерверыВнеKeep_ГасятсяФинальнымРубежом()
    {
        var cfgPath = Path.Combine(_root, "extra-mcp.json");
        File.WriteAllText(cfgPath, """
            {
              "mcpServers": {
                "miro":  { "type": "http", "url": "https://mcp.miro.com" },
                "dify":  { "type": "http", "url": "http://localhost:5999/dify",
                            "headers": { "Authorization": "Bearer x" } },
                "tasks": { "type": "http", "url": "http://localhost:5999/tasks" }
              }
            }
            """);
        _files.Add(cfgPath);

        var servers = BuildFor(AllHttpMcp(), BuildTrimProvider("tasks", "memory"),
            mcpConfigPath: cfgPath);

        servers.Should().Contain("tasks", "tasks в KeepMcpServers и в базовом конфиге");
        servers.Should().NotContain("miro",
            "miro в McpConfigPath, но не в KeepMcpServers — финальный рубеж обязан отрезать");
        servers.Should().NotContain("dify",
            "dify в McpConfigPath, но не в KeepMcpServers — финальный рубеж гасит");
    }

    /// <summary>
    /// В конфиге хода у trim-провайдера с KeepMcpServers=["tasks","memory"]
    /// ровно эти два ключа и больше ничего (из числа продуктовых HTTP-узлов).
    /// Состав строго соответствует KeepMcpServers — независимо от того, какие
    /// ещё ветки (notes/personas/...) активны у сессии.
    /// </summary>
    [Fact]
    public void СоставСовпадаетСKeepMcpServers_НезависимоОтПутиСервера()
    {
        // Контекст со всеми возможными продуктовыми ветками активными; dify
        // подан через DifyMcp (продуктовый путь), external — пустой, McpConfigPath
        // тоже не задан: проверяем что сам факт TrimMcpServers у провайдера даёт
        // ИМЕННО KeepMcpServers в конфиге хода и ничего сверх.
        var servers = BuildFor(AllHttpMcp(), BuildTrimProvider("tasks", "memory"));

        // Возвращённая коллекция содержит дубликаты из сигнатуры shapes ("tasks:t:http"
        // и просто "tasks") — фильтруем уникальные имена серверов через разбор ':'.
        var uniqueServerKeys = servers
            .Select(k => k.Split(':', 2)[0])
            .Distinct()
            .ToList();

        uniqueServerKeys.Should().BeEquivalentTo(new[] { "tasks", "memory" },
            "у провайдера с TrimMcpServers=true в конфиг хода едет строго KeepMcpServers, "
            + "остальные продуктовые ветки (notes/personas/widgets/codegraph/dify/...) "
            + "гасятся первым рубежом hasX &= Keep");
    }
}
