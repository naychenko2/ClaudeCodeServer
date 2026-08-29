namespace ClaudeHomeServer.Models;

// Каркас проекта для знакомства v2 (п.2 плана): статические данные в коде по образцу
// FeatureFlagCatalog. Состав выведен из трёх живых непрограммных проектов владельца,
// где один и тот же конвейер документов сложился независимо; тексты заготовок — рамки
// с пояснениями курсивом, а не пустые файлы. Папки «Команда/» нет (решение владельца):
// команда проекта живёт в персонах приложения, отдельная папка с файлами не нужна.
//
// Каждый пресет — четыре раздела с РАЗНЫМИ приёмниками записи (см. ProjectPresetService):
// папки и файлы — в репозиторий, область документации — в .docs репозитория (файл сильнее
// настройки проекта и едет вместе с папкой), колонки доски — в Project.BoardColumns.
//
// Ключи каталога обязаны не пересекаться со служебными значениями ProjectPreset
// (pending/none) — сторож в PresetKey_ЗарезервированныеЗначения_НеПересекаютсяСКлючамиКаталога.

/// <summary>Файл-заготовка: путь от корня проекта (прямые слэши) и содержимое целиком.</summary>
public record PresetFile(string Path, string Content);

/// <summary>Колонка Kanban-доски пресета: имя, категория статусов задач в ней и необязательная
/// роль (см. BoardColumn.Role — "review" требует у дефектов заполненные шаги воспроизведения).</summary>
public record PresetColumn(string Name, TaskItemStatus Category, string? Role = null);

/// <summary>
/// Пресет каркаса. <see cref="Files"/> — только добавление: существующие файлы
/// не перезаписываются никогда (проект часто заводят на живой папке с рабочим CLAUDE.md).
/// </summary>
public record PresetDefinition(
    string Key,
    string Title,
    IReadOnlyList<string> Folders,
    IReadOnlyList<PresetFile> Files,
    DocsScope DocsScope,
    IReadOnlyList<DocTypeDef> DocTypes,
    IReadOnlyList<PresetColumn> BoardColumns);

public static class PresetCatalog
{
    // Место названия проекта в заготовках. В каталоге лежит токен, реальное имя
    // подставляется при записи файла на диск (ProjectPresetService.Apply) — каталог
    // не знает имён и остаётся одинаковым для всех проектов.
    public const string ProjectNameToken = "<Название проекта>";

    /// <summary>
    /// Подстановка названия проекта в заготовку при материализации файла. Пустое имя
    /// оставляет токен на месте — для человека это подсказка заполнить шапку, а не мусор.
    /// </summary>
    public static string Materialize(string content, string? projectName) =>
        string.IsNullOrWhiteSpace(projectName)
            ? content
            : content.Replace(ProjectNameToken, projectName);

    // Общие куски заготовок. «Статус.md» в каждом пресете свой, но вводная строка про панель
    // «Документы» одна — с неё открывается область, и человек должен об этом знать.
    private const string StatusHint =
        "Короткая сводка, которую видно первой. Держите её живой — с этой страницы\n" +
        "открывается панель «Документы».";

    // Документный конвейер: Исходники → Входящие → Рабочие документы → Архив.
    private const string DocsClaudeMd = """
        # <Название проекта>

        _О чём проект: одна-две фразы._

        ## Структура папок

        | Папка | Назначение | Правило |
        |---|---|---|
        | `Исходники/` | Первоисточники как получены: docx, pdf, xlsx, записи встреч | **Только чтение.** Не редактировать, не перезаписывать, не удалять |
        | `Входящие/` | Текстовые копии источников в Markdown, 1:1 без правок | Промежуточное хранилище |
        | `Встречи/` | Расшифровки встреч | Имя: `Тема.ГГГГ-ММ-ДД.md` |
        | `Рабочие документы/` | Актуальные версии рабочих документов | Правки только здесь |
        | `Рабочие документы/Входы/` | Черновики разделов от участников | Не путать с `Входящие/` — это своя подготовка, а не внешние материалы |
        | `Рабочие документы/assets/` | Схемы и диаграммы | PNG рядом с SVG |
        | `Рабочие документы/Word/` | Оформленные `.docx`, собранные из Markdown | **Не править руками** — перегенерируются |
        | `Архив/` | Вытесненные версии документов | Только добавление при архивации |

        ## Версионирование документов

        - Версия — в конце имени файла: ` v<Major>.<Minor>`, например `Регламент v1.0.md`.
        - `Minor` — мелкие правки, `Major` — существенные изменения.
        - При повышении версии старая уходит в `Архив/`, новая создаётся в `Рабочие документы/`.
        - В `Рабочие документы/` всегда лежит только актуальная версия.

        ## Работа с документом

        1. Первоисточник кладётся в `Исходники/` и больше не трогается.
        2. Конвертация в Markdown — в `Входящие/`, без правок.
        3. Рабочая версия — копия из `Входящие/` в `Рабочие документы/<имя> v1.0.md`.
        4. Дальше правится только рабочий документ; при значимом изменении версия поднимается.

        ## Правила

        - Markdown — источник правды. Офисные форматы собираются из него и руками не правятся.
        - Все `.md` — UTF-8 без BOM.
        - Схемы — Mermaid-кодом в ```mermaid-блоках; сложная вёрстка — виджетом.
        """;

