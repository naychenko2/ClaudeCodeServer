using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Подметает scope-сироты в slice агентов при старте бэкенда.
///
/// Гашение scope по выходу процесса висит на событии <c>Process.Exited</c>, а оно живёт
/// ровно столько же, сколько процесс бэкенда. Упал бэкенд во время хода или прогрева
/// сборки — в slice остаётся scope с узлами MSBuild и VBCSCompiler: <c>--collect</c> не
/// срабатывает (из cgroup никто не выходил), <c>ProcessRegistry.PruneDead</c> о них не знает
/// (записей нет). В режиме <c>BuildNodeReuse=false</c> таких узлов не возникало вовсе —
/// то есть реюз добавил путь накопления памяти в тот самый slice, где нас дважды убивал OOM.
/// Единственный момент, когда сироту видно, — следующий старт бэкенда.
///
/// <para><b>Как отличить своё от чужого.</b> Три замка, ни один из них — не таймер.</para>
/// <list type="number">
/// <item>Чужой ПОЛЬЗОВАТЕЛЬ невидим по построению: работаем через <c>systemctl --user</c>,
/// а user-шина у каждого своя — до scope другого пользователя мы не дотянемся физически.</item>
/// <item>Чужой ИНСТАНС того же пользователя (dev-стенд рядом с продом — у нас это
/// повседневность) отличается по ОТПЕЧАТКУ ВЛАДЕЛЬЦА в имени юнита:
/// <c>ccs-run-&lt;pid бэкенда в hex&gt;-&lt;guid&gt;.scope</c> (<see cref="LocalProcessRunner.NewScopeUnitName"/>).
/// Гасим только те, чей владелец МЁРТВ. Живой сосед и scope, заведённый секунду назад
/// параллельным стартом, остаются нетронутыми — их владелец жив, и правило про возраст
/// юнита для этого не нужно (оно бы только оттягивало уборку настоящих сирот и при этом
/// ошибалось бы на долгом ходе упавшего инстанса).</item>
/// <item>Имя не нашего формата не трогаем вовсе — в том числе <c>ccs-run-&lt;guid&gt;</c>
/// без отпечатка (дореализационный формат этой же ветки, в бой не уезжал).</item>
/// </list>
///
/// <para>Ложное срабатывание возможно ровно в одну — безопасную — сторону: если номер
/// мёртвого владельца система успела переиспользовать, сирота выглядит живой и переживёт
/// уборку до следующего старта (проверено на живом systemd: подсунутый в имя номер 42
/// оказался ядерным потоком, и scope уцелел). Обратная ошибка — погасить чужой живой
/// scope — требует, чтобы живой владелец считался мёртвым, а этого
/// <c>Process.GetProcessById</c> не делает. Номера бэкенду система выдаёт из обычного
/// диапазона по возрастанию, так что столкновение с вечноживущим системным номером —
/// теоретическое.</para>
///
/// <para>Убить процессы упавшего инстанса — ровно то же решение, что уже принято в
/// <see cref="ProcessRegistry.Initialize"/> (чистка claude/node-сирот по pid-файлу):
/// пережившие свой бэкенд ходы не продолжаются, а только едят память.</para>
/// </summary>
public static class ScopeOrphanSweeper
{
    // ccs-run-<owner pid, 8 hex>-<guid, 32 hex>.scope
    private static readonly Regex UnitPattern = new(
        @"^ccs-run-([0-9a-f]{8})-[0-9a-f]{32}\.scope$", RegexOptions.Compiled);

    /// <summary>Шов для тестов: чем перечислять юниты (аргумент — путь к systemctl).</summary>
    internal static Func<string, IReadOnlyList<string>> ListScopes { get; set; } = ListViaSystemctl;

    /// <summary>Шов для тестов: жив ли процесс с таким номером.</summary>
    internal static Func<int, bool> IsProcessAlive { get; set; } = DefaultIsProcessAlive;

    /// <summary>
    /// Найти и погасить сирот. Fail-open: любой сбой — предупреждение в лог, не исключение.
    /// Зовётся при старте бэкенда, до первых запусков агентов.
    /// </summary>
    public static void Sweep(IsolationOptions options)
    {
        if (!options.Enabled || OperatingSystem.IsWindows()) return;
        var systemdRun = LocalProcessRunner.ResolveSystemdRunPath(options);
        if (string.IsNullOrEmpty(systemdRun)) return;
        var systemctl = LocalProcessRunner.ResolveSystemctlPath(systemdRun);
        try
        {
            var orphans = SelectOrphans(ListScopes(systemctl), IsProcessAlive);
            foreach (var unit in orphans)
            {
                Console.WriteLine($"[exec] гашу scope-сироту {unit}: бэкенд-владелец не запущен");
                LocalProcessRunner.StopScope(systemctl, unit);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[exec] уборка scope-сирот не удалась: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Чистая часть решения: какие из юнитов — сироты. Своё имя формата не имеет владельца,
    /// поэтому не наш формат — мимо; живой владелец — мимо (в том числе мы сами).
    /// </summary>
    internal static List<string> SelectOrphans(IEnumerable<string> units, Func<int, bool> isAlive)
    {
        var orphans = new List<string>();
        foreach (var unit in units)
        {
            var m = UnitPattern.Match(unit.Trim());
            if (!m.Success) continue;
            var ownerPid = Convert.ToInt32(m.Groups[1].Value, 16);
            if (isAlive(ownerPid)) continue;
            orphans.Add(unit.Trim());
        }
        return orphans;
    }

    private static bool DefaultIsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        // Нет доступа к чужому процессу — он существует, значит владелец жив
        catch (Exception) { return true; }
    }

    // `systemctl --user list-units --all --plain --no-legend --type=scope 'ccs-run-*.scope'`
    // — первая колонка каждой строки и есть имя юнита.
    private static IReadOnlyList<string> ListViaSystemctl(string systemctl)
    {
        var psi = new ProcessStartInfo(systemctl)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 { "--user", "list-units", "--all", "--plain", "--no-legend", "--type=scope", "ccs-run-*.scope" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("systemctl не запустился");
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(); } catch { /* уже вышел */ }
            throw new TimeoutException("systemctl --user list-units не ответил за 10 с");
        }
        return [.. stdout.GetAwaiter().GetResult()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Where(n => n.Length > 0)];
    }
}
