using ClaudeHomeServer.Services.Sessions;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Сужение набора MCP слепком этапа (B3, И5): Stage.McpAllow — фильтр ПОВЕРХ уже выданного
// персоне/проекту, а не источник новых серверов. Итог = выданное ∩ McpAllow, поэтому чужой
// ключ (которого персоне не выдано) ничего не добавляет: контексты строятся только для выданных
// серверов, а StageAllows лишь решает, оставить ли их.
public class StageMcpAllowTests
{
    [Fact]
    public void StageAllows_БезСужения_ПропускаетВсё()
    {
        BuiltinMcpContexts.StageAllows(null, "notes").Should().BeTrue();
        BuiltinMcpContexts.StageAllows(null, "любой-ключ").Should().BeTrue();
    }

    [Fact]
    public void StageAllows_ПересечениеНеОбъединение_ПропускаетТолькоПереченное()
    {
        IReadOnlyList<string> allow = ["notes", "wsp"];

        BuiltinMcpContexts.StageAllows(allow, "notes").Should().BeTrue();
        BuiltinMcpContexts.StageAllows(allow, "WSP").Should().BeTrue("ключи сравниваются без учёта регистра");
        BuiltinMcpContexts.StageAllows(allow, "higgsfield").Should().BeFalse("этап не перечислил сервер — его нет у исполнителя");
        BuiltinMcpContexts.StageAllows(allow, "personas").Should().BeFalse();
    }

    [Fact]
    public void StageAllows_ЧужойКлюч_НеДаётЕгоИсполнителю_ИНеВключаетОстальные()
    {
        // Этап перечислил сервер, которого у персоны нет: фильтр его не «создаёт» (создаёт
        // контексты только выдача персоны), а остальные выданные серверы он отрезает
        IReadOnlyList<string> allow = ["чужой-сервер"];

        BuiltinMcpContexts.StageAllows(allow, "notes").Should().BeFalse();
        BuiltinMcpContexts.StageAllows(allow, "memory").Should().BeFalse();
    }

    [Fact]
    public void StageAllows_ПустойСписок_СнимаетВсёКромеTasks()
    {
        IReadOnlyList<string> allow = [];

        BuiltinMcpContexts.StageAllows(allow, "notes").Should().BeFalse();
        // Исполнитель обязан закрыть свою задачу через mcp__tasks__*: сервер tasks этап не снимает
        BuiltinMcpContexts.StageAllows(allow, "tasks").Should().BeTrue();
    }
}
