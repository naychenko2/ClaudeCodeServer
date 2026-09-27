using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Белые списки инструментов (LlmProviders:*:KeepMcpTools) в ОТГРУЖАЕМОМ appsettings.json.
// Фильтр McpToolWhitelist берёт пересечение списка с настоящим составом сервера и опечатку
// не замечает: неверное имя молча выключает инструмент у локальной модели. Поэтому каждое
// имя сверяется с полным составом настоящего тулсета, а сервер без эталона здесь — отказ:
// новый ключ в конфиге обязан прийти вместе с эталоном в этом тесте.
public class KeepMcpToolsConfigTests
{
    private static string AppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ClaudeHomeServer", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Не найден appsettings.json сервера от " + AppContext.BaseDirectory);
    }

    private static IConfiguration Real() => new ConfigurationBuilder().AddJsonFile(AppSettingsPath()).Build();

    // Полный состав каждого сервера — объединение всех его вариантов (проектный/личный чат,
    // личная/командная память/досье), как их отдают сами тулсеты
    private static readonly Dictionary<string, HashSet<string>> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tasks"] = Names(TasksToolset.ProjectChatTools, TasksToolset.PersonalTools),
        ["memory"] = Names(MemoryToolset.PersonalTools, MemoryToolset.TeamTools, MemoryToolset.DossierTools),
        ["codegraph"] = Names(CodeGraphToolset.AllTools),
        ["websearch"] = Names(WebSearchToolset.Tools),
        ["watch"] = Names(WatchToolset.Tools),
    };

    private static HashSet<string> Names(params IReadOnlyList<McpToolSchema>[] sets) =>
        sets.SelectMany(s => s).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void ВсеИменаВБелыхСписках_СуществуютВНастоящихТулсетах()
    {
        var checkedLists = 0;
        foreach (var provider in Real().GetSection("LlmProviders").GetChildren())
        {
            var keep = provider.GetSection("KeepMcpTools");
            var keepServers = provider.GetSection("KeepMcpServers").Get<string[]>() ?? [];
            var trims = provider.GetValue("TrimMcpServers", false);
            foreach (var server in keep.GetChildren())
            {
                KnownTools.Should().ContainKey(server.Key,
                    $"у {provider.Key}:KeepMcpTools:{server.Key} нет эталона в тесте — добавь состав тулсета");
                var tools = server.Get<string[]>() ?? [];
                tools.Should().NotBeEmpty($"{provider.Key}:KeepMcpTools:{server.Key} пуст — фильтр трактует это как «фильтра нет»");
                tools.Should().OnlyContain(t => KnownTools[server.Key].Contains(t),
                    $"{provider.Key}:KeepMcpTools:{server.Key} — каждое имя обязано быть инструментом сервера (регистр тоже)");
                tools.Should().OnlyHaveUniqueItems();
                if (trims)
                    keepServers.Should().Contain(server.Key,
                        $"у {provider.Key} сервер режется TrimMcpServers — список без сервера в KeepMcpServers мёртвый");
                checkedLists++;
            }
        }
        checkedLists.Should().BePositive("сторож без единого списка ничего не доказывает — проверь путь конфига");
    }

    // Состав локальной модели по фактическому спросу (решение 2026-09-19): закреплён целиком,
    // чтобы расширение или сужение шло осознанно, с новыми цифрами в #comment-keeptools-demand
    [Fact]
    public void LocalQwen_СоставПоФактическомуСпросу()
    {
        var keep = Real().GetSection("LlmProviders:local-qwen:KeepMcpTools");

        keep.GetSection("tasks").Get<string[]>().Should().BeEquivalentTo(
            "tasks_get", "tasks_update", "tasks_complete", "tasks_add_subtask", "tasks_toggle_subtask", "tasks_create");
        keep.GetSection("memory").Get<string[]>().Should().BeEquivalentTo(
            "memory_recall", "memory_search", "memory_remember", "team_memory_search");
        keep.GetSection("codegraph").Get<string[]>().Should().BeEquivalentTo("codegraph_find");
        keep.GetChildren().Select(c => c.Key).Should().BeEquivalentTo(["tasks", "memory", "codegraph"],
            "websearch и watch остаются целиком");
    }
}
