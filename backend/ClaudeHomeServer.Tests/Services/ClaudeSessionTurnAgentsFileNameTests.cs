using System.IO;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Поведенческий тест гонки за файл --agents (дефект ed581448, доработка по ревью Глеба).
//
// Суть дефекта: файл --agents пишется в BuildArgs, а при несовпадении сигнатуры запуска
// убивается прежний прогон этой же сессии, и ЕГО финализация (File.Delete) удаляла тот же
// файл, на который стартует новый процесс → exit=1 → Unreachable → фолбэк.
//
// Фикс — как у --mcp-config: путь уникален на каждую сборку (Guid), поэтому финализация
// одного прогона не трогает файл другого. --agents исключён из BuildLaunchSignature, смена
// состава сабагентов-персон ловится fingerprint'ом содержимого, а не путём.
//
// Мутация «общий/стабильный путь» (оба вызова BuildTurnAgentsFileName дают одно имя) обязана
// покрасить тесты ниже: файл второй сборки удалится финализацией первой.
public class ClaudeSessionTurnAgentsFileNameTests
{
    // Контент сабагентов (детерминирован по персонам)
    private const string AgentsJson = """{"agents":[{"name":"denis","prompt":"p1"}]}""";
    // Другой контент (правка персоны)
    private const string OtherAgentsJson = """{"agents":[{"name":"kira","prompt":"p2"}]}""";

    // Гонка финализации: два хода одной сессии, одна сборка за другой. Финализация убитого
    // прогона первого хода удаляет СВОЙ файл; файл второго хода (на который стартует новый
    // процесс) должен существовать в момент Start.
    [Fact]
    public void ФинализацияПервогоХода_НеУдаляетФайлВторого()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // сборка хода 1
            var p1 = Path.Combine(dir, ClaudeSession.BuildTurnAgentsFileName());
            File.WriteAllText(p1, AgentsJson);

            // сборка хода 2 (та же сессия, тот же контент)
            var p2 = Path.Combine(dir, ClaudeSession.BuildTurnAgentsFileName());
            File.WriteAllText(p2, AgentsJson);

            // финализация убитого прогона хода 1 удаляет свой файл
            File.Delete(p1);

            File.Exists(p2).Should().BeTrue(
                "финализация хода 1 не должна тронуть файл хода 2, на который стартует новый процесс");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Две сборки (или две сессии) с одним содержимым → РАЗНЫЕ пути в args. Общий путь —
    // мутация, краснеющая здесь: оба вызова дали бы одно имя.
    [Fact]
    public void ДвеСборкиСОднимКонтентом_РазныеПути()
    {
        var a = ClaudeSession.BuildTurnAgentsFileName();
        var b = ClaudeSession.BuildTurnAgentsFileName();
        a.Should().NotBe(b, "путь --agents уникален на каждую сборку, как у --mcp-config");
    }

    // Формат имени: claude-agents-{32 hex}.json (Guid в N-формате)
    [Fact]
    public void ИмяНесётФорматGuid()
    {
        var name = ClaudeSession.BuildTurnAgentsFileName();
        name.Should().StartWith("claude-agents-");
        name.Should().EndWith(".json");
        var token = name["claude-agents-".Length..^5];
        token.Should().HaveLength(32);
        token.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    // Fingerprint содержимого: детерминирован, различен для разного контента (смена состава
    // сабагентов-персон → новый fingerprint → новый процесс; правка персоны — отдельно).
    [Fact]
    public void FingerprintДетерминированИРазличенДляДругогоКонтента()
    {
        var same1 = ClaudeSession.TurnAgentsFingerprint(AgentsJson);
        var same2 = ClaudeSession.TurnAgentsFingerprint(AgentsJson);
        same1.Should().Be(same2, "fingerprint детерминирован на том же содержимом");

        var other = ClaudeSession.TurnAgentsFingerprint(OtherAgentsJson);
        other.Should().NotBe(same1, "смена состава сабагентов меняет fingerprint → новый процесс");
    }
}
