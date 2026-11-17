using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using JournalShape = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>>;

namespace ClaudeHomeServer.Services.ChildMode;

// Журнал присутствия ребёнка по пространствам (разрез §3, KM-B5b):
// data/child-usage/{ownerId}.json — минуты присутствия по дням и пространствам.
// Время в пространстве считается присутствием, а не длительностью ходов: фронт шлёт
// heartbeat раз в 60 с, пока вкладка видима (KM-F2), минуты копит этот стор.
//
// Форма файла — словарь: день → пространство → минуты дня (по возрастанию):
//   { "2026-11-17": { "lessons": [540, 541] } }
// день — "yyyy-MM-dd" МЕСТНОЙ даты момента в таймзоне, которую передаёт вызывающий
// (таймзону выбирает вызывающий: у пользователя она в профиле, у стора своей нет);
// минута — целая минута дня 0..1439, округление вниз: секунды отбрасываются.
// Ключ записи — (день, пространство, минута): повторный heartbeat той же минуты ничего
// не добавляет, поэтому «минут сегодня» нельзя получить простым счётчиком вызовов.
//
// Хранятся последние RetentionDays дней, срезка — при записи. Отсчёт от самой свежей даты
// журнала, а не от «сейчас»: поздняя запись старой минуты (повторный heartbeat, догон
// пропущенного вызова) не должна срезать свежие дни того же файла.
//
// Запись атомарна и весь read-modify-write одного файла держится локом по пути файла, а не
// по экземпляру стора (тот же приём, что у ChatUserStateStore.cs:74-90 и JsonFileStore.cs:342):
// две вкладки одного ребёнка пишут в один файл, и дубль той же минуты обязан быть виден
// второй вкладке. Битый файл не стирается молча: цельный уходит в .corrupt-*.bak, частично
// валидный поднимается поэлементно (JsonFileStore) — журнал продолжается пустым, исключение
// наружу не выходит: потеря одной минуты присутствия не роняет запрос пользователя.
// Каталог лежит в data/ и едет в архив сам (строки-исключения для него в BackupPaths нет);
// это новое хранилище, а не изменение существующего формата — BackupSchema.Version не трогается.
public sealed class ChildUsageStore(string root, ILogger<ChildUsageStore>? log = null)
{
    public const string DirName = "child-usage";

    // Сколько дней журнала остаётся; старше — срезаются при записи.
    public const int RetentionDays = 60;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ChildUsageStore>? _log = log;

