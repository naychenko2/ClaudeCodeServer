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
    // (замер 2026-10-29: N=1 — 5,5 ГиБ, N=3 — 9,5 ГиБ при лимите ccs-agents.slice 18 ГиБ).
    // Жёсткий предел конфига; потолок по умолчанию ниже — AdaptiveShards.DefaultMax
    public const int MaxShards = 6;

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

// Снимок машины для выбора числа шардов: потоков, load1, MemAvailable в байтах и запас cgroup
// (минимум memory.max − memory.current по цепочке; null — cgroup нет или везде max)
public readonly record struct MachineSnapshot(int Cpus, double Load1, long MemAvailableBytes, long? CgroupFreeBytes = null);

// Число шардов монолита по текущей нагрузке машины (TestRuns:AdaptiveShards). Те же константы и то
// же правило — в scripts/test-shards.py (choose_shards); табличные тесты обеих сторон идут по одним
// строкам, правка порога — в обоих местах сразу. Статическая параллель упирается в CPU: днём (load
// 30–50 на 24 потоках) больше трёх шардов не ускоряет, а в тихие окна три недогружают машину
public static class AdaptiveShards
{
    // Потолок по умолчанию. 4, а не 6: пик RSS при N=6 не замерен (02.11 slice был занят на 22 из
    // 24 ГиБ при load1 71 — замер не влезал), поднимать после замера
    public const int DefaultMax = 4;
    // Нет /proc или он не прочитался — прежнее фиксированное значение
    public const int Fallback = 3;
    // load1 < 0,5·nproc — машина свободна: потолок; < 1,5·nproc — 3; иначе 2
    public const double IdleLoad = 0.5;
    public const double BusyLoad = 1.5;
    public const int Mid = 3;
    public const int Min = 2;
    public const long GiB = 1024L * 1024 * 1024;
    // Память на шард: пик RSS на шард прошлого прогона (ShardPeakMemory) × 1,2, но не ниже 1 ГиБ
    // (оборванный на старте прогон не должен сойти за лёгкий); нет записи — запасное 3,5 ГиБ (замер
    // 2026-10-29: N=3 дал пик 9,5 ГиБ, ≈3,2 на шард, запас уже внутри — множитель к нему не
    // применяется). N ≤ floor(запас / память на шард), не меньше 1; запас — минимум MemAvailable и
    // запаса cgroup, в котором пойдут шарды (ccs-agents.slice): хост бывает свободен при почти полном
    // slice (02.11: 28 ГиБ на хосте при 16,9 из 24 в slice)
    public const long ShardMemBytes = 7 * GiB / 2;
    public const double PeakMargin = 1.2;
    public const long PeakFloorBytes = GiB;
    // Ядер на шард: N ≤ max(1, floor(nproc / 4)). У каждого шарда свой testhost с xunit
    // maxParallelThreads=12 (тесты больше ждут, чем считают: замер 17.11.2026 — 16 и 24 потока не
    // быстрее 12, только тяжелее); меньше ~4 ядер на шард — процессы делят одни ядра и только мешают
    // друг другу (24 потока — 6, 8 — 2, 4 — 1)
    public const int CoresPerShard = 4;

    // Память на шард для правила: прошлый пик × 1,2 (не ниже 1 ГиБ) или запасные 3,5 ГиБ
    public static long ShardMem(long? peakPerShard) =>
        peakPerShard is { } p ? (long)(Math.Max(p, PeakFloorBytes) * PeakMargin) : ShardMemBytes;

    // Чистая функция от снимка (null — снимка нет) и прошлого пика на шард (null — записи нет):
    // число шардов и причина для сводки
    public static (int Shards, string Reason) Choose(MachineSnapshot? snapshot, int max, long? peakPerShard = null)
    {
        max = Math.Max(1, max);
        if (snapshot is not { } s) return (Math.Min(Fallback, max), "нет /proc — фиксированное значение");
        var n = s.Load1 < IdleLoad * s.Cpus ? max : s.Load1 < BusyLoad * s.Cpus ? Mid : Min;
        n = Math.Min(n, Math.Max(1, s.Cpus / CoresPerShard));
        var headroom = s.CgroupFreeBytes is { } cg ? Math.Min(s.MemAvailableBytes, cg) : s.MemAvailableBytes;
        n = Math.Min(n, (int)Math.Max(1, headroom / ShardMem(peakPerShard)));
        var load = s.Load1.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
        var where = s.CgroupFreeBytes is { } free ? $", в cgroup {free / GiB} ГиБ" : "";
        return (Math.Min(n, max), $"load1 {load} из {s.Cpus}, свободно {s.MemAvailableBytes / GiB} ГиБ{where}");
    }

