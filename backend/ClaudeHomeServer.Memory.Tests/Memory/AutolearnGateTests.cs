using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Memory;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Memory;

// Гейт содержательности хода (persona-memory-autolearn / team-memory-autolearn):
// временный чат, делегированный ход (чат-исполнитель задачи), короткий ход.
public class AutolearnGateTests
{
    [Fact]
    public void CheckSession_ВременныйЧат_Пропуск()
    {
        var session = new Session { ExpiresAfterMinutes = 60 };

        AutolearnGate.CheckSession(session).Should().Be(AutolearnSkipReason.TempChat);
    }

    [Fact]
    public void CheckSession_ДелегированныйХод_Пропуск()
    {
        var session = new Session { TaskExecution = true };

        AutolearnGate.CheckSession(session).Should().Be(AutolearnSkipReason.Delegated);
    }

    [Fact]
    public void CheckSession_ВременныйЧатПриоритетнееДелегирования()
    {
        var session = new Session { ExpiresAfterMinutes = 30, TaskExecution = true };

        AutolearnGate.CheckSession(session).Should().Be(AutolearnSkipReason.TempChat);
    }

    [Fact]
    public void CheckSession_ОбычныйЧат_НетПропуска()
    {
        var session = new Session();

        AutolearnGate.CheckSession(session).Should().Be(AutolearnSkipReason.None);
    }

    [Fact]
    public void CheckContent_КороткийХод_Пропуск()
    {
        var history = new List<StoredMessage>
        {
            new StoredUserMessage("привет"),
            new StoredTextMessage("угу"),
        };

        AutolearnGate.CheckContent(history, 300).Should().Be(AutolearnSkipReason.LowContent);
    }

    [Fact]
    public void CheckContent_СодержательныйХод_НетПропуска()
    {
        var history = new List<StoredMessage>
        {
            new StoredUserMessage(new string('a', 200)),
            new StoredTextMessage(new string('b', 200)),
        };

        AutolearnGate.CheckContent(history, 300).Should().Be(AutolearnSkipReason.None);
    }

    [Fact]
    public void LastTurnLength_СчитаетТолькоХвостПослеПоследнегоСообщенияПользователя()
    {
        var history = new List<StoredMessage>
        {
            // Старый ход — не должен попасть в подсчёт
            new StoredUserMessage(new string('x', 5000)),
            new StoredTextMessage(new string('y', 5000)),
            // Последний (короткий) ход
            new StoredUserMessage("да"),
            new StoredTextMessage("ок"),
        };

        AutolearnGate.LastTurnLength(history).Should().Be(4);
    }

    [Fact]
    public void LastTurnLength_ИгнорируетТекстСабагентаИСлужебныеСообщения()
    {
        var history = new List<StoredMessage>
        {
            new StoredUserMessage("вопрос"),
            new StoredToolUseMessage { Name = "Read" },
            new StoredTextMessage("текст сабагента", parentToolUseId: "tool-1"),
            new StoredTextMessage("основной ответ"),
        };

        // "вопрос" (6) + "основной ответ" (14) = 20; текст сабагента и tool_use не считаются
        AutolearnGate.LastTurnLength(history).Should().Be(20);
    }

    [Fact]
    public void LastTurnLength_ПустаяИстория_Ноль()
    {
        AutolearnGate.LastTurnLength(new List<StoredMessage>()).Should().Be(0);
    }
}
