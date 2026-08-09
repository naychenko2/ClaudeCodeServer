using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Tests.Fixtures;

// Замороженные кейсы для бенча промпта постановки задачи (план «Оптимизация потребления
// токенов», шаг 1.5). Входы ЛИТЕРАЛЬНЫЕ: ни Dify, ни диска, ни живых сторов — иначе бенч
// перестаёт быть детерминированным и сравнение «до/после» теряет смысл.
//
// Id задач и подзадач заданы явно (а не Guid.NewGuid()): они попадают в текст промпта,
// и случайные значения давали бы разный baseline при каждом прогоне.
internal static class PromptBenchFixtures
{
    // Блок заметок «как из Dify»: BuildNotesContextAsync приватный и требует живого
    // NotesKnowledgeService, поэтому в бенче воспроизводим ЕГО ФОРМАТ литералом.
    // Формат — зеркало TaskExecutionService.BuildNotesContextAsync.
    //
    // ВАЖНО: блок заморожен в состоянии «до» (3 заметки) и после снятия baseline НЕ меняется —
    // иначе сравнение «до/после» поедет по входным данным, а не по коду. Эффект перевода
    // topK 5→2 бенчем не измеряется: он виден только на живых данных (task-prompts.jsonl),
    // потому что размер выдачи Dify зависит от содержимого vault конкретного владельца.
    private const string NotesBlock = """

## Возможно релевантные заметки из базы знаний
(семантически близкие к задаче выдержки — используй как контекст, если полезно; не полагайся слепо)

### Архитектура слоя провайдеров (AI Home)
Единственный рантайм — claude CLI; сторонние провайдеры подключаются env-оверрайдами
процесса на каждый ход. Конфиг — секция LlmProviders; ключи в appsettings.Local.json.
Резолв провайдера, цены и возможности — LlmProviderRegistry.

### Инварианты изоляции данных
Все сторы per-owner: чужие данные недостижимы на уровне запросов. Пути — только через
SafeJoin. Бэкенд работает с хостовыми путями, IPathMapper переводит в контейнерные
в момент запуска процесса.

### Правила работы с задачами
У задач PersonaId != null подразумевает Assignee = Claude. Смена ExecutionEnvironment
при существующих чатах запрещена. Доступы персоны формируют disallowed-инструменты.
""";

    public sealed record BenchCase(
        string Name,
        TaskItem Task,
        Persona? Persona,
        ModelTierAliases Aliases,
        string? CategoryProfilesPath,
        string NotesBlock);

