using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// «Разрешать всегда»: решение живёт на сессии (Session.AutoAllowTools), а не в памяти
// адаптера. Адаптер пересоздаётся рестартом сервера, ленивым восстановлением чата и сменой
// собеседника — прежний ConcurrentDictionary при этом обнулялся, и человек жал «всегда»
// заново (жалоба с прода: три рестарта — три нажатия). Здесь проверяется чтение списка
// в DecidePermissionAsync: инструмент из списка не порождает новой карточки.
public class ClaudeSessionAutoAllowTests
{
    private static readonly MethodInfo Decide =
        typeof(ClaudeSession).GetMethod("DecidePermissionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewClaudeSession(Session info)
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null);
        return (new ClaudeSession(info, context), sent);
    }

    private static Task<string> DecideAsync(ClaudeSession session, string requestId, string toolName)
    {
        using var doc = JsonDocument.Parse("{}");
        return (Task<string>)Decide.Invoke(session,
            [requestId, toolName, doc.RootElement.Clone(), new object()])!;
    }

    [Theory]
    [InlineData("Bash")]      // ровно то имя, что записал SessionManager
    [InlineData("bash")]      // регистр не важен — сравнение OrdinalIgnoreCase
    public async Task ИнструментИзСпискаСессии_РазрешаетБезКарточки(string storedTool)
    {
        var info = new Session { AutoAllowTools = [storedTool] };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var decision = await DecideAsync(session, "req-1", "Bash");

        decision.Should().Be("allow");
        lock (sent) sent.Should().BeEmpty("повторной карточки быть не должно — «всегда» уже нажато");
    }

    // Контроль: механика не разрешает всё подряд — незнакомый инструмент по-прежнему
    // спрашивает (карточка ушла в ленту, ход ждёт ответа пользователя)
    [Fact]
    public async Task ИнструментНеИзСписка_ПоказываетКарточку()
    {
        var info = new Session { AutoAllowTools = ["Bash"] };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var pending = DecideAsync(session, "req-2", "Write");

        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        lock (sent)
            sent.OfType<PermissionRequestMessage>().Should().ContainSingle()
                .Which.ToolName.Should().Be("Write");

        // Разбираем ожидание, чтобы тест не оставлял висящий ход (иначе — таймаут в 60 мин)
        session.RespondPermission("req-2", "deny");
        (await pending).Should().Be("deny");
    }

    // Ответ allow_always у адаптера сводится к обычному allow: запоминает и сохраняет
    // теперь SessionManager.RespondPermission, а не сам адаптер
    [Fact]
    public async Task ОтветAllowAlways_ПревращаетсяВAllow()
    {
        var (session, sent) = NewClaudeSession(new Session());
        await using var _ = session;

        var pending = DecideAsync(session, "req-3", "Write");
        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });

        session.RespondPermission("req-3", "allow_always");

        (await pending).Should().Be("allow", "CLI понимает только allow/deny");
    }

    // Ждём событие, а не спим фиксированно (тесты гоняются и на слабом CI-раннере)
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "карточка разрешения так и не пришла");
            await Task.Delay(10);
        }
    }
}
