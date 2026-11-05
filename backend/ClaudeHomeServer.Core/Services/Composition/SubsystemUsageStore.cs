using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Composition;

// Стор счётчика использования подсистем (шаг S1a): по каждой подсистеме — время последнего
// обращения и число обращений за последние 24 часа («когда к подсистеме обращались в последний
// раз и как часто это было вчера»). Ничего не вещает: это наблюдение для диагностики,
// а не источник правды для поведения подсистем.
//
// Память ограничена по построению: ключи — только из набора, переданного в конструктор
// (чужой ключ молча игнорируется: счётчик не валидатор вызывающего). На ключ — ровно
// 24 часовых счётчика по кругу, без записей на каждое обращение: верхняя граница —
// 24 int + 1 время на ключ, файл < 16 КБ при 50 ключах (проверено тестом объёма).
//
// Время — параметром `now`, `DateTime.UtcNow` внутри стора нет: модельное время (тест
// на 7 суток, TTL при Load) детерминировано.
//
// Окно — по часовым корзинам. Слот времени t = ⌊UTC(t)/1ч⌋: DateTimeOffset нормализуется к UTC
// до слотования (UtcTicks), поэтому один миг в любом представлении — один слот; локальные Ticks
// дали бы разные слоты одному мигу в +00:00 и +03:00 и съезжающее на целые часы окно (R-S1a).
// Last24h(now) = сумма слотов
// [⌊now/1ч⌋−23 … ⌊now/1ч⌋] — ровно 24 слота, заканчивающихся текущим. Обращение ровно
// за 24 часа до now попадает в 25-й слот и из окна выпадает; ровно за 23 ч 59 мин —
// остаётся (границы окна закрыты тестом RingWindow_HitsAtExactBounds).
//
// Инвариант кольца: голова кольца = час(LastUsedAt). LastUsedAt хранится как максимум
// времён обращений, поэтому при загрузке из файла голова восстанавливается из единственного
// сохранённого времени и перенос корзины не нужен.
public sealed class SubsystemUsageStore
{
    // Часовых корзин на ключ: ровно сутки. Больше — уже не «за 24 часа».
    public const int HourBuckets = 24;

    // TTL загружаемого ключа: ключ без обращений старше — не интересен, выбрасывается.
    private static readonly TimeSpan IdleTtl = TimeSpan.FromDays(30);

    // Версия формата файла. Сломать формат = поднять версию (и сверить с бэкапом).
    private const int FileFormatVersion = 1;

    // Web-дефолты (camelCase + регистронезависимое чтение) — как у остального JSON проекта:
    // файл стора читает и человек, и тот же JsonSerializer; компактный — ради бюджета < 16 КБ.
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    // Один SemaphoreSlim на путь файла: два одновременных Save одного файла сериализуются,
    // разные пути не блокируют друг друга. Реестр статический: путь — свой у каждого
    // экземпляра стора, а два стора на один путь (диагностика + прод) должны писать
    // в файл по очереди, иначе File.Move двух temp-файлов даст победителя наугад.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.Ordinal);

    private readonly HashSet<string> _allowedKeys;

