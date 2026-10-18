using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Services.Llm;

// Одна видеокарта хоста из nvidia-smi. Ключ карты — Uuid, Index — для подписи.
// Числовые поля nullable: nvidia-smi отдаёт [N/A] для недоступного поля (например,
// power.draw на старых драйверах) — тогда null, а не 0.
//
// Поля для вкладки «Железо» (Б1): fan.speed, clocks.*, pstate, pcie.*, utilization.memory,
// clocks_event_reasons.* (троттлинг) и pci.bus_id (связка с файлом помощника ccs-gddr6
// по busId). Память GDDR6X и hotspot берутся НЕ отсюда, а из файла помощника (см. Gddr6HelperReader).
public sealed record GpuSample
{
    public int Index { get; init; }
    public string Uuid { get; init; } = "";
    public string Name { get; init; } = "";
    // Утилизация GPU, %.
    public int? Utilization { get; init; }
    // Утилизация шины памяти, %.
    public int? UtilizationMemory { get; init; }
    // Память, МБ.
    public long? MemoryUsed { get; init; }
    public long? MemoryTotal { get; init; }
    // Температура ядра, °C.
    public int? Temperature { get; init; }
    // Вентилятор, %.
    public int? FanSpeed { get; init; }
    // Частоты, МГц.
    public int? ClocksSm { get; init; }
    public int? ClocksMaxSm { get; init; }
    public int? ClocksMem { get; init; }
    // Состояние питания (P0..P15); null — [N/A].
    public string? Pstate { get; init; }
    // PCIe: поколение и ширина ссылки (текущие).
    public int? PcieGen { get; init; }
    public int? PcieWidth { get; init; }
    // Мощность, Вт.
    public double? PowerDraw { get; init; }
    public double? PowerLimit { get; init; }
    // Причины троттлинга (clocks_event_reasons.*): true — активен, false — «Not Active»,
    // null — [N/A] (поле не поддержано).
    public bool? SwPowerCap { get; init; }
    public bool? HwThermalSlowdown { get; init; }
    public bool? SwThermalSlowdown { get; init; }
    // PCI bus id в формате nvidia-smi (00000000:0B:00.0). Связка с помощником — по
    // канонической форме (CanonicalBusId).
    public string BusId { get; init; } = "";
}

// Сэмплер видеокарт: по запросу (не в фоне) запускает nvidia-smi на хосте бэкенда.
// Таймаут 3 с, результат кэшируется на GpuCacheTtl (2 с); запросы сериализует lock.
// Все карты хоста (ADR-029 п.7: всё, что видит хостовый nvidia-smi, — «текст»).
// Нет бинаря/ошибка/пусто → пустой список («нет блока видеокарт»), без исключений наружу.
public interface IGpuSampler
{
    Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default);
}

public sealed class GpuSampler : IGpuSampler
{
    // nvidia-smi: csv,noheader,nounits — числа без единиц, [N/A] для недоступного поля.
    // Порядок полей ПОСЛЕ имени фиксирован (см. QueryArgs и ParseCsv): 18 полей.
    private const string QueryArgs =
        "--query-gpu=index,uuid,name,utilization.gpu,utilization.memory,memory.used,memory.total," +
        "temperature.gpu,fan.speed,clocks.sm,clocks.max.sm,clocks.mem,pstate,pcie.link.gen.current," +
        "pcie.link.width.current,power.draw,power.limit,clocks_event_reasons.sw_power_cap," +
        "clocks_event_reasons.hw_thermal_slowdown,clocks_event_reasons.sw_thermal_slowdown,pci.bus_id" +
        " --format=csv,noheader,nounits";

    // Число полей после имени (name) — фиксированный хвост, который ParseCsv читает справа.
    private const int FieldsAfterName = 18;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private readonly TimeSpan _cacheTtl;
    private readonly Func<Task<string?>> _runner;
    private readonly ILogger<GpuSampler> _log;
    private readonly object _gate = new();

    private DateTimeOffset _lastSample;
    // Пустой список — нет бинаря/ошибка/карты (кэшируем и его, чтобы не дёргать nvidia-smi чаще TTL).
    private IReadOnlyList<GpuSample> _cached = [];

    public GpuSampler(TimeProvider time, TimeSpan cacheTtl, ILogger<GpuSampler> log)
    {
        _time = time;
        _cacheTtl = cacheTtl;
        _log = log ?? NullLogger<GpuSampler>.Instance;
        _runner = () => RunNvidiaSmiAsync();
    }

    // internal: тест подменяет runner (фейковый nvidia-smi), не трогая Process; log — опционален.
    internal GpuSampler(TimeProvider time, TimeSpan cacheTtl, Func<Task<string?>> runner, ILogger<GpuSampler>? log = null)
    {
        _time = time;
        _cacheTtl = cacheTtl;
        _runner = runner;
        _log = log ?? NullLogger<GpuSampler>.Instance;
    }