    // 6 кейсов: от минимального до худшего случая. Порядок фиксирован — по нему
    // строится prompt-baseline.json и сравнение в PromptBenchTests.
    public static IReadOnlyList<BenchCase> All =>
    [
        // 1. Минимум без персоны — сторож обратной совместимости (прежний короткий формат)
        new("01-minimal-no-persona",
            new TaskItem { Id = "bench-task-01", Title = "Починить сборку" },
            null, ModelTierAliases.None, null, ""),

        // 2. Минимум с персоной — 6-секционный контракт на пустом контексте
        new("02-minimal-persona",
            new TaskItem { Id = "bench-task-02", Title = "Починить сборку" },
            new Persona { Name = "Вера", Role = "QA-тестер" },
            ModelTierAliases.None, null, ""),

        // 3. Описание + подзадачи + файлы, без персоны
        new("03-rich-no-persona",
            new TaskItem
            {
                Id = "bench-task-03",
                Title = "Оптимизировать промпт исполнителя задач",
                Description = "Сократить размер системного промпта на 50% от baseline, "
                    + "сохранив верификационную дисциплину. Замер — фикстурным бенчем.",
                Subtasks =
                [
                    new TaskSubtask { Id = "bench-sub-03-1", Title = "Снять baseline" },
                    new TaskSubtask { Id = "bench-sub-03-2", Title = "Объединить секции правил" },
                    new TaskSubtask { Id = "bench-sub-03-3", Title = "Ужать таблицу делегирования", IsDone = true },
                    new TaskSubtask { Id = "bench-sub-03-4", Title = "Снизить topK заметок" },
                    new TaskSubtask { Id = "bench-sub-03-5", Title = "Сверить бенч" },
                ],
                LinkedFiles =
                [
                    "backend/ClaudeHomeServer/Services/TaskExecutionService.cs",
                    "backend/ClaudeHomeServer/Services/Spend/SpendAnalyticsService.cs",
                    "backend/ClaudeHomeServer.Tests/Services/TaskExecutionServiceTests.cs",
                ],
            },
            null, ModelTierAliases.None, null, ""),

        // 4. Персона + полные алиасы тиров + путь справочника категорий:
        //    максимальная секция ДЕЛЕГИРОВАНИЕ
        new("04-persona-full-delegation",
            new TaskItem
            {
                Id = "bench-task-04",
                Title = "Реализовать разрез «Задача» в аналитике токенов",
                Description = "Добавить task в PivotLevels, KeyOf и ResolveName; "
                    + "на фронте — SpendDim, DIM_LABELS и пресет «По задачам».",
                Subtasks = [new TaskSubtask { Id = "bench-sub-04-1", Title = "Бэкенд: PivotLevels" }],
                LinkedFiles = ["frontend/src/lib/spend.ts"],
            },
            new Persona { Name = "Денис", Role = "Backend-разработчик" },
            new ModelTierAliases("opus", "sonnet", "haiku"),
            "/home/user/.claude/omc/category-profiles.md",
            ""),

        // 5. Персона + блок заметок: вклад семантического контекста в размер промпта
        new("05-persona-with-notes",
            new TaskItem
            {
                Id = "bench-task-05",
                Title = "Сократить доклад о завершении задачи",
                Description = "Model Z должен слать краткий факт вместо полного resultMarkdown.",
            },
            new Persona { Name = "Денис", Role = "Backend-разработчик" },
            new ModelTierAliases("opus", "sonnet", "haiku"),
            null, NotesBlock),

        // 6. Худший случай: всё сразу — длинное описание, подзадачи, файлы, алиасы,
        //    справочник и заметки
        new("06-worst-case",
            new TaskItem
            {
                Id = "bench-task-06",
                Title = "Провести глубокий анализ и оптимизацию цикла исполнения задач",
                Description = "Цикл «постановка → исполнение → отчёт» потребляет заметный объём "
                    + "токенов. Нужно измерить состав промпта постановки по секциям, "
                    + "зафиксировать baseline фикстурным бенчем, затем сократить постановку "
                    + "и доклад суммарно на 50%, не тронув верификационную дисциплину "
                    + "(секции ОБЯЗАТЕЛЬНО и НЕЛЬЗЯ защищены). Отдельно — новый стор метрик "
                    + "промпта с исключением из облачного бэкапа и разрез «Задача» "
                    + "в аналитике токенов с разбивкой постановки по секциям при раскрытии узла.",
                Subtasks =
                [
                    new TaskSubtask { Id = "bench-sub-06-1", Title = "Инструментация MeasurePrompt", IsDone = true },
                    new TaskSubtask { Id = "bench-sub-06-2", Title = "Фикстуры и заморозка baseline" },
                    new TaskSubtask { Id = "bench-sub-06-3", Title = "Объединить ОБЯЗАТЕЛЬНО и НЕЛЬЗЯ" },
                    new TaskSubtask { Id = "bench-sub-06-4", Title = "Ужать делегирование" },
                    new TaskSubtask { Id = "bench-sub-06-5", Title = "Снизить topK заметок до 2" },
                    new TaskSubtask { Id = "bench-sub-06-6", Title = "Краткий доклад Model Z" },
                    new TaskSubtask { Id = "bench-sub-06-7", Title = "Стор task-prompts.jsonl" },
                    new TaskSubtask { Id = "bench-sub-06-8", Title = "Разрез «Задача» в pivot" },
                ],
                LinkedFiles =
                [
                    "backend/ClaudeHomeServer/Services/TaskExecutionService.cs",
                    "backend/ClaudeHomeServer/Services/Spend/SpendAnalyticsService.cs",
                    "backend/ClaudeHomeServer/Services/Spend/SpendStore.cs",
                    "backend/ClaudeHomeServer/Services/Backup/BackupPaths.cs",
                    "backend/ClaudeHomeServer/Controllers/SpendController.cs",
                    "frontend/src/lib/spend.ts",
                    "frontend/src/features/spend/SpendAnalysis.tsx",
                ],
                ResultMarkdown = "Промпт сокращён, бенч зелёный, отчёт приложен.",
            },
            new Persona { Name = "Денис", Role = "Backend-разработчик" },
            new ModelTierAliases("opus", "sonnet", "haiku"),
            "/home/user/.claude/omc/category-profiles.md",
            NotesBlock),
    ];
}
