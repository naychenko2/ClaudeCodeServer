using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Адресат доклада новой задачи, унаследованный от задачи-родителя вызывающего чата
public class InheritedReportTargetTests
{
    private const string Caller = "caller";

    // Активная (ждёт результата) задача, которую исполняет чат Caller
    private static TaskItem Active(string? reportTo = null, string? launchedBy = null, string? source = null) => new()
    {
        Id = "t", Title = "T", ClaudeStartedAt = DateTime.UtcNow,
        ReportToSessionId = reportTo, ExecutorLaunchedBySessionId = launchedBy, SourceSessionId = source,
    };

    private static string? Run(TaskItem? parent, Func<string, bool>? owned = null) =>
        TasksToolset.InheritedReportTarget(Caller, "owner", _ => parent, owned ?? (_ => true));

    [Fact]
    public void ОбычныйЧатНеИсполнитель_Null() => Run(null).Should().BeNull();

    [Fact]
    public void ИсполнительЗавершённойЗадачи_Null()
    {
        var t = Active(reportTo: "X");
        t.ClaudeResult = "success";
        Run(t).Should().BeNull();
    }

    [Fact]
    public void ReportTo_Приоритетнее() => Run(Active("X", "Y", "Z")).Should().Be("X");

    [Fact]
    public void БезReportTo_Запустивший() => Run(Active(null, "Y", "Z")).Should().Be("Y");

    [Fact]
    public void БезReportToИЗапустившего_Источник() => Run(Active(null, null, "Z")).Should().Be("Z");

    [Fact]
    public void ТоЖеЧтоВызыватель_Null() => Run(Active(Caller)).Should().BeNull();

    [Fact]
    public void ПустаяСтрока_Null() => Run(Active("")).Should().BeNull();

    [Fact]
    public void СессияДругогоВладельца_Null() =>
        Run(Active("X"), id => id != "X").Should().BeNull();
}
