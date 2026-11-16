using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Потолки среза узла (ADR-029, «Узлы фермы» → «Потолки»). Бэк проверяет их ВСЕГДА, агенту не доверяет.
// Значение длиннее потолка отбрасывается ЦЕЛИКОМ (как будто файла нет), а не обрезается:
// обрезанное число или строка mountinfo превратились бы в правдоподобную неправду.
public static class NodeSliceLimits
{
    // Тело ответа: читаем не больше 256 КБ + 1 байт; больше — срез отброшен, узел down.
    public const int MaxBodyBytes = 256 * 1024;
    // Карты: разбираем первые 16 строк gpuCsv, остальное отбрасываем.
    public const int MaxGpuRows = 16;
    // Чипы hwmon: не больше 64 различных каталогов hwmonN (общий счёт для files и links).
    public const int MaxHwmonDirs = 64;
    // Записи files / links: отбрасываем записи сверх 4096 (позиция в ответе).
    public const int MaxFiles = 4096;
    public const int MaxLinks = 4096;
    // statvfs: не больше 64 записей.
    public const int MaxStatvfs = 64;
    // Значения files (кроме mountinfo), строки gpuCsv, ссылки и mount-точки statvfs: 4 КБ.
    public const int MaxTextBytes = 4096;
    // Исключения из потолка строки: /proc/self/mountinfo после фильтра — 64 КБ, gddr6 — 16 КБ.
    public const int MaxMountinfoBytes = 64 * 1024;
    public const int MaxGddr6Bytes = 16 * 1024;
    // ipmi — тот же потолок, что у gddr6 (ADR-029, «Узел — набор устройств»).
    public const int MaxIpmiBytes = 16 * 1024;
    // Свежесть ipmi: |clockMs − at| ≤ 30 000 (покрывает один пропуск помощника и сдвиг часов
    // узла в обе стороны, ADR-029).
    public const long IpmiFreshAfterMs = 30_000;
}