    // Корень — из существующего резолвера путей данных (тот же приём, что у ChatUserStateStore):
    // каталог data — директория DataPath, а не хардкод пути.
    public static string RootFromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(Path.GetDirectoryName(dataPath)!, DirName);
    }

    /// <summary>
    /// Засчитать минуту присутствия `at` (округление вниз до минуты) в пространстве `space`.
    /// Возвращает true, если минута засчитана впервые, false — если такая минута уже в журнале.
    /// </summary>
    public bool RecordMinute(string ownerId, string space, DateTimeOffset at, TimeZoneInfo tz)
    {
        RequireSpace(space);
        var (day, minute) = LocalMinute(at, RequireTimeZone(tz));
        var path = FileFor(ownerId);

        lock (LockFor(path))
        {
            var journal = ReadAll(path) ?? [];
            if (!TryAddMinute(journal, day, space, minute)) return false;   // эта минута уже учтена
            Prune(journal);
            JsonFileStore.Save(path, journal, Json);
            return true;
        }
    }

    /// <summary>
    /// Сколько минут сегодня (по местному дню `now` в таймзоне вызывающего) числится за
    /// пространством `space`. Нет файла, нет дня, нет пространства — 0. Чтение ничего не пишет.
    /// </summary>
    public int MinutesToday(string ownerId, string space, DateTimeOffset now, TimeZoneInfo tz)
    {
        RequireSpace(space);
        var (day, _) = LocalMinute(now, RequireTimeZone(tz));
        // Лока нет: вся правка держится под локом по пути файла в RecordMinute, а сам перенос
        // файла атомарен (tmp + File.Move, JsonFileStore.cs:309-332) — наполовину записанного
        // файла читателю не встречается.
        var journal = ReadAll(FileFor(ownerId));
        if (journal is null || !journal.TryGetValue(day, out var spaces)) return 0;
        return spaces.TryGetValue(space, out var minutes) ? minutes.Count : 0;
    }

    // Момент → (местный день, целая минута дня) в таймзоне вызывающего.
    private static (string Day, int Minute) LocalMinute(DateTimeOffset at, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(at, tz);
        return (
            local.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            local.Hour * 60 + local.Minute);   // секунды отбрасываются — это и есть «вниз до минуты»
    }

    // Добавить минуту в журнал. Список минут дня держится отсортированным: дубль видно
    // двоичным поиском, а файл остаётся сравнимым (одинаковый вход → одинаковый JSON).
    private static bool TryAddMinute(JournalShape journal, string day, string space, int minute)
    {
        if (!journal.TryGetValue(day, out var spaces))
        {
            spaces = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            journal[day] = spaces;
        }
        if (!spaces.TryGetValue(space, out var minutes))
        {
            minutes = [];
            spaces[space] = minutes;
        }

        var pos = minutes.BinarySearch(minute);
        if (pos >= 0) return false;
        minutes.Insert(~pos, minute);
        return true;
    }

    // Срезка старых дней: остаются RetentionDays дней, считая от самой свежей даты журнала.
    // Срезка идёт при КАЖДОЙ записи, а не только когда дней накопилось больше RetentionDays:
    // журнал из трёх дат, разнесённых на 61 день, — уже вне окна хранения, и старый день в
    // нём лишний, хоть он и один.
    private void Prune(JournalShape journal)
    {
        var days = new List<(string Key, DateOnly Day)>();
        foreach (var key in journal.Keys.ToArray())
        {
            // Ключ, который не является датой (следы правки файла руками или частично
            // поднятого битого журнала), днём не считается и срезается.
            if (TryParseDay(key, out var day)) days.Add((key, day));
            else journal.Remove(key);
        }
        if (days.Count == 0) return;

        var newest = days.Max(x => x.Day);
        var cutoff = newest.AddDays(-(RetentionDays - 1));

        var stale = 0;
        foreach (var (key, day) in days)
        {
            if (day >= cutoff) continue;
            journal.Remove(key);
            stale++;
        }
        if (stale == 0) return;

        // Срезка — штатный ход журнала (пишем каждый heartbeat), не аномалия: Warning здесь
        // шумел бы раз в сутки.
        _log?.LogInformation("ChildUsageStore: срезано дней {Count} (окно хранения {Retention} дней от {Newest})",
            stale, RetentionDays, newest);
    }

    private const string DayFormat = "yyyy-MM-dd";

    // Разбор имени дня. Форма одна, какую пишет сам стор; на мусорном ключе (не-дата) —
    // false, без исключения наружу: срезка не должна ронять запись минуты.
    private static bool TryParseDay(string key, out DateOnly day)
    {
        day = default;
        try
        {
            day = DateOnly.ParseExact(key, DayFormat, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RequireSpace(string space)
    {
        // Пространство — устойчивый идентификатор ("lessons", далее "studio"): он ключ записи в
        // файле, поэтому ограничивается идентификатором, а не произвольным текстом.
        if (string.IsNullOrWhiteSpace(space) || space.Length > 64 || !space.All(IsSpaceChar))
            throw new ArgumentException("Недопустимое пространство", nameof(space));
    }

    private static bool IsSpaceChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static TimeZoneInfo RequireTimeZone(TimeZoneInfo? tz) =>
        tz ?? throw new ArgumentNullException(nameof(tz), "Таймзону передаёт вызывающий: день журнала — местный");

    private string FileFor(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.IndexOfAny(['/', '\\']) >= 0 || ownerId.Contains(".."))
            throw new ArgumentException("Недопустимый идентификатор пользователя", nameof(ownerId));
        return SafePath.Join(root, ownerId + ".json");
    }

    private JournalShape? ReadAll(string path) => JsonFileStore.Load<JournalShape>(path, Json, _log);

    // Лок на путь файла, общий для всех экземпляров стора этого процесса: файл один на
    // пользователя, а RecordMinute — read-modify-write всего файла.
    private static readonly ConcurrentDictionary<string, Lock> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static Lock LockFor(string path) =>
        PathLocks.GetOrAdd(Path.GetFullPath(path), _ => new Lock());
}
