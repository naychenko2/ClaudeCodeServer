using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

// Читатель состояния конвейера транскрибации для вкладки «Встречи» (Б1,
// GET /api/local-model/transcription). Читает только файлы самого конвейера под
// Transcription:PipelineRoot на этом узле и только в момент запроса: фоновых опросов,
// хранилища и обращений в другие вертикали нет.
//
// Файлы (формат задаёт конвейер, lib_journal.py / config.yaml):
//   logs/state.json         — «чем занят конвейер сейчас»: phase (русскими), updated "yyyy-MM-dd HH:mm:ss"
//                           (местное время узла), pid, queue (число), queue_items (файлы очереди
//                           {file, folder, since}, since — unix-СЕКУНД → unix-мс); на этапе — current/folder,
//                           на старте пачки — batch_total.
//   logs/summary_state.json — снимок потока сводок: phase, pending, current, folder, updated, pid.
//   logs/dify_state.json    — результат сверки с Dify (после М2): { checked: unix-сек, folders:
//                           [{folder, local, inDify|null, checked, error?}] }.
//   logs/journal.tsv        — события: время\tитог\tпапка\tфайл\tминут\tспикеров\tпримечание;
//                           итог = ok|asr|fail|batch|start|stop. Читается ХВОСТ: файл только растёт.
//   config.yaml             — folders[].name/watch/output — папки конвейера и каталоги их вывода.
//
// Устойчивость (требование задачи): файла нет / не читается / битый JSON — не исключение и не 500:
// пустые блоки, фаза «stopped», уровня нет (null — «не проверялось», не «unknown»: значения
// «unknown» во фронтовом типе level нет). Времена файлов — местное время узла
// (конвейер пишет time.strftime без смещения), сравниваются с TimeProvider.GetUtcNow().
public interface ITranscriptionStateReader
{
    // Снимок состояния конвейера на момент вызова. Не бросает исключений.
    TranscriptionSnapshot Read();
}