    public Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _lastSample < _cacheTtl)
                return Task.FromResult(_cached);
            // Синхронный запуск: runner завершён до возврата, и повторный ReadAsync
            // в пределах TTL видит кэш (lock сериализует, параллельных «в полёте» нет).
            var result = RunOnce();
            return Task.FromResult(result);
        }
    }

    // Один запуск: runner вызывается синхронно (GetAwaiter().GetResult()), чтобы результат
    // был готов до возврата из ReadAsync. Все карты хоста; нет бинаря/ошибка/пусто → пустой
    // список, без исключений наружу.
    private IReadOnlyList<GpuSample> RunOnce()
    {
        IReadOnlyList<GpuSample> result = [];
        try
        {
            var text = _runner().GetAwaiter().GetResult();
            if (text is not null)
                result = ParseCsv(text); // все карты хоста; ADR-029 п.7 — всё «текст»
        }
        catch
        {
            // Нет бинаря/ошибка — пустой список, без исключений наружу.
            result = [];
        }
        finally
        {
            lock (_gate)
            {
                _cached = result;
                _lastSample = _time.GetUtcNow();
            }
        }
        return result;
    }

    // Запуск nvidia-smi обычным Process (не лаунчер агентов, не BuildConcurrencyGate).
    // null — нет бинаря/таймаут/ошибка. stderr читаем параллельно с stdout: иначе заполненный
    // pipe-буфер stderr может повесить процесс (дедлок), а stdout так и не завершится.
    // Нестатический: логирует непустой stderr в Debug (через _log).
    private async Task<string?> RunNvidiaSmiAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = QueryArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var finished = await Task.WhenAny(stdoutTask, Task.Delay(Timeout));
        if (finished != stdoutTask)
        {
            // Таймаут: убиваем процесс.
            try { process.Kill(entireProcessTree: true); } catch { /* уже умер */ }
            return null;
        }
        var output = await stdoutTask;
        var stderr = await stderrTask; // дожидаемся дренажа stderr (непустое — в Debug)
        if (!string.IsNullOrWhiteSpace(stderr))
            _log.LogDebug("nvidia-smi stderr: {Stderr}", stderr.Trim());
        process.WaitForExit(); // дожидаемся завершения, чтобы ExitCode был корректен
        return process.ExitCode == 0 ? output : null;
    }

    // Чистая функция: разбирает CSV nvidia-smi (csv,noheader,nounits).
    // Формат строки: index, uuid, name, <18 полей в фиксированном порядке, см. QueryArgs>.
    // name — поле 3 (может содержать запятые, nvidia-smi их не экранирует), поэтому имя
    // берём как всё между uuid (2-е) и фиксированным хвостом из 18 полей (справа).
    // [N/A] в числовом поле → null; [N/A] в clocks_event_reasons.* → null (не поддержано).
    public static IReadOnlyList<GpuSample> ParseCsv(string? text)
    {
        var result = new List<GpuSample>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            // Минимум: index, uuid, name (1 поле) + 18 полей хвоста = 21.
            if (parts.Length < 2 + 1 + FieldsAfterName) continue;

            // index=0, uuid=1, name=2..(len-19), хвост 18 полей = len-18..len-1.
            var name = string.Join(",", parts, 2, parts.Length - 2 - FieldsAfterName);
            var p = parts.Length;
            result.Add(new GpuSample
            {
                Index = ParseInt(parts[0]) ?? 0,
                Uuid = parts[1].Trim(),
                Name = name.Trim(),
                Utilization = ParseInt(parts[p - 18]),
                UtilizationMemory = ParseInt(parts[p - 17]),
                MemoryUsed = ParseLong(parts[p - 16]),
                MemoryTotal = ParseLong(parts[p - 15]),
                Temperature = ParseInt(parts[p - 14]),
                FanSpeed = ParseInt(parts[p - 13]),
                ClocksSm = ParseInt(parts[p - 12]),
                ClocksMaxSm = ParseInt(parts[p - 11]),
                ClocksMem = ParseInt(parts[p - 10]),
                Pstate = ParsePstate(parts[p - 9]),
                PcieGen = ParseInt(parts[p - 8]),
                PcieWidth = ParseInt(parts[p - 7]),
                PowerDraw = ParseDouble(parts[p - 6]),
                PowerLimit = ParseDouble(parts[p - 5]),
                SwPowerCap = ParseThrottle(parts[p - 4]),
                HwThermalSlowdown = ParseThrottle(parts[p - 3]),
                SwThermalSlowdown = ParseThrottle(parts[p - 2]),
                BusId = parts[p - 1].Trim(),
            });
        }
        return result;
    }

    // [N/A] → null; число — в int.
    private static int? ParseInt(string s)
    {
        s = s.Trim();
        if (s == "[N/A]" || s.Length == 0) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static long? ParseLong(string s)
    {
        s = s.Trim();
        if (s == "[N/A]" || s.Length == 0) return null;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? ParseDouble(string s)
    {
        s = s.Trim();
        if (s == "[N/A]" || s.Length == 0) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // pstate: nvidia-smi отдаёт "P0".."P15" (или [N/A]). Храним строку как есть.
    private static string? ParsePstate(string s)
    {
        s = s.Trim();
        if (s == "[N/A]" || s.Length == 0) return null;
        return s;
    }

    // clocks_event_reasons.*: "Active" → true, "Not Active" → false, "[N/A]" → null.
    private static bool? ParseThrottle(string s)
    {
        s = s.Trim();
        if (s == "[N/A]" || s.Length == 0) return null;
        if (s == "Active") return true;
        if (s == "Not Active") return false;
        return null; // неизвестное значение — не доверяем
    }

    // Каноническая форма PCI bus id для связки nvidia-smi ↔ файл помощника ccs-gddr6:
    // нижний регистр + домен, дополненный нулями до 8 hex. nvidia-smi отдаёт
    // "00000000:0B:00.0" (8-значный домен, верхний регистр), помощник — "0000:0b:00.0"
    // (4-значный домен, нижний): обе формы сводятся к "00000000:0b:00.0".
    public static string CanonicalBusId(string? busId)
    {
        if (string.IsNullOrWhiteSpace(busId)) return "";
        var s = busId.Trim().ToLowerInvariant();
        var colon = s.IndexOf(':');
        if (colon < 0) return s;
        var domain = s[..colon];
        var rest = s[colon..];
        while (domain.Length < 8)
            domain = "0" + domain;
        return domain + rest;
    }
}
