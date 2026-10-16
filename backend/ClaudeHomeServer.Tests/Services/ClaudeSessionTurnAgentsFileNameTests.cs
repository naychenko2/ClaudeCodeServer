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
// Тест идёт через BuildTurnAgentsFileIdentity — ЕДИНУЮ точку, которую BuildArgs использует
// для имени файла и fingerprint'а (не через чистые BuildTurnAgentsFileName/TurnAgentsFingerprint
// в обход неё): иначе мутация «общий/стабильный путь» в BuildArgs остаётся невидимой, и
// тест тавтологичен (сам пишет два файла и сам удаляет первый). Мутация «общий путь +
// --agents обратно в сигнатуре» (оба вызова дают одно имя, путь входит в сигнатуру) обязана
// покрасить тесты ниже: файл второй сборки удалится финализацией первой.
public class ClaudeSessionTurnAgentsFileNameTests
{
    // Контент сабагентов (детерминирован по персонам)
    private const string AgentsJson = """{"agents":[{"name":"denis","prompt":"p1"}]}""";
    // Другой контент (правка персоны)
    private const string OtherAgentsJson = """{"agents":[{"name":"kira","prompt":"p2"}]}""";

    // Гонка финализации: два хода одной сессии, одна сборка за другой. Финализация убитого
    // прогона первого хода удаляет СВОЙ файл; файл второго хода (на который стартует новый
    // процесс) должен существовать в момент Start. Идём через BuildTurnAgentsFileIdentity —
    // ту же точку, что BuildArgs: имена двух сборок обязаны различаться, иначе оба файла —
    // один, и File.Delete первого убивает второй.
    [Fact]
    public void ФинализацияПервогоХода_НеУдаляетФайлВторого()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // сборка хода 1 и хода 2 — через ту же точку, что BuildArgs
            var id1 = ClaudeSession.BuildTurnAgentsFileIdentity(AgentsJson);
            var id2 = ClaudeSession.BuildTurnAgentsFileIdentity(AgentsJson);

            var p1 = Path.Combine(dir, id1.FileName);
            var p2 = Path.Combine(dir, id2.FileName);
            File.WriteAllText(p1, AgentsJson);
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

    // Две сборки (или две сессии) с одним содержимым → РАЗНЫЕ имена файлов. Общий/стабильный
    // путь — мутация, краснеющая здесь: оба вызова BuildTurnAgentsFileIdentity дали бы одно имя.
    [Fact]
    public void ДвеСборкиСОднимКонтентом_РазныеПути()
    {
        var a = ClaudeSession.BuildTurnAgentsFileIdentity(AgentsJson).FileName;
        var b = ClaudeSession.BuildTurnAgentsFileIdentity(AgentsJson).FileName;
        a.Should().NotBe(b, "имя --agents уникален на каждую сборку, как у --mcp-config");
    }

    // Формат имени: claude-agents-{32 hex}.json (Guid в N-формате)
    [Fact]
    public void ИмяНесётФорматGuid()
    {
        var name = ClaudeSession.BuildTurnAgentsFileIdentity(AgentsJson).FileName;
        name.Should().StartWith("claude-agents-");
        name.Should().EndWith(".json");
        var token = name["claude-agents-".Length..^5];
        token.Should().HaveLength(32);
        token.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    // Fingerprint содержимого (то, что входит в BuildLaunchSignature ВМЕСТО пути): детерминирован
    // и различен для разного контента. Свойство «сигнатуры совпадают при том же составе
    // сабагентов и различаются при другом» держится именно на нём: совпадение fingerprint =
    // совпадение вклада --agents в сигнатуру.
    [Fact]
    public void FingerprintДетерминированИРазличенДляДругогоКонтента()
    {
        var same1 = ClaudeSession.BuildTurnAgentsFileIdentity(AgentsJson).Fingerprint;
        var same2 = ClaudeSession.BuildTurnAgentsFileIdentity(AgentsJson).Fingerprint;
        same1.Should().Be(same2, "fingerprint детерминирован на том же содержимом");

        var other = ClaudeSession.BuildTurnAgentsFileIdentity(OtherAgentsJson).Fingerprint;
        other.Should().NotBe(same1, "смена состава сабагентов меняет fingerprint → новый процесс");
    }
}
