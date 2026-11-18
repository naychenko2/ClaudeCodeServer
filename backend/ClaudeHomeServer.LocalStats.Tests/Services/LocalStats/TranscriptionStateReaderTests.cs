using System.Text;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Читатель состояния конвейера транскрибации (вкладка «Встречи», Б1). Все файлы — во временных
/// каталогах; часы — фиксированные (границы 5 мин и 24 ч проверяются детерминированно, без дрифта);
/// живость pid — подставной щуп (на CI /proc чужих pid нет).
/// </summary>
public class TranscriptionStateReaderTests : IDisposable
{
    // ─── фикстура ───────────────────────────────────────────────────────────────────

    private readonly List<string> _roots = [];

    private sealed class Opts(TranscriptionOptions value) : IOptionsMonitor<TranscriptionOptions>
    {
        public TranscriptionOptions CurrentValue => value;
        public TranscriptionOptions Get(string? name) => value;
        public IDisposable OnChange(Action<TranscriptionOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    // Фиксированные часы: снимок «сейчас», усечённый до секунды — моменты в файлах строятся
    // от него, возраст ровно тот, что задан тестом.
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateTimeOffset NowTrunc()
    {
        var n = DateTimeOffset.Now;
        return new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, n.Offset);
    }

    // Точка ровно на минуте: журнал несёт время с точностью до минуты, поэтому граница окна
    // [now−24ч, now) проверяется детерминированно только от минутной базы.
    private static DateTimeOffset NowMinute()
    {
        var n = DateTimeOffset.Now;
        return new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, 0, n.Offset);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccs-transc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        _roots.Add(root);
        return root;
    }

