using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

/// <summary>
/// Сторож длины описаний MCP-инструментов. CLI ≥ 2.1.280 молча режет описание инструмента
/// и инструкцию сервера по CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH (2048): модель видит
/// усечённый текст без точки, и «не делай X» превращается в «не дел». Тест проходит по
/// ВСЕМ тулсетам из McpToolsetRegistry (статическим и параметризованным) и падает при
/// описании длиннее 2048 с именем инструмента. Платформонезависимый: резолвит реестр из
/// DI, не читает файлы и не запускает node.
/// </summary>
public class McpToolDescriptionLengthTests
{
    // Порог CLI: CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH. Описание длиннее — CLI режет молча.
    private const int MaxDescriptionLength = 2048;

    [Fact]
    public void НиОдноОписаниеИнструмента_НеДлиннееПорогаCli()
    {
        using var factory = new TestWebApplicationFactory();
        // CreateClient заставляет WebApplicationFactory собрать IServiceProvider — иначе
        // Services пустые, и резолвы падают с InvalidOperationException.
        _ = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<McpToolsetRegistry>();

        // Пустой реестр = сторож не проверил ничего (ложно-зелёный). Ядро тулсетов
        // зарегистрировано в боевом Program.cs, поэтому в полном контейнере он не пуст.
        registry.Names.Should().NotBeEmpty(
            "McpToolsetRegistry обязан содержать тулсеты — иначе сторож длины описаний не проверил бы ничего");

        var tooLong = new List<string>();
        foreach (var name in registry.Names)
        {
            var toolset = registry.Find(name);
            if (toolset is null) continue;
            foreach (var tool in ToolsOf(toolset))
            {
                if (tool.Description.Length > MaxDescriptionLength)
                    tooLong.Add($"{name}/{tool.Name} ({tool.Description.Length} символов)");
            }
        }

        tooLong.Should().BeEmpty(
            "описания длиннее 2048 символов CLI ≥ 2.1.280 режет молча "
            + "(CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH): " + string.Join("; ", tooLong));
    }

    // Состав инструментов тулсета: статический — Tools, параметризованный — ToolsFor на
    // фиксированном контексте. Длина описания — свойство схемы, от контекста не зависит;
    // контекст нужен только, чтобы параметризованный тулсет отдал состав. Если ToolsFor
    // на фиксированном контексте бросает (чужой владелец, fail-closed) — пропускаем:
    // сторож ловит длину, а не резолв.
    private static IEnumerable<McpToolSchema> ToolsOf(IMcpToolset toolset)
    {
        if (toolset is IMcpStaticToolset staticToolset)
        {
            foreach (var tool in staticToolset.Tools) yield return tool;
        }
        else if (toolset is IMcpParameterizedToolset param)
        {
            var context = new McpToolCallContext(OwnerId: "desc-guard", CallerSessionId: null);
            IEnumerable<McpToolSchema> tools;
            try { tools = param.ToolsFor(context); }
            catch { yield break; }
            foreach (var tool in tools) yield return tool;
        }
    }
}