    private const string DocsStatusMd = $"""
        # Статус проекта

        {StatusHint} О чём проект, кто в команде, что происходит сейчас.

        ## О чём проект

        _Пара абзацев: зачем проект существует и какой результат считается итогом._

        ## Команда

        _Кто за что отвечает._

        ## Что сейчас

        _Статус по направлениям: что в работе, что на согласовании, что ждёт._

        ## Решения

        _Принятые решения с датами — чтобы не возвращаться к обсуждённому._

        ## Открытые вопросы

        _Что не решено и от кого ждём ответа._
        """;

    // Разработка: CLAUDE.md НЕ создаём — в репозитории с кодом он почти всегда уже есть
    // и несёт рабочие правила, подменять их шаблоном нельзя. Вместо него — правило
    // нумерации ADR на месте их будущей папки.
    private const string DevStatusMd = $"""
        # Статус проекта

        {StatusHint} О чём проект и в каком он состоянии.

        ## О чём проект

        _Пара абзацев: зачем проект существует и какой результат считается итогом._

        ## Как запустить

        _Команды сборки и запуска, окружение, на что смотреть в первую очередь._

        ## Что сейчас

        _Статус по направлениям: что в работе, что на ревью, что ждёт._

        ## Решения

        _Принятые решения с датами — чтобы не возвращаться к обсуждённому._

        ## Открытые вопросы

        _Что не решено и от кого ждём ответа._
        """;

    private const string DevAdrReadmeMd = """
        # Решения (ADR)

        Короткие записи архитектурных решений: контекст, что решили, чем заплатили.
        Один файл — одно решение.

        ## Нумерация

        - Имя файла: `ADR-<номер>-<слаг>.md`, например `ADR-001-cache-layer.md`.
        - Номер следующий по порядку в этой папке, без пропусков и без переиспользования:
          отклонённое и заменённое решение остаётся в истории.

        ## Статусы

        Свойство «Статус» в шапке документа: Предложен, Принят, Отклонён, Заменён.
        «Заменён» дополняют ссылкой на преемника.
        """;

    private const string PersonalStatusMd = """
        # <Название проекта>

        _Что это за дело и чем оно должно закончиться._

        ## Что сейчас

        _Где я нахожусь._

        ## Следующие шаги

        _Что делать дальше._

        ## Важное

        _Даты, контакты, цифры, к которым придётся возвращаться._
        """;

