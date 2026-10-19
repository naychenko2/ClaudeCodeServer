using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Одна карта из файла помощника ccs-gddr6: температура памяти GDDR6X и hotspot,
// которых драйвер/NVML не отдают (регистры читаются root-службой через /dev/mem).
// Ключ — busId (в канонической форме связывается с картой nvidia-smi, см. GpuSampler.CanonicalBusId).
public sealed record Gddr6GpuTemp
{
    public string BusId { get; init; } = "";
    // null — регистр не ответил (помощник сам санитизирует мусор).
    public int? MemTempC { get; init; }
    public int? HotspotTempC { get; init; }
}

// Читатель файла помощника ccs-gddr6 (root-служба, deploy/gddr6-helper, deploy/systemd/ccs-gddr6.service).
// Бэкенд ТОЛЬКО читает файл (недоверенный источник): любой сбой разбора → null, а не исключение
// наружу (иначе упадёт BackgroundService, а с ним весь хост). Метка "at" в файле — признак свежести:
// старше StaleAfter → данные протухли (mem/hotspot = null, sensor:'nodata'), остальное работает.
public sealed class Gddr6HelperReader
{
    // Метка "at" старше этого → данные протухли.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;

    public Gddr6HelperReader(TimeProvider time)
    {
        _time = time;
    }

    // Читает файл: null — файла нет/сбой чтения/битый JSON; иначе (список температур, свеж).
    // Свежесть — по метке "at" в файле (DateTimeOffset из unix-мс); метки нет → протухло.
    public (IReadOnlyList<Gddr6GpuTemp> gpus, bool fresh)? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path);
            return Parse(text);
        }
        catch
        {
            return null;
        }
    }

    // Чистая функция: разбирает JSON помощника. null — не тот формат (v != 1) или битый JSON.
    // Свежесть: (now - at) <= StaleAfter.
    public (IReadOnlyList<Gddr6GpuTemp> gpus, bool fresh)? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                return null;

            long at = 0;
            if (root.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.Number)
                at = atEl.GetInt64();

            var gpus = new List<Gddr6GpuTemp>();
            if (root.TryGetProperty("gpus", out var gpusEl) && gpusEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gpusEl.EnumerateArray())
                {
                    var busId = g.TryGetProperty("busId", out var b) && b.ValueKind == JsonValueKind.String
                        ? b.GetString()!
                        : "";
                    gpus.Add(new Gddr6GpuTemp
                    {
                        BusId = busId,
                        MemTempC = ReadInt(g, "memTempC"),
                        HotspotTempC = ReadInt(g, "hotspotTempC"),
                    });
                }
            }

            var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(at);
            return (gpus, age <= StaleAfter);
        }
        catch
        {
            return null;
        }
    }

    // Числовое поле: null — отсутствует, не число или null в JSON.
    private static int? ReadInt(JsonElement g, string name)
    {
        if (g.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }
}
