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
