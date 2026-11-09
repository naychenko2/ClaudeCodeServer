using System.Globalization;

namespace ClaudeHomeServer.Services.Llm;

// Оперативная память: занято/всего из /proc/meminfo. Температур нет (узел «Оперативная
// память»: tempC=null, sensor:'none', деталь — «занято N из M ГБ»).
public sealed record MeminfoSnapshot
{
    public long? UsedGb { get; init; }
    public long? TotalGb { get; init; }
}

// Читатель /proc/meminfo (недоверенный источник: сбой → null, не исключение наружу).
public sealed class MeminfoReader
{
    private readonly string _path;

    public MeminfoReader() : this("/proc/meminfo") { }
    public MeminfoReader(string path) { _path = path; }

    public MeminfoSnapshot? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            long? total = null;
            long? available = null;
            foreach (var line in File.ReadAllLines(_path))
            {
                if (line.StartsWith("MemTotal:"))
                    total = ParseKb(line);
                else if (line.StartsWith("MemAvailable:"))
                    available = ParseKb(line);
            }
            if (total is null) return null;
            var used = total.Value - (available ?? 0);
            return new MeminfoSnapshot
            {
                UsedGb = used / 1024 / 1024,
                TotalGb = total.Value / 1024 / 1024,
            };
        }
        catch { return null; }
    }

    // "MemTotal:       127341452 kB" → 127341452.
    private static long? ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }
}
