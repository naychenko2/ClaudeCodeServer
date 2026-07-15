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

    // Специальность персоны → замещаемые советнические типы агентов плагина.
    // Tester сознательно не мапится: qa-tester/test-engineer/verifier запускают команды.
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

    // Фолбэк для персон без заполненной специальности (созданы до появления поля):
    // распознаём её по отображаемому названию роли
    private static readonly (string Keyword, PersonaSpecialty Specialty)[] RoleFallback =
    [
        ("аналитик", PersonaSpecialty.Analyst),
        ("планировщик", PersonaSpecialty.Planner),
        ("ревьюер", PersonaSpecialty.Reviewer),
        ("критик", PersonaSpecialty.Reviewer),
        ("консультант", PersonaSpecialty.Consultant),
        ("архитектор", PersonaSpecialty.Consultant),
        ("библиотекарь", PersonaSpecialty.Librarian),
        ("секретарь", PersonaSpecialty.Secretary),
        ("дизайнер", PersonaSpecialty.Designer),
        ("тестировщик", PersonaSpecialty.Tester),
    ];

    public static bool MentionsPluginCommand(string text) =>
        text.Contains(CommandPrefix, StringComparison.OrdinalIgnoreCase);

    public static string[] AgentTypesFor(PersonaSpecialty specialty) =>
        SpecialtyToAgents.GetValueOrDefault(specialty, []);

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
        var lines = new List<string>();
        foreach (var p in personas)
        {
            var types = AgentTypesFor(EffectiveSpecialty(p));
            if (types.Length == 0) continue;
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            lines.Add($"- {string.Join(", ", types.Select(t => "oh-my-claudecode:" + t))} → \"{p.Handle}\" — {title}");
        }
        if (lines.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== ПОДБОР СУБАГЕНТОВ: ПЕРСОНЫ ВМЕСТО СТАНДАРТНЫХ ===");
        sb.AppendLine("У пользователя есть персоны — сабагенты со своим характером и памятью. Перед вызовом " +
            "Task с subagent_type=\"oh-my-claudecode:<тип>\" сверься с таблицей ниже: если тип в ней есть — " +
            "вызови персону (subagent_type в кавычках из таблицы) вместо стандартного агента; вопрос в prompt " +
            "формулируй самодостаточно — персона не видит этот разговор. Стандартный тип oh-my-claudecode:* " +
            "используй, только если типа нет в таблице или подзадача требует записи файлов/запуска команд — " +
            "персоны-консультанты работают только на чтение.");
        sb.AppendLine("Соответствия:");
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }
}
