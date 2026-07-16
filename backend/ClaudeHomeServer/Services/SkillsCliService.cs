using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services;

// Навык из реестра skills.sh (результат поиска/листинга репозитория).
// Source — «owner/repo», Skill — имя навыка внутри репозитория (сегмент после @).
public record RegistrySkill(
    string Source,
    string Skill,
    string? Description,
    int? Installs,
    string Url);

// Область установки навыка: Project — в .claude/skills проекта; Global — в ~/.claude/skills.
public enum SkillScope { Project, Global }

// Обёртка официального CLI «npx skills» (github.com/vercel-labs/skills). CLI работает
// анонимно и не-интерактивно (сам детектит окружение claude-code), закрывая весь функционал:
// поиск по реестру (find), листинг навыков репозитория с описаниями (add -l), установку в
// проект/глобально (add --copy) и удаление (remove). Прямой доступ к API skills.sh недоступен
// (401, только 12-часовой Vercel-OIDC) — поэтому идём через CLI. Вывод CLI — раскрашенный
// текст, парсим после стриппинга ANSI.
public partial class SkillsCliService(IConfiguration config, ILogger<SkillsCliService> log,
    Execution.ILauncherFactory launchers)
{
    // Пакет npx (можно запинить версию в конфиге). Дефолт — latest.
    private string NpxPackage => config["Skills:NpxPackage"] is { Length: > 0 } p ? p : "skills@latest";

    // Готовая команда вместо npx (если в образ предустановлен `skills` глобально) — тогда
    // клонирование пакета на каждый вызов не нужно. Пусто — идём через npx.
    private string? DirectCommand => config["Skills:Command"] is { Length: > 0 } c ? c : null;

    private TimeSpan Timeout =>
        TimeSpan.FromMilliseconds(int.TryParse(config["Skills:CliTimeoutMs"], out var ms) ? ms : 180_000);

    // --- Санитизация аргументов ---
    // На Windows CLI запускается через cmd.exe /c (npx — batch-скрипт), а cmd.exe повторно
    // разбирает командную строку: метасимволы &|^<>%! в аргументах дают выполнение
    // произвольной команды (класс BatBadBut). Экранирование ArgumentList рассчитано на argv
    // и от cmd-разбора не защищает — поэтому валидируем всё пользовательское до запуска.

    [GeneratedRegex(@"^[A-Za-z0-9._/@-]+$")]
    private static partial Regex IdentifierArgRegex();

    // Идентификаторы (source «owner/repo», skill, owner) — только безопасный whitelist
    private static bool ValidIdentifier(string s) =>
        s.Length is > 0 and <= 200 && IdentifierArgRegex().IsMatch(s);

    // Поисковый запрос — свободный текст, но без метасимволов cmd.exe и управляющих символов
    private static bool ValidQuery(string s) =>
        s.Length is > 0 and <= 200 &&
        !s.Any(ch => ch is '&' or '|' or '^' or '<' or '>' or '%' or '!' or '"' || char.IsControl(ch));

    // --- Публичные операции ---

    // Поиск навыков по реестру (semantic для многословных запросов, fuzzy для одного слова).
    // owner — опциональное сужение по GitHub-владельцу.
    public async Task<IReadOnlyList<RegistrySkill>> FindAsync(string query, string? owner = null,
        CancellationToken ct = default)
    {
        if (!ValidQuery(query) || (!string.IsNullOrWhiteSpace(owner) && !ValidIdentifier(owner.Trim())))
        {
            log.LogWarning("skills find: недопустимые символы в запросе или owner");
            return [];
        }
        var args = new List<string> { "find", query };
        if (!string.IsNullOrWhiteSpace(owner)) { args.Add("--owner"); args.Add(owner.Trim()); }

        var (code, stdout, stderr) = await RunAsync(args, workDir: null, ct);
        if (code != 0)
        {
            log.LogWarning("skills find «{Query}» завершился с кодом {Code}: {Err}", query, code, Trunc(stderr));
            return [];
        }
        return ParseFind(StripAnsi(stdout));
    }

    // Листинг навыков репозитория С ОПИСАНИЯМИ (CLI клонирует репозиторий) — используется
    // для каталога и LLM-подбора. Source — «owner/repo».
    public async Task<IReadOnlyList<RegistrySkill>> ListRepoAsync(string source, CancellationToken ct = default)
    {
        if (!ValidIdentifier(source))
        {
            log.LogWarning("skills add -l: недопустимый source «{Source}»", Trunc(source));
            return [];
        }
        var (code, stdout, stderr) = await RunAsync(["add", source, "-l"], workDir: null, ct);
        if (code != 0)
        {
            log.LogWarning("skills add {Source} -l завершился с кодом {Code}: {Err}", source, code, Trunc(stderr));
            return [];
        }
        return ParseList(StripAnsi(stdout), source);
    }

    // Установка навыка. scope=Project требует projectRootPath (cwd = корень проекта);
    // scope=Global ставит в ~/.claude/skills независимо от cwd. Всегда --copy (в контейнере
    // symlink между разными деревьями ненадёжен) и --agent claude-code. Возвращает
    // (успех, лог вывода для диагностики).
    public async Task<(bool Ok, string Output)> InstallAsync(string source, string skill, SkillScope scope,
        string? projectRootPath, CancellationToken ct = default)
    {
        if (!ValidIdentifier(source) || !ValidIdentifier(skill))
            return (false, "Недопустимые символы в имени источника или навыка");
        var args = new List<string>
        {
            "add", source,
            "--skill", skill,
            "--agent", "claude-code",
            "--copy", "-y",
        };
        if (scope == SkillScope.Global) args.Add("-g");

        var workDir = scope == SkillScope.Project ? projectRootPath : null;
        var (code, stdout, stderr) = await RunAsync(args, workDir, ct);
        var output = (stdout + "\n" + stderr).Trim();
        if (code != 0)
            log.LogWarning("skills add {Source}@{Skill} ({Scope}) код {Code}: {Err}",
                source, skill, scope, code, Trunc(stderr));
        return (code == 0, output);
    }

    // Удаление установленного навыка из указанной области.
    public async Task<bool> RemoveAsync(string skill, SkillScope scope, string? projectRootPath,
        CancellationToken ct = default)
    {
        if (!ValidIdentifier(skill))
        {
            log.LogWarning("skills remove: недопустимое имя навыка «{Skill}»", Trunc(skill));
            return false;
        }
        var args = new List<string> { "remove", "--skill", skill, "--agent", "claude-code", "-y" };
        if (scope == SkillScope.Global) args.Add("-g");
        var workDir = scope == SkillScope.Project ? projectRootPath : null;
        var (code, _, stderr) = await RunAsync(args, workDir, ct);
        if (code != 0)
            log.LogWarning("skills remove {Skill} ({Scope}) код {Code}: {Err}", skill, scope, code, Trunc(stderr));
        return code == 0;
    }

    // --- Запуск процесса ---

    // Собирает команду запуска CLI кросс-платформенно. Windows: npx — это npx.cmd (batch),
    // запускаем через cmd.exe /c. Linux (контейнер): npx напрямую. stdin CLI не нужен —
    // закрываем сразу, чтобы неинтерактивный режим не ждал ввода.
    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> skillArgs, string? workDir, CancellationToken ct)
    {
        var launcher = launchers.Local; // среда владельца подключится этапом песочницы
        var args = new List<string>();
        string fileName;

        // Префикс запуска: cmd /c npx <pkg> | npx <pkg> | прямая команда.
        // Обвязка выбирается по ОС ЦЕЛЕВОЙ среды (в песочнице npx запускается напрямую)
        if (DirectCommand is { } direct)
        {
            fileName = launcher.TargetIsWindows ? "cmd.exe" : direct;
            if (launcher.TargetIsWindows) { args.Add("/c"); args.Add(direct); }
        }
        else if (launcher.TargetIsWindows)
        {
            fileName = "cmd.exe";
            args.AddRange(["/c", "npx", "-y", NpxPackage]);
        }
        else
        {
            fileName = "npx";
            args.AddRange(["-y", NpxPackage]);
        }

        args.AddRange(skillArgs);

        var turnId = Guid.NewGuid().ToString("N")[..12];
        using var process = launcher.Start(new Execution.ProcessSpec
        {
            FileName = fileName,
            Args = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(workDir)
                ? EnsureOneShotDir(launcher)
                : workDir,
            // Телеметрия off + неинтерактивный режим
            Env = new Dictionary<string, string>
            {
                ["SKILLS_TELEMETRY_DISABLED"] = "1",
                ["CI"] = "1",
            },
            StdioEncoding = new UTF8Encoding(false),
            TurnId = turnId,
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            launcher.Kill(process, turnId);
            throw new InvalidOperationException("npx skills не ответил за отведённое время");
        }
    }

    private static string EnsureOneShotDir(Execution.IProcessLauncher launcher)
    {
        var dir = Path.Combine(launcher.HostTempDir, "skills-cli");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- Парсинг вывода ---

    // Результат find: строки вида «owner/repo@skill  1.6K installs», под ними URL со «└».
    internal static IReadOnlyList<RegistrySkill> ParseFind(string text)
    {
        var result = new List<RegistrySkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var m = FindLineRegex().Match(line);
            if (!m.Success) continue;
            var source = m.Groups["src"].Value;
            var skill = m.Groups["skill"].Value;
            var key = source + "@" + skill;
            if (!seen.Add(key)) continue;
            result.Add(new RegistrySkill(
                source, skill,
                Description: null,
                Installs: ParseInstalls(m.Groups["installs"].Value),
                Url: $"https://skills.sh/{source}/{skill}"));
        }
        return result;
    }

    // Результат «add -l»: категории (заголовки) + пары «slug \n описание». Имя навыка —
    // строка-slug (^[a-z0-9-]+$); следующая непустая строка большего отступа — описание.
    internal static IReadOnlyList<RegistrySkill> ParseList(string text, string source)
    {
        var result = new List<RegistrySkill>();
        RegistrySkill? pending = null;
        var descParts = new List<string>();

        void Flush()
        {
            if (pending is null) return;
            var desc = string.Join(" ", descParts).Trim();
            result.Add(pending with { Description = desc.Length > 0 ? desc : null });
            pending = null;
            descParts.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            // Убираем рамку «│» и служебные символы прогресса, берём чистый текст
            var line = raw.Replace('│', ' ').Replace('\r', ' ');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (SkillSlugRegex().IsMatch(trimmed))
            {
                Flush();
                pending = new RegistrySkill(source, trimmed, null, null, $"https://skills.sh/{source}/{trimmed}");
            }
            else if (pending is not null)
            {
                // Строки-описания идут после имени с бо́льшим отступом
                descParts.Add(trimmed);
            }
            // Иначе — заголовок категории/служебная строка до первого навыка: пропускаем
        }
        Flush();
        return result;
    }

    // «1.6K» → 1600, «135» → 135, «2.3M» → 2300000. Не распознано — null.
    internal static int? ParseInstalls(string s)
    {
        s = s.Trim().Replace(",", "");
        if (s.Length == 0) return null;
        var mult = 1.0;
        var last = char.ToUpperInvariant(s[^1]);
        if (last is 'K' or 'M')
        {
            mult = last == 'K' ? 1_000 : 1_000_000;
            s = s[..^1];
        }
        return double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? (int)Math.Round(n * mult)
            : null;
    }

    private static string StripAnsi(string s) => AnsiRegex().Replace(s, "");
    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s.Trim();

    // ANSI/VT: CSI (ESC[ … ), OSC (ESC] … BEL|ST) и одиночные ESC-последовательности.
    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))")]
    private static partial Regex AnsiRegex();

    // Строка результата find: «owner/repo@skill   1.6K installs»
    [GeneratedRegex(@"^(?<src>[A-Za-z0-9._-]+/[A-Za-z0-9._-]+)@(?<skill>[A-Za-z0-9._-]+)\s+(?<installs>[\d.,]+[KMkm]?)\s+installs?\b")]
    private static partial Regex FindLineRegex();

    // Имя навыка — slug: строчные латиница/цифры/дефис (без пробелов и заглавных)
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SkillSlugRegex();
}
