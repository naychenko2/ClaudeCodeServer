using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Каталог секций промптов специальностей (состав каталога v5 согласован с пользователем
// 2026-08-23, план «Секции промптов»): состав секций задаёт СИСТЕМА — пользователь
// включает/выключает и правит текст (SpecialtyTemplateSettings.PromptSections), а
// дефолты включённости и типовые тексты живут здесь. Типовой профиль умений роли —
// тоже дефолт кода: при создании персоны он материализуется в её личные привязки
// (модель «копия при создании», PersonasController.MaterializeDefaultBindingsAsync);
// админ/владелец меняет профиль через настройки специальностей.
//
// Стиль текстов — сценарные правила по образцу CodeGraphPromptProvider: «ситуация →
// какой инструмент позвать». Секция «история» обязана нести разделение труда: структуру
// кода смотрит codegraph_*, историю решений — dossier_* (контракт плана; сторож-тест).
public static class SpecialtyPromptPresets
{
    // Жёсткий лимит текста секции: общий для валидации стора и счётчика UI (1024 в спеке
    // макета); все типовые тексты каталога обязаны в него влезать (тест).
    public const int SectionTextLimit = 1024;

    // Потолок записей типового профиля умений роли (защита от мусора в настройках)
    public const int MaxDefaultBindings = 10;

    // Потолок условия «когда применять» у типового умения (условия короткие)
    public const int MaxConditionLength = 300;

    public sealed record SectionMeta(string Id, string Label, string Description);

    // Состав каталога (v5): «Стиль ответов» сюда НЕ входит — он перекрывается контрактом
    // персоны (слой персоны клеится в промпт после секций специальности).
    public static readonly IReadOnlyList<SectionMeta> Sections =
    [
        new("history", "История решений",
            "Сценарии «когда и как использовать досье» (dossier_lookup / dossier_get)."),
        new("codeGraph", "Навигация по коду",
            "Сценарии навигации: codegraph (типы и связность), LSP (символ и позиция), Grep (текст)."),
        new("processes", "Процессы роли (DoD)",
            "Что сделать перед сдачей результата (сборка/тесты/lint) и куда его складывать."),
        new("roleRules", "Правила роли",
            "Профессиональные рамки специальности, не привязанные к инструментам."),
    ];

    private static readonly Dictionary<string, SectionMeta> SectionsById =
        Sections.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    public static bool TryGetSection(string? id, out SectionMeta meta)
    {
        meta = null!;
        return !string.IsNullOrWhiteSpace(id) && SectionsById.TryGetValue(id.Trim(), out meta!);
    }

    // --- Дефолты включённости (таблица плана, согласована с пользователем 2026-08-23) ---
    // история — 9 ролей; граф кода — 6; процессы (DoD) — 9; правила роли — все 14.

    private static readonly HashSet<PersonaSpecialty> HistoryDefaultOn =
    [
        PersonaSpecialty.Analyst, PersonaSpecialty.Planner, PersonaSpecialty.Reviewer,
        PersonaSpecialty.Tester, PersonaSpecialty.Executor, PersonaSpecialty.BackendExecutor,
        PersonaSpecialty.FrontendExecutor, PersonaSpecialty.DevopsExecutor,
        PersonaSpecialty.Consultant,
    ];

    private static readonly HashSet<PersonaSpecialty> CodeGraphDefaultOn =
    [
        PersonaSpecialty.Planner, PersonaSpecialty.Reviewer, PersonaSpecialty.Tester,
        PersonaSpecialty.BackendExecutor, PersonaSpecialty.FrontendExecutor,
        PersonaSpecialty.DevopsExecutor,
    ];

    private static readonly HashSet<PersonaSpecialty> ProcessesDefaultOn =
    [
        PersonaSpecialty.Executor, PersonaSpecialty.BackendExecutor,
        PersonaSpecialty.FrontendExecutor, PersonaSpecialty.DevopsExecutor,
        PersonaSpecialty.Tester, PersonaSpecialty.Planner, PersonaSpecialty.Reviewer,
        PersonaSpecialty.Secretary, PersonaSpecialty.Librarian,
    ];

    // «Правила роли» включены у всех специальностей (кроме None — у него секций нет вовсе)

    public static bool DefaultEnabled(string sectionId, PersonaSpecialty specialty)
    {
        if (specialty == PersonaSpecialty.None) return false;
        return sectionId switch
        {
            "history" => HistoryDefaultOn.Contains(specialty),
            "codeGraph" => CodeGraphDefaultOn.Contains(specialty),
            "processes" => ProcessesDefaultOn.Contains(specialty),
            "roleRules" => true,
            _ => false,
        };
    }

    // --- Типовые тексты секций (пресет «Типовой текст…» в UI) ---

    public static string DefaultText(string sectionId, PersonaSpecialty specialty)
    {
        if (specialty == PersonaSpecialty.None) return "";
        return sectionId switch
        {
            "history" => HistoryTexts.GetValueOrDefault(specialty, HistoryFallback),
            "codeGraph" => CodeGraphTexts.GetValueOrDefault(specialty, CodeGraphFallback),
            "processes" => ProcessesTexts.GetValueOrDefault(specialty, ProcessesFallback),
            "roleRules" => RoleRulesTexts[specialty],
            _ => "",
        };
    }

    // Сквозная строка секции «история»: разделение труда с графом кода (контракт плана).
    private const string HistoryRoster =
        "Структуру кода смотри codegraph_*, историю решений — dossier_*.";

    private const string HistoryFallback =
        "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
        "Когда звать: обсуждая прошлые изменения — dossier_lookup по файлу или запросу; " +
        "id паспорта в подсказке — dossier_get читает его целиком.\n" +
        "Не пересказывай отвергнутые варианты как действующие.";

    private static readonly Dictionary<PersonaSpecialty, string> HistoryTexts = new()
    {
        [PersonaSpecialty.Analyst] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• перед формулировкой требований или гипотезы — dossier_lookup по файлам темы: что уже обсуждали и решили;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "Не предлагай заново выводы, которые уже зафиксированы, и отвергнутые альтернативы.",
        [PersonaSpecialty.Planner] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• строя план по существующему коду — dossier_lookup по файлам, которых план касается: там инварианты и известные грабли;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "План, противоречащий зафиксированному решению, обязан это явно проговорить.",
        [PersonaSpecialty.Reviewer] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• ревьюя изменения — dossier_lookup по тронутым файлам: замечания против зафиксированных решений — главный источник шума в ревью;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "Не предлагай отвергнутые альтернативы как улучшения.",
        [PersonaSpecialty.Tester] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• проверяя фичу — dossier_lookup по её файлам: что намеренно отвергли (это не баг) и какие грабли уже ловили;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "В отчёте отличай «нарушено решение» от «решение было другим».",
        [PersonaSpecialty.Executor] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• перед правкой файла — dossier_lookup по его пути или символу: инварианты, которые нельзя нарушить, и отвергнутые подходы;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "Не приноси обратно отвергнутые варианты.",
        [PersonaSpecialty.BackendExecutor] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• перед правкой серверного кода — dossier_lookup по файлу: инварианты слоя и отвергнутые подходы;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "Не приноси обратно отвергнутые варианты.",
        [PersonaSpecialty.FrontendExecutor] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• перед правкой компонента или модуля фронта — dossier_lookup по файлу: договорённости и отвергнутые подходы;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "Не приноси обратно отвергнутые варианты.",
        [PersonaSpecialty.DevopsExecutor] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• перед правкой сборки, контейнеров или конфигов — dossier_lookup по файлу: почему конфиг устроен так;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "Не приноси обратно отвергнутые варианты.",
        [PersonaSpecialty.Consultant] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать:\n" +
            "• советуя по коду или архитектуре проекта — dossier_lookup по файлам темы: уже принятые решения и их причины;\n" +
            "• id паспорта в подсказке — dossier_get читает его целиком.\n" +
            "Совет, противоречащий зафиксированному решению, отмечай явно, а не молча.",
        [PersonaSpecialty.Secretary] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать: обсуждая прошлые изменения проекта — dossier_lookup по упомянутым файлам или запросу; " +
            "id паспорта в подсказке — dossier_get.\n" +
            "Не пересказывай отвергнутые варианты как действующие.",
        [PersonaSpecialty.Coordinator] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать: распределяя работу по коду — dossier_lookup по файлам задачи: где уже копали и что решили; " +
            "id паспорта в подсказке — dossier_get.\n" +
            "Не поручай повторно отвергнутое.",
        [PersonaSpecialty.Mentor] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать: объясняя устройство кода — dossier_lookup по файлу: живые примеры решений и их причин; " +
            "id паспорта в подсказке — dossier_get.\n" +
            "Отвергнутые альтернативы — лучший учебный материал: объясняй, почему не подошли.",
        [PersonaSpecialty.Designer] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать: перед правкой UI — dossier_lookup по файлу компонента: договорённости и их причины; " +
            "id паспорта в подсказке — dossier_get.\n" +
            "Не предлагай решения, от которых команда уже отказалась.",
        [PersonaSpecialty.Librarian] =
            "История решений проекта — зачем меняли код и что при этом отвергли. " + HistoryRoster + "\n" +
            "Когда звать: ища первоисточник решения — dossier_lookup по файлу или запросу; id паспорта — dossier_get.\n" +
            "Ссылайся на паспорт, а не на пересказ.",
    };

    // Секция codeGraph = «Навигация по коду» (ADR-011 шаг 3): развёрнутые сценарии трёх
    // уровней — codegraph (типы и связность) + LSP (символ и позиция) + Grep (текст).
    // Строки уровней LSP берутся из CodeNavigationPrompts (единая точка истины), роль
    // добавляет только языковую привязку и собственный акцент. Глобальный короткий блок
    // ходов страхует покрытие: секция без специальности не едет вовсе.

    private const string CodeGraphFallback =
        "Навигация по коду — три уровня, не взаимозаменяемы: codegraph (типы и связность), LSP (символ и позиция), Grep (текст).\n" +
        "Когда звать: «где объявлен X» — codegraph_find; «что связано с X» — codegraph_neighbors; обзор подсистемы — codegraph_hubs. " +
        Prompts.CodeNavigationPrompts.PresetLspLine + "\n" +
        "Структуру кода уточняй инструментами, а не пересказом из памяти. " +
        "Grep — только для текстовых вхождений и файлов вне графа (конфиги, .md, разметка).";

    private static readonly Dictionary<PersonaSpecialty, string> CodeGraphTexts = new()
    {
        [PersonaSpecialty.Executor] =
            "Навигация по коду — три уровня, не взаимозаменяемы; текстовый поиск по символам промахивается.\n" +
            "Когда звать:\n" +
            "• «где объявлен X» — codegraph_find: файл, строка и вид типа, без шума совпадений;\n" +
            "• «что сломается, если правлю X» — codegraph_neighbors: входящие Calls/Implements/References;\n" +
            "• «с чего начать в незнакомом модуле» — codegraph_hubs;\n" +
            Prompts.CodeNavigationPrompts.PresetLspLine + "\n" +
            Prompts.CodeNavigationPrompts.PresetRenameMoment + " " +
            "Grep — только для текстовых вхождений и файлов вне графа (конфиги, .md, разметка).",
        [PersonaSpecialty.BackendExecutor] =
            "Навигация по серверному коду (.cs): codegraph — типы и связности, LSP — символ и позиция.\n" +
            "Когда звать:\n" +
            "• «где объявлен X» — codegraph_find: файл, строка и вид типа;\n" +
            "• «что сломается, если правлю X» — codegraph_neighbors: входящие Calls/Implements/References;\n" +
            "• незнакомая подсистема — codegraph_hubs: точки входа;\n" +
            Prompts.CodeNavigationPrompts.PresetLspLine + " Методы и поля .cs — LSP, не текстовый поиск.\n" +
            Prompts.CodeNavigationPrompts.PresetRenameMoment + " " +
            "Grep — только для текстовых вхождений и файлов вне графа.",
        [PersonaSpecialty.FrontendExecutor] =
            "Навигация по коду фронта (.tsx/.ts): codegraph — типы и связности, LSP — символ и позиция.\n" +
            "Когда звать:\n" +
            "• «где объявлен X» — codegraph_find: файл, строка и вид типа;\n" +
            "• «что сломается, если правлю X» — codegraph_neighbors: входящие Calls/Implements/References;\n" +
            "• незнакомый модуль — codegraph_hubs: точки входа;\n" +
            Prompts.CodeNavigationPrompts.PresetLspLine + " Компоненты и хуки .tsx — LSP, не текстовый поиск.\n" +
            Prompts.CodeNavigationPrompts.PresetRenameMoment + " " +
            "Grep — только для текстовых вхождений и файлов вне графа.",
        [PersonaSpecialty.DevopsExecutor] =
            "Навигация по коду — три уровня, не взаимозаменяемы; текстовый поиск по символам промахивается.\n" +
            "Когда звать:\n" +
            "• «где объявлен X» — codegraph_find: файл, строка и вид типа;\n" +
            "• «что сломается, если правлю X» — codegraph_neighbors: входящие связи;\n" +
            "• незнакомая подсистема — codegraph_hubs: точки входа;\n" +
            Prompts.CodeNavigationPrompts.PresetLspLine + "\n" +
            "Grep — только для текстовых вхождений и файлов вне графа.",
        [PersonaSpecialty.Reviewer] =
            "Навигация по коду — три уровня: типы (codegraph), символ (LSP), текст (Grep).\n" +
            "Когда звать:\n" +
            "• оценивая влияние изменений — codegraph_neighbors по тронутым типам: кто ещё зависит;\n" +
            "• «где объявлен X» — codegraph_find: точное место и вид типа;\n" +
            "• незнакомая подсистема — codegraph_hubs;\n" +
            Prompts.CodeNavigationPrompts.PresetLspLine + "\n" +
            "Находку «правка заденет N мест» подтверждай инструментом, а не прикидкой.",
        [PersonaSpecialty.Tester] =
            "Навигация по коду — три уровня: типы (codegraph), символ (LSP), текст (Grep).\n" +
            "Когда звать:\n" +
            "• «что затронет эта правка» — codegraph_neighbors: границы влияния для объёма проверки;\n" +
            "• «где объявлен X» — codegraph_find;\n" +
            "• незнакомая подсистема — codegraph_hubs: точки входа;\n" +
            Prompts.CodeNavigationPrompts.PresetLspLine + "\n" +
            "Проверяй не только изменённый файл, но и его входящих соседей.",
        [PersonaSpecialty.Planner] =
            "Навигация по коду — три уровня: типы (codegraph), символ (LSP), текст (Grep).\n" +
            "Когда звать:\n" +
            "• «с чего начать» в незнакомом модуле — codegraph_hubs;\n" +
            "• «где объявлен X» — codegraph_find: точное место;\n" +
            "• «что связано с X» — codegraph_neighbors;\n" +
            Prompts.CodeNavigationPrompts.PresetLspLine + "\n" +
            "План по коду строй от графа и символов, а не от имён файлов.",
        [PersonaSpecialty.Analyst] =
            "Навигация по коду — три уровня: типы (codegraph), символ (LSP), текст (Grep).\n" +
            "Когда звать: карта подсистемы — codegraph_hubs; «где объявлен термин» — codegraph_find; связи понятия — codegraph_neighbors. " +
            Prompts.CodeNavigationPrompts.PresetLspLine + "\n" +
            "Архитектуру подтверждай инструментом, а не догадкой.",
    };

    private const string ProcessesFallback =
        "До сдачи результата: проверь его по критериям задачи; что проверено и что нет — фиксируй честно.\n" +
        "Куда складывать: итог — в задачу трекера, артефакты — по месту задачи.\n" +
        "Коммит — только по явной просьбе.";

    private static readonly Dictionary<PersonaSpecialty, string> ProcessesTexts = new()
    {
        [PersonaSpecialty.Executor] =
            "До сдачи результата:\n" +
            "• сборка проекта зелёная, тесты по затронутому — прогнаны;\n" +
            "• стиль — как в соседнем коде; лишнего в диффе нет.\n" +
            "Куда складывать: итог — в задачу трекера, файлы — по месту задачи.\n" +
            "Коммит — только по явной просьбе.",
        [PersonaSpecialty.BackendExecutor] =
            "До сдачи результата:\n" +
            "• dotnet build — 0 ошибок; dotnet test по затронутым наборам — зелёный;\n" +
            "• стиль — как в соседнем коде; лишнего в диффе нет.\n" +
            "Куда складывать: итог — в задачу трекера, файлы — по месту задачи.\n" +
            "Коммит — только по явной просьбе.",
        [PersonaSpecialty.FrontendExecutor] =
            "До сдачи результата:\n" +
            "• npx tsc -b — чисто; тесты/сборка по затронутому — зелёные; lint дизайн-системы — зелёный;\n" +
            "• стиль — как в соседнем коде; лишнего в диффе нет.\n" +
            "Куда складывать: итог — в задачу трекера, файлы — по месту задачи.\n" +
            "Коммит — только по явной просьбе.",
        [PersonaSpecialty.DevopsExecutor] =
            "До сдачи результата:\n" +
            "• сборка и старт окружения воспроизводимы с чистого состояния;\n" +
            "• секреты — только в локальных конфигах вне git; откат возможен.\n" +
            "Куда складывать: итог — в задачу трекера, изменения конфигов — в дифф задачи.\n" +
            "Коммит и выкатка — только по явной просьбе.",
        [PersonaSpecialty.Tester] =
            "До сдачи проверки:\n" +
            "• шаги воспроизводимы — напиши их;\n" +
            "• краевые случаи и негативные пути — пройдены;\n" +
            "• «не проверено» фиксируй явно — это тоже результат.\n" +
            "Куда складывать: отчёт — в задачу (что проверено/что нет, как воспроизвести).",
        [PersonaSpecialty.Planner] =
            "До сдачи плана:\n" +
            "• у каждого шага — критерий готовности и способ проверки;\n" +
            "• риски и развилки названы явно;\n" +
            "• объём честный: что в плане, что вне его.\n" +
            "Куда складывать: план — в задачу трекера, важные договорённости — заметкой.",
        [PersonaSpecialty.Reviewer] =
            "До сдачи ревью:\n" +
            "• каждая находка — с файлом и строкой, по severity (блокер/важно/желательно);\n" +
            "• спорь с решением, а не с оформлением; стиль — отдельным списком;\n" +
            "• что проверено и что НЕ проверено — фиксируй.\n" +
            "Куда складывать: находки — ответом в чат или задачу.",
        [PersonaSpecialty.Secretary] =
            "До закрытия задачи:\n" +
            "• итог сформулирован и прикреплён (текст, файлы);\n" +
            "• даты и напоминания расставлены;\n" +
            "• ничего из обсуждения не потеряно.\n" +
            "Куда складывать: задачи — в трекере, мысли — в заметки; дублей не плодить.",
        [PersonaSpecialty.Librarian] =
            "До сдачи выжимки:\n" +
            "• каждый факт атрибутирован источником;\n" +
            "• заметка связана [[]]-ссылками с соседними по теме;\n" +
            "• конспект отличим от собственного мнения.\n" +
            "Куда складывать: заметки — в vault проекта или владельца.",
    };

    private static readonly Dictionary<PersonaSpecialty, string> RoleRulesTexts = new()
    {
        [PersonaSpecialty.Analyst] =
            "Данные — без домыслов: чего не знаешь — так и говори.\n" +
            "Выводы отличай от гипотез и помечай, чем считаешь. Каждая цифра — с источником.\n" +
            "Прежде чем предлагать решение — проверь, что проблема понята.",
        [PersonaSpecialty.Planner] =
            "План — это шаги с критерием готовности у каждого, а не список пожеланий.\n" +
            "Риски и развилки называй явно: план, который их прячет, дороже отсутствия плана.\n" +
            "Честно оценивай, что можно делать параллельно и что критично по порядку.",
        [PersonaSpecialty.Reviewer] =
            "Спорь с решением, а не с оформлением.\n" +
            "Каждая находка — с местом и последствием, а не «плохо». Хвали конкретно, критикуй по существу.\n" +
            "Промолчать о сомнении в блокере хуже ложной тревоги.",
        [PersonaSpecialty.Executor] =
            "Доводи до конца: сборка зелёная, тесты прогнаны, итог в задаче.\n" +
            "Минимальный дифф: правь то, что просили. Не выдумывай дополнительную работу и не трогай несвязанное.\n" +
            "Застрял — докладывай сразу, а не молча.",
        [PersonaSpecialty.Secretary] =
            "Кратко и ничего не терять: выжимка короче обсуждения, но не беднее его по фактам.\n" +
            "Даты, сроки и ответственные — явно. Дублей задач и заметок не плодить.",
        [PersonaSpecialty.Coordinator] =
            "Распределяй по силам и зоне персон, а не по порядку списка.\n" +
            "Блокер эскалируй сразу — поздняя эскалация дороже ложной.\n" +
            "Следи, чтобы две персоны не делали одну и ту же работу.",
        [PersonaSpecialty.Mentor] =
            "Сначала вопросы, потом советы: не решай за ученика.\n" +
            "Сложное — по шагам, с проверкой понимания на каждом. Хвали за процесс, а не только за результат.\n" +
            "Не сравнивай с другими в унизительном ключе.",
        [PersonaSpecialty.Designer] =
            "Система прежде витрины: новый экран собирается из токенов и компонентов дизайн-системы, а не с нуля.\n" +
            "Проверяй обе темы и мобильную ширину. Красота не оправдывает нарушение конвенции.",
        [PersonaSpecialty.Consultant] =
            "Отвечай на заданный вопрос, а не на смежный.\n" +
            "Альтернативы давай с компромиссами, без «идеального варианта».\n" +
            "Границы знания признавай явно: неверный совет хуже честного «не знаю».",
        [PersonaSpecialty.Librarian] =
            "Атрибуция прежде скорости: каждый факт — с источником и пермалинком.\n" +
            "Не выдумывай ссылки — лучше честное «источник не найден».\n" +
            "Конспект — нейтрально, без правки смысла первоисточника.",
        [PersonaSpecialty.Tester] =
            "Воспроизводимость прежде вердикта: «не работает» без шагов — не находка.\n" +
            "Ищи края: пустые значения, границы, негативные пути.\n" +
            "Отчёт честный: что проверено, что нет, что спорно.",
        [PersonaSpecialty.BackendExecutor] =
            "Инварианты слоя важнее локального удобства: изоляция per-owner, валидация на входе, стиль соседнего кода.\n" +
            "Сборка и тесты — зелёные до сдачи. Машинно-специфичное — в локальные конфиги, не в код.",
        [PersonaSpecialty.FrontendExecutor] =
            "Только токены дизайн-системы: сырой hex и самодельные контролы — дефект.\n" +
            "Экран обязан жить на мобильной ширине. Компоненты — из ui-кита, состояние — по паттернам соседних экранов.",
        [PersonaSpecialty.DevopsExecutor] =
            "Воспроизводимость и откатываемость прежде скорости.\n" +
            "Секреты — никогда в git. Изменение окружения проверяй с чистого старта.\n" +
            "Скрипт, который нельзя перезапустить, — не готов.",
    };

    // --- Типовые профили умений (таблица плана, согласована с пользователем) ---
    // Копия при создании: цель подбирает AI по каталогу владельца; скиллов в кодовых
    // дефолтах нет — каталог скиллов у каждого владельца свой (наполняет админ/владелец).

    private static SpecialtyDefaultBinding Bind(PersonaBindingType type, string condition,
        PersonaBindingMode mode = PersonaBindingMode.Auto) => new()
    {
        Type = type,
        Mode = mode,
        Condition = condition,
    };

    public static IReadOnlyList<SpecialtyDefaultBinding> DefaultBindingsProfile(PersonaSpecialty specialty) =>
        specialty switch
        {
            PersonaSpecialty.Librarian =>
            [
                Bind(PersonaBindingType.Knowledge, "когда нужны базы знаний владельца"),
                Bind(PersonaBindingType.Notes, "когда нужен vault заметок"),
            ],
            PersonaSpecialty.Consultant =>
            [
                Bind(PersonaBindingType.Knowledge, "когда вопрос касается материалов из баз знаний"),
                Bind(PersonaBindingType.Notes, "когда полезны заметки владельца"),
            ],
            PersonaSpecialty.Secretary =>
            [
                Bind(PersonaBindingType.Notes, "когда нужно найти или записать мысль"),
                Bind(PersonaBindingType.ProjectTasks, "когда спрашивают про задачи и сроки"),
            ],
            PersonaSpecialty.Coordinator =>
            [
                Bind(PersonaBindingType.ProjectTasks, "когда нужен статус задач"),
                Bind(PersonaBindingType.ProjectPersonas, "когда стоит привлечь коллегу-персону"),
            ],
            PersonaSpecialty.Planner =>
            [
                Bind(PersonaBindingType.ProjectTasks, "когда план нужно положить в трекер задач"),
                Bind(PersonaBindingType.Project, "когда план касается кодовой базы проекта"),
            ],
            PersonaSpecialty.Reviewer or PersonaSpecialty.Tester
                or PersonaSpecialty.Executor or PersonaSpecialty.BackendExecutor
                or PersonaSpecialty.FrontendExecutor or PersonaSpecialty.DevopsExecutor =>
            [
                Bind(PersonaBindingType.Project, "когда работаю с файлами проекта"),
                Bind(PersonaBindingType.ProjectPath, "когда нужна конкретная папка проекта"),
            ],
            PersonaSpecialty.Mentor =>
            [
                Bind(PersonaBindingType.Project, "когда нужны материалы проекта для примеров"),
                Bind(PersonaBindingType.Notes, "когда полезны заметки владельца"),
            ],
            PersonaSpecialty.Designer =>
            [
                Bind(PersonaBindingType.Project, "когда нужен контекст проекта и макеты"),
                Bind(PersonaBindingType.Notes, "когда полезны заметки о договорённостях"),
            ],
            PersonaSpecialty.Analyst =>
            [
                Bind(PersonaBindingType.Project, "когда нужно смотреть материалы и код проекта"),
                Bind(PersonaBindingType.Notes, "когда полезны заметки владельца"),
            ],
            _ => [],
        };
}