public sealed class TranscriptionStateReader(
    IOptionsMonitor<TranscriptionOptions> options,
    IConfiguration configuration,
    TimeProvider? time = null,
    Func<int, bool>? processAlive = null) : ITranscriptionStateReader
{
    private readonly IOptionsMonitor<TranscriptionOptions> _options = options;
    private readonly IConfiguration _configuration = configuration;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // Живость процесса по pid на этом узле: /proc/<pid> (конвейер и CCS после М1 — на одном узле).
    // Тесты подставляют свой щуп (на Windows /proc нет — там конвейера нет в принципе).
    private readonly Func<int, bool> _processAlive = processAlive ?? DefaultProcessAlive;

    // Умолчания v1 (docs/mockups/local-model-transcription-proposal.md, принято Андреем 18.11):
    // порог «Завис?», окно «сбоев/встреч» [now−24ч, now] (half-open) и потолок строк каждого блока.
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(24);
    internal const int RowLimit = 20;
    // Хвост journal.tsv: не больше этих байт с конца файла (день событий — единицы КБ).
    internal const int JournalTailBytes = 128 * 1024;

    private static bool DefaultProcessAlive(int pid) => pid > 0 && Directory.Exists("/proc/" + pid);

    public TranscriptionSnapshot Read()
    {
        var now = _time.GetUtcNow();
        var at = now.ToUnixTimeMilliseconds();
        var root = _options.CurrentValue.PipelineRoot;

        var state = ReadState(Path.Combine(root, "logs", "state.json"));
        var summary = ReadState(Path.Combine(root, "logs", "summary_state.json"));
        var folders = ReadConfigFolders(Path.Combine(root, "config.yaml"));
        var journal = ReadJournal(Path.Combine(root, "logs", "journal.tsv"), now);
        var (difyRows, difyCheckedAt) = ReadDifyState(Path.Combine(root, "logs", "dify_state.json"));

        // Снимок сводок принадлежит живому потоку, только когда это тот же процесс
        // (правило status.py: старый снимок прошлого процесса или последовательного режима
        // показывать нельзя).
        var summaryValid = summary is not null && state?.Pid is { } mainPid && summary.Pid == mainPid;

        var localNode = LocalNodeKey(_configuration);
        var alive = state?.Pid is { } pid && _processAlive(pid);

        // «Завис?» и «Остановлен» (внутренний уровень плашки, в ответ не выходит — в плоском
        // контракте его нет): состояния нет — «unknown»; процесса нет (или он сам написал
        // «остановлен») — «stopped»; снимок старше порога при живом pid — «stale»; иначе — «ok».
        var pipelineLevel =
            state?.UpdatedAt is null ? "unknown" :
            state.Phase == "остановлен" || !alive ? "stopped" :
            now - state.UpdatedAt > StaleAfter ? "stale" :
            "ok";

        // «Пишет в»: общий каталог выходов папок против узла CCS. Каталога на узле нет —
        // конвейер пишет не здесь («не та машина», warn). Конфига нет — «не проверялось».
        var outputDir = CommonOutputDir(folders);
        string? outputHost = null;
        var outputLevel = "unknown";
        if (outputDir is not null)
        {
            if (Directory.Exists(outputDir))
            {
                outputHost = localNode;
                outputLevel = localNode is not null ? "ok" : "unknown";
            }
            else outputLevel = "warn";
        }

        TranscriptionFailDto[] fails =
            [.. journal.Where(r => r.Result == "fail").Take(RowLimit)
                .Select(r => new TranscriptionFailDto(
                    r.At.ToUnixTimeMilliseconds(), r.Folder, r.File, r.Minutes, r.Note))];

        var outputs = OutputByFolder(folders);
        TranscriptionMeetingDto[] recent =
            [.. journal.Where(r => r.Result == "ok" && r.File is not null).Take(RowLimit)
                .Select(r => new TranscriptionMeetingDto(
                    r.At.ToUnixTimeMilliseconds(), r.Folder, r.File!, r.Minutes, r.Speakers, r.Note,
                    MdPath(r, outputs)))];

        // Свежесть сигналов конвейера та же, что у его плашки: stale/stopped/unknown — зависимости
        // не проверялись («не проверялось»).
        var deps = BuildDeps(state, summary, summaryValid, pipelineLevel, journal, folders, at);

        // Точка вкладки (proposal.md): красная при «Завис?», «Остановлен» или сбое зависимости;
        // жёлтая при расхождении с Dify или «Пишет в» не туда. «Не проверялось» — уровня нет
        // (null): фронтовый тип level знает только ok/warn/danger, «unknown» в нем нет.
        var level =
            pipelineLevel is "stale" or "stopped" ? "danger" :
            deps.Any(d => d.State == "down") ? "danger" :
            outputLevel == "warn" ? "warn" :
            // сверка — из dify_state.json конвейера; дефицит (local > inDify) — жёлтая точка
            difyRows.Any(d => d.Local is { } l && d.InDify is { } n && l > n) ? "warn" :
            pipelineLevel == "unknown" ? null :
            "ok";

        var nowInfo = BuildNow(state, summary, summaryValid, now);

        // Плоский ответ по proposal.md: поля блоков на верхнем уровне, вложенных
        // pipeline/now/queue нет (форма — фронтовый тип TranscriptionSnapshot).
        return new TranscriptionSnapshot
        {
            Phase = MapPhase(state?.Phase, alive),
            UpdatedAt = state?.UpdatedAt?.ToUnixTimeMilliseconds(),
            Host = localNode,
            Pid = state?.Pid,
            Alive = alive,
            OutputHost = outputHost,
            OutputDir = outputDir,
            Level = level,
            File = nowInfo?.File,
            Folder = nowInfo?.Folder,
            Stage = nowInfo?.Stage,
            StageStartedAt = nowInfo?.StageStartedAt,
            DurationMin = nowInfo?.DurationMin,
            BatchIndex = nowInfo?.BatchIndex,
            BatchSize = nowInfo?.BatchSize,
            // asrQueue — из queue_items самого конвейера (М2); их нет — пустой список.
            AsrQueue = state?.QueueItems ?? [],
            SummaryQueue = summaryValid ? summary!.Pending ?? 0 : 0,
            Fails = fails,
            Recent = recent,
            Dify = difyRows,
            CheckedAt = difyCheckedAt,
            Deps = deps,
        };
    }

    // ─── state.json / summary_state.json ────────────────────────────────────────────

    // Снимок состояния одним чтением файла. Нет файла / не читается / битый JSON / чужая форма —
    // null: дальше работает путь «unknown», не исключение.
    private static StateSnapshot? ReadState(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is null) return null;
            return new StateSnapshot(
                node["phase"]?.GetValue<string>(),
                TryParseStamp(node["updated"]?.GetValue<string>(), "yyyy-MM-dd HH:mm:ss", out var updated) ? updated : null,
                AsInt(node["pid"]), AsInt(node["pending"]),
                node["current"]?.GetValue<string>(), node["folder"]?.GetValue<string>(),
                AsInt(node["batch_total"]),
                ReadQueueItems(node));
        }
        catch (Exception) { return null; }
    }

    private sealed record StateSnapshot(
        string? Phase, DateTimeOffset? UpdatedAt, int? Pid, int? Pending,
        string? Current, string? Folder, int? BatchTotal,
        TranscriptionQueueFileDto[] QueueItems);

    // queue_items (М2): файлы, ждущие распознавания, {file, folder, since} — since в unix-СЕКУНДАХ
    // конвейера, в контракте unix-мс. Строки без имени пропускаются; с нечисловым since остаются
    // без времени (файл ждёт — неизвестна только дата). Битый массив — пустой список, но не потеря снимка.
    private static TranscriptionQueueFileDto[] ReadQueueItems(JsonNode node)
    {
        try
        {
            if (node["queue_items"] is not JsonArray arr) return [];
            var items = new List<TranscriptionQueueFileDto>();
            foreach (var n in arr)
            {
                try
                {
                    if (n?["file"]?.GetValue<string>() is { Length: > 0 } file)
                        items.Add(new TranscriptionQueueFileDto(file, n["folder"]?.GetValue<string>(), ToUnixMs(n["since"])));
                }
                catch (Exception) { }
            }
            return [.. items];
        }
        catch (Exception) { return []; }
    }

    private static int? AsInt(JsonNode? node)
    {
        try { return node?.GetValue<int>(); } catch (Exception) { return null; }
    }

    // unix-секунды файлов конвейера → unix-миллисекунды контракта.
    private static long? ToUnixMs(JsonNode? node)
    {
        try { return node?.GetValue<long>() * 1000; } catch (Exception) { return null; }
    }

    // Нормализация русских фаз конвейера (supervisor.py) во фронтовое множество
    // "idle"|"batch"|"stopped". Факт «остановлен» в state.json сильнее живой pid (процесс пишет
    // этот снимок в finally); мёртвый pid — "stopped" при любой фазе; живый процесс при фазе,
    // не описанной контрактом (ждёт зависимость, неизвестная строка) — занят, "batch".
    private static string MapPhase(string? raw, bool alive) => raw switch
    {
        "остановлен" => "stopped",
        _ when !alive => "stopped",
        "ожидание" => "idle",
        _ => "batch",
    };

    // Внутренняя форма блока «Сейчас»: в ответе его поля — на верхнем уровне снимка, отдельного
    // объекта «now» в контракте нет.
    private sealed record NowInfo(
        string File, string? Folder, string Stage, long? StageStartedAt, double? DurationMin, int? BatchIndex, int? BatchSize);

    // Блок «Сейчас»: основной поток (распознавание; в последовательном режиме и сводка — тот же
    // state.json), при простое основного — параллельный поток сводок (тот же процесс).
    // Этап не опознан — null: в плоском контракте «Сейчас» просто не отдаётся.
    private static NowInfo? BuildNow(StateSnapshot? state, StateSnapshot? summary, bool summaryValid, DateTimeOffset now)
    {
        if (state?.Current is { Length: > 0 } mainFile)
        {
            var stage = state.Phase switch { "транскрибация" => "asr", "саммари" => "summary", _ => null };
            if (stage is not null)
                return new NowInfo(mainFile, state.Folder, stage,
                    state.UpdatedAt?.ToUnixTimeMilliseconds(),
                    state.UpdatedAt is { } st ? Math.Round((now - st).TotalMinutes, 1) : null,
                    null, state.BatchTotal);
        }

        if (summaryValid && summary!.Phase == "саммари" && summary.Current is { Length: > 0 } sumFile)
            return new NowInfo(sumFile, summary.Folder, "summary",
                summary.UpdatedAt?.ToUnixTimeMilliseconds(),
                summary.UpdatedAt is { } st ? Math.Round((now - st).TotalMinutes, 1) : null,
                null, null);

        return null;
    }

    // ─── config.yaml ────────────────────────────────────────────────────────────────

    private sealed class ConfigFolder
    {
        public string Name = "";
        public string? Watch;
        public string? Output;
    }

    // Минимальный парсер секции folders: только name/watch/output. Прочий YAML (якоря, кавычки,
    // вложенные структуры) не разбирается — форма файла наша и фиксированная; сбой чтения —
    // пустой список, дальше блоки работают как «не проверялось».
    private static List<ConfigFolder> ReadConfigFolders(string path)
    {
        var result = new List<ConfigFolder>();
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception) { return result; }

        var inFolders = false;
        ConfigFolder? current = null;
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (!char.IsWhiteSpace(raw[0]))
            {
                // верхний уровень: «folders:» открывает секцию, любой другой ключ — закрывает
                inFolders = trimmed == "folders:";
                current = null;
                continue;
            }
            if (!inFolders) continue;

            if (trimmed.StartsWith("- "))
            {
                current = new ConfigFolder();
                result.Add(current);
                var (key, value) = SplitKeyValue(trimmed[2..]);
                if (key == "name") current.Name = value ?? "";
                continue;
            }
            if (current is null) continue;

            var (k, v) = SplitKeyValue(trimmed);
            switch (k)
            {
                case "watch": current.Watch = v; break;
                case "output": current.Output = v; break;
            }
        }
        return result;
    }

    // «ключ: значение» с хвостовым комментарием; без двоеточия или с пустым значением — (null, null).
    private static (string? Key, string? Value) SplitKeyValue(string text)
    {
        var i = text.IndexOf(':');
        if (i < 0) return (null, null);
        var key = text[..i].Trim();
        var value = text[(i + 1)..];
        var c = value.IndexOf(" #", StringComparison.Ordinal);
        if (c >= 0) value = value[..c];
        value = value.Trim();
        return key.Length == 0 || value.Length == 0 ? (key.Length == 0 ? null : key, null) : (key, value);
    }

    // Общий каталог выходов папок («Пишет в» — одна строка): наибольший общий префикс путей по
    // сегментам. Общего нет (папки пишут в разные места) — null.
    private static string? CommonOutputDir(List<ConfigFolder> folders)
    {
        var paths = folders.Where(f => f.Output is not null).Select(f => f.Output!).ToList();
        if (paths.Count == 0) return null;
        var parts = paths.Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var common = new List<string>();
        for (var i = 0; i < parts[0].Length; i++)
        {
            var seg = parts[0][i];
            if (!parts.All(p => p.Length > i && p[i] == seg)) break;
            common.Add(seg);
        }
        return common.Count == 0 ? null : "/" + string.Join("/", common);
    }

    private static Dictionary<string, string> OutputByFolder(List<ConfigFolder> folders)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in folders)
            if (f.Name.Length > 0 && f.Output is not null)
                map[f.Name] = f.Output;
        return map;
    }

    // ─── dify_state.json (результат сверки конвейера с Dify, М2) ─────────────────────────

    // Строки сверки по папкам и метка проверки из файла самого конвейера: времена — unix-СЕКУНД →
    // unix-мс. Нет файла / не читается / битый JSON — пустой блок, не исключение. inDify null
    // (конвейер не получил список Dify) отдаётся как есть; поле error папки в ответ не выходит —
    // в dify-блоке контракта комментария нет, контракт не расширяем.
    private static (TranscriptionDifyDto[] Rows, long? CheckedAt) ReadDifyState(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is null) return ([], null);

            var rows = new List<TranscriptionDifyDto>();
            if (node["folders"] is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    try
                    {
                        if (n?["folder"]?.GetValue<string>() is { Length: > 0 } folder)
                            rows.Add(new TranscriptionDifyDto(folder, AsInt(n["local"]), AsInt(n["inDify"])));
                    }
                    catch (Exception) { }
                }
            }
            return ([.. rows], ToUnixMs(node["checked"]));
        }
        catch (Exception) { return ([], null); }
    }

    // Имя .md по строке журнала: старое форматирование писало «….mp4.md», новое пишет «….mp4»;
    // нормализация одинакова для обоих: basename без расширения + ".md".
    private static string? MdPath(JournalRow r, Dictionary<string, string> outputs)
    {
        if (r.Folder is null || r.File is null || !outputs.TryGetValue(r.Folder, out var dir)) return null;
        return dir.TrimEnd('/') + "/" + Path.GetFileNameWithoutExtension(r.File) + ".md";
    }

    // ─── journal.tsv (хвост) ────────────────────────────────────────────────────────

    private sealed record JournalRow(
        DateTimeOffset At, string Result, string? Folder, string? File,
        double? Minutes, int? Speakers, string Note);

    // Хвост журнала, свежие строки первыми, только окно [now−24ч, now). Строки вне окна и битые
    // строки отбрасываются; за начало хвоста отвечает JournalTailBytes.
    private static List<JournalRow> ReadJournal(string path, DateTimeOffset now)
    {
        var rows = new List<JournalRow>();
        string[] lines;
        try { lines = ReadTailLines(path, JournalTailBytes); }
        catch (Exception) { return rows; }

        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue; // заголовок и комментарии
            var parts = line.Split('\t');
            if (parts.Length < 7) continue;
            if (!TryParseStamp(parts[0], "yyyy-MM-dd HH:mm", out var at)) continue;
            if (at < now - HistoryWindow) continue;
            rows.Add(new JournalRow(at, parts[1].Trim(),
                DashOrNull(parts[2]), DashOrNull(parts[3]), AsDouble(parts[4]), AsIntOrNull(parts[5]),
                parts[6].Trim()));
        }
        rows.Sort((a, b) => b.At.CompareTo(a.At));
        return rows;
    }

    // Последние строки файла: читается не больше maxBytes с конца; осколок строки на границе
    // чтения отбрасывается (файл только растёт — весь его читать незачем).
    private static string[] ReadTailLines(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = fs.Length;
        var take = (int)Math.Min(len, maxBytes);
        fs.Seek(len - take, SeekOrigin.Begin);
        var buf = new byte[take];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        // начало не с нуля файла — первая строка осколок предыдущей
        if (len > take && lines.Length > 0) lines = lines[1..];
        return [.. lines.Select(l => l.TrimEnd('\r'))];
    }

    private static string? DashOrNull(string s) => s.Trim() == "-" ? null : s.Trim();

    private static double? AsDouble(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? AsIntOrNull(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    // Метки времени конвейера — местное время узла без смещения (time.strftime); DateTimeStyles.None
    // даёт им местное смещение этой машины — тот же узел, что и писавший.
    private static bool TryParseStamp(string? s, string format, out DateTimeOffset value)
    {
        value = default;
        return s is not null &&
            DateTimeOffset.TryParseExact(s.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    // ─── зависимости ────────────────────────────────────────────────────────────────

    // Состояние зависимостей — по тому, что конвейер сам пишет о них: фазы «ждёт …» — сбой машины
    // распознавания, «ждёт модель» — сбой модели сводок, «dify отложено» в журнале — Dify не принял,
    // «dify=ok» — принял. «Медленно» (slow) из этих файлов не измеряется. Свежесть — как у плашки:
    // stale/stopped/unknown → всё «не проверялось».
    private static TranscriptionDepDto[] BuildDeps(
        StateSnapshot? state, StateSnapshot? summary, bool summaryValid, string pipelineLevel,
        List<JournalRow> journal, List<ConfigFolder> folders, long at)
    {
        if (pipelineLevel is not "ok")
            return [.. TranscriptionSnapshot.UnknownDeps(at)];

        // pipelineLevel == "ok" возможен только при живом state с читаемым временем.
        var asr = state!.Phase switch
        {
            "ждёт машину с GPU" or "ждёт простоя ComfyUI" or "ждёт память GPU" => "down",
            "транскрибация" => "ok",
            _ => "unknown",
        };

        var summaryStatus = summaryValid ? summary!.Phase switch
        {
            "ждёт модель" => "down",
            "саммари" => "ok",
            _ => "unknown",
        } : "unknown";

        // Самый свежий dify-маркер журнала в окне (rows уже по убыванию времени).
        var difyMarker = journal.FirstOrDefault(r => r.Note.Contains("dify", StringComparison.OrdinalIgnoreCase));
        var dify = difyMarker is null ? "unknown" :
            difyMarker.Note.Contains("dify отложено", StringComparison.OrdinalIgnoreCase) ? "down" : "ok";

        var watched = folders.Where(f => f.Watch is { Length: > 0 }).ToList();
        var share = watched.Count == 0 ? "unknown" :
            watched.All(f => Directory.Exists(f.Watch)) ? "ok" : "down";

        return
        [
            new TranscriptionDepDto("asr", asr, at),
            new TranscriptionDepDto("summary", summaryStatus, at),
            new TranscriptionDepDto("dify", dify, at),
            new TranscriptionDepDto("share", share, at),
        ];
    }

    // Узел, на котором работает CCS: local-узел реестра HardwareNodes (тот же источник, что у
    // HardwareSampler/HardwareReportService); без секции EffectiveNodes даёт ровно один «worker».
    private static string? LocalNodeKey(IConfiguration configuration) =>
        HardwareNodesOptions.FromConfiguration(configuration).EffectiveNodes()
            .FirstOrDefault(n => n.Source == "local")?.Key;
}
