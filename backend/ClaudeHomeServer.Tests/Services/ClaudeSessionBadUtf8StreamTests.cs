using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Гипотеза инцидента 15.08.2026 (два хода подряд оборвались после tool_result с выводом
// powershell в OEM-кодировке): невалидные UTF-8 байты в stream-json ломают разбор потока.
// Направленная проверка фактического разбора — строки потока прогоняются через настоящий
// ProcessLineAsync (reflection, приём ClaudeSessionPendingControlDeathTests).
//
// Механика на стороне сервера устойчива по построению:
//   • StreamReader c UTF8Encoding не бросает на битых байтах — декодер подставляет U+FFFD
//     (replacement char), строка остаётся текстом;
//   • невалидный JSON (в т.ч. строка, обрезанная посреди мультибайтной последовательности)
//     глушится JsonException-скипом в начале ProcessLineAsync — ход продолжает жить;
//   • валидный JSON с U+FFFD внутри значений парсится штатно.
// Сырой процессный тест с настоящими битыми байтами платформенно не воспроизводим
// (в backend-CI нет pwsh/node, echo cmd/sh не даёт контролируемых байт) — но поведение
// StreamReader от источника байтов не зависит, замещение происходит до разбора.
public class ClaudeSessionBadUtf8StreamTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;
    private static readonly MethodInfo ProcessLineMethod =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<System.Diagnostics.Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) p.Dispose();
    }

    private object NewRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new System.Diagnostics.Process();   // незапущенный: разбор строки его не трогает
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    private static ClaudeSession NewSession(List<ServerMessage> sink)
    {
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: m => { lock (sink) sink.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null);
        return new ClaudeSession(new Session(), context);
    }

    private static Task ProcessLine(ClaudeSession s, object run, string line) =>
        (Task)ProcessLineMethod.Invoke(s, [run, line])!;

    // tool_result, чей текст декодер собрал из битых байт (OEM-вывод powershell): U+FFFD в
    // значениях — валидный JSON, разбор проходит без исключения, событие доезжает до ленты
    [Fact]
    public async Task ToolResult_СЗамещённымиБайтами_РазбираетсяБезИсключения()
    {
        var messages = new List<ServerMessage>();
        var session = NewSession(messages);
        var run = NewRun();

        var act = () => ProcessLine(session, run,
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool-1","content":"вывод NotifyIcon: ��корректные ��байты OEM"}]}}""");

        await act.Should().NotThrowAsync("замещённые байты — обычный текст для парсера");
        messages.OfType<ToolResultMessage>().Should().ContainSingle("tool_result обязан доехать до ленты");
    }

    // Строка, обрезанная посреди JSON (процесс умер на середине записи): JsonException-скип,
    // исключение наружу НЕ уходит — иначе общий catch ридера погасил бы весь ход целиком
    [Fact]
    public async Task СтрокаОбрезанаПосредиJson_СкипаетсяБезИсключения()
    {
        var messages = new List<ServerMessage>();
        var session = NewSession(messages);
        var run = NewRun();

        var act = () => ProcessLine(session, run,
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"abcÿ""");

        await act.Should().NotThrowAsync("битый JSON — тихий скип строки, а не смерть цикла чтения");
        messages.Should().BeEmpty("недоразобранная строка в ленту не попадает");
    }

    // После битых строк поток жив: следующее событие взводит TurnGotEvent — смерть процесса
    // позже классифицируется как обрыв посреди хода (а не «пустая» смерть прогона)
    [Fact]
    public async Task ПослеБитыхСтрок_ПотокЖив()
    {
        var messages = new List<ServerMessage>();
        var session = NewSession(messages);
        var run = NewRun();

        await ProcessLine(session, run,
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool-0","content":"��"}]}}""");
        await ProcessLine(session, run, """{"type":"user","message":{"content":"недорезанная""");
        await ProcessLine(session, run,
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool-2","content":"нормальный результат"}]}}""");

        ((bool)CliRunType.GetField("TurnGotEvent")!.GetValue(run)!).Should().BeTrue(
            "валидное событие после битых строк засчитывается — ход жив");
    }
}
