using System.Text;

namespace ClaudeHomeServer.Services.TestRuns;

// Шарды монолита по КЛАССАМ для run_tests: монолит без filter идёт N процессами
// `dotnet test --no-build --filter <классы шарда>` параллельно под ОДНИМ слотом
// BuildConcurrencyGate (второй семафор не заводится — счётчик единственный на процесс).
// Список классов берётся из уже снятой фазы --list-tests, повторно она не идёт.
//
// Тот же принцип, что у scripts/test-shards.py (CI), с одним отличием: профиля длительностей у
// сервера нет, поэтому упаковка жадная по ЧИСЛУ тестов класса. Самопроверка та же: каждый тест
// списка попадает ровно в один шард, иначе шардов нет и прогон идёт одним процессом.
public static class TestShards
{
    // Потолок числа шардов: каждый — свой testhost с maxParallelThreads, пик RSS растёт с N
    // (замер 2026-10-29: N=1 — 5,5 ГиБ, N=3 — 9,5 ГиБ при лимите ccs-agents.slice 18 ГиБ)
    public const int MaxShards = AdaptiveShards.DefaultMax;

    // Потолок списка в памяти: монолит — ~7 тыс. имён, больше — не наш случай
    public const int MaxListedTests = 100_000;

    // Цель — монолит из настроек: каталог проекта или его .csproj, разделители и хвостовой «/» не важны
    public static bool IsMonolith(string? target, string? monolithTarget)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(monolithTarget)) return false;
        static string Norm(string p) => p.Replace('\\', '/').Trim().TrimEnd('/');
        var t = Norm(target);
        var m = Norm(monolithTarget);
        if (t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var slash = t.LastIndexOf('/');
            t = slash >= 0 ? t[..slash] : "";
        }
        return string.Equals(t, m, StringComparison.Ordinal);
    }

    // Полное имя метода из строки списка: аргументы теории отрезаются
    public static string TestFqn(string listed)
    {
        var name = listed.Trim();
        var paren = name.IndexOf('(');
        return (paren >= 0 ? name[..paren] : name).Trim();
    }

    public static string ClassOf(string fqn) => fqn.LastIndexOf('.') is var dot and > 0 ? fqn[..dot] : fqn;

    // Раскладка классов на n шардов: самые многочисленные классы первыми — в наименее загруженный
    public static IReadOnlyList<IReadOnlyList<string>> Layout(IReadOnlyList<string> tests, int n)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tests)
        {
            var c = ClassOf(t);
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        var shards = Enumerable.Range(0, n).Select(_ => new List<string>()).ToArray();
        var load = new int[n];
        foreach (var (cls, count) in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var target = 0;
            for (var i = 1; i < n; i++) if (load[i] < load[target]) target = i;
            shards[target].Add(cls);
            load[target] += count;
        }
        return [.. shards.Select(s => (IReadOnlyList<string>)[.. s.Order(StringComparer.Ordinal)])];
    }

    // «Содержит Класс.»: точка не даёт зацепить класс с тем же префиксом имени
    public static bool Matches(string cls, string fqn) => fqn.Contains(cls + ".", StringComparison.Ordinal);

    // Тесты, которые не попали ни в один шард или попали в несколько; пусто — объединение == список
    public static IReadOnlyList<string> Verify(IReadOnlyList<string> tests, IReadOnlyList<IReadOnlyList<string>> shards)
    {
        var dotted = shards.Select(s => s.Select(c => c + ".").ToArray()).ToArray();
        var bad = new List<string>();
        foreach (var t in tests)
        {
            var hits = 0;
            foreach (var shard in dotted)
                if (shard.Any(c => t.Contains(c, StringComparison.Ordinal))) hits++;
            if (hits != 1) bad.Add(t);
        }
        return bad;
    }

    // Спецсимволы выражения фильтра VSTest экранируются обратной косой
    public static string Filter(IReadOnlyList<string> shard)
    {
        var sb = new StringBuilder();
        foreach (var cls in shard)
        {
            if (sb.Length > 0) sb.Append('|');
            sb.Append("FullyQualifiedName~");
            foreach (var ch in cls)
            {
                if (ch is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~') sb.Append('\\');
                sb.Append(ch);
            }
            sb.Append('.');
        }
        return sb.ToString();
    }

    // Фильтры шардов или null — шардировать нечего или самопроверка не сошлась
    public static IReadOnlyList<string>? Plan(IReadOnlyList<string> listed, int n)
    {
        if (n < 2 || listed.Count == 0) return null;
        var tests = listed.Select(TestFqn).ToList();
        var shards = Layout(tests, n).Where(s => s.Count > 0).ToList();
        if (shards.Count < 2 || Verify(tests, shards).Count > 0) return null;
        return [.. shards.Select(Filter)];
    }
}

// Снимок машины для выбора числа шардов: потоков, load1, MemAvailable в байтах
public readonly record struct MachineSnapshot(int Cpus, double Load1, long MemAvailableBytes);

// Число шардов монолита по текущей нагрузке машины (TestRuns:AdaptiveShards). Те же константы и то
// же правило — в scripts/test-shards.py (choose_shards); табличные тесты обеих сторон идут по одним
// строкам, правка порога — в обоих местах сразу. Статическая параллель упирается в CPU: днём (load
// 30–50 на 24 потоках) больше трёх шардов не ускоряет, а в тихие окна три недогружают машину
public static class AdaptiveShards
{
    public const int DefaultMax = 6;
    // Нет /proc или он не прочитался — прежнее фиксированное значение
    public const int Fallback = 3;
    // load1 < 0,5·nproc — машина свободна: потолок; < 1,5·nproc — 3; иначе 2
    public const double IdleLoad = 0.5;
    public const double BusyLoad = 1.5;
    public const int Mid = 3;
    public const int Min = 2;
    public const long GiB = 1024L * 1024 * 1024;
    // MemAvailable меньше — не больше 3 (3 шарда монолита ≈ 9,5 ГиБ); меньше 8 ГиБ — не больше 2
    public const long MemMid = 16 * GiB;
    public const long MemLow = 8 * GiB;

    // Чистая функция от снимка (null — снимка нет): число шардов и причина для сводки
    public static (int Shards, string Reason) Choose(MachineSnapshot? snapshot, int max)
    {
        max = Math.Max(1, max);
        if (snapshot is not { } s) return (Math.Min(Fallback, max), "нет /proc — фиксированное значение");
        var n = s.Load1 < IdleLoad * s.Cpus ? max : s.Load1 < BusyLoad * s.Cpus ? Mid : Min;
        if (s.MemAvailableBytes < MemLow) n = Math.Min(n, Min);
        else if (s.MemAvailableBytes < MemMid) n = Math.Min(n, Mid);
        var load = s.Load1.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
        return (Math.Min(n, max), $"load1 {load} из {s.Cpus}, свободно {s.MemAvailableBytes / GiB} ГиБ");
    }

    // Снимок из /proc; нет его (Windows, macOS) или не прочитался — null, без исключения
    public static MachineSnapshot? Read(string procRoot = "/proc")
    {
        try
        {
            var loadavg = File.ReadAllText(Path.Combine(procRoot, "loadavg"));
            var load1 = double.Parse(loadavg.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                System.Globalization.CultureInfo.InvariantCulture);
            foreach (var line in File.ReadLines(Path.Combine(procRoot, "meminfo")))
            {
                if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                var kb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1],
                    System.Globalization.CultureInfo.InvariantCulture);
                return new MachineSnapshot(Environment.ProcessorCount, load1, kb * 1024);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
            or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