    public static readonly IReadOnlyList<PresetDefinition> All =
    [
        new PresetDefinition(
            Key: "docs",
            Title: "Документный проект",
            Folders:
            [
                "Исходники", "Входящие", "Встречи",
                "Рабочие документы", "Рабочие документы/Входы", "Рабочие документы/assets",
                "Рабочие документы/Word", "Архив",
            ],
            Files:
            [
                new PresetFile("CLAUDE.md", DocsClaudeMd),
                new PresetFile("Статус.md", DocsStatusMd),
            ],
            DocsScope: new DocsScope(
                ["Рабочие документы", "Встречи", "Входящие", "Архив"],
                ["Статус.md"],
                ["markdown", "pdf", "office", "diagram", "image"],
                "Статус.md"),
            DocTypes:
            [
                new DocTypeDef(
                    "working-doc", "Рабочий документ", ["Рабочие документы"], Match: null,
                    BadgeProperty: "Статус",
                    Properties:
                    [
                        new DocPropertyDef("Статус", DocPropertyKind.Choice, Choices:
                        [
                            new DocPropertyChoice("Черновик", "gray"),
                            new DocPropertyChoice("На согласовании", "warning"),
                            new DocPropertyChoice("Утверждён", "success"),
                        ]),
                        new DocPropertyDef("Версия", DocPropertyKind.Text),
                        new DocPropertyDef("Обновлён", DocPropertyKind.Date, AutoUpdate: true),
                    ]),
                new DocTypeDef(
                    "meeting", "Встреча", ["Встречи"], Match: null, BadgeProperty: null,
                    Properties:
                    [
                        new DocPropertyDef("Дата", DocPropertyKind.Date),
                        new DocPropertyDef("Участники", DocPropertyKind.Text),
                    ]),
                new DocTypeDef(
                    "incoming", "Входящий материал", ["Входящие"], Match: null, BadgeProperty: null,
                    Properties:
                    [
                        new DocPropertyDef("Источник", DocPropertyKind.Text),
                        new DocPropertyDef("Получен", DocPropertyKind.Date),
                    ]),
                new DocTypeDef(
                    "archived", "Устаревшая версия", ["Архив"], Match: null, BadgeProperty: null,
                    Properties: [new DocPropertyDef("Заменён на", DocPropertyKind.DocLink)]),
            ],
            BoardColumns:
            [
                new PresetColumn("Разобрать", TaskItemStatus.Todo),
                new PresetColumn("В работе", TaskItemStatus.InProgress),
                new PresetColumn("На согласовании", TaskItemStatus.InProgress, Role: "review"),
                new PresetColumn("Готово", TaskItemStatus.Done),
            ]),

        new PresetDefinition(
            Key: "dev",
            Title: "Разработка",
            Folders: ["docs", "docs/adr", "notes"],
            Files:
            [
                new PresetFile("Статус.md", DevStatusMd),
                new PresetFile("docs/adr/README.md", DevAdrReadmeMd),
            ],
            DocsScope: new DocsScope(
                ["docs"],
                ["README.md", "Статус.md"],
                ["markdown", "diagram"],
                "Статус.md"),
            DocTypes:
            [
                new DocTypeDef(
                    "adr", "Решение (ADR)", ["docs/adr"], Match: "ADR-*.md",
                    BadgeProperty: "Статус",
                    Properties:
                    [
                        new DocPropertyDef("Статус", DocPropertyKind.Choice, Choices:
                        [
                            new DocPropertyChoice("Предложен", "plan"),
                            new DocPropertyChoice("Принят", "success"),
                            new DocPropertyChoice("Отклонён", "danger"),
                            new DocPropertyChoice("Заменён", "gray"),
                        ]),
                        new DocPropertyDef("Дата", DocPropertyKind.Date),
                    ]),
            ],
            BoardColumns:
            [
                new PresetColumn("Бэклог", TaskItemStatus.Todo),
                new PresetColumn("В работе", TaskItemStatus.InProgress),
                new PresetColumn("Ревью", TaskItemStatus.InProgress, Role: "review"),
                new PresetColumn("Готово", TaskItemStatus.Done),
            ]),

        new PresetDefinition(
            Key: "personal",
            Title: "Личное дело",
            Folders: ["Материалы", "Заметки", "Архив"],
            Files: [new PresetFile("Статус.md", PersonalStatusMd)],
            DocsScope: new DocsScope(
                ["Заметки", "Материалы"],
                ["Статус.md"],
                ["markdown", "pdf", "image"],
                "Статус.md"),
            // Свойств документов нет намеренно: для личного дела это лишняя церемония
            DocTypes: [],
            BoardColumns:
            [
                new PresetColumn("Надо", TaskItemStatus.Todo),
                new PresetColumn("Делаю", TaskItemStatus.InProgress),
                new PresetColumn("Сделано", TaskItemStatus.Done),
            ]),
    ];

    private static readonly Dictionary<string, PresetDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    /// <summary>Пресет по ключу из запроса; null — ключ неизвестен (400 эндпоинта).</summary>
    public static PresetDefinition? Find(string key) =>
        key is not null && ByKey.TryGetValue(key, out var preset) ? preset : null;
}