    // Источник правды — память; файл — снимок. Ключ появляется здесь только по первому
    // принятому Hit (или при Load), поэтому словарь не разрастается чужими ключами.
    private readonly ConcurrentDictionary<string, KeyState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    // Набор наблюдаемых ключей (IAppSubsystem.Key). Пустой/пробельный ключ в наборе —
    // ошибка конфигурации (тот же контракт, что у SubsystemRegistration.AddSubsystems);
    // дубли ключей в наборе — не ошибка: HashSet схлопывает их регистронезависимо.
    public SubsystemUsageStore(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException(
                    "Пустой или null ключ подсистемы в наборе — счётчик по такому ключу невозможен.",
                    nameof(keys));
            _allowedKeys.Add(key);
        }
    }

    // Отметить обращение. Чужой (не из набора) или пустой ключ — молча игнор.
    // Лок — по ключу, не общий на стор: два потока на разных ключах не ждут друг друга,
    // два потока на одном ключе сериализуются и счёт не теряется (Hit_Parallel_NoLostCounts).
    public void Hit(string key, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        // TryGetValue вернёт каноническое написание из набора: Snapshot и файл держат
        // ключ так, как его объявил конструктор, а не так, как его написал вызывающий.
        if (!_allowedKeys.TryGetValue(key, out var canonical)) return;

        var state = _states.GetOrAdd(canonical, static _ => new KeyState());
        lock (state.Gate)
        {
            var hour = HourOf(now);

            if (state.LastUsedAt is null)
            {
                // Первый принятый Hit этого ключа: кольцо пустое, голова = этот час.
                state.LastUsedAt = now;
                state.Counts[SlotOf(hour)]++;
                return;
            }

            var headHour = HourOf(state.LastUsedAt.Value);

            if (hour > headHour)
            {
                // Поворот вперёд: корзины между старой и новой головой обнуляются —
                // это и есть TTL кольца («старше 24 ч» здесь = выпало за 24 слота).
                if (hour - headHour >= HourBuckets)
                    Array.Clear(state.Counts, 0, HourBuckets);
                else
                    for (var h = headHour + 1; h <= hour; h++)
                        state.Counts[SlotOf(h)] = 0;

                state.LastUsedAt = now; // час(now) > час(LastUsedAt) ⇒ now свежее по построению
            }
            else if (hour >= headHour - (HourBuckets - 1))
            {
                // Тот же час или час назад (в пределах кольца): счёт идёт в свою корзину,
                // голова не двигается. Возможна только при откате модельного времени —
                // LastUsedAt остаётся более свежим, инвариант «голова = час(LastUsedAt)» не ломается.
                state.Counts[SlotOf(hour)]++;
                if (now > state.LastUsedAt) state.LastUsedAt = now;
                return;
            }
            else
            {
                // Откат времени за пределы кольца: обращение не выразимо 24 корзинами
                // относительно последней активности — игнорируем (полный сброс перанкорил
                // бы кольцо на старый час и выбросил ВСЮ свежую статистику ради одного отсчёта).
                return;
            }

            state.Counts[SlotOf(hour)]++;
        }
    }

    // Снимок для диагностики: только ключи, у которых было хотя бы одно обращение
    // (полный список подсистем даёт SubsystemStateStore, здесь — наблюдаемые).
    // Порядок — по ключу, алфавит (OrdinalIgnoreCase, как ActiveKeys у SubsystemStateStore).
    public IReadOnlyList<SubsystemUsageInfo> Snapshot(DateTimeOffset now)
    {
        var nowHour = HourOf(now);
        var list = new List<SubsystemUsageInfo>();
        foreach (var (key, state) in _states)
        {
            lock (state.Gate)
            {
                if (state.LastUsedAt is null) continue; // KeyState создан Hit'ом в полёте
                var headHour = HourOf(state.LastUsedAt.Value);
                var total = 0;
                for (var slot = 0; slot < HourBuckets; slot++)
                {
                    // Слоту принадлежит самый свежий час ≤ headHour с этим номером по модулю колец.
                    var hour = headHour - Mod(headHour - slot, HourBuckets);
                    if (hour >= nowHour - (HourBuckets - 1) && hour <= nowHour)
                        total += state.Counts[slot];
                }
                list.Add(new SubsystemUsageInfo(key, state.LastUsedAt, total));
            }
        }
        return list.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Прочитать снимок из файла. Отказ по контракту: отсутствующий, пустой или битый файл —
    // пустой стор, исключение наружу не выходит (счётчик не должен ронять старт процесса).
    // now нужен TTL-отсечке: время внутри стора не читается.
    public void Load(string path, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        UsageFile? file;
        try
        {
            if (!File.Exists(path)) return;
            file = JsonSerializer.Deserialize<UsageFile>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception)
        {
            return; // пустой/битый/нечитаемый файл — пустой стор
        }

        if (file?.Entries is null) return;

        foreach (var entry in file.Entries)
        {
            if (entry?.Key is null || string.IsNullOrWhiteSpace(entry.Key)) continue;
            if (!_allowedKeys.TryGetValue(entry.Key, out var canonical)) continue; // чужой ключ
            if (now - entry.LastUsedAt > IdleTtl) continue; // TTL: старше 30 дней — выброс
            if (entry.Counts is null || entry.Counts.Length != HourBuckets) continue; // битая строка

            // Память — источник правды: то, что уже есть в памяти, файлом не перетирается.
            var state = _states.GetOrAdd(canonical, static _ => new KeyState());
            lock (state.Gate)
            {
                if (state.LastUsedAt is not null) continue;
                state.LastUsedAt = entry.LastUsedAt;
                Array.Copy(entry.Counts, state.Counts, HourBuckets);
            }
        }
    }

    // Записать снимок. Два одновременных Save одного пути сериализуются (FileGates);
    // temp-файл с уникальным именем + File.Move(overwrite) — как в SpendStore/VideoThreadStore:
    // убийство процесса посреди записи не оставляет обрезанный JSON.
    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Путь файла счётчика пуст — писать некуда.", nameof(path));

        var full = Path.GetFullPath(path);
        var gate = FileGates.GetOrAdd(full, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var entries = new List<UsageEntry>();
            foreach (var (key, state) in _states)
            {
                lock (state.Gate)
                {
                    if (state.LastUsedAt is null) continue;
                    entries.Add(new UsageEntry(key, state.LastUsedAt.Value, (int[])state.Counts.Clone()));
                }
            }
            entries.Sort(static (a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(temp, JsonSerializer.Serialize(
                new UsageFile(FileFormatVersion, entries), JsonOpts));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            gate.Release();
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp — не ценность */ }
        }
    }

    // Абсолютный номер часового слота по UTC. Ticks DateTimeOffset считает «часовое» время с
    // offset (локальные часы), UtcTicks — по UTC: без нормализации один миг в +00:00 и +03:00
    // дал бы разные слоты (R-S1a).
    private static long HourOf(DateTimeOffset t) => t.UtcTicks / TimeSpan.TicksPerHour;

    private static int SlotOf(long hour) => (int)Mod(hour, HourBuckets);

    private static long Mod(long x, int m) => ((x % m) + m) % m;

    // Состояние одного ключа: ровно 24 int + одно время. Голова кольца не хранится —
    // она равна час(LastUsedAt) (см. инвариант в шапке класса).
    private sealed class KeyState
    {
        public readonly object Gate = new();
        public readonly int[] Counts = new int[HourBuckets];
        public DateTimeOffset? LastUsedAt;
    }

    // Строка файла: ключ, время последнего обращения, 24 часовых счётника кольца.
    private sealed record UsageEntry(string Key, DateTimeOffset LastUsedAt, int[] Counts);

    private sealed record UsageFile(int Version, List<UsageEntry> Entries);
}

// DTO снимка: Key — канонический ключ подсистемы; LastUsedAt — время последнего принятого
// обращения (null для ключа без данных — в Snapshot таких нет, поле остаётся nullable
// по контракту шага); Last24h — число обращений за окно [now−23ч … now] по часовым корзинам.
public sealed record SubsystemUsageInfo(string Key, DateTimeOffset? LastUsedAt, int Last24h);
