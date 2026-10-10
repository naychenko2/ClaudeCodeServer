using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.TestRuns;

// Прогресс `npm run <скрипт>` по этапам цепочки (docs/research/build-stand-progress-2026-10.md,
// раздел 2). Живого счётчика у vite без TTY нет, зато этапы видны точно: число этапов известно ДО
// старта — это разбор строки скрипта из package.json по `&&`, а смену этапа выдают маркеры вывода:
//  • `npm run X` (и pnpm/yarn) — эхо npm `> пакет@версия X` перед вложенным скриптом;
//  • `vite build` (и `npx vite build`) — строка `vite vN… building`;
//  • прочие команды (tsc, node -e …) маркера не имеют: этап засчитывается, когда маркер дал
//    следующий за ним.
// Внутри этапа vite — под-этапы (transforming → rendering chunks → computing gzip size) для подписи.
// Процент — по весам прошлого успешного прогона той же цели и скрипта (доля прошлого времени, к
// которой был готов каждый этап), между маркерами полоса ползёт по времени, но не дальше конца
// текущего этапа в прошлый раз; без памяти — равными долями. Потолок 99, Exact всегда false.
// Не потокобезопасен: синхронизация — у вызывающего (строки идут из двух потоков сразу).
public sealed partial class NpmBuildProgress
{
    // Потолок числа этапов: строка скрипта из дерева агента не раздует подпись и память
    public const int MaxStages = 50;

    // Потолок длины подписи этапа — команда целиком бывает длинной
    private const int MaxStageLabel = 60;

    // Потолок длины этапа, который сверяется с маркерами вывода: строку скрипта пишет агент (до
    // мегабайта), а настоящая команда этапа — десятки символов. Этап длиннее маркером не узнаётся
    // (засчитается, когда маркер даст следующий за ним), и ни одна регулярка его не видит
    public const int MaxStageLength = 1024;

    private readonly IReadOnlyList<string> _stages;
    // Маркеры этапов разобраны ОДИН раз при создании: имя вложенного скрипта и признак vite build.
    // Строка вывода дальше сравнивается с готовыми значениями, а не гоняет регулярки по этапам
    private readonly string?[] _nested;
    private readonly bool[] _vite;
    private readonly BuildRunMemory? _previous;
    // Секунда, к которой готов каждый пройденный этап (индекс → секунда)
    private readonly Dictionary<int, double> _finished = [];
    private string? _subStage;

    public NpmBuildProgress(IReadOnlyList<string> stages, BuildRunMemory? previous)
    {
        _stages = stages.Count > 0 ? stages : [""];
        _nested = [.. _stages.Select(NestedScript)];
        _vite = [.. _stages.Select(IsViteBuild)];
        // Память другой формы (скрипт поменяли) — веса не про эту цепочку
        _previous = previous is { Seconds: > 0 } p && p.Total == _stages.Count ? p : null;
    }

    // Индекс текущего этапа (с нуля)
    public int Current { get; private set; }

    public int Total => _stages.Count;

    // Готовых этапов: все до текущего
    public int Done => Current;

    public bool FromMemory => _previous is not null;

    // --- Разбор строки скрипта ---

