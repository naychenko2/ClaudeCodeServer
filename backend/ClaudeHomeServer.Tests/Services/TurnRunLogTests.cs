using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Журнал паспортов ходов: сводка «что ломалось за сутки» и дневной файл на диске.
public class TurnRunLogTests
{
    private static TurnRunPassport Passport(string sessionId, string outcome,
        string? errorClass = null, string? error = null) =>
        new(sessionId, DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow, 30, outcome,
            "opus", "claude", "opus", "claude", 1, 0, 0, ["opus"], errorClass, error, 120_000,
            DateTime.UtcNow);

    [Fact]
    public void Сводка_СчитаетПровалыИРазноситПоПричинам()
    {
        var log = new TurnRunLog();
        log.Record(Passport("s1", "success"));
        log.Record(Passport("s2", "egress_down", "unreachable", "ECONNREFUSED"));
        log.Record(Passport("s3", "egress_down", "unreachable", "ECONNREFUSED"));
        log.Record(Passport("s4", "failed", "rate_limit"));
        log.Record(Passport("s5", "interrupted"));

        var summary = log.Summary();

        summary.Turns.Should().Be(5);
        // interrupted — не сбой: человек сам остановил ход
        summary.Failed.Should().Be(3);
        summary.ByOutcome.Should().Contain(s => s.Outcome == "egress_down" && s.Turns == 2);
        summary.ByErrorClass.Should().Contain(s => s.ErrorClass == "unreachable" && s.Turns == 2);
    }

    [Fact]
    public void Recent_СвежиеПервыми()
    {
        var log = new TurnRunLog();
        log.Record(Passport("старый", "success"));
        log.Record(Passport("свежий", "success"));

        log.Recent(10).Select(r => r.SessionId).Should().ContainInOrder("свежий", "старый");
    }

    [Fact]
    public void ДлинныйТекстОшибкиУсекается()
    {
        var log = new TurnRunLog();
        log.Record(Passport("s1", "failed", "unreachable", new string('x', 5000)));

        log.Recent(1)[0].LastError!.Length.Should().BeLessThan(400);
    }

    [Fact]
    public void ПишетДневнойФайлРядомССервернымЛогом()
    {
        // Путь строим от GetTempPath + Combine: Windows-литерал на Linux-раннере
        // считался бы относительным (конвенция проекта про платформонезависимость тестов)
        var dir = Path.Combine(Path.GetTempPath(), "ccs-turn-runs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new TurnRunLog(dir, retainDays: 14);
            log.Record(Passport("s1", "egress_down", "unreachable"));

            var file = Path.Combine(dir, $"turn-runs-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            File.Exists(file).Should().BeTrue();
            var line = File.ReadAllText(file);
            line.Should().Contain("\"outcome\":\"egress_down\"");
            line.Should().Contain("\"sessionId\":\"s1\"");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void СбойЗаписиНаДискНеРоняетХод()
    {
        // Диагностика не имеет права ронять ход: недоступный путь — просто нет файла
        var log = new TurnRunLog(Path.Combine(Path.GetTempPath(), "ccs\0bad-path"), retainDays: 1);

        var write = () => log.Record(Passport("s1", "success"));

        write.Should().NotThrow();
        log.Recent(1).Should().ContainSingle();
    }
}
