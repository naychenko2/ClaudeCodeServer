using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// FS-O2a: журнал оценок фичи (событие estimate_set). Каждая запись голоса с непустой оценкой
// (TaskManager.SetFeatureNote) дописывает в журнал фичи строку kind=estimate_set:
// expectedSteps/etaMinutes — из самой оценки; stage/planState/accepted/total — из контекста
// вызывающего (FeatureEstimateContext). С FS-O2b живые пути (MCP tasks_update, REST PUT note)
// передают его из своей же проекции → в журнале он есть; без снимка (проекции нет) — null.
// Живые пути проверяются в своих сборках: TasksToolsetFeatureNoteTests (MCP) и
// TasksControllerTests (REST) — здесь только сам контракт записи.
// Голос без оценки строки не пишет; снятие голоса пустым текстом — тоже. Запись идёт под
// тем же _featureNoteLock, что и запись голоса: последняя строка журнала соответствует
// сохранённой оценке (тест гонки).
//
// FS-O2b: базовый срок InitialEtaMinutes фиксируется один раз — при первой оценке, записанной
// при нарезанном плане (снимок planState == "cut"); проверка «базы ещё нет» и её запись — под
// тем же замком (ПервыеОценкиПриCut_Параллельно_БазаОдна). Само правило и его мутация
// «вернуть условие StartedKinds» — SetFeatureNote_InitialEtaMinutes_... в TaskManagerTests.
//
// FS-PB1: база плана BaselineSteps фиксируется один раз — из контекста первой оценки, записанной
// при нарезанном ИЛИ полностью принятом плане (planState == "cut" или "done"; поправка
// постановщика 11.11: окно «cut» закрывается само при accepted == total, FeatureStepAccounting).
// Проверка отдельна от InitialEtaMinutes: оценка только с expectedSteps (без срока) тоже
// фиксирует базу; без записанной оценки (фичи без оценки) — нет. Записанное значение не меняется
// никогда; проверка «базы ещё нет» и запись — под тем же замком
// (ПервыеОценкиПриCut_Параллельно_БазаОдна_BaselineSteps).
//
// Мутации (критерии c2/c4, прогонялись в копии дерева):
//  • «AppendJournal после finally» (за окна _featureNoteLock) → красный
//    СтрокаОценки_ПодЗамком_ШовАппендаПервойРаньшеХукаВторой — детерминированно, через
//    шов FeatureJournalStore.JournalAppending: при мутации первая отпускает feature-замок
//    до своего append-а, хук второй наступает раньше контрольной точки «append начался,
//    строки ещё нет». ПорядокСтрок_ПодЗамком… на ней гонка
//    (в пробе строки не догнали контрольную точку — красный; это не гарантия);
//    ДвеОценки_Параллельно… зелёный — порядок релизов совпадает с порядком append-ов
//    (проба ревью 11/11), он ловит только вариант «до входа в замок»;
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
    // Форма «AppendJournal после finally» на этот тест не красная: к моменту проверки первый
    // ещё не вышел из критической секции, его append ещё не случилось — строки нет при любом
    // порядке. Её ловит тест СтрокаОценки_ПодЗамком_ЗамокСтора… (замок стора держится
    // маркером, append первой блокируется вне своего feature-замка).
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

    // Детерминированный контроль инварианта «последняя строка журнала = сохранённая оценка»
    // (доработка по ревью FS-O2a). Три записи с форсированным порядком: первая держится в
    // своей критической секции (шов FeatureNoteInside), вторая и третья встают в очередь
    // (швы Contended: третья — только после того, как вторая подтвердила ожидание; см.
    // прецедент SessionManagerTests «Гонка снятия и отсрочка»).
    // Первая часть: в момент хука первой (первое действие её критической секции) — ни одной
    // новой строки: append первой идёт после хука, второй и третьей ещё нет у замка. Прежняя
    // точка наблюдения — сразу после contended2 — гоняла с пробуждением самой первой (тот же
    // event отпускал её из хука, её строка успевала дойти до файла): флаковый тест, краснел
    // под загрузью. Вторая часть — контрольная точка под замом третьей
    // (шов FeatureNoteInside): голоса первых двух уже в сторе, свой третьей ещё не
    // написан — строки первых двух уже в файле, порядок строк = порядок записей голоса.
    // Форма «до входа в замок» краснеет в первой части: строка первой появляется раньше её
    // же хука. Форма «после finally» гонка во второй части: append прежней записи идёт
    // сразу после её finally, без ожидания, а наблюдение — контрольная точка под замом
    // третьей — в пробе мутацией строки не догнали её (красный), но это не гарантия.
    // Детерминированно «после finally» ловит следующий тест —
    // СтрокаОценки_ПодЗамком_ШовАппендаПервойРаньшеХукаВторой (шов append стора:
    // контрольная точка «append начался, строки ещё нет» под feature-замком первой;
    // при мутации хук второй наступает раньше этой точки).
    [Fact]
    public async Task ПорядокСтрок_ПодЗамком_ТретьяЗаписьВидитПервыеПодСвоимЗамком()
    {
        var epic = CreateEpic();
        var path = JournalPath("u", epic.Id);
        var baseline = File.ReadAllLines(path).Length;

        var inside1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var insideCount = 0;
        var contendedCount = 0;
        _sut.FeatureNoteInside = () =>
        {
            switch (++insideCount)
            {
                case 1:
                    // Первый под своим замком: держим его, пока вторая и третья не встали в очередь.
                    inside1.TrySetResult();
                    contended2.Task.Wait(TimeSpan.FromSeconds(10));
                    break;
                case 2:
                    // Второй: сквозной хук — проходит свою критическую секцию целиком.
                    break;
                default:
                    // Третий под своим замком, своей оценки ещё нет — это контрольная точка.
                    inside3.TrySetResult();
                    release3.Task.Wait(TimeSpan.FromSeconds(10));
                    break;
            }
        };
        _sut.FeatureNoteContended = () =>
        {
            if (++contendedCount == 1) contended1.TrySetResult();
            else contended2.TrySetResult();
        };

        var first = Task.Run(() => _sut.SetFeatureNote(
            epic.Id, "первый", null, false, "prod", "running", Estimate(11, 111)));
        await inside1.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Точка наблюдения: первая на первом действии своей критической секции (хук раньше
        // её записи), вторая и третьей ещё нет у замка — ни одной новой строки. На форме
        // «до входа в замок» строка первой уже здесь (append до её хука) — красный.
        File.ReadAllLines(path).Length.Should().Be(baseline,
            "пока первая на своём хуке, нет ни одной новой строки журнала");

        var second = Task.Run(() => _sut.SetFeatureNote(
            epic.Id, "второй", null, false, "prod", "running", Estimate(22, 222)));
        // Событие, не пауза: вторая ждёт замок — ставим третью в очередь после неё.
        await contended1.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var third = Task.Run(() => _sut.SetFeatureNote(
            epic.Id, "третий", null, false, "prod", "running", Estimate(33, 333)));
        // Хук первой отпускает только Contended третьей — цепочка собрана целиком.
        await contended2.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Отпускаем первого — цепочка идёт по очереди замка: второй (целиком), затем третий
        // (до своей контрольной точки).
        await inside3.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Контрольная точка: третий под своим замком, своей оценки ещё не записал.
        var atCheckpoint = File.ReadAllLines(path).Length;
        release3.TrySetResult();

        var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(30));
        results.Should().HaveCount(3);

        // Под правильным замком строки первой и второй — под их собственными замками, до
        // релиза: третий, удерживая свой замок, видит обе.
        atCheckpoint.Should().Be(baseline + 2,
            "строки оценок пишутся только пока их замок удержан: под замком третьего "
            + "строки первых двух уже в файле");

        var saved = _sut.GetById(epic.Id)!.Note!.Estimate!;
        var lines = _journal.Read("u", epic.Id).Entries
            .Where(e => e.Kind == "estimate_set").ToList();
        lines.Should().HaveCount(3);
        // System.Threading.Lock не гарантирует FIFO: освободившийся замок первой может взять
        // и третья запись раньше второй (под нагрузкой так и бывает). Порядок второй и третьей
        // не проверяем — они равноправны; первая держала замок всё время, значит строго первая.
        lines[0].ExpectedSteps.Should().Be(11, "первая держала замок — её строка первая");
        lines.Skip(1).Select(l => l.ExpectedSteps).Should().BeEquivalentTo(new int?[] { 22, 33 });
        // Инвариант: последняя строка = сохранённая оценка (при любом порядке очереди замка).
        lines[^1].ExpectedSteps.Should().Be(saved.ExpectedSteps);
        lines[^1].EtaMinutes.Should().Be(saved.EtaMinutes);
    }

    // Прямой контроль порядка «сначала строка оценки, потом выход из-под _featureNoteLock»
    // (доработка 2 по ревью FS-O2a, форма «AppendJournal после finally»). Детерминизм — через
    // шов FeatureJournalStore.JournalAppending: он вызывается внутри per-key замка стора
    // непосредственно перед записью строки — наблюдаемый момент «append начался, строки ещё
    // нет», которого у File.AppendAllText нет (прежняя форма опрашивала файл 4-килобайтным
    // окном и ловила мутацию гонкой десятков мкс — флаковый тест).
    // Ход: первая запись держится хуком FeatureNoteInside в своём feature-замке, вторая встаёт
    // в очередь (Contended). Релиз первой: её append (правильная форма) входит в замок стора,
    // не выходя из feature-замка; шов держит стор и запись — контрольная точка. Контроль:
    // FeatureNoteInside второй ещё не случилось — он возможен только после Exit первой из
    // feature-замка, то есть после её append. На мутации «после finally» первая отпускает
    // feature-замок до своего append-а: вторая входит в него сразу, её хук (первое действие
    // её критической секции — раньше её же append-а) наступает раньше контрольной точки —
    // красный.
    // Без опроса файла и окон времени: контроль — состояние хука второй в момент шва первой.
    // Task.Delay нет: ждём событий (шов, хуки).
    [Fact]
    public async Task СтрокаОценки_ПодЗамком_ШовАппендаПервойРаньшеХукаВторой()
    {
        var epic = CreateEpic();

        var inside1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var insideCount = 0;
        _sut.FeatureNoteInside = () =>
        {
            switch (++insideCount)
            {
                case 1:
                    // Первый под своим замком: ждём отпускания наблюдателем.
                    inside1.TrySetResult();
                    release1.Task.Wait(TimeSpan.FromSeconds(10));
                    break;
                default:
                    // Второй под своим замком: держим до отпускания тестом.
                    inside2.TrySetResult();
                    release2.Task.Wait(TimeSpan.FromSeconds(10));
                    break;
            }
        };
        _sut.FeatureNoteContended = () => contended1.TrySetResult();
        // Шов стора: первая же append-запись держит per-key замок до отпускания тестом —
        // это и есть контрольная точка «append начался, строки ещё нет».
        var appendCount = 0;
        _journal.JournalAppending = _ =>
        {
            if (++appendCount == 1)
            {
                appendStarted.TrySetResult();
                releaseAppend.Task.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var first = Task.Run(() => _sut.SetFeatureNote(
            epic.Id, "первый", null, false, "prod", "running", Estimate(11, 111)));
        await inside1.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = Task.Run(() => _sut.SetFeatureNote(
            epic.Id, "второй", null, false, "prod", "running", Estimate(22, 222)));
        // Событие, не пауза: вторая ждёт замок первой.
        await contended1.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Релиз первой: при правильном порядке её append идёт под её feature-замком —
        // вторая физически не может войти в него, пока первая не выйдет (после своего append).
        release1.TrySetResult();
        await appendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Контрольная точка: append первой начался, её строки ещё нет. Хук второй — не раньше:
        // он возможен только после Exit первой из feature-замка (под мутации «после finally»
        // первая отпускает замок до своего append-а — хук второй наступает раньше этой точки).
        inside2.Task.IsCompleted.Should().BeFalse(
            "хук второй не может наступить при незавершённом append первой: первая запись держит "
            + "свой feature-замок до своей строки журнала (хук — только после её Exit)");

        releaseAppend.TrySetResult();
        release2.TrySetResult();
        var results = await Task.WhenAll([first, second]).WaitAsync(TimeSpan.FromSeconds(30));
        results.Should().HaveCount(2);

        var lines = _journal.Read("u", epic.Id).Entries
            .Where(e => e.Kind == "estimate_set").ToList();
        lines.Should().HaveCount(2, "обе оценки оставляют свои строки");
        var saved = _sut.GetById(epic.Id)!.Note!.Estimate!;
        lines[^1].ExpectedSteps.Should().Be(saved.ExpectedSteps,
            "последняя строка журнала = сохранённая оценка");
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
    // что осталась в сторе. Мутация «AppendJournal до входа в _featureNoteLock»: окно между
    // «записали голос» и «записали журнал» путает порядок → последняя строка не совпадает с
    // Note.Estimate (красный). Форма «AppendJournal после finally» на этот тест зелёная:
    // порядок релизов почти всегда совпадает с порядком append-ов — гонка, а не гарантия
    // (проба ревью: 11 прогонов мутации — зелёные). Её детерминированно ловит
    // СтрокаОценки_ПодЗамком_ЗамокСтораДержитПервуюДоСвоейСтроки (замок стора держится
    // маркером).
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

    // ─── Базовый срок: ровно один, при нарезанном плане (FS-O2b) ────────────────────────────

    // Десять первых оценок при planState == "cut" идут параллельно. Проверка «базы ещё нет» и её
    // запись — под одним _featureNoteLock (TaskManager.cs), поэтому базу ставит тот, кто первым
    // вошёл в замок: в тесте это A — он держится в своей критической секции (шов FeatureNoteInside),
    // пока остальные девять не уперлись в занятый замок (шов FeatureNoteContended, порядок
    // детерминирован, не пауза). Мутация «проверка InitialEtaMinutes вне блокировки» (читать
    // task.InitialEtaMinutes до TryEnter): все десять видят null и каждая пишет свой срок — база
    // становится последней из очереди, а не A → первый Should красный (проверено прогоном в копии
    // дерева). Само правило и его мутация «вернуть условие StartedKinds» —
    // SetFeatureNote_InitialEtaMinutes_... в TaskManagerTests.
    [Fact]
    public async Task ПервыеОценкиПриCut_Параллельно_БазаОдна()
    {
        var epic = CreateEpic();
        var ctx = new TaskManager.FeatureEstimateContext("prod", "cut", 0, 3);

        var aInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var othersQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var insideCount = 0;
        var contendedCount = 0;
        _sut.FeatureNoteInside = () =>
        {
            // Первый вошедший — A: держим его в критической секции, пока остальные не встали в
            // очередь замка; их критические секции сквозные (хук ничего не делает).
            if (++insideCount == 1)
            {
                aInside.TrySetResult();
                othersQueued.Task.Wait(TimeSpan.FromSeconds(10));
            }
        };
        _sut.FeatureNoteContended = () =>
        {
            if (++contendedCount == 9) othersQueued.TrySetResult();
        };
        try
        {
            var a = Task.Run(() => _sut.SetFeatureNote(
                epic.Id, "база A", null, false, "prod", "running", Estimate(10, 300), ctx));
            await aInside.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var others = Enumerable.Range(0, 9)
                .Select(i => Task.Run(() => _sut.SetFeatureNote(
                    epic.Id, $"ход {i}", null, false, "prod", "running", Estimate(11 + i, 400 + i), ctx)))
                .ToArray();
            await othersQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var results = await Task.WhenAll([a, .. others]).WaitAsync(TimeSpan.FromSeconds(30));
            results.Should().HaveCount(10);

            _sut.GetById(epic.Id)!.InitialEtaMinutes.Should().Be(300,
                "базу пишет тот, кто первым держал замок: остальные видят уже записанную");

            // Дальше база не двигается: следующая оценка при cut её не перезаписывает.
            _sut.SetFeatureNote(epic.Id, "позже", null, false, "prod", "running", Estimate(30, 900), ctx);
            _sut.GetById(epic.Id)!.InitialEtaMinutes.Should().Be(300);
        }
        finally
        {
            _sut.FeatureNoteInside = null;
            _sut.FeatureNoteContended = null;
        }
    }

    // ─── База плана: ровно одна, при нарезанном или принятом плане (FS-PB1) ────────────────────

    // Правило (решение Андрея 11.11, поправка постановщика 11.11): BaselineSteps — из контекста
    // первой ЗАПИСАННОЙ оценки при planState == "cut" ИЛИ "done" с Total > 0; пишется один раз и
    // не меняется никогда (ни другой оценкой, ни снятием голоса). Отказы: без снимка учёта, при
    // pending, при Total == 0 — база не пишется, исключения наружу нет. Мутации (прогон в копии
    // дерева, вывод в отчёте задачи):
    //  • убрать условие BaselineSteps is null — вторая оценка при cut (Total 15) перезапишет 12 →
    //    красный Should «не меняется никогда»;
    //  • убрать "done" из условия — красный SetFeatureNote_ПерваяОценкаПриDone_БазаЕсть;
    //  • читать task.BaselineSteps вне блокировки (до TryEnter) — красный
    //    ПервыеОценкиПриCut_Параллельно_БазаОдна_BaselineSteps (все десять видят null, база
    //    становится последним Total очереди).
    [Fact]
    public void SetFeatureNote_BaselineSteps_ПишетсяОдинРазПриНарезанномИлиПринятомПлане()
    {
        var epic = CreateEpic();

        // Без снимка учёта базы нет вовсе (в живых путях снимка нет только когда нет проекции).
        _sut.SetFeatureNote(epic.Id, "без снимка", null, false, "prod", "running", Estimate(5, 100));
        _sut.GetById(epic.Id)!.BaselineSteps.Should().BeNull("нет снимка учёта — нет базы");

        // План не нарезан: оценку при pending не о чём мерить.
        _sut.SetFeatureNote(epic.Id, "план не нарезан", null, false, "prod", "running", Estimate(5, 100),
            new TaskManager.FeatureEstimateContext("prod", "pending", 0, 0));
        _sut.GetById(epic.Id)!.BaselineSteps.Should().BeNull("оценка до нарезки плана — не база");

        // Пустой знаменатель (Total == 0) — отказ, без исключения наружу.
        _sut.SetFeatureNote(epic.Id, "пустой план", null, false, "prod", "running", Estimate(5, 100),
            new TaskManager.FeatureEstimateContext("prod", "cut", 0, 0));
        _sut.GetById(epic.Id)!.BaselineSteps.Should().BeNull("Total == 0 — не база");

        // Первая оценка при cut: база = Total снимка вызывающего.
        _sut.SetFeatureNote(epic.Id, "идёт", null, false, "prod", "running", Estimate(10, 300),
            new TaskManager.FeatureEstimateContext("prod", "cut", 0, 12));
        _sut.GetById(epic.Id)!.BaselineSteps.Should().Be(12);

        // Повторная оценка при cut с другим Total базу не двигает; снятие голоса — тоже.
        _sut.SetFeatureNote(epic.Id, "дольше", null, false, "prod", "running", Estimate(13, 400),
            new TaskManager.FeatureEstimateContext("prod", "cut", 4, 15));
        _sut.SetFeatureNote(epic.Id, "", null, false, "prod", "running");
        _sut.GetById(epic.Id)!.BaselineSteps.Should().Be(12,
            "записанная база не меняется никогда: ни другой оценкой, ни снятием голоса");
    }

    // Поправка постановщика 11.11: окно «cut» закрывается само при accepted == total
    // (FeatureStepAccounting.cs:428) — без «done» база не записалась бы у фичи, чью первую оценку
    // дали после приёмки всех шагов.
    [Fact]
    public void SetFeatureNote_ПерваяОценкаПриDone_БазаЕсть()
    {
        var epic = CreateEpic();

        _sut.SetFeatureNote(epic.Id, "всё принято", null, false, "prod", "running", Estimate(10, 300),
            new TaskManager.FeatureEstimateContext("prod", "done", 12, 12));

        _sut.GetById(epic.Id)!.BaselineSteps.Should().Be(12);
    }

    // Проверка базы отдельна от InitialEtaMinutes: оценка только с expectedSteps (без срока) при
    // cut тоже фиксирует базу; базовый срок при этом не пишется.
    [Fact]
    public void SetFeatureNote_ОценкаБезСрока_БазуФиксирует_InitialEtaMinutesНеТронут()
    {
        var epic = CreateEpic();

        _sut.SetFeatureNote(epic.Id, "идёт", null, false, "prod", "running",
            new(["шаг"], null, "проверка базы", 9),
            new TaskManager.FeatureEstimateContext("prod", "cut", 0, 9));

        _sut.GetById(epic.Id)!.BaselineSteps.Should().Be(9, "база не зависит от наличия срока");
        _sut.GetById(epic.Id)!.InitialEtaMinutes.Should().BeNull("оценка без срока — не базовый срок");
    }

    // Голос без оценки (и снятие голоса) — не оценка: база не пишется, даже при cut с Total > 0
    // («фичи без оценки: BaselineSteps остаётся null»).
    [Fact]
    public void SetFeatureNote_БезОценки_БазыНет_ДажеПриCut()
    {
        var epic = CreateEpic();
        var ctx = new TaskManager.FeatureEstimateContext("prod", "cut", 0, 12);

        _sut.SetFeatureNote(epic.Id, "голос без оценки", null, false, "prod", "running", null, ctx);
        _sut.SetFeatureNote(epic.Id, "", null, false, "prod", "running", null, ctx);

        _sut.GetById(epic.Id)!.BaselineSteps.Should().BeNull();
    }

    // Десять первых оценок при cut идут параллельно, у каждой — свой Total (12…21). Проверка
    // «базы ещё нет» и её запись — под одним _featureNoteLock (TaskManager.cs), поэтому базу ставит
    // тот, кто первым держал замок: в тесте это A — он задержан в своей критической секции (шов
    // FeatureNoteInside), пока остальные девять не уперлись в занятый замок (шов
    // FeatureNoteContended, порядок детерминирован, не пауза). Мутация «читать task.BaselineSteps
    // до TryEnter»: все десять видят null и каждая пишет свой Total → база становится последним
    // значением очереди, первый Should красный (проверено прогоном в копии дерева).
    [Fact]
    public async Task ПервыеОценкиПриCut_Параллельно_БазаОдна_BaselineSteps()
    {
        var epic = CreateEpic();

        var aInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var othersQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var insideCount = 0;
        var contendedCount = 0;
        _sut.FeatureNoteInside = () =>
        {
            // Первый вошедший — A: держим его в критической секции, пока остальные не встали в
            // очередь замка; их критические секции сквозные (хук ничего не делает).
            if (++insideCount == 1)
            {
                aInside.TrySetResult();
                othersQueued.Task.Wait(TimeSpan.FromSeconds(10));
            }
        };
        _sut.FeatureNoteContended = () =>
        {
            if (++contendedCount == 9) othersQueued.TrySetResult();
        };
        try
        {
            var a = Task.Run(() => _sut.SetFeatureNote(
                epic.Id, "база A", null, false, "prod", "running", Estimate(10, 300),
                new TaskManager.FeatureEstimateContext("prod", "cut", 0, 12)));
            await aInside.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var others = Enumerable.Range(0, 9)
                .Select(i => Task.Run(() => _sut.SetFeatureNote(
                    epic.Id, $"ход {i}", null, false, "prod", "running", Estimate(11 + i, 400 + i),
                    new TaskManager.FeatureEstimateContext("prod", "cut", 0, 13 + i))))
                .ToArray();
            await othersQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var results = await Task.WhenAll([a, .. others]).WaitAsync(TimeSpan.FromSeconds(30));
            results.Should().HaveCount(10);

            _sut.GetById(epic.Id)!.BaselineSteps.Should().Be(12,
                "базу пишет тот, кто первым держал замок: остальные видят уже записанную");

            // Дальше база не двигается: следующая оценка при cut с другим Total её не перезаписывает.
            _sut.SetFeatureNote(epic.Id, "позже", null, false, "prod", "running", Estimate(30, 900),
                new TaskManager.FeatureEstimateContext("prod", "cut", 5, 30));
            _sut.GetById(epic.Id)!.BaselineSteps.Should().Be(12);
        }
        finally
        {
            _sut.FeatureNoteInside = null;
            _sut.FeatureNoteContended = null;
        }
    }
}