// Запись statvfs одной точки монтирования: байты, как DriveInfo.TotalSize/TotalFreeSpace на worker.
// mount — поле 5 строки mountinfo дословно, с экранированием \040 (сопоставление без разбора).
public sealed record NodeStatvfs
{
    public string Mount { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
}

// Срез одного узла фермы по схеме v1 (ADR-029, «Узлы фермы»): сырые тексты, без толкования.
// Разобранные словари — ровно форма SliceFileSource (тот же разбор железа, что на локальном диске):
// Files/Links/Statvfs → SliceFileSource, GpuCsv → GpuSampler.ParseCsv, Gddr6 → Gddr6HelperReader.Parse.
// В модели только безопасные формы: все потолки применены при разборе, возрастающие N чипов —
// порядок ключей словари наследуют от ответа (JsonDocument сохраняет порядок свойств JSON).
public sealed record NodeSlice
{
    // Версия схемы. Принимаемые: текущая и предыдущая (сейчас — только 1).
    public int V { get; init; }
    // Возраст среза по монотонным часам агента (мс). Метки истории ставит worker при получении.
    public long AgeMs { get; init; }
    // Настенные часы узла (unix-мс) момента сборки. Нужны для свежести файлов root-помощников:
    // и ipmi (TryReadIpmi), и gddr6 (Gddr6HelperReader): метка "at" выставлена часами узла,
    // с часами worker не сравнивается (IpmiFreshAfterMs, Gddr6HelperReader.StaleAfter, ADR-029). Ни для чего другого.
    public long ClockMs { get; init; }
    // Абсолютный путь → текст дословно (с завершающим \n). Файла нет или он за потолка — ключа нет.
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    // Путь → цель ссылки (readlink) дословно, без нормализации.
    public IReadOnlyDictionary<string, string> Links { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    // stdout nvidia-smi (не больше MaxGpuRows строк, завершающий перевод строки снят);
    // null — бинаря нет, код выхода ≠ 0 или тайм-аут.
    public string? GpuCsv { get; init; }
    // Текст /run/ccs-gddr6/temps.json дословно (≤ MaxGddr6Bytes); null — файла нет.
    public string? Gddr6 { get; init; }
    // Текст /run/ccs-ipmi/sdr.json дословно (≤ MaxIpmiBytes). Необязательное поле v1 без смены v
    // (ADR-029, «Узел — набор устройств»): нет ключа, null или не строка — null, срез принимается.
    public string? Ipmi { get; init; }
    // По одной записи на точку монтирования из отданного mountinfo (≤ MaxStatvfs).
    public IReadOnlyList<NodeStatvfs> Statvfs { get; init; } = [];

    // Разбор обёртки ipmi {at, sdr} (тот же образец, что Gddr6HelperReader.Parse):
    // null — текста нет, JSON кривой, полей нет или нет или не те типы, или несвежее.
    // Свежесть симметрична: |ClockMs − at| ≤ IpmiFreshAfterMs. Метка в будущем (at > ClockMs)
    // — не «свежее»: помощник не мог написать sdr.json позже сборки среза, это сдвиг часов
    // или недоверенное значение (хвост 53e2dc3c). Часы узла — свои, с часами worker не сравниваем.
    // Вычитание свежести может переполниться (at недоверенное число, напр. long.MinValue):
    // переполнение — это заведомо не «свежее», поэтому checked и null через catch.
    // Толкование строк SDR — не здесь (Ш2d3).
    public (long at, string sdr)? TryReadIpmi()
    {
        if (Ipmi is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(Ipmi);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("at", out var atEl) || atEl.ValueKind != JsonValueKind.Number || !atEl.TryGetInt64(out var at))
                return null;
            if (!root.TryGetProperty("sdr", out var sdrEl) || sdrEl.ValueKind != JsonValueKind.String)
                return null;
            var age = checked(ClockMs - at);
            if (age > NodeSliceLimits.IpmiFreshAfterMs || age < -NodeSliceLimits.IpmiFreshAfterMs)
                return null;
            return (at, sdrEl.GetString()!);
        }
        catch
        {
            // Недоверенный текст помощника: наружу не выходит.
            return null;
        }
    }
}

public enum NodeSliceParseStatus
{
    Ok,
    // JSON разобран, но v нет, v не число или не входит в принимаемые.
    Incompatible,
    // Это не срез: тело больше потолка / пусто, не JSON, корень не объект, у обязательного
    // поля нет ключа или не тот тип. Узел down.
    Invalid,
}

// Результат разбора недоверенного ответа: всегда статус, исключение наружу не выходит никогда
// (тот же закон, что у Gddr6HelperReader: необработанное исключение роняет хост через StopHost).
public readonly struct NodeSliceParseResult
{
    private NodeSliceParseResult(NodeSliceParseStatus status, NodeSlice? slice, long version, string reason)
    {
        Status = status;
        Slice = slice;
        Version = version;
        Reason = reason;
    }

    public NodeSliceParseStatus Status { get; }
    // Только для Ok.
    public NodeSlice? Slice { get; }
    // Только для Incompatible: пришедшая версия как есть, вне диапазона int НЕ усечена
    // (0 — её нет или она не число). Только для диагностики.
    public long Version { get; }
    // Только для Invalid: причина (для лога).
    public string Reason { get; }

    public bool IsOk => Status == NodeSliceParseStatus.Ok;

    public static NodeSliceParseResult Ok(NodeSlice slice) => new(NodeSliceParseStatus.Ok, slice, slice.V, "");
    public static NodeSliceParseResult Incompatible(long version) => new(NodeSliceParseStatus.Incompatible, null, version, "");
    public static NodeSliceParseResult Invalid(string reason) => new(NodeSliceParseStatus.Invalid, null, 0, reason);
}

// Разбор тела GET /v1/slice (чистая функция, как PrometheusTextParser).
public static class NodeSliceParser
{
    // Принимаемые версии: текущая и предыдущая (ADR-029, «Версии и статусы узла»). Сейчас — только 1.
    private static bool IsAcceptedVersion(int v) => v == 1;

    public static NodeSliceParseResult Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            // Потолок тела проверяется ДО разбора.
            if (body.Length == 0)
                return NodeSliceParseResult.Invalid("пустое тело");
            if (body.Length > NodeSliceLimits.MaxBodyBytes)
                return NodeSliceParseResult.Invalid($"тело {body.Length} Б > {NodeSliceLimits.MaxBodyBytes} Б");

