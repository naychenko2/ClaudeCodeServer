using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Services.Llm;

// Одна видеокарта хоста из nvidia-smi. Ключ карты — Uuid, Index — для подписи.
// Числовые поля nullable: nvidia-smi отдаёт [N/A] для недоступного поля (например,
// power.draw на старых драйверах) — тогда null, а не 0.
public sealed record GpuSample
{
    public int Index { get; init; }
    public string Uuid { get; init; } = "";
    public string Name { get; init; } = "";
    // Утилизация GPU, %.
    public int? Utilization { get; init; }
    // Память, МБ.
    public long? MemoryUsed { get; init; }
    public long? MemoryTotal { get; init; }
    // Температура, °C.
    public int? Temperature { get; init; }
    // Мощность, Вт.
    public double? PowerDraw { get; init; }
    public double? PowerLimit { get; init; }
}

// Сэмплер видеокарт: по запросу (не в фоне) запускает nvidia-smi на хосте бэкенда.
// Таймаут 3 с, результат кэшируется на GpuCacheTtl (2 с); запросы сериализует lock.
// Нет бинаря/ошибка → null («нет блока видеокарт»), без исключений наружу.
public interface IGpuSampler
{
    Task<GpuSample?> ReadAsync(CancellationToken ct = default);
}

public sealed class GpuSampler : IGpuSampler
{
    // nvidia-smi: csv,noheader,nounits — числа без единиц, [N/A] для недоступного поля.
    private const string QueryArgs =
        "--query-gpu=index,uuid,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,power.limit" +
        " --format=csv,noheader,nounits";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private readonly TimeSpan _cacheTtl;
    private readonly Func<Task<string?>> _runner;
    private readonly ILogger<GpuSampler> _log;
    private readonly object _gate = new();

    private DateTimeOffset _lastSample;
    private GpuSample? _cached;      // null — нет бинаря/ошибка (кэшируем и её, чтобы не дёргать nvidia-smi чаще TTL)

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

    public Task<GpuSample?> ReadAsync(CancellationToken ct = default)
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
    // был готов до возврата из ReadAsync. null — нет бинаря/ошибка, без исключений наружу.
    private GpuSample? RunOnce()
    {
        GpuSample? result = null;
        try
        {
            var text = _runner().GetAwaiter().GetResult();
            if (text is not null)
            {
                var samples = ParseCsv(text);
                if (samples.Count > 0)
                    result = samples[0]; // карта «текст» — индекс 0 (первая); остальные — медиа-узел
            }
        }
        catch
        {
            // Нет бинаря/ошибка — null, без исключений наружу.
            result = null;
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
    // Формат строки: index,uuid,name,utilization,memory.used,memory.total,temperature,power.draw,power.limit
    // [N/A] в числовом поле → null. Имя (name) может содержать запятые — но nvidia-smi
    // его не экранирует, поэтому имя берём как всё между 2-й и 4-й запятой (uuid + name).
    public static IReadOnlyList<GpuSample> ParseCsv(string text)
    {
        var result = new List<GpuSample>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            // Разбиваем на поля: 9 колонок, name — вторая по счёту (может содержать запятые,
            // но на практике nvidia-smi не экранирует; берём жёстко 9 полей, name — склейка 3..(n-6)).
            var parts = line.Split(',');
            if (parts.Length < 9) continue;

            // index=0, uuid=1, name=2..(len-7), utilization=len-6, mem.used=len-5, mem.total=len-4,
            // temp=len-3, power.draw=len-2, power.limit=len-1.
            var name = string.Join(",", parts, 2, parts.Length - 8);
            result.Add(new GpuSample
            {
                Index = ParseInt(parts[0]) ?? 0,
                Uuid = parts[1].Trim(),
                Name = name.Trim(),
                Utilization = ParseInt(parts[parts.Length - 6]),
                MemoryUsed = ParseLong(parts[parts.Length - 5]),
                MemoryTotal = ParseLong(parts[parts.Length - 4]),
                Temperature = ParseInt(parts[parts.Length - 3]),
                PowerDraw = ParseDouble(parts[parts.Length - 2]),
                PowerLimit = ParseDouble(parts[parts.Length - 1]),
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
}
