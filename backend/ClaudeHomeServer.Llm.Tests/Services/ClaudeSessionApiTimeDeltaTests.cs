using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Приращение времени API (задача 654c083c): duration_api_ms в result-строке CLI — накопительный
// счётчик за всю жизнь процесса (как total_cost_usd), а не время хода. Время хода = приращение
// между итогами одного процесса (ClaudeSession.TurnApiDelta), иначе знаменатель скорости ответа —
// время всей сессии, и скорость занижается в разы (замер с прода 20.11: Opus 6,3 вместо 83,7 ток/с).
// Отличие от дельты стоимости одно: счётчик в транскрипт не пишется и на --resume не
// восстанавливается — новый процесс начинает с нуля, поэтому база живёт на CliRun (с процессом),
// а не на Session. Правила:
//   * поле не пришло (0 — сторонний поток) → null;
//   * новый процесс (базы нет или счётчик меньше прежнего) → сам снимок;
//   * тот же процесс → разница; нулевая разница (продолжение/микро-ход без вызова модели) → null.
// Чистая функция — её тесты без процесса CLI; доставка значения в ResultMessage — белобокс-драйв
// ProcessLineAsync (тот же приём, что в ClaudeSessionUpstreamRouteTests), платформонезависим.
public class ClaudeSessionApiTimeDeltaTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo ProcessLineAsyncMethod =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) ProcessTestCleanup.SafeDispose(p);
    }

    // result-строка с накопительным duration_api_ms (value — сырой снимок процесса).
    private static string ResultLine(long apiMsRaw) =>
        """{"type":"result","subtype":"success","duration_ms":10000,"duration_api_ms":""" + apiMsRaw +
        ""","num_turns":1,"usage":{"input_tokens":10,"output_tokens":500,"cache_read_input_tokens":1000,"cache_creation_input_tokens":100},"total_cost_usd":0.1}""";

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewSession()
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: "",
            PermissionRules: null,
            TasksMcp: null);
        return (new ClaudeSession(new Session { Model = "opus", Provider = "claude", OwnerId = "u1" }, context), sent);
    }

    // CliRun вручную (белый-бокс): новый объект = новый процесс (базы LastApiMs нет).
    private object NewRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    private static async Task DriveLine(ClaudeSession session, object run, string line)
    {
        var task = (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;
        await task;
    }

    // Сame-process подача следующего хода: TrySubmitTurn сбрасывает TurnDone перед submit —
    // без этого следующий result был бы классифицирован как продолжение и пропущен.
    private static void SubmitNextTurn(object run) =>
        CliRunType.GetField("TurnDone", BindingFlags.Public | BindingFlags.Instance)!.SetValue(run, false);

    private static List<ResultMessage> Results(List<ServerMessage> sent)
    {
        lock (sent) return sent.OfType<ResultMessage>().ToList();
    }

    // 1. Ядро задачи: последовательность из трёх итогов одного процесса с РАСТУЩИМ накопительным
    //    счётчиком. Первый (базы нет) — сам снимок; дальше — только разница, а не весь счётчик.
    [Fact]
    public void Приращение_РастущийСчётчик_ПервыйСнимок_ЗатемРазница()
    {
        ClaudeSession.TurnApiDelta(10_000, prev: null).Should().Be(10_000);
        ClaudeSession.TurnApiDelta(25_000, prev: 10_000).Should().Be(15_000);
        ClaudeSession.TurnApiDelta(30_000, prev: 25_000).Should().Be(5_000);
    }

    // 2. Рестарт процесса: счётчик нового процесса меньше прежнего (с нуля) — база не годится,
    //    берётся сам снимок. Отрицательной дельты или «ничего» в записи быть не должно.
    [Fact]
    public void Приращение_НовыйПроцессСчётчикМеньше_СамСнимок()
    {
        ClaudeSession.TurnApiDelta(5_000, prev: 20_000).Should().Be(5_000);
    }

    // 3. Нулевое приращение (между итогами счётчик не двигался — продолжение или микро-ход без
    //    вызова модели): null, а не 0 — знаменатель скорости ответа не выдумывается.
    [Fact]
    public void Приращение_СчётчикНеДвигался_Null()
    {
        ClaudeSession.TurnApiDelta(20_000, prev: 20_000).Should().BeNull();
    }

    // 4. Поле не пришло (сторонний поток: 0) — null при любой базе.
    [Fact]
    public void Приращение_ПоляНет_Null()
    {
        ClaudeSession.TurnApiDelta(0, prev: null).Should().BeNull();
        ClaudeSession.TurnApiDelta(0, prev: 10_000).Should().BeNull();
    }

    // 5. Доставка в ResultMessage (end-to-end через reader): три итога одного процесса —
    //    в сообщения идут приращения 10000 / 15000 / 5000, а не накопленные 10000 / 25000 / 30000.
    //    Мутация (убрать вычитание — отдавать сырой снимок) краснит вторую и третью строки.
    [Fact]
    public async Task ТриИтогаОдногоПроцесса_ВРезультатПриращения()
    {
        var (session, sent) = NewSession();
        var run = NewRun();

        await DriveLine(session, run, ResultLine(10_000));
        SubmitNextTurn(run);
        await DriveLine(session, run, ResultLine(25_000));
        SubmitNextTurn(run);
        await DriveLine(session, run, ResultLine(30_000));

        var results = Results(sent);
        results.Should().HaveCount(3);
        results[0].DurationApiMs.Should().Be(10_000);
        results[1].DurationApiMs.Should().Be(15_000, "второй итог несёт накопленные 25000 — в запись хода идёт приращение");
        results[2].DurationApiMs.Should().Be(5_000, "третий итог несёт накопленные 30000 — в запись хода идёт приращение");
    }

    // 6. Рестарт процесса (тот же чат, новый прогон): база живёт с процессом — у нового прогона
    //    её нет, и его первый итог идёт целиком, несмотря на «уменьшение» относительно прошлого
    //    процесса. Мутация (перенести базу на Session — она пережила бы прогон) краснит эту строку:
    //    было бы null (5000 < 20000).
    [Fact]
    public async Task РестартПроцесса_НовыйИтогЦеликом()
    {
        var (session, sent) = NewSession();

        var run1 = NewRun();
        await DriveLine(session, run1, ResultLine(20_000));

        var run2 = NewRun();
        await DriveLine(session, run2, ResultLine(5_000));

        var results = Results(sent);
        results.Should().HaveCount(2);
        results[0].DurationApiMs.Should().Be(20_000);
        results[1].DurationApiMs.Should().Be(5_000, "новый процесс начинает с нуля — его снимок и есть время хода");
    }

    // 7. Минор ревью 3b85ad09: итог без поля (сторонний поток, 0) не двигает базу процесса. Иначе
    //    после него следующий снимок давал бы приращение, равное всему накопленному счётчику:
    //    15000 вместо честных 15000−10000 = 5000. Мутация (писать 0 в базу безусловно) краснит
    //    третью строку.
    [Fact]
    public async Task ИтогБезПоля_БазуНеДвигает_СледующийИтогЧестный()
    {
        var (session, sent) = NewSession();
        var run = NewRun();

        await DriveLine(session, run, ResultLine(10_000));
        SubmitNextTurn(run);
        await DriveLine(session, run, ResultLine(0));
        SubmitNextTurn(run);
        await DriveLine(session, run, ResultLine(15_000));

        var results = Results(sent);
        results.Should().HaveCount(3);
        results[0].DurationApiMs.Should().Be(10_000);
        results[1].DurationApiMs.Should().BeNull("поля нет — времени хода нет");
        results[2].DurationApiMs.Should().Be(5_000, "база осталась 10000: приращение 15000−10000, а не весь счётчик");
    }
}
