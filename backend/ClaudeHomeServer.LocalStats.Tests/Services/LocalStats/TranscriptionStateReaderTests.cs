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

    private static TranscriptionStateReader Reader(string root, TimeProvider time, Func<int, bool>? alive = null) =>
        new(new Opts(new TranscriptionOptions { PipelineRoot = root }),
            new ConfigurationBuilder().Build(), time, alive ?? (_ => true));

    // Метка state.json: «yyyy-MM-dd HH:mm:ss» местного времени узла — строим от фиксированных часов.
    private static string Stamp(DateTimeOffset now, TimeSpan ago) => now.Subtract(ago).ToString("yyyy-MM-dd HH:mm:ss");

    private static string StampMin(DateTimeOffset now, TimeSpan ago) => now.Subtract(ago).ToString("yyyy-MM-dd HH:mm");

    private static string StateJson(string phase, DateTimeOffset now, int pid, TimeSpan age,
        int? queue = null, int? pending = null, string? current = null, string? folder = null, int? batchTotal = null)
    {
        var f = new List<string> { $"\"phase\": \"{phase}\"", $"\"updated\": \"{Stamp(now, age)}\"", $"\"pid\": {pid}" };
        if (queue is not null) f.Add($"\"queue\": {queue}");
        if (pending is not null) f.Add($"\"pending\": {pending}");
        if (current is not null) f.Add($"\"current\": \"{current}\"");
        if (folder is not null) f.Add($"\"folder\": \"{folder}\"");
        if (batchTotal is not null) f.Add($"\"batch_total\": {batchTotal}");
        return "{ " + string.Join(", ", f) + " }";
    }

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

        s.Pipeline.Phase.Should().Be("idle");
        s.Pipeline.PhaseText.Should().Be("ожидание");
        s.Pipeline.Level.Should().Be("ok");
        s.Pipeline.Alive.Should().BeTrue();
        s.Pipeline.Host.Should().Be("worker"); // реестра нет → фолбэк ровно один «worker»
        s.Pipeline.OutputHost.Should().Be("worker");
        s.Pipeline.OutputDir.Should().Be(output);
        s.Pipeline.OutputLevel.Should().Be("ok");
        s.Level.Should().Be("ok");
        // очередь: счётчики из state.json/summary_state.json; списка файлов конвейер не пишет
        s.Queue.AsrCount.Should().Be(2);
        s.Queue.SummaryCount.Should().Be(1);
        s.Queue.AsrQueue.Should().BeEmpty();
        // в простое зависимости не проверялись, кроме сетевой папки (есть на узле)
        s.Deps.Select(d => (d.Key, d.Status)).Should().Equal(
            ("asr", "unknown"), ("summary", "unknown"), ("dify", "unknown"), ("share", "ok"));
        s.Now.Should().BeNull();
        s.Fails.Should().BeEmpty();
        s.Recent.Should().BeEmpty();
    }

    // Граница порога строгая («старше 5 мин»): ровно 5 мин — ещё нет, 5 мин + 1 с — да.
    [Theory]
    [InlineData(0, 4, 50, "ok")]
    [InlineData(0, 5, 0, "ok")]
    [InlineData(0, 5, 1, "stale")]
    public void Завис_ГраницаПорога5МинПриЖивомPid(int h, int m, int sec, string expected)
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, new TimeSpan(0, h, m, sec),
            current: "X.mp4", folder: "Video-RR"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Pipeline.Level.Should().Be(expected);
        s.Level.Should().Be(expected == "stale" ? "danger" : "ok");
    }

    [Fact]
    public void МёртвыйPid_ОстановленДажеПриСвежемСнимке()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromSeconds(10),
            current: "X.mp4", folder: "Video-RR"));

        var s = Reader(root, new FixedTime(now), pid => false).Read();

        s.Pipeline.Alive.Should().BeFalse();
        s.Pipeline.Level.Should().Be("stopped");
        s.Level.Should().Be("danger");
        //stopped/stale — сигналы не проверялись
        s.Deps.Should().OnlyContain(d => d.Status == "unknown");
    }

    [Fact]
    public void ФазаОстановлен_ОстановленДажеПриЖивомPid()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("остановлен", now, 100, TimeSpan.FromSeconds(10)));

        var s = Reader(root, new FixedTime(now)).Read();

        // сам факт «остановлен» в state.json сильнее живой pid: процесс пишет этот снимок
        // в finally и доходит до выхода — уровень «stopped» независимо от живости
        s.Pipeline.Phase.Should().Be("stopped");
        s.Pipeline.Level.Should().Be("stopped");
        s.Level.Should().Be("danger");
    }

    [Fact]
    public void НетФайлов_СнимокUnknownБезИсключения()
    {
        var now = NowTrunc();
        var root = NewRoot();

        var s = Reader(root, new FixedTime(now)).Read();

        s.Level.Should().Be("unknown");
        s.Pipeline.Phase.Should().Be("unknown");
        s.Pipeline.Level.Should().Be("unknown");
        s.Pipeline.UpdatedAt.Should().BeNull();
        s.Queue.AsrCount.Should().Be(0);
        s.Fails.Should().BeEmpty();
        s.Recent.Should().BeEmpty();
        s.Dify.Should().BeEmpty();
        s.Deps.Should().OnlyContain(d => d.Status == "unknown");
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

        s.Pipeline.Level.Should().Be("unknown");
        s.Level.Should().Be("unknown");
    }

    [Fact]
    public void НеизвестнаяФаза_ПропускаетсяКакРаботаССыройТекстом()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("проверка архива", now, 100, TimeSpan.FromSeconds(10)));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Pipeline.Phase.Should().Be("working");
        s.Pipeline.PhaseText.Should().Be("проверка архива");
        s.Pipeline.Level.Should().Be("ok");
    }

    // ─── «Сейчас» ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Транскрибация_ЭтапAsrВДлительностьОтНачалаЭтапа()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("транскрибация", now, 100, TimeSpan.FromMinutes(3),
            queue: 1, current: "X.mp4", folder: "Video-RR"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Now!.File.Should().Be("X.mp4");
        s.Now.Folder.Should().Be("Video-RR");
        s.Now.Stage.Should().Be("asr");
        s.Now.DurationMin.Should().Be(3.0);
        s.Now.StageStartedAt.Should().Be(now.Subtract(TimeSpan.FromMinutes(3)).ToUnixTimeMilliseconds());
        // место в пачке и размер пачки state.json на файле не несёт
        s.Now.BatchIndex.Should().BeNull();
        s.Now.BatchSize.Should().BeNull();
        // распознавание идёт — зависимость asr «в норме»
        s.Deps.First(d => d.Key == "asr").Status.Should().Be("ok");
    }

    [Fact]
    public void ПараллельнаяСводкаПриПростоеОсновногоПотока_ЭтапSummary()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(20), queue: 0));
        Write(root, "logs/summary_state.json", StateJson("саммари", now, 100, TimeSpan.FromMinutes(1),
            current: "Y.mp4", folder: "Video-GPB"));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Now!.Stage.Should().Be("summary");
        s.Now.File.Should().Be("Y.mp4");
        s.Deps.First(d => d.Key == "summary").Status.Should().Be("ok");
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

        s.Now.Should().BeNull();
        s.Queue.SummaryCount.Should().Be(0);
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

        s.Now!.Stage.Should().Be("asr");
        s.Now.File.Should().Be("X.mp4");
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
            JournalRowStr(now, TimeSpan.FromMinutes(20), "ok", "Video-RR", "RR.Старое.mp4.md", "1.1", "2", "dify=ok"),
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

        s.Pipeline.OutputDir.Should().Be("/nonexistent-ccs-test/Встречи"); // общий префикс по сегментам
        s.Pipeline.OutputHost.Should().BeNull();
        s.Pipeline.OutputLevel.Should().Be("warn");
        s.Level.Should().Be("warn");
        // локальная половина сверки: каталога нет → «не проверялось» (null), не «ноль файлов»
        s.Dify.Should().HaveCount(2);
        s.Dify.Should().OnlyContain(d => d.Local == null && d.InDify == null);
    }

    [Fact]
    public void СверкаЛокальнаяПоловинаПоФактуЧислоMdВВыходе()
    {
        var now = NowTrunc();
        var root = NewRoot();
        var outGpb = Path.Combine(root, "out", "ГПБ");
        Directory.CreateDirectory(outGpb);
        File.WriteAllText(Path.Combine(outGpb, "a.md"), "x");
        File.WriteAllText(Path.Combine(outGpb, "b.md"), "x");
        File.WriteAllText(Path.Combine(outGpb, "c.txt"), "x");
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", Path.Combine(root, "watch"), outGpb)));

        var s = Reader(root, new FixedTime(now)).Read();

        s.Dify.Should().ContainSingle(d => d.Folder == "Video-GPB" && d.Local == 2 && d.InDify == null);
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

        s.Pipeline.Phase.Should().Be("waiting");
        s.Deps.First(d => d.Key == "asr").Status.Should().Be("down");
        s.Deps.First(d => d.Key == "summary").Status.Should().Be("down");
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
            JournalRowStr(now, TimeSpan.FromMinutes(30), "ok", "Video-GPB", "A.mp4", "1.0", "2", "dify=ok"),
            JournalRowStr(now, TimeSpan.FromMinutes(10), "ok", "Video-GPB", "B.mp4", "1.0", "2", "asr 0.9м + саммари 0.2м (локально), dify отложено")) + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        // самый свежий маркер — «dify отложено»
        s.Deps.First(d => d.Key == "dify").Status.Should().Be("down");
        s.Level.Should().Be("danger");
    }

    [Fact]
    public void DifyПринялМаркерOk_ЗависимостьDifyOk()
    {
        var now = NowTrunc();
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "watch"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        Write(root, "logs/state.json", StateJson("ожидание", now, 100, TimeSpan.FromSeconds(10)));
        Write(root, "config.yaml", ConfigYaml(("Video-GPB", Path.Combine(root, "watch"), Path.Combine(root, "out"))));
        Write(root, "logs/journal.tsv", string.Join('\n',
            "# время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание",
            JournalRowStr(now, TimeSpan.FromMinutes(10), "ok", "Video-GPB", "B.mp4", "1.0", "2", "dify=ok")) + "\n");

        var s = Reader(root, new FixedTime(now)).Read();

        s.Deps.First(d => d.Key == "dify").Status.Should().Be("ok");
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

        s.Deps.First(d => d.Key == "share").Status.Should().Be("down");
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

        s.Dify.Should().ContainSingle(d => d.Folder == "Video-GPB");
        s.Pipeline.OutputDir.Should().Be("/nonexistent-ccs-test/Встречи/ГПБ");
        // watch нет на узле теста → share down
        s.Deps.First(d => d.Key == "share").Status.Should().Be("down");
    }
}
