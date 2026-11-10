using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// FS-O2a: журнал оценок фичи (событие estimate_set). Каждая запись голоса с непустой оценкой
// (TaskManager.SetFeatureNote) дописывает в журнал фичи строку kind=estimate_set:
// expectedSteps/etaMinutes — из самой оценки; stage/planState/accepted/total — из необязательного
// контекста вызывающего (FeatureEstimateContext; в FS-O2a вызывающие его не передают → в журнале
// null). Голос без оценки строки не пишет; снятие голоса пустым текстом — тоже. Запись идёт под
// тем же _featureNoteLock, что и запись голоса: последняя строка журнала соответствует
// сохранённой оценке (тест гонки).
//
// Мутации (критерии c2/c4, прогонялись в копии дерева):
//  • «AppendJournal вне _featureNoteLock» → ДвеОценки_Параллельно… красный;
//  • «убрать AppendJournal в SetFeatureNote» → красные СОценкой_ПишетEstimateSet…,
//    ДвеОценки_ДвеСтроки…, Переполнение_EstimateSet_НеОтбрасывается… (список в отчёте задачи).
// Платформонезависимые: пути от Path.GetTempPath(); гонка — события, не паузы.
public class FeatureEstimateJournalTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _sut;
    private readonly FeatureJournalStore _journal;

    public FeatureEstimateJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fejt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();
        // Журнал — в своём каталоге (dir-конструктор), задачи — в DataPath; оба в _dir
        // (образец: FeatureJournalTransitionsTests).
        _journal = new FeatureJournalStore(Path.Combine(_dir, "feature-journal"));
        _sut = new TaskManager(config, journal: _journal);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TaskItem CreateEpic(string owner = "u") =>
        _sut.Create(null, owner, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));

    private string JournalPath(string owner, string featureId) =>
        Path.Combine(_dir, "feature-journal", owner, featureId + ".jsonl");

    // Оценка с заполненным why: порог сдвига FS-O1 молчит при непустом why
    // (EstimateShiftExceeds, TaskManager.cs:1789), поэтому разные оценки одной фичи
    // в тестах не отказывают.
    private static FeatureEstimate Estimate(int steps, int eta) =>
        new(["шаг"], eta, "проверка журнала", steps);

    // ─── Запись оценки → строка estimate_set ────────────────────────────────────────────────

    // Мутация «убрать AppendJournal в SetFeatureNote»: нет строки → Single падает (красный).
    [Fact]
    public void SetFeatureNote_СОценкой_ПишетEstimateSet_СЧисламиОценки()
    {
        var epic = CreateEpic();
        var before = DateTime.UtcNow;

        _sut.SetFeatureNote(epic.Id, "идём", null, false, "prod", "running", Estimate(12, 300));

        var entry = _journal.Read("u", epic.Id).Entries
            .Single(e => e.Kind == "estimate_set");
        entry.ExpectedSteps.Should().Be(12);
        entry.EtaMinutes.Should().Be(300);
        entry.TaskId.Should().Be(epic.Id);
        entry.At.Should().BeOnOrAfter(before);
        // Контекст вызывающего в FS-O2a не передаётся: снимок учёта — null.
        entry.Stage.Should().BeNull();
        entry.PlanState.Should().BeNull();
        entry.Accepted.Should().BeNull();
        entry.Total.Should().BeNull();
    }

    // Мутация «писать строку и без оценки» (estimate_set при пустом Estimate):
    // краснеют обе ветки Theory.
    [Theory]
    [InlineData("идём")]
    [InlineData("")]
    public void SetFeatureNote_БезОценки_НеПишетEstimateSet(string text)
    {
        var epic = CreateEpic();

        _sut.SetFeatureNote(epic.Id, text, null, false, "prod", "running");

        _journal.Read("u", epic.Id).Entries.Should().NotContain(e => e.Kind == "estimate_set");
    }

    // Пустой текст снимает голос вместе с оценкой (TaskManager.cs:1835): вторая запись
    // с той же оценкой строки не дописывает — история остаётся из одной строки.
    [Fact]
    public void SetFeatureNote_СнятиеГолоса_НеДописываетEstimateSet()
    {
        var epic = CreateEpic();
        _sut.SetFeatureNote(epic.Id, "идём", null, false, "prod", "running", Estimate(12, 300));

        _sut.SetFeatureNote(epic.Id, "", null, false, "prod", "running", Estimate(12, 300));

        _journal.Read("u", epic.Id).Entries.Count(e => e.Kind == "estimate_set").Should().Be(1);
        _sut.GetById(epic.Id)!.Note.Should().BeNull();
    }

    // Инвариант фичи: оценка не перезаписывается молча — каждая оставленная строка на месте.
    // Мутация «писать одну строку на фичу (replace вместо append)»: HaveCount(2) краснеет.
    [Fact]
    public void SetFeatureNote_ДвеОценки_ДвеСтроки_ВПорядкеЗаписи()
    {
        var epic = CreateEpic();

        _sut.SetFeatureNote(epic.Id, "первая", null, false, "prod", "running", Estimate(10, 200));
        _sut.SetFeatureNote(epic.Id, "вторая", null, false, "prod", "running", Estimate(14, 400));

        var lines = _journal.Read("u", epic.Id).Entries
            .Where(e => e.Kind == "estimate_set").ToList();
        lines.Should().HaveCount(2);
        lines[0].ExpectedSteps.Should().Be(10);
        lines[1].ExpectedSteps.Should().Be(14);
        // В сторе — последняя оценка, в журнале — обе.
        _sut.GetById(epic.Id)!.Note!.Estimate!.ExpectedSteps.Should().Be(14);
    }

    // Контекст вызывающего (для FS-O2b) доходит до журнала дословно.
    // Мутация «не читать estimateContext» (писать null): краснеет.
    [Fact]
    public void SetFeatureNote_СКонтекстомУчёта_ПишетЕгоВСтроку()
    {
        var epic = CreateEpic();

        _sut.SetFeatureNote(epic.Id, "идём", null, false, "review", "running", Estimate(9, 150),
            new TaskManager.FeatureEstimateContext("review", "cut", 4, 9));

        var entry = _journal.Read("u", epic.Id).Entries
            .Single(e => e.Kind == "estimate_set");
        entry.ExpectedSteps.Should().Be(9);
        entry.EtaMinutes.Should().Be(150);
        entry.Stage.Should().Be("review");
        entry.PlanState.Should().Be("cut");
        entry.Accepted.Should().Be(4);
        entry.Total.Should().Be(9);
    }

    // ─── Совместимость формата v:1 ──────────────────────────────────────────────────────────

    // Строка, записанная до новых полей, читается как есть; новые поля — null. Обратное:
    // строка без контекста не печатает лишних ключей — форма прочих записей не меняется.
    [Fact]
    public void СтараяСтрока_БезНовыхПолей_ЧитаетсяКакЕсть()
    {
        var epic = CreateEpic();
        var path = JournalPath("u", epic.Id);
        var createdLine = File.ReadLines(path).First();

        File.AppendAllText(path,
            "{\"v\":1,\"at\":\"2026-11-01T00:00:00Z\",\"kind\":\"created\",\"taskId\":\"t-old\"}\n");

        var old = _journal.Read("u", epic.Id).Entries.Single(e => e.TaskId == "t-old");
        old.Kind.Should().Be("created");
        old.ExpectedSteps.Should().BeNull();
        old.EtaMinutes.Should().BeNull();
        old.Stage.Should().BeNull();
        old.PlanState.Should().BeNull();
        old.Accepted.Should().BeNull();
        old.Total.Should().BeNull();

        createdLine.Should().NotContain("expectedSteps");
        createdLine.Should().NotContain("planState");
    }

    // ─── Поток 20 000 строк: estimate_set не отбрасывается ─────────────────────────────────

    // Файл доведён до потолка: turn Appendix пропускает (FeatureJournalStore.cs:204),
    // оценка — пишется. Мутация «estimate_set тоже под потолком»: Single падает (красный).
    [Fact]
    public void Переполнение_EstimateSet_НеОтбрасывается_TurnПропускается()
    {
        var epic = CreateEpic();
        // Create эпика уже дал 1 строку created — доводим ещё MaxLines-1 переходами.
        for (var i = 0; i < FeatureJournalStore.MaxLines - 1; i++)
        {
            _journal.Append("u", epic.Id, new FeatureJournalEntry
            {
                At = DateTime.UtcNow,
                Kind = "created",
                TaskId = $"fill-{i}",
            });
        }

        _journal.Append("u", epic.Id, new FeatureJournalEntry
        {
            At = DateTime.UtcNow,
            Kind = "turn",
            SessionId = "s1",
            TurnSeq = 1,
        });
        _sut.SetFeatureNote(epic.Id, "идём", null, false, "prod", "running", Estimate(12, 300));

        var result = _journal.Read("u", epic.Id);
        result.Truncated.Should().BeTrue();
        result.Entries.Should().HaveCount(FeatureJournalStore.MaxLines + 1);
        result.Entries.Should().NotContain(e => e.Kind == "turn");
        result.Entries.Should().ContainSingle(e => e.Kind == "estimate_set")
            .Which.ExpectedSteps.Should().Be(12);
    }

    // Структурная проверка контракта «запись голоса и строка журнала — под одной блокировкой»:
    // первый вызов держится внутри критической секции (шов FeatureNoteInside), пока второй
    // пытается записать свою оценку. Под правильным замком второй не может записать ни голос,
    // ни строку — ни одной новой строки журнала, пока занят замок.
    // Мутация «запись журнала вне блокировки» (AppendJournal до входа в _featureNoteLock):
    // вторая строка появляется при занятом замке → красный (проверено прогоном в копии дерева).
    // Форма «AppendJournal после finally» инвариант не ломает: append после релиза всё равно
    // раньше append следующего ожидающего — тест на неё не краснеет, и это свойство формулы,
    // а не слабость теста (см. отчёт задачи).
    [Fact]
    public async Task ЗаписьЖурнала_ПодОднимЗамком_СГолосом()
    {
        var epic = CreateEpic();
        var path = JournalPath("u", epic.Id);
        var baseline = File.ReadAllLines(path).Length;

        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.FeatureNoteInside = () =>
        {
            inside.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(10));
        };
        _sut.FeatureNoteContended = () => contended.TrySetResult();

        var first = Task.Run(() => _sut.SetFeatureNote(
            epic.Id, "первый", null, false, "prod", "running", Estimate(11, 111)));
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = Task.Run(() => _sut.SetFeatureNote(
            epic.Id, "второй", null, false, "prod", "running", Estimate(22, 222)));
        // Событие, не пауза: второй дошёл до занятого замка — к этому моменту при правильном
        // порядке он ещё ничего не записал.
        await contended.Task.WaitAsync(TimeSpan.FromSeconds(10));

        File.ReadAllLines(path).Length.Should().Be(baseline,
            "при занятом замке нет ни одной новой строки журнала: строка оценки пишется под тем же замком, что и голос");

        release.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        results.Should().HaveCount(2);
        var lines = _journal.Read("u", epic.Id).Entries
            .Where(e => e.Kind == "estimate_set").ToList();
        lines.Should().HaveCount(2);
        lines[0].ExpectedSteps.Should().Be(11, "порядок строк = порядок записей голоса");
        lines[1].ExpectedSteps.Should().Be(22);
        _sut.GetById(epic.Id)!.Note!.Estimate!.ExpectedSteps.Should().Be(22);
    }

    // ─── Таймлайн новый вид игнорирует ─────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);

    private static TaskItem PlainEpic(string id) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        Status = TaskItemStatus.Todo,
        CreatedAt = T0,
    };

    private static TaskItem PlainChild(string id, string epicId,
        DateTime? startedAt = null, DateTime? completedAt = null) => new()
    {
        Id = id,
        FeatureId = epicId,
        Role = TaskRoles.Step,
        Status = completedAt is not null ? TaskItemStatus.Done : TaskItemStatus.InProgress,
        CreatedAt = T0,
        ClaudeStartedAt = startedAt,
        CompletedAt = completedAt,
    };

    private static FeatureJournalEntry Entry(string kind, DateTime at, string? taskId = null,
        DateTime? startedAt = null, DateTime? endedAt = null, string? outcome = null) => new()
    {
        At = at,
        Kind = kind,
        TaskId = taskId,
        StartedAt = startedAt,
        EndedAt = endedAt,
        Outcome = outcome,
    };

    private static FeatureJournalReadResult Journal(params FeatureJournalEntry[] entries) => new()
    {
        StartsWithCreated = true,
        Truncated = false,
        Entries = entries.ToList(),
    };

    // Тот же журнал плюс строка estimate_set → те же итоги и тот же набор сегментов:
    // таймлайн новый вид не читает (он не переход и не ход).
    [Fact]
    public void Таймлайн_СтрокуEstimateSet_Игнорирует()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = PlainEpic("epic");
        var s1 = PlainChild("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var run = FeatureProjector.BuildCore(epic, [epic, s1]);

        var baseEntries = new[]
        {
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("turn", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
                endedAt: new DateTime(2026, 10, 1, 10, 0, 0), outcome: "finished"),
            Entry("waiting", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
            Entry("waiting_cleared", new DateTime(2026, 10, 1, 11, 0, 0), taskId: "s1"),
        };
        var without = FeatureTimeline.Build(run, Journal(baseEntries), now);
        var with = FeatureTimeline.Build(run, Journal([.. baseEntries,
            Entry("estimate_set", new DateTime(2026, 10, 1, 10, 30, 0), taskId: "epic")]), now);

        with.Totals.ElapsedMinutes.Should().Be(without.Totals.ElapsedMinutes);
        with.Totals.WorkWallMinutes.Should().Be(without.Totals.WorkWallMinutes);
        with.Totals.WaitingMinutes.Should().Be(without.Totals.WaitingMinutes);
        with.Totals.WaitingCount.Should().Be(without.Totals.WaitingCount);
        with.Totals.IdleMinutes.Should().Be(without.Totals.IdleMinutes);
        with.Totals.NotWorkingMinutes.Should().Be(without.Totals.NotWorkingMinutes);
        with.Segments.Select(s => s.Kind).Should().Equal(without.Segments.Select(s => s.Kind));
    }

    // ─── Гонка: последняя строка = сохранённая оценка ───────────────────────────────────────

    // 20 параллельных записей оценки одной фичи. Голос и строка журнала пишутся под одним
    // _featureNoteLock (TaskManager.cs), поэтому ровно 20 строк и последняя — та же оценка,
    // что осталась в сторе. Мутация «AppendJournal вне _featureNoteLock» (после finally):
    // окно между «записали голос» и «записали журнал» путает порядок → последняя строка
    // не совпадает с Note.Estimate (красный; проверено прогоном в копии дерева).
    [Fact]
    public async Task ДвеОценки_Параллельно_ПоследняяСтрокаРавнаСохранённой()
    {
        var epic = CreateEpic();

        var writes = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => _sut.SetFeatureNote(
                epic.Id, $"ход {i}", null, false, "prod", "running", Estimate(i + 1, 100 + i))))
            .ToArray();
        var results = await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(30));

        results.Should().HaveCount(20);
        results.Any(r => r is null).Should().BeFalse();
        var saved = _sut.GetById(epic.Id)!.Note!.Estimate!;

        var lines = File.ReadAllLines(JournalPath("u", epic.Id));
        var estimateLines = lines
            .Select(l => JsonSerializer.Deserialize<FeatureJournalEntry>(l))
            .Where(e => e is not null && e.Kind == "estimate_set")
            .ToList();
        estimateLines.Should().HaveCount(20, "каждая записанная оценка оставляет ровно одну строку");

        var last = lines.Reverse().First(l => l.Contains("\"estimate_set\""));
        var lastEntry = JsonSerializer.Deserialize<FeatureJournalEntry>(last)!;
        lastEntry.ExpectedSteps.Should().Be(saved.ExpectedSteps,
            "последняя строка журнала соответствует сохранённой оценке");
        lastEntry.EtaMinutes.Should().Be(saved.EtaMinutes);
    }
}
