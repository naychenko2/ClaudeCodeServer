using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// keeper/settings.json — настройки вики владельца (ADR-035, раздел «Ночной разбор и его настройки»).
// Поля этого шага (S4c, Ф1): общий выключатель «Применять без ревью» (умолчание — включён) и
// выключенные классы `origin × op` — их читает политика применения (WikiApplyPolicy.ApplyContext),
// классы же остаются единицей автовыключения (мера 7). Прочие параметры ночного разбора (окно,
// лимиты, пороги отбора) добавятся в этот же файл следующими шагами; V — признак формата файла.
//
// settings.json — не файл под pages/ и не файл раздела: его не держит блокировка раздела. Его
// держат per-owner замок и атомарная запись (temp+fsync→Move) — тот же приём, что у записи
// применения (WikiApplyService.WriteBytesAtomic), и per-owner замок — тот же приём, что у
// инициализации Хранителя. Пишет файл только код вертикали: ручка настроек
// (WikiSettingsController) и оценка здоровья классов (WikiClassHealth). Правка руками на диске
// подхватывается при следующем чтении; невалидный файл — умолчания плюс сигнал (Invalid в
// результате), а не исключение наружу (ADR-035:325): ни ручка настроек, ни фоновый читатель не
// должны падать на испорченном файле.
public static class WikiSettingsStore
{
    public const int ModelVersion = 1;
    private const string SettingsRelPath = "keeper/settings.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Класс несёт «×», причина по-русски: файл читает человек при разборе отказа.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Состояние класса в политике применения. DisabledAt и Reason — что выключило
    /// класс и почему (человек видит это в блоке «Применение» и в уведомлении). ResumedAt —
    /// момент возврата («Снова применять само»): с него начинается окно отсчёта класса заново
    /// (мера 7), старые откаты в новое окно не входят.</summary>
    public sealed record ClassState(
        string Class,
        bool Disabled,
        string? Reason,
        DateTimeOffset? DisabledAt,
        DateTimeOffset? ResumedAt);

    public sealed record Settings(int V, bool AutoApplyEnabled, IReadOnlyList<ClassState> Classes)
    {
        public bool IsClassDisabled(string classKey) =>
            Classes.Any(c => c.Class == classKey && c.Disabled);

        /// <summary>Начало окна отсчёта класса: последняя «Снова применять само». null — окно
        /// не сбрасывали, отсчёт идёт с начала журнала.</summary>
        public DateTimeOffset? WindowStart(string classKey) =>
            Classes.Where(c => c.Class == classKey).Select(c => c.ResumedAt).Max();

        /// <summary>Множество выключенных классов — ровно то, что ждёт поле ApplyContext
        /// .DisabledClasses политики: формат ключа тот же (WikiPackage.ClassKey).</summary>
        public IReadOnlySet<string> DisabledClasses =>
            Classes.Where(c => c.Disabled).Select(c => c.Class).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Умолчания: «Применять без ревью» включён, выключенных классов нет (мера 7:
    /// «умолчание — включён»).</summary>
    public static Settings Defaults() => new(ModelVersion, true, []);

    public readonly record struct ReadResult(Settings Settings, bool Invalid);

    /// <summary>Прочитать настройки. Файла нет — умолчания (вики до первой правки настрое —
    /// норма). Файл невалиден (битый JSON, чужой V, испорченная запись класса) — умолчания и
    /// Invalid=true: вызывающий ставит сигнал (_health.md), исключения наружу нет. Целый файл
    /// не «чинится» частично: половина прочитанного файла притворилась бы половиной настрое.</summary>
    public static ReadResult Read(string root)
    {
        var full = SafePath.Join(root, SettingsRelPath);
        if (!File.Exists(full))
            return new(Defaults(), false);

        string text;
        try
        {
            text = File.ReadAllText(full, Encoding.UTF8);
        }
        catch (IOException) { return new(Defaults(), true); }
        catch (UnauthorizedAccessException) { return new(Defaults(), true); }

        try
        {
            var parsed = JsonSerializer.Deserialize<Settings>(text, JsonOpts);
            if (parsed is null || parsed.V != ModelVersion || parsed.Classes is null)
                return new(Defaults(), true);
            foreach (var c in parsed.Classes)
                if (c is null || !IsValidClassKey(c.Class))
                    return new(Defaults(), true);
            return new(parsed, false);
        }
        catch (JsonException)
        {
            return new(Defaults(), true);
        }
    }

    // Замок на владельца: настройки — файл владельца, не раздела. Держит read-modify-write
    // целиком: одновременные PUT и оценка здоровья не затирают друг друга молча.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerLocks = new();

    /// <summary>Изменить настройки под per-owner замком: чтение текущего состояния → функция →
    /// атомарная запись. Функция получает состояние на момент записи, а не чтение, сделанное
    /// вне замка раньше: вызывающий, решавший по устаревшему состоянию, не затрёт чужую
    /// правку. Возвращает записанное состояние.</summary>
    public static Settings Mutate(string dataDir, string ownerId, Func<Settings, Settings> mutate)
    {
        var sem = _ownerLocks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1));
        sem.Wait();
        try
        {
            var root = WikiPaths.Root(dataDir, ownerId);
            var current = Read(root).Settings;
            var next = mutate(current) with { V = ModelVersion };
            WriteAtomic(root, next);
            return next;
        }
        finally
        {
            sem.Release();
        }
    }

    // Та же атомарная запись, что у прообразов: половина файла настрое притворилась бы
    // половиной настрое.
    private static void WriteAtomic(string root, Settings settings)
    {
        var full = SafePath.Join(root, SettingsRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOpts);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Класс правки — составление, тем же и проверяемое, чем оно ставится
    /// (WikiPackage.ClassKey): «origin×op», origin из закрытого перечня (WikiGate.OriginValues),
    /// op из перечня операций (WikiPackage.OpValues). Кривой ключ в настройках невозможен:
    /// политике о нём не вынести решения, и автовыключение не назовёт его в уведомлении.</summary>
    internal static bool IsValidClassKey(string? classKey)
    {
        if (classKey is null) return false;
        var parts = classKey.Split('×');
        return parts.Length == 2
            && WikiGate.OriginValues.Contains(parts[0])
            && WikiPackage.OpValues.Contains(parts[1]);
    }
}
