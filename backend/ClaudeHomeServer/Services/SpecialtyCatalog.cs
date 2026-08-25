using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Шаблон прав и инструментов персоны по специальности: что подставляется в поля
// Access/Tools/DisallowedTools при выборе специальности.
// После подстановки поля живут своей жизнью — источник правды у персоны, шаблоны
// ничего не ограничивают (жёсткого потолка нет).
// Tools: null — все возможности (tasks+notes+web), как у Persona.Tools=null.
// DisallowedTools имеет смысл только при Access == Custom.
public sealed record SpecialtyTemplate(
    PersonaAccess Access,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<string>? DisallowedTools);

// Каталог специальностей персоны: машинный ключ (wire-значение), подпись для UI,
// семейство исполнителя и дефолтный шаблон прав. Единственный источник подписей —
// все потребители (API, планировщик команды) берут их отсюда.
// Дефолты СЕКЦИЙ ПРОМПТОВ и типовых профилей умений живут отдельно:
// SpecialtyPromptPresets (состав каталога v5, план «Секции промптов»).
public static class SpecialtyCatalog
{
    // Ключ-маркер «любая специальность» в правилах пресетов (SpecialtySettingsStore)
    public const string AnySpecialtyKey = "any";

    // Icon и Color — дефолты кода, ТОЛЬКО для показа: персонализация их не
    // переопределяет (в слое настроек полей значка и цвета нет). Значок — имя для
    // отрисовки на фронте, цвет — ключ палитры аватаров (AGENT_COLORS фронта).
    // Значения утверждены человеком, источник — макет персонализированных
    // специальностей (каталог ролей в docs/mockups/personas-specialties/index.html).
    public sealed record Entry(
        PersonaSpecialty Specialty,
        string Key,
        string Label,
        string Description,
        bool ExecutorFamily,
        SpecialtyTemplate? DefaultTemplate,
        string Icon = "",
        string Color = "");

    // Универсальный исполнитель переименован в подписи (данные не мигрировались),
    // профильные добавлены рядом; их подписи утверждены человеком.
    private static readonly SpecialtyTemplate ExecutorDefaultTemplate =
        new(PersonaAccess.Full, Tools: null, DisallowedTools: null);