    private static void Write(string root, string rel, string text)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text, new UTF8Encoding(false));
    }

    // Файл вывода с заданным mtime: provenance вывода мерится по времени файла, поэтому тест
    // ставит его прямо — как у файла, принесённого синхронизацией (mtime — дата создания на
    // чужом узле, не дата копирования).
    private static void WriteOutputFile(string dir, string name, DateTimeOffset mtime)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "сводка встречи\n");
        File.SetLastWriteTimeUtc(p, mtime.UtcDateTime);
    }

    // Маркер «последнего писавшего» в каталоге вывода (М5): строка «узел\tunix\tвремя\tseq» на
    // узел, как его пишет lib_journal.mark_output_host. Колонку «время» читатель не читает —
    // порядок задаёт seq.
    private static void WriteOutputHostMarker(string dir, params (string Node, long Unix, int Seq)[] rows)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string> { "# узел\tunix\tвремя\tseq" };
        foreach (var (node, unix, seq) in rows)
            lines.Add(string.Join('\t', node, unix, DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), seq));
        File.WriteAllText(Path.Combine(dir, TranscriptionStateReader.OutputHostName), string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }

    private static TranscriptionStateReader Reader(string root, TimeProvider time, Func<int, bool>? alive = null) =>
        new(new Opts(new TranscriptionOptions { PipelineRoot = root }),
            new ConfigurationBuilder().Build(), time, alive ?? (_ => true));

    // Метка state.json: «yyyy-MM-dd HH:mm:ss» местного времени узла — строим от фиксированных часов.
    private static string Stamp(DateTimeOffset now, TimeSpan ago) => now.Subtract(ago).ToString("yyyy-MM-dd HH:mm:ss");

    private static string StampMin(DateTimeOffset now, TimeSpan ago) => now.Subtract(ago).ToString("yyyy-MM-dd HH:mm");

    private static string StateJson(string phase, DateTimeOffset now, int pid, TimeSpan age,
        int? queue = null, int? pending = null, string? current = null, string? folder = null, int? batchTotal = null,
        string? queueItems = null, long? stageStartedAt = null)
    {
        var f = new List<string> { $"\"phase\": \"{phase}\"", $"\"updated\": \"{Stamp(now, age)}\"", $"\"pid\": {pid}" };
        if (queue is not null) f.Add($"\"queue\": {queue}");
        if (queueItems is not null) f.Add($"\"queue_items\": {queueItems}");
        if (pending is not null) f.Add($"\"pending\": {pending}");
        if (current is not null) f.Add($"\"current\": \"{current}\"");
        if (folder is not null) f.Add($"\"folder\": \"{folder}\"");
        if (batchTotal is not null) f.Add($"\"batch_total\": {batchTotal}");
        if (stageStartedAt is not null) f.Add($"\"stage_started_at\": {stageStartedAt}");
        return "{ " + string.Join(", ", f) + " }";
    }

    // stage_started_at (М4) конвейер пишет в unix-СЕКУНДАХ (int(time.time())): тест строит их
    // от тех же фиксированных часов — сравнение с unix-мс ответа тогда точное, без дрифта.
    private static long StageSec(DateTimeOffset now, TimeSpan ago) => now.Subtract(ago).ToUnixTimeSeconds();

    private static string JournalRowStr(DateTimeOffset now, TimeSpan age, string result,
        string folder = "-", string file = "-", string minutes = "-", string speakers = "-", string note = "") =>
        string.Join('\t', StampMin(now, age), result, folder, file, minutes, speakers, note);

    private static string ConfigYaml(params (string Name, string Watch, string Output)[] folders)
    {
        var sb = new StringBuilder("# конфиг конвейера (фрагмент для теста)\n");
        if (folders.Length > 0) sb.Append("folders:\n");
        foreach (var (name, watch, output) in folders)
            sb.Append($"  - name: {name}\n    watch: {watch}\n    output: {output}\n");
        sb.Append("\nwhisper:\n  model: large-v3-turbo\n");
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var r in _roots)
            try { Directory.Delete(r, true); } catch (IOException) { }
    }

    // ─── состояние и уровень ────────────────────────────────────────────────────────

    [Fact]
    public void Idle_СвежийСнимок_УровеньОк()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Directory.CreateDirectory(output);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(30), queue: 2));
        Write(root, "logs/summary_state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(30), pending: 1));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", watch, output)));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Phase.Should().Be("idle");
        s.Alive.Should().BeTrue();
        s.Host.Should().Be("worker"); // реестра нет → фолбэк ровно один «worker»
        s.OutputHost.Should().Be("worker");
        s.OutputDir.Should().Be(output);
        s.Level.Should().Be("ok");
        // очередь: summaryQueue из summary_state.json; списка файлов конвейер не пишет — asrQueue пуст (М2)
        s.SummaryQueue.Should().Be(1);
        s.AsrQueue.Should().BeEmpty(); // queue_items нет — очередь пустая (число «queue» в плоском контракте не отдаётся)
        // в простое зависимости не проверялись, кроме сетевой папки (есть на узле)
        s.Deps.Select(d => (d.Key, d.State)).Should().Equal(
            ("asr", "unknown"), ("summary", "unknown"), ("dify", "unknown"), ("share", "ok"));
        s.File.Should().BeNull();
        s.Stage.Should().BeNull();
        s.Fails.Should().BeEmpty();
        s.Recent.Should().BeEmpty();
    }

    // Граница порога строгая («старше 5 мин»): ровно 5 мин — ещё нет, 5 мин + 1 с — да.
    // «stale» — внутренний подуровень ридера: в плоском контракте он внешний level «danger».
    [Theory]
    [InlineData(0, 4, 50, "ok")]
    [InlineData(0, 5, 0, "ok")]
    [InlineData(0, 5, 1, "danger")]
    public void Завис_ГраницаПорога5МинПриЖивомPid(int h, int m, int sec, string expected)
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, new TimeSpan(0, h, m, sec),
            current: "X.mp4", folder: "Video-RR"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Level.Should().Be(expected);
        s.Phase.Should().Be("batch"); // живой процесс на файле — занят
    }

    [Fact]
    public void МёртвыйPid_ОстановленДажеПриСвежемСнимке()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromSeconds(10),
            current: "X.mp4", folder: "Video-RR"));

        var s = Reader(root, new FixedTime(now), pid => false).Read();

        s.Alive.Should().BeFalse();
        s.Phase.Should().Be("stopped"); // мёртвый pid — «stopped» при любой фазе
        s.Level.Should().Be("danger");
        // stopped/stale — сигналы не проверялись
        s.Deps.Should().OnlyContain(d => d.State == "unknown");
    }

    [Fact]
    public void ФазаОстановлен_ОстановленДажеПриЖивомPid()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("остановлен", now, 100, TimeSpan.FromSeconds(10)));

        var s = Reader(root, new FixedTime(now)).Read();

        // сам факт «остановлен» в state.json сильнее живой pid: процесс пишет этот снимок
        // в finally и доходит до выхода — фаза «stopped» независимо от живости
        s.Phase.Should().Be("stopped");
        s.Level.Should().Be("danger");
    }

    [Fact]
    public void НетФайлов_ПустойСнимокБезИсключения()
    {
        var now = NowTrunc();
        var root = NewRoot();

        var s = Reader(root, new FixedTime(now)).Read();

        s.Phase.Should().Be("stopped"); // состояния нет — живого процесса не знаем
        s.Level.Should().BeNull(); // «не проверялось» — уровня нет, а не «unknown»
        s.UpdatedAt.Should().BeNull();
        s.SummaryQueue.Should().Be(0);
        s.Fails.Should().BeEmpty();
        s.Recent.Should().BeEmpty();
        s.Dify.Should().BeEmpty();
        s.CheckedAt.Should().BeNull();
        s.Deps.Should().OnlyContain(d => d.State == "unknown");
    }

    [Theory]
    [InlineData("{ «phase»: «ожидание» }")] // битый JSON
    [InlineData("[1, 2, 3]")] // чужая форма
    [InlineData("")] // пусто
    public void БитыйStateJson_СнимокUnknown(string body)
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", body);

        var s = Reader(root, new FixedTime(now)).Read();

        s.Level.Should().BeNull(); // «не проверялось» — уровня нет
        s.Phase.Should().Be("stopped");
    }

    [Fact]
    public void НеизвестнаяФазаЖивогоПроцесса_КакЗанятBatch()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("проверка архива", now, 100, TimeSpan.FromSeconds(10)));

        var s = Reader(root, new FixedTime(now)).Read();

        // живый процесс при неопознанной фазе занят — «batch»; сырой текст в плоском контракте не отдаётся
        s.Phase.Should().Be("batch");
        s.Level.Should().Be("ok");
    }

    // Контракт значений (frontend/src/features/localModel/types.ts): phase — только
    // "idle"|"batch"|"stopped"; «ждёт зависимость» и незнакомая фаза живого процесса — "batch",
    // мёртвый pid — "stopped" при любой фазе. level, stage и deps[].state — вне своих множеств
    // не выходят («не проверялось» — null в level/stage, «unknown» допустим только в deps).
    [Theory]
    [InlineData("ожидание", true, "idle")]
    [InlineData("транскрибация", true, "batch")]
    [InlineData("очередь копится", true, "batch")]
    [InlineData("ждёт машину с GPU", true, "batch")]
    [InlineData("что-то незнакомое", true, "batch")]
    [InlineData("остановлен", true, "stopped")]
    [InlineData("транскрибация", false, "stopped")]
    public void КонтрактЗначений_ФазаТолькоИзФронтовогоМножества(string raw, bool alive, string expected)
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson(raw, now, 100, TimeSpan.FromSeconds(10)));

        var s = Reader(root, new FixedTime(now), _ => alive).Read();

        s.Phase.Should().Be(expected);
        s.Level.Should().BeOneOf("ok", "warn", "danger");
        new[] { (string?)null, "asr", "summary", "dify" }.Should().Contain(s.Stage);
        s.Deps.Should().OnlyContain(d => new[] { "ok", "slow", "down", "unknown" }.Contains(d.State));
    }

    // ─── «Сейчас» ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Транскрибация_ЭтапAsrДлительностьВозрастаСнимка()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromMinutes(3),
            queue: 1, current: "X.mp4", folder: "Video-RR"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.File.Should().Be("X.mp4");
        s.Folder.Should().Be("Video-RR");
        s.Stage.Should().Be("asr");
        s.DurationMin.Should().Be(3.0); // возраст последней правки снимка (updated) — как раньше
        // место в пачке и размер пачки state.json на файле не несёт
        s.BatchIndex.Should().BeNull();
        s.BatchSize.Should().BeNull();
        // распознавание идёт — зависимость asr «в норме»
        s.Deps.First(d => d.Key == "asr").State.Should().Be("ok");
    }

    // ─── «Сейчас»: начало текущей операции (stage_started_at, М4 → доработка Б1) ─────────
    // stageStartedAt — метка начала операции из самого снимка конвейера, а НЕ updated: пульс
    // обновляет только updated, и подстановка его вместо начала операции давала «10 секунд»
    // вместо реальных минут. Метку начала и возраст снимка не смешивать: «Завис?» — по updated.

    [Fact]
    public void НачалоОперацииИзStageStartedAtСекундыСтановятсяМс()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromSeconds(10),
            current: "X.mp4", folder: "Video-RR", stageStartedAt: StageSec(now, TimeSpan.FromMinutes(12))));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Stage.Should().Be("asr"); // вид операции — по фазе, она здесь прежняя
        s.StageStartedAt.Should().Be(now.Subtract(TimeSpan.FromMinutes(12)).ToUnixTimeMilliseconds());
    }

    // Метки нет — операции нет (ожидание, подъём vLLM) или снимок писался без поля (до М4):
    // StageStartedAt null, подмены updated нет.
    [Fact]
    public void НетМеткиНачала_StageStartedAtNullНеМеткаПравкиСнимка()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromSeconds(10),
            current: "X.mp4", folder: "Video-RR"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Stage.Should().Be("asr");
        s.StageStartedAt.Should().BeNull();
        s.UpdatedAt.Should().NotBeNull(); // возраст снимка остаётся своим полем, он никуда не делся
    }

    // Контрактный тест доработки: метка начала — не возраст снимка. Метка старше порога «Завис?»
    // (12 мин), сам снимок свежий (10 с) → «Завис?» по-прежнему по updated и остаётся ok:
    // длинная операция порогом не мерится. Мутация «подставить updated вместо stage_started_at»
    // краснит этот тест по всем трём проверкам.
    [Fact]
    public void StageStartedAtНеРавноUpdatedAtЗависПоПрежнемуПоВозрастуСнимка()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromSeconds(10),
            current: "X.mp4", folder: "Video-RR", stageStartedAt: StageSec(now, TimeSpan.FromMinutes(12))));

        var s = Reader(root, new FixedTime(now)).Read();

        s.StageStartedAt.Should().NotBe(s.UpdatedAt);
        s.StageStartedAt.Should().Be(now.Subtract(TimeSpan.FromMinutes(12)).ToUnixTimeMilliseconds());
        s.UpdatedAt.Should().Be(now.Subtract(TimeSpan.FromSeconds(10)).ToUnixTimeMilliseconds());
        s.Level.Should().Be("ok");
    }

    [Fact]
    public void ПараллельнаяСводкаПриПростоеОсновногоПотока_ЭтапSummary()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(20), queue: 0));
        Write(root, "logs/summary_state.json", StateJson("саммари", now, 100, TimeSpan.FromMinutes(1),
            current: "Y.mp4", folder: "Video-GPB", stageStartedAt: StageSec(now, TimeSpan.FromMinutes(8))));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Stage.Should().Be("summary");
        s.File.Should().Be("Y.mp4");
        // метка начала — из summary_state.json того же процесса, не из его updated
        s.StageStartedAt.Should().Be(now.Subtract(TimeSpan.FromMinutes(8)).ToUnixTimeMilliseconds());
        s.Deps.First(d => d.Key == "summary").State.Should().Be("ok");
    }

    [Fact]
    public void СнимокСводокЧужогоПроцесса_НеПоказывается()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(20)));
        // pid другого (прошлого) процесса — снимок считается старым (правило status.py)
        Write(root, "logs/summary_state.json", StateJson("саммари", now, 200, TimeSpan.FromSeconds(20),
            pending: 3, current: "Y.mp4", folder: "Video-GPB"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.File.Should().BeNull();
        s.Stage.Should().BeNull();
        s.SummaryQueue.Should().Be(0);
    }

    [Fact]
    public void ОсновнойПотокНаФайле_ГлавнееПараллельнойСводки()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromSeconds(30),
            current: "X.mp4", folder: "Video-RR"));
        Write(root, "logs/summary_state.json", StateJson("саммари", now, 100, TimeSpan.FromSeconds(30),
            current: "Y.mp4", folder: "Video-GPB"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Stage.Should().Be("asr");
        s.File.Should().Be("X.mp4");
    }

    // ─── очередь asr (queue_items, М2) ──────────────────────────────────────────────

    // asrQueue — из queue_items самого конвейера: {file, folder, since} в unix-СЕКУНДАХ → unix-мс.
    // Строка без имени файла пропускается; строка с нечисловым since остаётся без времени
    // (файл правда ждёт — неизвестна только дата, фронт рисует «ждёт»); порядка не меняем.
    [Fact]
    public void ОчередьAsrИзQueueItemsСекундыСтановятсяМс()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10), queue: 3,
            queueItems: """[ { "file": "A.mp4", "folder": "Video-GPB", "since": 1794997217 }, { "file": "B.mp4", "since": "не число" }, { "noFile": 1 }, { "file": "C.mp4", "since": 1794997220 } ]"""));

        var s = Reader(root, new FixedTime(now)).Read();

        s.AsrQueue.Should().HaveCount(3);
        s.AsrQueue[0].File.Should().Be("A.mp4");
        s.AsrQueue[0].Folder.Should().Be("Video-GPB");
        s.AsrQueue[0].Since.Should().Be(1_794_997_217_000);
        s.AsrQueue[1].File.Should().Be("B.mp4");
        s.AsrQueue[1].Since.Should().BeNull();
        s.AsrQueue[2].File.Should().Be("C.mp4");
        s.AsrQueue[2].Since.Should().Be(1_794_997_220_000);
    }

    // Битый queue_items (не массив) — пустой список, но снимок живой: фаза и уровень не теряются.
    [Fact]
    public void БитыйQueueItemsПустаяОчередьНоСнимокЖивой()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10), queueItems: "\"не массив\""));

        var s = Reader(root, new FixedTime(now)).Read();

        s.AsrQueue.Should().BeEmpty();
        s.Phase.Should().Be("idle");
        s.Level.Should().Be("ok");
    }

    // ─── журнал: хвост, окно, лимиты ────────────────────────────────────────────────

    // Окно [now−24ч, now): ровно на границе — входит, на минуту старше — нет. Строки «Сбоев»
    // сами по себе точку не красят (proposal.md: красная — Завис?/Остановлен/сбой зависимости):
    // конвейер жив, снимок свежий → уровень остаётся ok.
    [Fact]
    public void СбоиОкно24ЧГраницаСтрогая()
    {
        var now = NowMinute();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));

        Write(root, "logs/journal.tsv", string.Join('\n',
            new List<string>
            {
                "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
                JournalRowStr(now, TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(1)), "fail", "Video-RR", "ЗаОкном.mp4", note:"за окном"),
                JournalRowStr(now, TimeSpan.FromHours(24), "fail", "Video-RR", "Граница.mp4", note:"на границе"),
                JournalRowStr(now, TimeSpan.FromMinutes(2), "fail", "Video-GPB", "F2.mp4", note:"сбой 2"),
                JournalRowStr(now, TimeSpan.FromMinutes(1), "fail", "Video-GPB", "F1.mp4", note:"сбой 1"),
            }) + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Fails.Should().HaveCount(3);
        s.Fails[0].File.Should().Be("F1.mp4"); // свежайший первым
        s.Fails[2].File.Should().Be("Граница.mp4");
        s.Fails.Should().NotContain(f => f.File == "ЗаОкном.mp4");
        s.Level.Should().Be("ok");
    }

    [Fact]
    public void СбоиПотолок20СвежиеПервыми()
    {
        var now = NowMinute();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));

        var lines = new List<string> { "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание" };
        for (var i = 1; i <= 25; i++)
            lines.Add(JournalRowStr(now, TimeSpan.FromMinutes(i), "fail", "Video-GPB", $"F{i}.mp4", "1.5", "-", $"сбой {i}"));
        Write(root, "logs/journal.tsv", string.Join('\n', lines) + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Fails.Should().HaveCount(20); // 25 в окне → потолок 20
        s.Fails[0].Comment.Should().Be("сбой 1");
        s.Fails.Should().NotContain(f => f.File == "F21.mp4");
        s.Level.Should().Be("ok");
    }

    [Fact]
    public void ХвостЖурналаАНеВесьФайл_ДальниеСтрокиНеЧитаются()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));

        // файл заведомо длиннее JournalTailBytes: самый старый сбой — в начале файла,
        // дальше только служебные строки. Сбой из-за хвоста в ответ не попадает.
        var lines = new List<string> { "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromMinutes(60), "fail", "Video-RR", "HEADFAIL.mp4", note:"сбой в начале файла") };
        for (var i = 0; i < 4000; i++)
            lines.Add(JournalRowStr(now, TimeSpan.FromSeconds(59 - i % 50), "batch", note:$"пачка {i}"));
        var text = string.Join('\n', lines) + "\n";
        Encoding.UTF8.GetByteCount(text).Should().BeGreaterThan(TranscriptionStateReader.JournalTailBytes);
        Write(root, "logs/journal.tsv", text);

        var s = Reader(root, new FixedTime(now)).Read();

        s.Fails.Should().BeEmpty();
    }

    [Fact]
    public void БитаяСтрокаЖурналаПропускается()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "logs/journal.tsv", "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание\n" +
            "2026-13-99\tfail\tVideo-RR\n" + // ни даты, ни семи колонок
            JournalRowStr(now, TimeSpan.FromMinutes(1), "fail", "Video-RR", "X.mp4", note:"нормальный сбой") + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Fails.Should().HaveCount(1);
        s.Fails[0].File.Should().Be("X.mp4");
    }

    [Fact]
    public void ПустойЖурналТолькоЗаголовок_ПустыеБлоки()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "logs/journal.tsv", "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание\n");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Fails.Should().BeEmpty();
        s.Recent.Should().BeEmpty();
    }

    // ─── встречи и mdPath ───────────────────────────────────────────────────────────

    [Fact]
    public void ВстречиПоследние24Ч_MdPathПоВыходуПапкиИмяНормализовано()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var outRr = Path.Combine(root, "out", "RR");
        Directory.CreateDirectory(outRr);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", Path.Combine(root, "watch"), outRr)));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromMinutes(10), "ok", "Video-RR", "RR.Встреча.mp4", "2.9", "3", "asr 2.6м + саммари 0.4м (локально)"),
            // старое форматирование журнала писало имя уже с .md
            JournalRowStr(now, TimeSpan.FromMinutes(20), "ok", "Video-RR", "RR.Старое.mp4.md", "1.1", "2", "asr 1.0м + саммари 0.1м (локально)"),
            // папки нет в конфиге → mdPath null
            JournalRowStr(now, TimeSpan.FromMinutes(30), "ok", "Video-XX", "НетПапки.mp4"),
            // вне окна
            JournalRowStr(now, TimeSpan.FromHours(30), "ok", "Video-RR", "ЗаОкном.mp4")) + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Recent.Should().HaveCount(3);
        s.Recent[0].File.Should().Be("RR.Встреча.mp4");
        s.Recent[0].MdPath.Should().Be(Path.Combine(outRr, "RR.Встреча.md"));
        s.Recent[0].Minutes.Should().Be(2.9);
        s.Recent[0].Speakers.Should().Be(3);
        s.Recent[0].Comment.Should().Be("asr 2.6м + саммари 0.4м (локально)");
        s.Recent[1].MdPath.Should().Be(Path.Combine(outRr, "RR.Старое.mp4.md"));
        s.Recent[2].MdPath.Should().BeNull();
        s.Recent.Should().NotContain(r => r.File == "ЗаОкном.mp4");
    }

    // ─── «Пишет в» и сверка ─────────────────────────────────────────────────────────

    [Fact]
    public void ВыходНеНаЭтомУзле_УровеньWarnИТочкаЖёлтая()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "watch")); // watch на узле — единственный «не ок» остаёт вывод
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(
            ("Video-GPB", Path.Combine(root, "watch"), "/nonexistent-ccs-test/Встречи/ГПБ"),
            ("Video-RR", Path.Combine(root, "watch"), "/nonexistent-ccs-test/Встречи/RR")));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputDir.Should().Be("/nonexistent-ccs-test/Встречи"); // общий префикс по сегментам
        s.OutputHost.Should().BeNull();
        s.Level.Should().Be("warn"); // «не та машина» — внешний уровень плоского контракта
        // сверка — из файла конвейера; файла нет — пустой блок, не «ноль файлов»
        s.Dify.Should().BeEmpty();
        s.CheckedAt.Should().BeNull();
    }

    // ─── «Пишет в» не туда по данным конвейера (доработка Б1, дефект №2 приёмки) ──────────
    // Факт существования каталога вывода ничего не значит: на worker его наполнила синхронизация
    // с другого узла (18.11: писал «ai», читали с «worker», четыре встречи не дошли — а каталог
    // был, и признак молчал). Признак — .md в выводе свежее, чем последний локальный «ok»
    // журнала: эти файлы этот конвейер не писал.

    [Fact]
    public void ВыводНаполненКопированиемСЧужогоУзла_ПризнакНеТаМашина()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        Directory.CreateDirectory(watch);
        // конвейер жив и свеж, но свой последний «ok» — трёхдневной давности (порядок чисел
        // worker 18.11: журнал обрывается 15.11, свежие .md — 16–17.11)
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(
            ("Video-GPB", watch, Path.Combine(root, "out", "ГПБ")),
            ("Video-RR", watch, Path.Combine(root, "out", "RR"))));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromDays(3), "ok", "Video-RR", "RR.Своё.mp4", "2.9", "3", "asr 2.6м + саммари 0.4м (локально)")) + "\n");
        // то, что принесла синхронизация: mtime — вчера, позже последней своей записи журнала
        WriteOutputFile(Path.Combine(root, "out", "ГПБ"), "ИИ.Радул.Архсовет.2026-11-17 16-28-07.mp4.md", now.Subtract(TimeSpan.FromDays(1)));
        // свой файл того же «ok» остаётся своим: он не позже своей строки
        WriteOutputFile(Path.Combine(root, "out", "RR"), "RR.Своё.mp4.md", now.Subtract(TimeSpan.FromDays(3)));

        var s = Reader(root, new FixedTime(now)).Read();

        // каталог есть на узле — сам по себе это больше не «норма»: вывод ведётся не здесь
        s.OutputDir.Should().Be(Path.Combine(root, "out"));
        s.OutputHost.Should().BeNull(); // «не та машина»: имени чужого узла в плоском контракте нет
        s.Level.Should().Be("warn");
        // последний локальный «ok» — за пределами окна суток: блок «Встречи» его не показывает,
        // но provenance мерится по нему, а не по окну
        s.Recent.Should().BeEmpty();
    }

    // Норма: конвейер писал эти файлы сам — «ok» есть в локальном журнале, и он позже своего .md
    // (supervisor.py: сначала out_path, потом log_meeting). Метка строки отмерена с точностью до
    // минуты, поэтому свой файл может выглядеть на минуту «новее» своей же записи: без допуска
    // OutputOwnRowSlack этот тест краснеет — ложная тревога на каждом свежем файле.
    [Fact]
    public void ВыводВедётсяЛокальнымКонвейером_ПризнакНеСрабатывает()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromMinutes(10), "ok", "Video-RR", "RR.Свежее.mp4", "2.9", "3", "asr 2.6м + саммари 0.4м (локально)")) + "\n");
        // .md записан ДО строки «ok»: его время на 35 с позже отмеренной минуты строки
        WriteOutputFile(outDir, "RR.Свежее.mp4.md", now.Subtract(TimeSpan.FromMinutes(10)).Add(TimeSpan.FromSeconds(35)));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker"); // вывод здесь
        s.Level.Should().Be("ok");
        s.Recent.Should().ContainSingle(r => r.File == "RR.Свежее.mp4"); // файл и в журнале, и в выводе — свой
    }

    // Конвейер сейчас сам ведёт файл: его .md появится с минуты на минуту, а строка «ok» будет
    // после него. Свежий файл в выводе в этот момент — не чужой: пока он на файле, тревоги нет.
    [Fact]
    public void КонвейерСейчасВедётФайл_СвежийMdНеСчитаетсяЧужим()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("саммари", now, 100, TimeSpan.FromSeconds(10),
            current: "X.mp4", folder: "Video-RR"));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromHours(2), "ok", "Video-RR", "RR.Прежнее.mp4", "1.0", "2", "asr 1.0м + саммари 0.1м (локально)")) + "\n");
        WriteOutputFile(outDir, "X.mp4.md", now.Subtract(TimeSpan.FromMinutes(1))); // записан минуту назад, строки ещё нет

        var s = Reader(root, new FixedTime(now)).Read();

        s.Stage.Should().Be("summary");
        s.OutputHost.Should().Be("worker");
        s.Level.Should().Be("ok");
    }

    // Локального журнала нет (файла нет / ни одной строки) — сравнивать не с чем: provenance не
    // известна. Безопасное поведение — не показывать ложную тревогу: узел остаётся своим.
    [Fact]
    public void НетЛокальногоЖурнала_ЛожнойТревогиНет()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        WriteOutputFile(outDir, "ИИ.Радул.Архсовет.2026-11-17 16-28-07.mp4.md", now.Subtract(TimeSpan.FromDays(1)));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker"); // «не проверялось» — не «не та машина»
        s.Level.Should().Be("ok");
    }

    // ─── «Пишет в» по маркеру конвейера output_host.tsv (Б2, круг 2: по каждой папке отдельно) ──
    // Маркер — запись самого конвейера о том, кто писал в ЭТОТ каталог вывода последним (М5):
    // строка с наибольшим seq внутри своего файла. Счётчики по файлам независимы, поэтому
    // «последний писавший» судится по каждой папке отдельно и seq между папками не сравним;
    // тревога — только если во всех читаемых папках последним писал чужой узел. Маркера нет
    // или он битый — прежняя сверка по журналу (фолбэк Б1 не ломается).

    [Fact]
    public void МаркерЧужогоУзлаSeqБольше_ПризнакНеТаМашина()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        // часовой путь здесь дал бы «ok»: последний локальный «ok» час назад, его .md свой
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromHours(1), "ok", "Video-RR", "RR.Своё.mp4", "2.9", "3", "asr 2.6м + саммари 0.4м (локально)")) + "\n");
        WriteOutputFile(outDir, "RR.Своё.mp4.md", now.Subtract(TimeSpan.FromHours(1)));
        // но маркер говорит: в этот каталог последним писал «ai» — его строка с большим seq
        WriteOutputHostMarker(outDir,
            ("worker", now.Subtract(TimeSpan.FromHours(1)).ToUnixTimeSeconds(), 3),
            ("ai", now.Subtract(TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(), 4));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().BeNull(); // «не та машина»: маркер источник однозначный
        s.Level.Should().Be("warn");
    }

    [Fact]
    public void МаркерСвоегоУзлаSeqБольше_ПризнакаНет()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        // часовой путь здесь дал бы «warn»: последний локальный «ok» трёхдневной давности,
        // а в каталоге .md свежее (принесён синхронизацией)
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromDays(3), "ok", "Video-RR", "RR.Своё.mp4", "2.9", "3", "asr 2.6м + саммари 0.4м (локально)")) + "\n");
        WriteOutputFile(outDir, "ИИ.Радул.Архсовет.2026-11-17 16-28-07.mp4.md", now.Subtract(TimeSpan.FromDays(1)));
        // но маркер говорит: последним в этот каталог писал этот узел — его строка с большим seq
        WriteOutputHostMarker(outDir,
            ("ai", now.Subtract(TimeSpan.FromDays(1)).ToUnixTimeSeconds(), 2),
            ("worker", now.Subtract(TimeSpan.FromMinutes(10)).ToUnixTimeSeconds(), 5));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker"); // вывод здесь: маркер источник однозначный
        s.Level.Should().Be("ok");
    }

    // Порядок задаёт seq, а не unix: часы узлов расходятся (замер конвейера ~80 с), и строка
    // «новее по часам», но с меньшим seq, — не последняя. Мутация «взять последнего по unix»
    // краснит этот тест.
    [Fact]
    public void ПорядокПоSeqАНеПоUnix_СтрокаСБольшимВременемМеньшимSeqНеПоследняя()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        // «worker» новее по часам (минута назад), но его seq меньше: писал последним «ai» (час назад)
        WriteOutputHostMarker(outDir,
            ("worker", now.Subtract(TimeSpan.FromMinutes(1)).ToUnixTimeSeconds(), 2),
            ("ai", now.Subtract(TimeSpan.FromHours(1)).ToUnixTimeSeconds(), 7));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().BeNull();
        s.Level.Should().Be("warn");
    }

    // Маркера нет / он пуст / только заголовок / битые строки — это НЕ «чужой узел»: работает
    // прежняя сверка по журналу. Фикстура нормальная (свежий локальный «ok», свой файл в допуске
    // строки) — во всех вариантах ложной тревоги нет. Мутация «пустой маркер = чужой узел»
    // краснит этот тест.
    [Theory]
    [InlineData("")] // пусто
    [InlineData("# узел\tunix\tвремя\tseq\n")] // только заголовок
    [InlineData("мусор без табов\nworker\t1\t2\nai\t1\t2\tне-число\n")] // битые строки: нет колонок, нет seq
    public void МаркерОтсутствуетИлиБитый_ФолбэкНаСверкуПоЖурналу(string markerBody)
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromMinutes(10), "ok", "Video-RR", "RR.Свежее.mp4", "2.9", "3", "asr 2.6м + саммари 0.4м (локально)")) + "\n");
        // .md записан ДО строки «ok»: его время на 35 с позже отмеренной минуты строки — в допуске
        WriteOutputFile(outDir, "RR.Свежее.mp4.md", now.Subtract(TimeSpan.FromMinutes(10)).Add(TimeSpan.FromSeconds(35)));
        if (markerBody.Length > 0)
            Write(root, Path.Combine("out", TranscriptionStateReader.OutputHostName), markerBody);

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker"); // «не проверялось» по часовой сверке — не «не та машина»
        s.Level.Should().Be("ok");
    }

    // Инвариант круга 2 (вердикт Р-Б2, major): seq упорядочивает строки только ВНУТРИ своего
    // файла; между папками он не сравним — счётчики независимы. «Последний писавший» судится
    // по каждой папке отдельно, тревога — только если во всех читаемых папках он чужой.

    // Проба ревьюера (A): высокий seq чужого узла в его папке не перечёркивает нашу. A давно
    // не получает встреч («ai» дошёл до seq=5), в B наш узел пишет прямо сейчас с seq=3 —
    // в B последний писавший наш, вывод ведётся отсюда, тревоги нет (и не «висело» бы,
    // пока в A снова не начнут писать). Мутация «общий максимум seq по всем папкам» —
    // реализация Б2 на 32637d0 — краснит этот тест.
    [Fact]
    public void ВысокийSeqВЧужойПапкеНеПеречёркиваетСвою_ТревогиНет()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outA = Path.Combine(root, "out", "A");
        var outB = Path.Combine(root, "out", "B");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(
            ("Video-A", watch, outA),
            ("Video-B", watch, outB)));
        WriteOutputHostMarker(outA, ("ai", now.Subtract(TimeSpan.FromDays(2)).ToUnixTimeSeconds(), 5));
        WriteOutputHostMarker(outB, ("worker", now.Subtract(TimeSpan.FromMinutes(3)).ToUnixTimeSeconds(), 3));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker"); // в B последним писал наш — вывод идёт здесь
        s.Level.Should().Be("ok");
    }

    // Проба ревьюера (B), обратный случай: во всех читаемых папках последний писавший чужой —
    // тревога настоящая, даже когда счётчики папок далеко не равны.
    [Fact]
    public void ВоВсехПапкахПоследнийПисавшийЧужой_ПризнакНеТаМашина()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outA = Path.Combine(root, "out", "A");
        var outB = Path.Combine(root, "out", "B");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(
            ("Video-A", watch, outA),
            ("Video-B", watch, outB)));
        WriteOutputHostMarker(outA, ("ai", now.Subtract(TimeSpan.FromDays(2)).ToUnixTimeSeconds(), 5));
        WriteOutputHostMarker(outB, ("ai", now.Subtract(TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(), 7));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().BeNull(); // «не та машина»: в каждой папке последним писал «ai»
        s.Level.Should().Be("warn");
    }

    // Строки разных папок не влияют друг на друга: в A порядок задаёт её собственный счётчик
    // (наша строка seq=2 старше чужой seq=1, хоть чужая и «новее по часам»), и высокий seq=9
    // из B его не перечёркивает; в B последним писал чужой — одной папки с нашим писавшим
    // достаточно, чтобы тревоги нет.
    [Fact]
    public void СтрокиРазныхПапокНеВлияютДругНаДруга_СудимПоКаждомуФайлу()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outA = Path.Combine(root, "out", "A");
        var outB = Path.Combine(root, "out", "B");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(
            ("Video-A", watch, outA),
            ("Video-B", watch, outB)));
        WriteOutputHostMarker(outA,
            ("ai", now.Subtract(TimeSpan.FromMinutes(10)).ToUnixTimeSeconds(), 1),
            ("worker", now.Subtract(TimeSpan.FromHours(3)).ToUnixTimeSeconds(), 2));
        WriteOutputHostMarker(outB, ("ai", now.Subtract(TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(), 9));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker");
        s.Level.Should().Be("ok");
    }

    // Если маркер есть только в одной папке — судим по ней: чужой узел в единственном
    // читаемом маркере — настоящая тревога, «не читаемых» папок в оценке нет.
    [Fact]
    public void МаркерТолькоВОднойПапке_СудимПоНей()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outA = Path.Combine(root, "out", "A"); // маркера нет — этой папки нет в оценке
        var outB = Path.Combine(root, "out", "B");
        Directory.CreateDirectory(watch);
        Directory.CreateDirectory(outA);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(
            ("Video-A", watch, outA),
            ("Video-B", watch, outB)));
        WriteOutputHostMarker(outB, ("ai", now.Subtract(TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(), 2));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().BeNull();
        s.Level.Should().Be("warn");
    }

    // Имя в маркере пишет конвейер: TRANSCRIPTION_NODE или hostname (lib_journal.node_name) —
    // он может быть в другом регистре («Worker»), ключ реестра — строчный («worker»).
    // Сравнение регистронезависимое: иначе настоящий свой узел дал бы вечное «не та машина»
    // без диагностики. Мутация «Ordinal вместо OrdinalIgnoreCase» краснит этот тест.
    [Fact]
    public void ИмяУзлаВМаркереВДругомРегистре_ЭтоСвойУзелТревогиНет()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        WriteOutputHostMarker(outDir,
            ("Worker", now.Subtract(TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(), 4),
            ("ai", now.Subtract(TimeSpan.FromHours(1)).ToUnixTimeSeconds(), 3));

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker");
        s.Level.Should().Be("ok");
    }

    // Разбор строки сверен с живым lib_journal.mark_output_host (2026.11): файл = заголовок +
    // строки «узел\tunix\tвремя\tseq» на узел; конвейер хранит и строки без seq (len≥2, это
    // история чужих узлов, не вердикт). Ни битая строка, ни строка без seq, ни пустое имя, ни
    // лишним табом отмеченная — не «чужой узел»: их вердикта нет, остаётся сверка по журналу.
    // Фикстура без журнала → «не проверялось» (OutputHost=свой, уровня нет) во всех вариантах.
    [Theory]
    [InlineData("worker\t1794997217\t2026-11-18 12:00:17")] // строка без seq: конвейер её хранит, вердикта нет
    [InlineData("1794997217\t2026-11-18 12:00:17")] // нет имени и seq
    [InlineData("\t1794997217\t2026-11-18 12:00:17\t9")] // пустое имя — не узел
    [InlineData("worker\t1794997217\t2026-11-18 12:00:17\tabc")] // seq не число
    [InlineData("worker\t1794997217\t2026-11-18 12:00:17\t4\t")] // хвостовой таб: для конвейера строка целая (её seq считается) — и для читателя
    public void БитыеСтрокиМаркераНеСудятсяКакЧужойУзел(string markerBody)
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        Write(root, Path.Combine("out", TranscriptionStateReader.OutputHostName), markerBody);

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputHost.Should().Be("worker"); // ни одна битая строка не «чужой узел»
        s.Level.Should().Be("ok");
    }

    // Локальный узел неизвестен (в реестре только agent-узлы, local-ного нет): сравнивать
    // маркер не с кем. Пара (OutputHost=null + известный OutputDir) — ровно фронтовый признак
    // «не та машина», а назвать «свой» узел нечем: пару не отдаём — строка «Пишет в»
    // не проверялась. Мутация «убрать ветку localNode=null» краснит этот тест.
    [Fact]
    public void ЛокальныйУзелНеИзвестен_МаркерНеСудитсяЛожнойТревогиНет()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var watch = Path.Combine(root, "watch");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-RR", watch, outDir)));
        WriteOutputHostMarker(outDir, ("ai", now.Subtract(TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(), 4));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HardwareNodes:0:Key"] = "remote",
            ["HardwareNodes:0:Source"] = "http://192.0.2.10:8080",
        }).Build();
        var reader = new TranscriptionStateReader(
            new Opts(new TranscriptionOptions { PipelineRoot = root }), config, new FixedTime(now), _ => true);

        var s = reader.Read();

        s.Host.Should().BeNull(); // local-узла в реестре нет — имени «своего» узла нет
        s.OutputHost.Should().BeNull();
        s.OutputDir.Should().BeNull(); // пару «не та машина» не отдаём: строка не проверялась
        s.Level.Should().NotBe("warn"); // и ложной тревоги нет
    }

    // dify[] и checkedAt — из файла самого конвейера (dify_state.json, М2): времена unix-СЕКУНД →
    // unix-мс; inDify null (конвейер не получил список Dify) и local null (ошибка сверки папки по
    // R-М2) отдаются как есть, не 0; поле error папки в ответ не выходит (в dify-блоке контракта
    // комментария нет).
    [Fact]
    public void СверкаИзDifyStateJsonРядыCheckedAtINullInDify()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "watch"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", Path.Combine(root, "watch"), Path.Combine(root, "out"))));
        Write(root, "logs/dify_state.json", """
            { "checked": 1794997258, "folders": [
                { "folder": "Video-GPB", "local": 897, "inDify": 897, "checked": 1794997252 },
                { "folder": "Video-RR", "local": 12, "inDify": null, "error": "dify: список не получен" },
                { "folder": "Video-УТ", "local": null, "inDify": null, "error": "dify: таймаут" }
            ], "updated": "2026-11-18 09:00:58", "pid": 777 }
            """);

        var s = Reader(root, new FixedTime(now)).Read();

        s.Dify.Should().HaveCount(3);
        s.Dify[0].Folder.Should().Be("Video-GPB");
        s.Dify[0].Local.Should().Be(897);
        s.Dify[0].InDify.Should().Be(897);
        s.Dify[1].InDify.Should().BeNull(); // отсутствия половины Dify не прячем
        s.Dify[2].Local.Should().BeNull(); // local null (ошибка сверки папки, R-М2) — как есть, не 0
        s.Dify[2].InDify.Should().BeNull();
        s.CheckedAt.Should().Be(1_794_997_258_000); // unix-секунды файла → unix-мс
        // всё сходится, конвейер жив и свеж, вывод и watch на узле — тревоги нет
        s.Level.Should().Be("ok");
    }

    // Жёлтая точка по proposal.md: расхождение с Dify (local > inDify). Конвейер жив, свеж,
    // вывод и watch на узле — единственный повод тревоги — дефицит.
    [Fact]
    public void РасхождениеСDifyТочкаЖёлтая()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "watch"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", Path.Combine(root, "watch"), Path.Combine(root, "out"))));
        Write(root, "logs/dify_state.json", """{ "checked": 1794997258, "folders": [ { "folder": "Video-GPB", "local": 5, "inDify": 3 } ] }""");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Level.Should().Be("warn");
    }

    // Битый dify_state.json — пустой блок и нет метки, остальной снимок живой (устойчивость).
    [Fact]
    public void БитыйDifyStateJsonПустойБлокНоСнимокЖивой()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "logs/dify_state.json", "{ «checked»: «не число»");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Dify.Should().BeEmpty();
        s.CheckedAt.Should().BeNull();
        s.Phase.Should().Be("idle");
    }

    // ─── зависимости ────────────────────────────────────────────────────────────────

    [Fact]
    public void ФазыЖдётЗависимость_СбойAsrИSummaryТочкаКрасная()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "watch"));
        Write(root, "logs/state.json", StateJson("ждёт машину с GPU", now, 100, TimeSpan.FromSeconds(10), queue: 1));
        Write(root, "logs/summary_state.json", StateJson("ждёт модель", now, 100, TimeSpan.FromSeconds(10), pending: 1));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", Path.Combine(root, "watch"), Path.Combine(root, "out"))));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Phase.Should().Be("batch"); // ждёт зависимость: процесс жив и занят, такой фазы во фронте нет
        s.Deps.First(d => d.Key == "asr").State.Should().Be("down");
        s.Deps.First(d => d.Key == "summary").State.Should().Be("down");
        s.Level.Should().Be("danger");
    }

    [Fact]
    public void DifyОтложеноВЖурнале_ЗависимостьDifyDown()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "watch"));
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", Path.Combine(root, "watch"), Path.Combine(root, "out"))));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromMinutes(30), "ok", "Video-GPB", "A.mp4", "1.0", "2", "asr 1.0м + саммари 0.1м (локально)"),
            JournalRowStr(now, TimeSpan.FromMinutes(10), "ok", "Video-GPB", "B.mp4", "1.0", "2", "asr 0.9м + саммари 0.2м (локально), dify отложено")) + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        // самая свежая удачная загрузка — с «dify отложено»
        s.Deps.First(d => d.Key == "dify").State.Should().Be("down");
        s.Level.Should().Be("danger");
    }

    [Fact]
    public void DifyПринялЗагрузку_ЗависимостьDifyOk()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "watch"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", Path.Combine(root, "watch"), Path.Combine(root, "out"))));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromMinutes(10), "ok", "Video-GPB", "B.mp4", "1.0", "2", "asr 1.0м + саммари 0.1м (локально)")) + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Deps.First(d => d.Key == "dify").State.Should().Be("ok");
        s.Level.Should().Be("ok");
    }

    [Fact]
    public void СетевойПапкиНетНаУзле_ЗависимостьShareDown()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(
            ("Video-GPB", Path.Combine(root, "watch"), Path.Combine(root, "out")),
            ("Video-RR", Path.Combine(root, "watch-нет"), Path.Combine(root, "out"))));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Deps.First(d => d.Key == "share").State.Should().Be("down");
        s.Level.Should().Be("danger");
    }

    // ─── конфиг ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void КонфигБерётсяТолькоСекцияFoldersПрочиеКлючиИгнорируются()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        // после folders — другие секции с вложенными ключами: не должны породить фантомных папок;
        // watch/output — заведомо несуществующие пути (на worker /mnt/video реальный SMB-монтирование)
        Write(root, "config.yaml", """
            folders:
              - name: Video-GPB # комментарий на строке
                watch: /nonexistent-ccs-test/Video/Video-GPB # SMB
                output: /nonexistent-ccs-test/Встречи/ГПБ # локальный вывод

            whisper:
              model: large-v3-turbo
              batch_size: 8
            dify:
              sync_interval_minutes: 30
            """);

        var s = Reader(root, new FixedTime(now)).Read();

        s.OutputDir.Should().Be("/nonexistent-ccs-test/Встречи/ГПБ");
        s.Dify.Should().BeEmpty(); // dify_state.json нет — пустой блок, конфиг его не заменяет
        // watch нет на узле теста → share down
        s.Deps.First(d => d.Key == "share").State.Should().Be("down");
    }
}