            // У JsonDocument нет перегрузки под ReadOnlySpan<byte> — копия тела (≤ 256 КБ) на разбор.
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return NodeSliceParseResult.Invalid("корень не объект");

            // Версия проверяется раньше остальных полей: не-срез этой версии разбирать не за чем.
            if (!root.TryGetProperty("v", out var vEl) || vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt32(out var v) || !IsAcceptedVersion(v))
                return NodeSliceParseResult.Incompatible(TryGetInt64(root, "v", out var rawV) ? rawV : 0);

            // Все ключи обязательны (ADR-029): нет ключа или не тот тип — Invalid (узел down).
            // Единственное исключение — карты files/links: там отсутствие ключа штатно.
            if (!TryGetInt64(root, "ageMs", out var ageMs))
                return NodeSliceParseResult.Invalid("ageMs отсутствует или не число");
            if (!TryGetInt64(root, "clockMs", out var clockMs))
                return NodeSliceParseResult.Invalid("clockMs отсутствует или не число");
            if (!root.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object)
                return NodeSliceParseResult.Invalid("files отсутствует или не объект");
            if (!root.TryGetProperty("links", out var linksEl) || linksEl.ValueKind != JsonValueKind.Object)
                return NodeSliceParseResult.Invalid("links отсутствует или не объект");
            if (!root.TryGetProperty("statvfs", out var statvfsEl) || statvfsEl.ValueKind != JsonValueKind.Array)
                return NodeSliceParseResult.Invalid("statvfs отсутствует или не массив");
            if (!TryGetStringOrNull(root, "gpuCsv", out var gpuCsv))
                return NodeSliceParseResult.Invalid("gpuCsv отсутствует или не строка");
            if (!TryGetStringOrNull(root, "gddr6", out var gddr6))
                return NodeSliceParseResult.Invalid("gddr6 отсутствует или не строка");

            // Потолок чипов общий для files и links: это одни и те же каталоги hwmonN.
            var allowedHwmon = AllowedHwmonDirs(filesEl, linksEl);

            // gddr6 — исключение из общего потолка строки: свой, 16 КБ; сверх — null, как будто файла нет.
            if (gddr6 != null && Encoding.UTF8.GetByteCount(gddr6) > NodeSliceLimits.MaxGddr6Bytes)
                gddr6 = null;

            // ipmi — необязательное поле без смены v (ADR-029, «Узел — набор устройств»):
            // нет ключа, null или не строка — null, срез принимается (это не Invalid);
            // потолок 16 КБ, как у gddr6: сверх — отбрасывается целиком, не обрезается.
            string? ipmi = null;
            if (root.TryGetProperty("ipmi", out var ipmiEl) && ipmiEl.ValueKind == JsonValueKind.String)
                ipmi = ipmiEl.GetString();
            if (ipmi != null && Encoding.UTF8.GetByteCount(ipmi) > NodeSliceLimits.MaxIpmiBytes)
                ipmi = null;

