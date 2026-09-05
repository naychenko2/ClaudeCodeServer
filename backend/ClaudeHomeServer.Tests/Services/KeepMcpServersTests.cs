using System.Reflection;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож правки ClaudeSession.cs#853: при TrimMcpServers=true провайдера состав MCP-серверов
/// сокращается НЕ скопом, а выборочно — по белому списку KeepMcpServers. Главная цель —
/// оставлять локальному исполнителю ровно tasks (без него tasks_complete не вызывается,
/// задача-исполнитель не закрывается), а весь остальной набор (46 480 токенов при замере
/// 2026-09-05, ~21.7 ток/с генерации) гасить. Состав по-прежнему зависит только от СВОЙСТВ
/// СЕССИИ (EffectiveModel → провайдер, а из него — белый список): на McpToolsetStabilityTests
/// правка не влияет, сигнатура запуска стабильна.
///
/// Набор берётся из НАСТОЯЩЕГО BuildTurnMcpConfig через рефлексию — текстовая проверка тут
/// ничего не доказала бы: ошибка в условном сбросе (напр. typo в ключе «memory») тихо
/// оставила бы лишний сервер в конфиге хода и пожрала бы контекст.
/// </summary>
public class KeepMcpServersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-keep-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _configs = [];

    public KeepMcpServersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { /* уборка best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
    }

    // Реестр провайдеров: один локальный (TrimMcpServers=true, заданный белый список) + один
    // облачный (TrimMcpServers=false — состав не режется; нужен для регрессии «соседнего»
    // провайдера правка не задевает).
    private static LlmProviderRegistry BuildProviders(bool trimMcp, params string[] keep)
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:TrimMcpServers"] = trimMcp ? "true" : "false",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
        };
        for (var i = 0; i < keep.Length; i++)
            dict[$"LlmProviders:local-qwen:KeepMcpServers:{i}"] = keep[i];
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Полный набор продуктовых MCP-контекстов (HTTP-ветка): если бы серверы были активны,
    // их объявление прошло бы в `servers` — после блока TrimMcpServers большинство из них
    // гасится. Реальные stdio-файлы серверов здесь не нужны (UseHttp=true), так что тест не
    // зависит от дерева репозитория.
    private static LlmSessionContext AllHttpMcp() => new(
        RootPath: Path.Combine(Path.GetTempPath(), "ccs-keep-mcp-" + Guid.NewGuid().ToString("N")[..8]),
        OnMessage: _ => Task.CompletedTask,
        RawSystemPrompt: null,
        PermissionRules: null,
        TasksMcp: new TasksMcpContext("http://localhost:5999", () => "tok", ProjectId: null,
            UseHttp: true),
        NotesMcp: new NotesMcpContext("http://localhost:5999", () => "tok", ProjectId: null,
            AnnotationsEnabled: false, UseHttp: true),
        // Memory требует PersonaId (не nullable). Берём тестовую заглушку — реальный сервер
        // в этом тесте всё равно не дёргается, нас интересует только гейт hasMemory.
        MemoryMcp: new MemoryMcpContext("http://localhost:5999", () => "tok", PersonaId: "p1",
            ProjectId: "proj1", UseHttp: true),
        // Personas требует ProjectId (позиционный, nullable). MentionsToolsEnabled=false
        // роняет подсказку на стороне сервера, но не сам инструмент.
        PersonasMcp: new PersonasMcpContext("http://localhost:5999", () => "tok", ProjectId: "proj1",
            MentionsToolsEnabled: false),
        // Workspace требует Sections (даже пустой список проходит). ChatContextEnabled
        // оставляем дефолтом (false) — context_list вне dark launch.
        WorkspaceMcp: new WorkspaceMcpContext("http://localhost:5999", () => "tok", "proj1",
            Sections: []),
        NotificationsMcp: new NotificationsMcpContext("http://localhost:5999", () => "tok"),
        // ModulesMcpContext принимает список серверов. Пустой список = «модулей нет»,
        // hasModules=false после сборки, как и нужно.
        ModulesMcp: new ModulesMcpContext(new List<ModuleMcpServer>()),
        WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", () => "tok", UseHttp: true),
        // CodeGraph требует ProjectId (non-nullable).
        CodeGraphMcp: new CodeGraphMcpContext("http://localhost:5999", () => "tok", "proj1"),
        DifyMcp: null, // dify живёт за секцией Dify appsettings — без неё hasDify=false
        // Desktop — отдельный токен грани; нам важно проверить гейт hasDesktop,
        // а реальный файл ищется локально — null/none файла → hasDesktop=false,
        // но мы хотим видеть, что блок TrimMcp НЕ гасит desktop отдельно от себя.
        DesktopMcp: new DesktopMcpContext("http://localhost:5999", "tok", "sess-test"));

    // Вызов приватного BuildTurnMcpConfig готовой сессии + разбор temp-конфига.
    // Возвращает СПИСОК КЛЮЧЕЙ серверов из конфига (порядок не важен): с ним тесты говорят
    // «tasks есть», «notes нет», минуя привязку к форме JSON.
    private IReadOnlyList<string> BuildFor(LlmProviderRegistry providers, string model = "qwen3.8-27b")
    {
        var context = AllHttpMcp();
        var session = new ClaudeSession(new Session { Model = model }, context, providers: providers);
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
    /// При TrimMcpServers=true и KeepMcpServers=["tasks"] — tasks остаётся, остальные
    /// продуктовые серверы выключены. Регрессия на задачу 556c6561 — без этого теста любой
    /// typo в ключе «memory» оставил бы лишний сервер в конфиге хода и пожрал бы контекст.
    /// </summary>
    [Fact]
    public void KeepTasks_ОстальноеВыключено()
    {
        var servers = BuildFor(BuildProviders(trimMcp: true, "tasks"));

        servers.Should().Contain("tasks",
            "tasks в белом списке — локальный исполнитель задач остаётся со своим единственным инструментом");
        foreach (var key in new[] { "notes", "memory", "personas", "wsp", "notifications",
                                    "widgets", "codegraph", "dify", "watch", "fal-ai", "glif" })
        {
            servers.Should().NotContain(key,
                $"сервер «{key}» НЕ в белом списке — гасится селективным блоком");
        }
    }

    /// <summary>
    /// Регрессия прежнего поведения: при TrimMcpServers=true и пустом KeepMcpServers гасится
    /// всё (как до правки). Защищает инвариант «пустой список = прежнее «всё или ничего»»:
    /// если бы гашение скопом потерялось, белый список без надобности выключил бы всё подряд.
    /// </summary>
    [Fact]
    public void ПустойKeepВсёВыключеноКромеDesktop()
    {
        var servers = BuildFor(BuildProviders(trimMcp: true));

        foreach (var key in new[] { "tasks", "notes", "memory", "personas", "wsp", "notifications",
                                    "widgets", "codegraph", "dify", "watch", "fal-ai", "glif" })
            servers.Should().NotContain(key,
                $"пустой KeepMcpServers = прежнее «всё или ничего»: «{key}» не в списке, гасится");
    }

    /// <summary>
    /// TrimMcpServers=false (по умолчанию) — белый список игнорируется, состав полный. Тест
    /// защищает от регрессии «провайдер с TrimMcpServers=false вдруг тоже режется», что
    /// сломало бы родной Claude и облачных провайдеров.
    /// </summary>
    [Fact]
    public void TrimMcpВыключен_СоставНеРежется()
    {
        var servers = BuildFor(BuildProviders(trimMcp: false));

        servers.Should().Contain("tasks", "провайдер с TrimMcpServers=false не трогает состав — tasks на месте");
        servers.Should().Contain("notes", "провайдер с TrimMcpServers=false не трогает состав — notes на месте");
    }

    /// <summary>
    /// Белый список нечувствителен к регистру: KeepMcpServers=["TASKS"] оставляет tasks,
    /// словно в конфиге написано «tasks». Совпадает с правилом сравнения OrdinalIgnoreCase
    /// в ClaudeSession.BuildTurnMcpConfig — иначе конфиг пришлось бы причёсывать точно.
    /// </summary>
    [Fact]
    public void KeepРегистрНеВажен()
    {
        var servers = BuildFor(BuildProviders(trimMcp: true, "TASKS"));

        servers.Should().Contain("tasks", "OrdinalIgnoreCase: «TASKS» в белом списке = «tasks»");
        servers.Should().NotContain("notes");
    }

    /// <summary>
    /// На провайдере без TrimMcpServers состав ВСЕГДА полный — белый список и локальный
    /// исполнитель задач работают только на тех провайдерах, где TrimMcpServers включён.
    /// Защищает от регрессии «белый список применился к чужому провайдеру».
    /// </summary>
    [Fact]
    public void ОблачныйПровайдер_НеЗадеваетЛокальныйKeep()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:TrimMcpServers"] = "false",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:TrimMcpServers"] = "true",
            ["LlmProviders:deepseek:KeepMcpServers:0"] = "tasks",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
        };
        var providers = new LlmProviderRegistry(
            new ConfigurationBuilder().AddInMemoryCollection(dict).Build());

        // Ход на deepseek-v4-pro (TrimMcpServers=true) — белый список deepseek должен
        // применяться; ждём состав ровно «tasks».
        var deepseekServers = BuildFor(providers, model: "deepseek-v4-pro");
        deepseekServers.Should().Contain("tasks");
        deepseekServers.Should().NotContain("notes");

        // Ход на qwen3.8-27b (TrimMcpServers=false) — белый список вообще не применяется.
        var qwenServers = BuildFor(providers, model: "qwen3.8-27b");
        qwenServers.Should().Contain("tasks");
        qwenServers.Should().Contain("notes", "на провайдере с TrimMcpServers=false белый список молчит");
    }
}