    // Этапы скрипта: разрез по `&&` вне кавычек, пустые — выброшены, не больше MaxStages
    // (хвост склеивается в последний этап). Пустой скрипт — один этап с пустой подписью
    public static IReadOnlyList<string> SplitStages(string script)
    {
        var stages = new List<string>();
        var start = 0;
        char? quote = null;
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (quote is { } q)
            {
                if (c == q) quote = null;
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '&' && i + 1 < script.Length && script[i + 1] == '&')
            {
                stages.Add(script[start..i]);
                start = i + 2;
                i++;
            }
        }
        stages.Add(script[start..]);
        var trimmed = stages.Select(s => Collapse(s)).Where(s => s.Length > 0).ToList();
        if (trimmed.Count > MaxStages)
            trimmed = [.. trimmed.Take(MaxStages - 1), string.Join(" && ", trimmed.Skip(MaxStages - 1))];
        return trimmed;
    }

    private static string Collapse(string command) => Whitespace().Replace(command, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Имя вложенного скрипта, если этап — `npm run X` / `npm run-script X` / `pnpm [run] X` /
    // `yarn [run] X` (с флагами до имени); иначе null (и для этапа длиннее MaxStageLength)
    internal static string? NestedScript(string stage)
    {
        if (stage.Length > MaxStageLength) return null;
        try { return NestedScriptCommand().Match(stage) is { Success: true } m ? m.Groups["name"].Value : null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    // Флаг до имени — `-x…` или `--x…`, второй символ флага не «-»: так у флага ровно один разбор.
    // Прежнее `-{1,2}\S+` читало `--a` и как `-`+`-a`, и как `--`+`a` — 2^k вариантов на этапе из
    // k флагов без имени (ReDoS, ревью этапа 3)
    [GeneratedRegex(@"^(?:npm\s+(?:run|run-script)|pnpm(?:\s+run)?|yarn(?:\s+run)?)(?:\s+--?[^\s-]\S*)*\s+(?<name>[^\s-]\S*)",
        RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex NestedScriptCommand();

    // Этап запускает `vite … build`: слово vite, где-то после него слово build. Разбор по словам —
    // линейный, без регулярки (прежний ленивый шаблон был квадратичным по длине этапа)
    internal static bool IsViteBuild(string stage)
    {
        if (stage.Length > MaxStageLength) return false;
        var words = stage.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var vite = Array.IndexOf(words, "vite");
        return vite >= 0 && Array.IndexOf(words, "build", vite + 1) > vite;
    }

    // --- Разбор вывода ---

    // Эхо npm перед скриптом: `> пакет@версия имя` (у пакета без имени — путь вместо пакета).
    // Без `@` или разделителя пути это вторая строка эха — сама команда (`> vite build`).
    // Части до первого `@` и до первого разделителя — без него самого: один разбор на строку
    [GeneratedRegex(@"^> (?:[^\s@]*@\S+|[^\s/\\]*[/\\]\S*) (?<name>\S+)$", RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex NpmEcho();

    // Старт vite: `vite v8.0.16 building client environment for production...`
    [GeneratedRegex(@"^vite v\d\S* building", RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex ViteStart();

    // ANSI-последовательности (CSI и OSC): rolldown пишет их даже с NO_COLOR
    public static string StripAnsi(string line) => TestRunSummaryFormatter.StripAnsi(line);

    // Разобранная строка вывода: имя скрипта из эха npm, старт vite, под-этап vite
    public readonly record struct OutputLine(string? Echo, bool ViteStart, string? SubStage);

    // Разбор строки (уже без ANSI) — чистая функция без состояния: вызывающий зовёт её ВНЕ своего
    // замка, под замком остаётся только Apply. Длинная строка и сработавший потолок
    // сопоставления — строка не распознана
    public static OutputLine Parse(string line)
    {
        if (line.Length > VsTestConsoleParser.MaxOutcomeLineLength) return default;
        var trimmed = line.Trim();
        var sub = SubStageOf(trimmed);
        try
        {
            var echo = NpmEcho().Match(trimmed) is { Success: true } m ? m.Groups["name"].Value : null;
            return new OutputLine(echo, echo is null && ViteStart().IsMatch(trimmed), sub);
        }
        catch (RegexMatchTimeoutException)
        {
            return new OutputLine(null, false, sub);
        }
    }

    // Строка вывода (уже без ANSI); true — сменился этап или под-этап
    public bool Feed(string line, TimeSpan elapsed) => Apply(Parse(line), elapsed);

    // Итог разбора строки — в состояние; только сравнения с готовыми маркерами этапов
    public bool Apply(OutputLine line, TimeSpan elapsed)
    {
        if (Advance(line, elapsed)) return true;
        if (line.SubStage is { } sub && sub != _subStage)
        {
            _subStage = sub;
            return true;
        }
        return false;
    }

    // Первый ПОСЛЕДУЮЩИЙ этап, чей маркер узнан в строке: этапы без маркера между ними — пройдены
    private bool Advance(OutputLine line, TimeSpan elapsed)
    {
        if (line.Echo is null && !line.ViteStart) return false;
        for (var next = Current + 1; next < _stages.Count; next++)
        {
            var matches = line.Echo is not null
                ? _nested[next] == line.Echo
                : _vite[next];
            if (!matches) continue;
            for (var i = Current; i < next; i++) _finished[i] = elapsed.TotalSeconds;
            Current = next;
            _subStage = null;
            return true;
        }
        return false;
    }

    // Под-этап vite по его строкам; built — этап vite закончил работу
    private static string? SubStageOf(string line) => line switch
    {
        _ when line.StartsWith("transforming", StringComparison.Ordinal) => "трансформация модулей",
        _ when line.StartsWith("rendering chunks", StringComparison.Ordinal) => "сборка чанков",
        _ when line.StartsWith("computing gzip size", StringComparison.Ordinal) => "подсчёт gzip",
        _ when line.StartsWith("✓ built in", StringComparison.Ordinal) => "собрано",
        _ => null,
    };

    public int Percent(TimeSpan elapsed)
    {
        if (_previous is not { } previous) return Math.Min(99, Current * 100 / Total);
        var done = Current == 0 ? 0 : Fraction(previous, Current - 1);
        var next = Math.Max(Fraction(previous, Current), done);
        var byTime = Math.Min(elapsed.TotalSeconds / previous.Seconds, next);
        return Math.Clamp((int)(Math.Max(done, byTime) * 100), 0, 99);
    }

    // Доля прошлого времени, к которой был готов этап index; нет записи — конец
    private static double Fraction(BuildRunMemory previous, int index) =>
        previous.Finished.TryGetValue(Key(index), out var seconds)
            ? Math.Clamp(seconds / previous.Seconds, 0, 1)
            : 1.0;

    private static string Key(int index) => (index + 1).ToString(CultureInfo.InvariantCulture);

    // Подпись команды этапа: длинная — обрезана
    public string StageLabel(int index)
    {
        var stage = _stages[index];
        return stage.Length <= MaxStageLabel ? stage : stage[..(MaxStageLabel - 1)] + "…";
    }

    // «vite build · этап 2 из 6 · сборка чанков»
    public string Label()
    {
        var name = StageLabel(Current);
        var head = name.Length == 0 ? "" : name + " · ";
        var tail = _subStage is null ? "" : " · " + _subStage;
        return $"{head}этап {Current + 1} из {Total}{tail}";
    }

    public TestRunProgress Snapshot(TimeSpan elapsed) => new("build", Label(), Percent(elapsed), Exact: false);

    // Память этого прогона для следующего: только по успешной сборке (вызывающий). Последний
    // этап (и пропущенные маркерами в конце) готов к концу прогона
    public BuildRunMemory ToMemory(TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var finished = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < Total; i++)
            finished[Key(i)] = _finished.TryGetValue(i, out var at) ? at : seconds;
        return new BuildRunMemory(Total, seconds, finished);
    }
}