            return NodeSliceParseResult.Ok(new NodeSlice
            {
                V = v,
                AgeMs = ageMs,
                ClockMs = clockMs,
                Files = ReadMap(filesEl, NodeSliceLimits.MaxFiles, allowedHwmon),
                Links = ReadMap(linksEl, NodeSliceLimits.MaxLinks, allowedHwmon),
                GpuCsv = TrimGpuCsv(gpuCsv),
                Gddr6 = gddr6,
                Ipmi = ipmi,
                Statvfs = ReadStatvfs(statvfsEl),
            });
        }
        catch (Exception e)
        {
            // Недоверенный вход: битый UTF-8/JSON, переполнение чисел — что угодно. Наружу не выходит.
            return NodeSliceParseResult.Invalid($"сбой разбора: {e.Message}");
        }
    }

    // Числовое поле: false — ключа нет или он не число.
    private static bool TryGetInt64(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out value);
    }

    // Строковое поле: false — ключа нет или он не строка; true — строка или null (null — «у агента нет»).
    private static bool TryGetStringOrNull(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.Null)
            return true;
        if (el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString();
        return true;
    }

    // Потолок значения по пути: общий 4 КБ; исключение — /proc/self/mountinfo (64 КБ после фильтра).
    private static int TextLimit(string path) =>
        path == "/proc/self/mountinfo" ? NodeSliceLimits.MaxMountinfoBytes : NodeSliceLimits.MaxTextBytes;

    // Карта «путь → текст»: записи по позиции в ответе, не больше потолка; значение сверх потолка
    // отбрасывается целиком; записи лишних каталогов hwmon — отбрасываются.
    private static Dictionary<string, string> ReadMap(JsonElement map, int maxEntries, HashSet<int>? allowedHwmon)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = 0;
        foreach (var prop in map.EnumerateObject())
        {
            if (seen++ >= maxEntries)
                break;
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;
            var text = prop.Value.GetString()!;
            if (Encoding.UTF8.GetByteCount(text) > TextLimit(prop.Name))
                continue;
            if (!HwmonAllowed(prop.Name, allowedHwmon))
                continue;
            result[prop.Name] = text;
        }
        return result;
    }

    // Индекс N из пути "/sys/class/hwmon/hwmonN/…"; false — не путь каталога hwmon.
    private static bool TryHwmonIndex(string path, out int n)
    {
        n = 0;
        const string prefix = "/sys/class/hwmon/hwmon";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var start = prefix.Length;
        var i = start;
        while (i < path.Length && path[i] is >= '0' and <= '9')
            i++;
        if (i == start || i == path.Length || path[i] != '/')
            return false;
        return int.TryParse(path.AsSpan(start, i - start), out n);
    }

    // Допустимые каталоги hwmon: если различных N больше потолка — оставляем наименьшие N
    // (агент обходит их по возрастанию, ADR-029), независимо от порядка в JSON.
    // null — потолка нет, фильтр не нужен.
    private static HashSet<int>? AllowedHwmonDirs(params JsonElement[] maps)
    {
        var ns = new SortedSet<int>();
        foreach (var map in maps)
            foreach (var prop in map.EnumerateObject())
                if (TryHwmonIndex(prop.Name, out var n))
                    ns.Add(n);
        if (ns.Count <= NodeSliceLimits.MaxHwmonDirs)
            return null;
        var allowed = new HashSet<int>();
        foreach (var n in ns.Take(NodeSliceLimits.MaxHwmonDirs))
            allowed.Add(n);
        return allowed;
    }

    private static bool HwmonAllowed(string path, HashSet<int>? allowedHwmon) =>
        allowedHwmon is null || !TryHwmonIndex(path, out var n) || allowedHwmon.Contains(n);

    // gpuCsv: остаются первые 16 строк; строка длиннее 4 КБ отбрасывается целиком
    // (так же делает агент, ccs_node_stats.py). Пустые строки (в т.ч. завершающий \n stdout)
    // строками не считаются; GpuSampler.ParseCsv перевод строки не требует.
    private static string? TrimGpuCsv(string? csv)
    {
        if (csv is null)
            return null;
        var kept = new List<string>(NodeSliceLimits.MaxGpuRows);
        foreach (var raw in csv.Split('\n'))
        {
            if (kept.Count >= NodeSliceLimits.MaxGpuRows)
                break;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (Encoding.UTF8.GetByteCount(line) > NodeSliceLimits.MaxTextBytes)
                continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    // statvfs: не больше 64 записей; запись без mount/чисел или с mount сверх 4 КБ пропускается
    // (это данные, не обязательные поля среза — как лишняя строка mountinfo).
    private static List<NodeStatvfs> ReadStatvfs(JsonElement arr)
    {
        var result = new List<NodeStatvfs>();
        var seen = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (seen++ >= NodeSliceLimits.MaxStatvfs)
                break;
            if (el.ValueKind != JsonValueKind.Object)
                continue;
            if (!el.TryGetProperty("mount", out var m) || m.ValueKind != JsonValueKind.String)
                continue;
            if (!TryGetInt64(el, "total", out var total) || !TryGetInt64(el, "free", out var free))
                continue;
            var mount = m.GetString()!;
            if (Encoding.UTF8.GetByteCount(mount) > NodeSliceLimits.MaxTextBytes)
                continue;
            result.Add(new NodeStatvfs { Mount = mount, Total = total, Free = free });
        }
        return result;
    }
}
