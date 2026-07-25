using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Инвариант: СОСТАВ инструментов MCP-серверов не зависит от хода.
///
/// Набор ключей серверов и отпечаток состава их инструментов входят в сигнатуру запуска CLI
/// (BuildLaunchSignature). Как только состав начинает зависеть от свойств хода — глубины
/// делегирования, текста, флага подавления — сигнатура «мерцает» между ходами, и каждый такой
/// переход убивает процесс claude вместе со ВСЕМИ MCP-серверами: незавершённые вызовы падают
/// «Stream closed», а инструменты то появляются, то исчезают («No such tool available»).
///
/// На эти грабли наступали трижды: WORKSPACE_WRITE по интенту хода, PERSONAS_WRITE/MENTIONS по
/// тексту, TASKS_EXECUTE и срезание секций chats/destructive по agentDepth. Все ограничения
/// теперь проверяет бэкенд по актуальному состоянию сессии (DenyOnDelegatedTurnAttribute),
/// а состав остаётся постоянным.
///
/// Тест сторожевой: читает исходник BuildTurnMcpConfig и следит, чтобы в нём не появилось
/// обращений к состоянию хода. Подсказки в системном промпте под запрет не попадают — они
/// в сигнатуру не входят и меняться от хода к ходу могут.
/// </summary>
public class McpToolsetStabilityTests
{
    private static string? FindSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "ClaudeHomeServer",
                "Services", "Llm", "Claude", "ClaudeSession.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public void СоставИнструментовХода_НеЗависитОтСостоянияХода()
    {
        var path = FindSource();
        Skip.If(path is null, "ClaudeSession.cs не найден (сборка вне дерева репозитория)");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("private (string? Path, string ServerKeys) BuildTurnMcpConfig", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "метод сборки MCP-конфига хода обязан существовать");

        // Конец метода — начало следующего (MapMcpPath идёт сразу за ним)
        var end = source.IndexOf("private string? MapMcpPath", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "за BuildTurnMcpConfig обязан идти MapMcpPath");

        var body = source[start..end];

        // Комментарии из проверки убираем: они объясняют ИСТОРИЮ гейтов и упоминают их имена
        var code = string.Join('\n', body.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        code.Should().NotContain("_currentTurnAgentDepth",
            "глубина делегирования не должна влиять на состав инструментов — гейт живёт на бэкенде "
            + "(DenyOnDelegatedTurnAttribute), иначе процесс CLI перезапускается между ходами");
        code.Should().NotContain("_currentTurnSuppressTasksExecute",
            "подавление запуска исполнителя проверяет бэкенд, а не состав инструментов");
        code.Should().NotContain("turnText",
            "текст хода не должен влиять на состав инструментов (грабли WriteIntentGate)");
    }
}
