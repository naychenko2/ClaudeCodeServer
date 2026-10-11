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
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
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

    // run_tests в «Авто» — как безопасный Bash: без карточки
    [Fact]
    public async Task Авто_RunTests_РазрешаетБезКарточки()
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        // Таймаут: без авто-разрешения решение ждало бы ответа на карточку час, а не падало
        var pending = DecideAsync(session, "req-6", "mcp__tests__run_tests", "");
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != pending) session.RespondPermission("req-6", "deny");

        (await pending).Should().Be("allow");
        lock (sent) sent.Should().BeEmpty("в «Авто» прогон тестов идёт без карточки");
    }

    // Сборка и стенд (dev: build, start_stand, stop_stand) в «Авто» — так же, как run_tests: без карточки
    [Theory]
    [InlineData("mcp__dev__build")]
    [InlineData("mcp__dev__start_stand")]
    [InlineData("mcp__dev__stop_stand")]
    public async Task Авто_Build_РазрешаетБезКарточки(string toolName)
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var pending = DecideAsync(session, "req-6b", toolName, "");
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != pending) session.RespondPermission("req-6b", "deny");

        (await pending).Should().Be("allow");
        lock (sent) sent.Should().BeEmpty("в «Авто» сборка идёт без карточки");
    }

    // Остальные режимы на run_tests показывают карточку как раньше
    [Theory]
    [InlineData(ClaudeMode.Default)]
    [InlineData(ClaudeMode.AcceptEdits)]
    [InlineData(ClaudeMode.Plan)]
    public async Task ДругойРежим_RunTests_ПоказываетКарточку(ClaudeMode mode)
    {
        var info = new Session { Mode = mode };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var pending = DecideAsync(session, "req-7", "mcp__tests__run_tests", "");

        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        lock (sent)
            sent.OfType<PermissionRequestMessage>().Should().ContainSingle()
                .Which.ToolName.Should().Be("mcp__tests__run_tests");

        session.RespondPermission("req-7", "deny");
        (await pending).Should().Be("deny");
    }

    // Авто-разрешение точечное: соседний инструмент того же сервера и прочие MCP спрашивают
    [Theory]
    [InlineData("mcp__tests__run_tests_all")]
    [InlineData("mcp__dev__build_all")]
    [InlineData("mcp__dev__start_stand_all")]
    [InlineData("mcp__fal__run_model")]
    public async Task Авто_ДругойMcp_ПоказываетКарточку(string toolName)
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info);
        await using var _ = session;

        var pending = DecideAsync(session, "req-8", toolName, "");

        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        lock (sent)
            sent.OfType<PermissionRequestMessage>().Should().ContainSingle()
                .Which.ToolName.Should().Be(toolName);

        session.RespondPermission("req-8", "deny");
        (await pending).Should().Be("deny");
    }

    // Deny-правило проекта сильнее авто-разрешения run_tests
    [Fact]
    public async Task Авто_RunTests_DenyПравилоПроекта_Побеждает()
    {
        var info = new Session { Mode = ClaudeMode.Auto };
        var (session, sent) = NewClaudeSession(info,
        [
            new PermissionRule { Pattern = "mcp__tests__run_tests", Action = "deny" },
        ]);
        await using var _ = session;

        var decision = await DecideAsync(session, "req-9", "mcp__tests__run_tests", "");

        decision.Should().Be("deny");
        lock (sent) sent.Should().BeEmpty();
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
