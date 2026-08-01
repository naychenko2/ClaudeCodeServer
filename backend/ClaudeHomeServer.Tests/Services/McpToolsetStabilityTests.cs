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
    private static string? FindSource() => FindSource("Services", "Llm", "Claude", "ClaudeSession.cs");

    private static string? FindSource(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, "backend", "ClaudeHomeServer", .. relative]);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // Тело метода SessionManager по его сигнатуре (до начала следующего объявления)
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"метод «{signature}» обязан существовать");
        var end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        if (end < 0) end = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "за методом обязано идти следующее объявление");
        return string.Join('\n', source[start..end].Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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

    /// <summary>
    /// Per-persona рубильники серверов (ключи personas/consultants/codegraph/notifications/
    /// widgets): гейт обязан жить в SessionManager и решаться ТОЛЬКО по персоне — иначе состав
    /// tools/list начнёт зависеть от хода со всеми последствиями выше.
    /// </summary>
    [SkippableTheory]
    [InlineData("private WidgetsMcpContext? BuildWidgetsContext", "widgets")]
    [InlineData("private NotificationsMcpContext? BuildNotificationsContext", "notifications")]
    [InlineData("private CodeGraphMcpContext? BuildCodeGraphContext", "codegraph")]
    [InlineData("private Func<string?, Task<string?>>? BuildCodeGraphProvider", "codegraph")]
    [InlineData("private bool PersonasEnabled", "personas")]
    [InlineData("private bool ConsultantsEnabled", "consultants")]
    public void РубильникиСерверов_ГейтятсяТолькоПоПерсоне(string signature, string key)
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!), signature);

        body.Should().Contain($"\"{key}\"",
            $"сервер обязан гейтиться Tool-ключом {key} (Off-привязка type: tool, target: {key})");
        body.Should().MatchRegex(@"(ServerToolEnabled|ConsultantsEnabled)\(",
            "решение принимает единая точка PersonaBindingsService.ServerToolEnabled "
            + "(для консультантов и персон — обёртки ConsultantsEnabled/PersonasEnabled "
            + "с исключением для групповых чатов)");
        body.Should().NotContain("_currentTurn",
            "состояние хода не должно влиять на состав серверов");
    }

    /// <summary>
    /// Провайдер сабагентов-консультантов (pmem-серверы + --add-dir) гейтится тем же
    /// ConsultantsEnabled, а не собственной копией правила.
    /// </summary>
    [SkippableFact]
    public void ПровайдерКонсультантов_ГейтитсяЧерезConsultantsEnabled()
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!),
            "private Func<PersonaAgentsContext?>? BuildPersonaAgentsProvider");

        body.Should().Contain("ConsultantsEnabled(");
    }
}