    // Описания ролей отдаются в каталог (SpecialtiesController.List) для карточек UI
    // панели «Инструкции для роли» (этап 4 плана «Секции промптов»). Тексты утверждены
    // человеком, источник — макет v4/прототип docs/mockups/specialty-prompt-sections.html.
    public static readonly IReadOnlyList<Entry> All =
    [
        new(PersonaSpecialty.None, KeyOf(PersonaSpecialty.None), "Не задана", "", false, null),
        new(PersonaSpecialty.Analyst, KeyOf(PersonaSpecialty.Analyst), "Аналитик",
            "Данные, выводы и риски — без домыслов и без переоценки найденного", false, null,
            Icon: "chart", Color: "blue"),
        new(PersonaSpecialty.Planner, KeyOf(PersonaSpecialty.Planner), "Планировщик",
            "Превращает задачу в план по шагам: что делаем, в каком порядке, кто и как проверим", false, null,
            Icon: "checks", Color: "purple"),
        new(PersonaSpecialty.Reviewer, KeyOf(PersonaSpecialty.Reviewer), "Ревьюер",
            "Находки по severity, соглашения проекта, сценарии отказа — спорит с решением, а не с оформлением", false, null,
            Icon: "shield", Color: "red"),
        new(PersonaSpecialty.Executor, KeyOf(PersonaSpecialty.Executor), "Исполнитель (универсальный)",
            "Универсальный исполнитель: доводит задачу до конца с минимальным диффом", true, ExecutorDefaultTemplate,
            Icon: "hammer", Color: "brown"),
        new(PersonaSpecialty.Secretary, KeyOf(PersonaSpecialty.Secretary), "Секретарь",
            "Задачи и заметки: зафиксировать, напомнить, найти — коротко и ничего не терять", false, null,
            Icon: "notebook", Color: "green"),
        new(PersonaSpecialty.Coordinator, KeyOf(PersonaSpecialty.Coordinator), "Координатор",
            "Распределяет работу по силам и зоне персон, эскалирует блокеры", false, null,
            Icon: "share", Color: "cyan"),
        new(PersonaSpecialty.Mentor, KeyOf(PersonaSpecialty.Mentor), "Наставник",
            "Сначала вопросы, потом советы: учит через понимание, не решает за ученика", false, null,
            Icon: "cap", Color: "yellow"),
        new(PersonaSpecialty.Designer, KeyOf(PersonaSpecialty.Designer), "Дизайнер",
            "Макеты в docs/mockups, дизайн-система проекта, обе темы и мобильная раскладка", false, null,
            Icon: "palette", Color: "orange"),
        new(PersonaSpecialty.Consultant, KeyOf(PersonaSpecialty.Consultant), "Консультант",
            "Отвечает на заданный вопрос с альтернативами и компромиссами, признаёт границы знания", false, null,
            Icon: "bulb", Color: "yellow"),
        new(PersonaSpecialty.Librarian, KeyOf(PersonaSpecialty.Librarian), "Библиотекарь",
            "Отвечает про библиотеки и чужой код доказательствами: документ и пермалинк", false, null,
            Icon: "book", Color: "brown"),
        new(PersonaSpecialty.Tester, KeyOf(PersonaSpecialty.Tester), "Тестировщик",
            "Воспроизводимость прежде вердикта: шаги, края, честный отчёт о проверке", false, null,
            Icon: "flask", Color: "green"),
        new(PersonaSpecialty.BackendExecutor, KeyOf(PersonaSpecialty.BackendExecutor), "Исполнитель (бэкенд)",
            "Серверный исполнитель: инварианты слоя, стиль соседнего кода, сборка и тесты", true, ExecutorDefaultTemplate,
            Icon: "server", Color: "blue"),
        new(PersonaSpecialty.FrontendExecutor, KeyOf(PersonaSpecialty.FrontendExecutor), "Исполнитель (фронтенд)",
            "Фронтовый исполнитель: дизайн-система, мобильная ширина, ui-kit", true, ExecutorDefaultTemplate,
            Icon: "monitor", Color: "pink"),
        new(PersonaSpecialty.DevopsExecutor, KeyOf(PersonaSpecialty.DevopsExecutor), "Исполнитель (DevOps)",
            "Исполнитель сборки и окружений: воспроизводимость, откатываемость, секреты вне git", true, ExecutorDefaultTemplate,
            Icon: "box", Color: "red"),
    ];

    private static readonly Dictionary<PersonaSpecialty, Entry> BySpecialty =
        All.ToDictionary(e => e.Specialty);

    private static readonly Dictionary<string, Entry> ByKey =
        All.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    // Машинный ключ специальности — camelCase-имя enum (совпадает с wire-значением
    // JSON-конвертера PersonaManager): BackendExecutor → "backendExecutor"
    public static string KeyOf(PersonaSpecialty specialty) =>
        JsonNamingPolicy.CamelCase.ConvertName(specialty.ToString());

    public static Entry Get(PersonaSpecialty specialty) => BySpecialty[specialty];

    public static string Label(PersonaSpecialty specialty) => Get(specialty).Label;

    public static bool TryGetByKey(string? key, out Entry entry)
    {
        entry = null!;
        return !string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key.Trim(), out entry!);
    }

    // Семейство исполнителя: универсальный + профильные. Единая точка для каскадов —
    // write-набор сабагента (PersonaConsultantToolset), роутинг oh-my-claudecode
    // (OmcPersonaRouting), git-секция (PersonaBindingsService). Tester сюда НЕ входит:
    // у него свой набор секций, исполнительские права он получает отдельным условием.
    public static bool IsExecutorKind(PersonaSpecialty specialty) => Get(specialty).ExecutorFamily;

    // Write-доступ к заметкам у сабагента: секретарь/координатор/планировщик + аналитик/библиотекарь
    public static bool CanWriteNotes(PersonaSpecialty specialty) =>
        specialty is PersonaSpecialty.Secretary
         or PersonaSpecialty.Coordinator
         or PersonaSpecialty.Planner
         or PersonaSpecialty.Analyst
         or PersonaSpecialty.Librarian;

    // Write-доступ к задачам у сабагента: секретарь/координатор/планировщик
    public static bool CanWriteTasks(PersonaSpecialty specialty) =>
        specialty is PersonaSpecialty.Secretary
         or PersonaSpecialty.Coordinator
         or PersonaSpecialty.Planner;
}
