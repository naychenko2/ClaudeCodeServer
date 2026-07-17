using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Prompts;

// Роутинг советнических ролей oh-my-claudecode на персон-сабагентов. Скиллы плагина
// жёстко зовут Task(subagent_type="oh-my-claudecode:<тип>") — перехватить вызов на
// сервере нельзя (он происходит внутри CLI), поэтому при детекте команды плагина к ходу
// дописывается правило «советнические типы замещай персонами». Исполнительские типы
// (executor, git-master, qa-tester, test-engineer, verifier и т.п.) не замещаются:
// персоны-сабагенты работают только на чтение (PersonaConsultantToolset).
public static class OmcPersonaRouting
{
    public const string CommandPrefix = "/oh-my-claudecode:";

    // Специальность персоны → замещаемые советнические типы агентов плагина
    private static readonly Dictionary<PersonaSpecialty, string[]> SpecialtyToAgents = new()
    {
        [PersonaSpecialty.Analyst] = ["analyst", "scientist"],
        [PersonaSpecialty.Planner] = ["planner"],
        [PersonaSpecialty.Reviewer] = ["critic", "code-reviewer", "security-reviewer"],
        [PersonaSpecialty.Consultant] = ["architect"],
        [PersonaSpecialty.Librarian] = ["document-specialist"],
        [PersonaSpecialty.Secretary] = ["writer"],
        [PersonaSpecialty.Designer] = ["designer"],
    };

    // Исполнительские типы (правят файлы/запускают команды) — доступны только персонам
    // с write-доступом в сабагентах (PersonaConsultantToolset.IsExecutor)
    private static readonly Dictionary<PersonaSpecialty, string[]> ExecutorSpecialtyToAgents = new()
    {
        [PersonaSpecialty.Executor] = ["executor", "debugger", "git-master"],
        [PersonaSpecialty.Tester] = ["qa-tester", "test-engineer", "verifier"],
    };

    // Фолбэк для персон без заполненной специальности (созданы до появления поля):
    // распознаём её по отображаемому названию роли
    private static readonly (string Keyword, PersonaSpecialty Specialty)[] RoleFallback =
    [
        ("аналитик", PersonaSpecialty.Analyst),
        ("планировщик", PersonaSpecialty.Planner),
        ("мастер", PersonaSpecialty.Executor),
        ("исполнитель", PersonaSpecialty.Executor),
        ("разработчик", PersonaSpecialty.Executor),
        ("ревьюер", PersonaSpecialty.Reviewer),
        ("критик", PersonaSpecialty.Reviewer),
        ("консультант", PersonaSpecialty.Consultant),
        ("архитектор", PersonaSpecialty.Consultant),
        ("библиотекарь", PersonaSpecialty.Librarian),
        ("секретарь", PersonaSpecialty.Secretary),
        ("дизайнер", PersonaSpecialty.Designer),
        ("тестировщик", PersonaSpecialty.Tester),
    ];

    // Магслова ultrawork/ulw включают тот же режим плагина (keyword-detector OMC) —
    // роутинг персон должен срабатывать и на них. Паттерн зеркалит фронтовый
    // lib/ultrawork.ts: границы слова, чтобы «ulw» внутри других слов не считался.
    private static readonly Regex MagicWordRe = new(@"(?<![\p{L}\p{N}])(ultrawork|ulw)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool MentionsPluginCommand(string text) =>
        text.Contains(CommandPrefix, StringComparison.OrdinalIgnoreCase)
        || MagicWordRe.IsMatch(text);

    // executorCapable — у персоны есть write-доступ в сабагентах: к советническим типам
    // добавляются исполнительские её специальности
    public static string[] AgentTypesFor(PersonaSpecialty specialty, bool executorCapable = false)
    {
        var advisory = SpecialtyToAgents.GetValueOrDefault(specialty, []);
        if (!executorCapable) return advisory;
        var executor = ExecutorSpecialtyToAgents.GetValueOrDefault(specialty, []);
        return executor.Length == 0 ? advisory : [.. advisory, .. executor];
    }

    // Эффективная специальность: явная, а при None — угаданная по названию роли
    public static PersonaSpecialty EffectiveSpecialty(Persona persona)
    {
        if (persona.Specialty != PersonaSpecialty.None) return persona.Specialty;
        var role = persona.Role?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(role)) return PersonaSpecialty.None;
        foreach (var (keyword, specialty) in RoleFallback)
            if (role.Contains(keyword))
                return specialty;
        return PersonaSpecialty.None;
    }

    // Блок-правило для текста хода: таблица «стандартный тип → персона».
    // personas — сабагенты этого хода (группа Subagents из SplitConsultants).
    // null — замещать некем (ни у одной персоны нет подходящей специальности).
    public static string? BuildHint(IReadOnlyList<Persona> personas)
    {
        // Детерминизм при дублях специальности: каждый тип плагина закрепляется за ОДНОЙ
        // персоной (первой по Handle ordinal), иначе в таблице две строки на один тип и
        // модель выбирает произвольно. Оставшиеся кандидаты — одной строкой «резерв».
        var lines = new List<string>();
        var reserve = new List<string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in personas.OrderBy(x => x.Handle, StringComparer.Ordinal))
        {
            var executor = PersonaConsultantToolset.IsExecutor(p);
            var all = AgentTypesFor(EffectiveSpecialty(p), executor);
            if (all.Length == 0) continue;
            var types = new List<string>();
            foreach (var t in all)
                if (taken.Add(t))
                    types.Add(t);
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            if (types.Count == 0)
            {
                reserve.Add($"\"{p.Handle}\" — {title}");
                continue;
            }
            var marker = executor ? " (исполнитель: может править файлы и запускать команды)" : "";
            lines.Add($"- {string.Join(", ", types.Select(t => "oh-my-claudecode:" + t))} → \"{p.Handle}\" — {title}{marker}");
        }
        if (lines.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== ПОДБОР СУБАГЕНТОВ: ПЕРСОНЫ ВМЕСТО СТАНДАРТНЫХ ===");
        sb.AppendLine("У пользователя есть персоны — сабагенты со своим характером и памятью. Перед вызовом " +
            "Task с subagent_type=\"oh-my-claudecode:<тип>\" сверься с таблицей ниже: если тип в ней есть — " +
            "вызови персону (subagent_type в кавычках из таблицы) вместо стандартного агента; задание в prompt " +
            "формулируй самодостаточно — персона не видит этот разговор. Стандартный тип oh-my-claudecode:* " +
            "используй, только если типа нет в таблице. Персонам без пометки «исполнитель» нельзя поручать " +
            "запись файлов и запуск команд — они консультанты, работают только на чтение.");
        sb.AppendLine("Соответствия:");
        foreach (var l in lines) sb.AppendLine(l);
        if (reserve.Count > 0)
            sb.AppendLine("Резерв (та же специальность, зови только по прямой просьбе пользователя): "
                + string.Join(", ", reserve));
        return sb.ToString().TrimEnd();
    }
}
