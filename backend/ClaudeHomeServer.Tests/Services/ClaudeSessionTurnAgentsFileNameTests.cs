using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Имя per-run файла --agents (дефект ed581448): {id сессии}-{хэш содержимого}.
// До фикса имя было только {хэш} — детерминировано по (владелец + персоны), так что ВСЕ
// чаты одной персоны делили ОДИН файл, и выход одного чата (File.Delete при финализации)
// удалял файл, которым пользуется соседний живой/стартующий чат той же персоны: новый чат
// падал с «--agents file not found» (exit=1) и уходил в фолбэк на чужую подписку.
// Фикс добавляет id сессии в имя: файл перестал быть общим, а путь в пределах сессии
// стабилен (сигнатура запуска не дрожит) и меняется при правке персоны (штатный перезапуск).
public class ClaudeSessionTurnAgentsFileNameTests
{
    // Один и тот же контент сабагентов (детерминирован по персонам)
    private const string SameAgentsJson = """{"agents":[{"name":"denis","prompt":"p1"}]}""";
    // Другой контент (правка персоны)
    private const string OtherAgentsJson = """{"agents":[{"name":"kira","prompt":"p2"}]}""";

    // Два чата одной персоны: тот же контент, разные id сессий → РАЗНЫЕ имена файла.
    // Выход первого (удаление СВОЕГО файла) больше не трогает файл второго.
    [Fact]
    public void ДваЧатаОднойПерсоны_РазныеИменаФайла()
    {
        var a = ClaudeSession.BuildTurnAgentsFileName("session-a", SameAgentsJson);
        var b = ClaudeSession.BuildTurnAgentsFileName("session-b", SameAgentsJson);
        a.Should().NotBe(b, "файл --agents перестал быть общим между чатами одной персоны");
        a.Should().Contain("session-a");
        b.Should().Contain("session-b");
    }

    // Путь стабилен между ходами одной сессии: та же сессия, тот же контент → то же имя
    // (сигнатура запуска не дрожит, живой процесс доживания переиспользуется).
    [Fact]
    public void ПутьСтабиленМеждуХодамиОднойСессии()
    {
        var first = ClaudeSession.BuildTurnAgentsFileName("session-a", SameAgentsJson);
        var second = ClaudeSession.BuildTurnAgentsFileName("session-a", SameAgentsJson);
        first.Should().Be(second);
    }

    // Смена персоны меняет путь: та же сессия, другой контент → другой хэш → другое имя
    // (штатный перезапуск процесса на новый файл).
    [Fact]
    public void СменаПерсоны_МеняетИмяФайла()
    {
        var before = ClaudeSession.BuildTurnAgentsFileName("session-a", SameAgentsJson);
        var after = ClaudeSession.BuildTurnAgentsFileName("session-a", OtherAgentsJson);
        before.Should().NotBe(after);
    }

    // Формат имени: claude-agents-{id}-{16 hex}.json; хэш детерминирован (стабилен на том же входе).
    [Fact]
    public void ИмяНесётФорматИДетерминированныйХэш()
    {
        const string prefix = "claude-agents-abc-";
        var name = ClaudeSession.BuildTurnAgentsFileName("abc", SameAgentsJson);
        name.Should().StartWith(prefix);
        name.Should().EndWith(".json");
        var hashPart = name[prefix.Length..^5];
        hashPart.Should().HaveLength(16);
        hashPart.Should().MatchRegex("^[0-9a-f]{16}$");
        // Детерминированность: повторный вызов тем же входом — тот же хэш
        hashPart.Should().Be(ClaudeSession.BuildTurnAgentsFileName("abc", SameAgentsJson)[prefix.Length..^5]);
    }
}