    // Снимок из /proc и cgroupfs; нет /proc (Windows, macOS) или не прочитался — null, без исключения.
    // slice — имя slice systemd, в котором пойдут шарды (Execution:Isolation:Slice при включённой
    // изоляции); null — шарды живут в cgroup самого сервера
    public static MachineSnapshot? Read(string procRoot = "/proc", string cgroupRoot = "/sys/fs/cgroup",
        string? slice = null)
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
                return new MachineSnapshot(Environment.ProcessorCount, load1, kb * 1024,
                    CgroupFree(Path.Combine(procRoot, "self", "cgroup"), cgroupRoot, slice));
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
            or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    // Запас памяти cgroup v2, в котором пойдут шарды: минимум memory.max − memory.current по цепочке от
    // стартового каталога до корня. Старт — каталог slice (ищется у предков своего cgroup: у user-юнитов
    // сервер в app.slice, а ccs-agents.slice — в ccs.slice под тем же user@<uid>.service; uid в код не
    // зашит), без slice — свой cgroup. null — cgroup нет, slice не найден, везде max или значение
    // неразборчиво: правило тогда смотрит только MemAvailable
    public static long? CgroupFree(string cgroupFile, string cgroupRoot, string? slice)
    {
        try
        {
            if (!File.Exists(cgroupFile)) return null;
            var own = File.ReadLines(cgroupFile).FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal))?[3..].Trim();
            if (own is not { Length: > 0 } || own[0] != '/') return null;
            var root = Path.GetFullPath(cgroupRoot).TrimEnd('/');
            var start = Path.GetFullPath(root + own).TrimEnd('/');
            if (slice is { Length: > 0 })
            {
                if (SlicePath(slice) is not { } rel) return null;
                var found = Ancestors(start, root).Select(a => Path.Combine(a, rel)).FirstOrDefault(Directory.Exists);
                if (found is null) return null;
                start = found;
            }
            long? best = null;
            foreach (var node in Ancestors(start, root))
            {
                var maxFile = Path.Combine(node, "memory.max");
                if (!File.Exists(maxFile)) continue;
                var limit = File.ReadAllText(maxFile).Trim();
                if (limit == "max") continue;
                var free = Math.Max(0, long.Parse(limit, System.Globalization.CultureInfo.InvariantCulture)
                    - long.Parse(File.ReadAllText(Path.Combine(node, "memory.current")).Trim(),
                        System.Globalization.CultureInfo.InvariantCulture));
                best = best is { } b ? Math.Min(b, free) : free;
            }
            return best;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
            or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    // Путь slice в cgroupfs — единое правило Core (CgroupSlices.SlicePath)
    internal static string? SlicePath(string slice) => Services.Execution.CgroupSlices.SlicePath(slice);

    // Каталог и его предки до корня cgroupfs включительно
    private static IEnumerable<string> Ancestors(string node, string root)
    {
        for (var cur = node; cur.Length >= root.Length && cur.StartsWith(root, StringComparison.Ordinal);
             cur = Path.GetDirectoryName(cur) ?? "")
        {
            yield return cur;
            if (cur.Length == root.Length) yield break;
        }
    }
}

// Пик RSS на шард прошлого серверного прогона монолита: data/build-memory/shard-peak.json (рядом с
// BuildRunMemory, вне деревьев агентов — туда агент не пишет). Пишется, только когда шардов было ≥ 2
// (один процесс несёт весь монолит, и его пик как «на шард» навсегда прижал бы N к 1). Ключ один на
// машину: монолит в деревьях почти одинаков, а свежему дереву без своей записи пришлось бы ждать 3,5 ГиБ
public static class ShardPeakMemory
{
    public const string FileName = "shard-peak.json";
    // Правдоподобный предел значения: больше — мусор, правило возьмёт запасное
    public const long MaxBytes = 256 * AdaptiveShards.GiB;

    private sealed record Entry(long PerShardBytes, int Shards, long PeakBytes);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    // Нет, битый, вне (0, 256 ГиБ] — null
    public static long? Load(string? directory)
    {
        if (directory is null) return null;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var entry = System.Text.Json.JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json);
            return entry is { PerShardBytes: > 0 and <= MaxBytes } ? entry.PerShardBytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    // Пик дерева на число шардов; меньше двух шардов или пик не мерялся — не пишется. Сбой записи
    // прогон не роняет: память — лишь подсказка следующему выбору N
    public static void Save(string? directory, long peakBytes, int shards)
    {
        if (directory is null || shards < 2 || peakBytes <= 0) return;
        try
        {
            Directory.CreateDirectory(directory);
            var tmp = Path.Combine(directory, FileName + ".tmp");
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(new Entry(peakBytes / shards, shards, peakBytes), Json));
            File.Move(tmp, Path.Combine(directory, FileName), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// Сумма VmRSS процессов и всех их потомков по /proc (как descendants_rss_kb в test-shards.py); вне
// Linux или при сбое чтения — 0: пик тогда не пишется, правило берёт запасное значение
public static class ProcessTreeRss
{
    public static long SumBytes(IReadOnlyCollection<int> roots, string procRoot = "/proc")
    {
        if (roots.Count == 0 || !Directory.Exists(procRoot)) return 0;
        var children = new Dictionary<int, List<int>>();
        var rss = new Dictionary<int, long>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(procRoot))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                int ppid = 0;
                long kb = 0;
                try
                {
                    foreach (var line in File.ReadLines(Path.Combine(dir, "status")))
                    {
                        if (line.StartsWith("PPid:", StringComparison.Ordinal))
                            int.TryParse(line.AsSpan(5).Trim(), out ppid);
                        else if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                            long.TryParse(line.AsSpan(6).Trim().TrimEnd("kB").Trim(), out kb);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue; // процесс вышел между перечислением и чтением
                }
                (children.TryGetValue(ppid, out var list) ? list : children[ppid] = []).Add(pid);
                rss[pid] = kb;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        long total = 0;
        var seen = new HashSet<int>();
        var stack = new Stack<int>(roots);
        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            if (!seen.Add(pid)) continue;
            total += rss.GetValueOrDefault(pid);
            if (children.TryGetValue(pid, out var kids))
                foreach (var kid in kids) stack.Push(kid);
        }
        return total * 1024;
    }
}
