using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Режим «Авто»: shell-команды разрешаются без карточки (обещание «действует сам»),
// необратимые — по-прежнему спрашивают. Проверяем саму ветку DecidePermissionAsync:
// порядок относительно project-правил (deny сильнее авто-allow), охват обоих shell'ов
// и неприкосновенность остальных режимов.
public class ClaudeSessionAutoModeTests
{
    private static readonly MethodInfo Decide =
        typeof(ClaudeSession).GetMethod("DecidePermissionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewClaudeSession(
        Session info, IReadOnlyList<PermissionRule>? rules = null)
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: rules is null ? null : () => rules,
            TasksMcp: null);
        return (new ClaudeSession(info, context), sent);
    }

    private static Task<string> DecideAsync(ClaudeSession session, string requestId, string toolName, string command)
    {
        using var doc = JsonDocument.Parse($"{{\"command\": {JsonSerializer.Serialize(command)}}}");
        return (Task<string>)Decide.Invoke(session,
            [requestId, toolName, doc.RootElement.Clone(), new object()])!;
    }

    [Theory]
    [InlineData("Bash")]      // на Windows CLI зовёт то Bash, то PowerShell — закрываем оба
    [InlineData("PowerShell")]
    public async Task Авто_ShellБезопаснаяКоманда_РазрешаетБезКарточки(string toolName)
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var decision = await DecideAsync(session, "req-1", toolName, "dotnet build");

        decision.Should().Be("allow");
        lock (sent) sent.Should().BeEmpty("карточки быть не должно — «Авто» действует сам");
    }

    [Fact]
    public async Task Авто_ShellНеобратимаяКоманда_ПоказываетКарточку()
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var pending = DecideAsync(session, "req-2", "Bash", "rm -rf build");

        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        lock (sent)
            sent.OfType<PermissionRequestMessage>().Should().ContainSingle()
                .Which.ToolName.Should().Be("Bash");

        // Разбираем ожидание, чтобы тест не оставлял висящий ход
        session.RespondPermission("req-2", "deny");
        (await pending).Should().Be("deny");
    }

    // Авто-разрешение только у shell: прочие инструменты в «Авто» спрашивают как раньше
    [Fact]
    public async Task Авто_ПрочийИнструмент_ПоказываетКарточку()
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var pending = DecideAsync(session, "req-3", "Write", "rm -rf build");

        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        lock (sent)
            sent.OfType<PermissionRequestMessage>().Should().ContainSingle()
                .Which.ToolName.Should().Be("Write");

        session.RespondPermission("req-3", "deny");
        (await pending).Should().Be("deny");
    }

    // Порядок проверок: deny-правило проекта сильнее авто-разрешения режима
    [Fact]
    public async Task Авто_DenyПравилоПроекта_ПобеждаетАвторежим()
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info,
        [
            new PermissionRule { Pattern = "Bash", Action = "deny" },
        ]);
        await using var _ = session;

        var decision = await DecideAsync(session, "req-4", "Bash", "dotnet build");

        decision.Should().Be("deny");
        lock (sent) sent.Should().BeEmpty("deny отвечает сразу, без карточки пользователю");
    }

    // Авто-allow — прерогатива «Авто»: остальные режимы на ту же команду показывают карточку
    [Theory]
    [InlineData(ClaudeMode.Default)]
    [InlineData(ClaudeMode.AcceptEdits)]
    [InlineData(ClaudeMode.Plan)]
    [InlineData(ClaudeMode.DontAsk)]
    [InlineData(ClaudeMode.Bypass)]
    public async Task ДругойРежим_ShellКоманда_ПоказываетКарточку(ClaudeMode mode)
    {
        var info = new Session { Mode = mode };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var pending = DecideAsync(session, "req-5", "Bash", "dotnet build");

        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        lock (sent)
            sent.OfType<PermissionRequestMessage>().Should().ContainSingle()
                .Which.ToolName.Should().Be("Bash");

        session.RespondPermission("req-5", "deny");
        (await pending).Should().Be("deny");
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
