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
public static class SpecialtyCatalog
{
    // Ключ-маркер «любая специальность» в правилах пресетов (SpecialtySettingsStore)
    public const string AnySpecialtyKey = "any";

    public sealed record Entry(
        PersonaSpecialty Specialty,
        string Key,
        string Label,
        bool ExecutorFamily,
        SpecialtyTemplate? DefaultTemplate);

    // Универсальный исполнитель переименован в подписи (данные не мигрировались),
    // профильные добавлены рядом; их подписи утверждены человеком.
    private static readonly SpecialtyTemplate ExecutorDefaultTemplate =
        new(PersonaAccess.Full, Tools: null, DisallowedTools: null);

    public static readonly IReadOnlyList<Entry> All =
    [
        new(PersonaSpecialty.None, KeyOf(PersonaSpecialty.None), "Не задана", false, null),
        new(PersonaSpecialty.Analyst, KeyOf(PersonaSpecialty.Analyst), "Аналитик", false, null),
        new(PersonaSpecialty.Planner, KeyOf(PersonaSpecialty.Planner), "Планировщик", false, null),
        new(PersonaSpecialty.Reviewer, KeyOf(PersonaSpecialty.Reviewer), "Ревьюер", false, null),
        new(PersonaSpecialty.Executor, KeyOf(PersonaSpecialty.Executor), "Исполнитель (универсальный)", true, ExecutorDefaultTemplate),
        new(PersonaSpecialty.Secretary, KeyOf(PersonaSpecialty.Secretary), "Секретарь", false, null),
        new(PersonaSpecialty.Coordinator, KeyOf(PersonaSpecialty.Coordinator), "Координатор", false, null),
        new(PersonaSpecialty.Mentor, KeyOf(PersonaSpecialty.Mentor), "Наставник", false, null),
        new(PersonaSpecialty.Designer, KeyOf(PersonaSpecialty.Designer), "Дизайнер", false, null),
        new(PersonaSpecialty.Consultant, KeyOf(PersonaSpecialty.Consultant), "Консультант", false, null),
        new(PersonaSpecialty.Librarian, KeyOf(PersonaSpecialty.Librarian), "Библиотекарь", false, null),
        new(PersonaSpecialty.Tester, KeyOf(PersonaSpecialty.Tester), "Тестировщик", false, null),
        new(PersonaSpecialty.BackendExecutor, KeyOf(PersonaSpecialty.BackendExecutor), "Исполнитель (бэкенд)", true, ExecutorDefaultTemplate),
        new(PersonaSpecialty.FrontendExecutor, KeyOf(PersonaSpecialty.FrontendExecutor), "Исполнитель (фронтенд)", true, ExecutorDefaultTemplate),
        new(PersonaSpecialty.DevopsExecutor, KeyOf(PersonaSpecialty.DevopsExecutor), "Исполнитель (DevOps)", true, ExecutorDefaultTemplate),
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
}
