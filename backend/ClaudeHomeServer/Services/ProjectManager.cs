using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

public class ProjectManager
{
    // Встроенная часть системного промпта — всегда добавляется, пользователь не редактирует
    public const string BuiltInSystemPrompt =
        "Всегда общайся с пользователем на русском языке. Все сообщения, пояснения, вопросы и итоговые ответы пиши исключительно по-русски, независимо от языка запроса. Технические термины, идентификаторы кода, названия инструментов и команды оставляй в оригинальном виде.\n\n" +
        "Если пользователь просит сгенерировать, нарисовать, создать или изобразить что-либо визуальное (изображение, картинку, рисунок, арт, фото, иллюстрацию), видео, аудио или музыку — используй подключённые MCP-серверы генерации медиа: glif и fal-ai. Приоритет: по умолчанию иди в glif; в fal-ai уходи, только если glif не подключён, недоступен, вернул ошибку или задача явно требует конкретного endpoint'а fal. fal-ai — прямой доступ к моделям: явный выбор endpoint'а, очередь задач, точная стоимость прогона; подбери модель (recommend_model или search_models), запусти задачу (submit_job / run_model) и верни результат как есть. glif — агентские workflow из топовых моделей, готовые стили и скиллы, мультимодальные входы (upload_file для референсов): генерация асинхронная — вызови compose_project, затем опрашивай get_job_status до статуса completed/failed, и после completed один раз вызови view_media. Ссылки на изображение/видео/аудио из ответов fal-ai и glif отображаются в чате автоматически — НЕ открывай их в браузере и не используй для показа playwright, WebFetch или другие инструменты; после вызова генератора просто заверши ход. Никогда не рисуй ASCII-арт вместо настоящей генерации.\n\n" +
        // Workflow в новых CLI фоновый (сразу отдаёт Task ID), а в нашей архитектуре ход,
        // завершённый раньше workflow, убивает его процесс — уведомление о завершении не придёт
        "Инструмент Workflow запускается в фоне и сразу возвращает Task ID. НИКОГДА не завершай ход, пока запущенный workflow работает: сразу после запуска вызывай TaskOutput с его task_id и block=true, при таймауте повторяй вызов, дождись завершения и изложи итог workflow в этом же ходу. Уведомления о завершении фоновых задач в этой среде не приходят — ход, завершённый раньше workflow, необратимо убивает его.\n\n" +
        // Схемы — кодом Mermaid: чат рендерит ```mermaid-блоки в интерактивный SVG
        // (MermaidDiagram.tsx). ASCII-арт разъезжается при переносе строк; виджет
        // остаётся запасным путём для того, что Mermaid не выражает
        "Схемы и диаграммы рисуй кодом Mermaid в кодовом блоке ```mermaid — чат рендерит его в интерактивный SVG: flowchart TD для структуры, sequenceDiagram для взаимодействия, erDiagram/classDiagram для моделей данных, xychart-beta для графиков. Подписи узлов пиши коротко; спецсимволы и скобки в подписях заключай в кавычки. ASCII-арт для схем не используй — он разъезжается при переносе строк. Если Mermaid не подходит (свободная вёрстка, интерактив) — рисуй виджетом (инструмент widget_show, SVG/HTML): там линии — это элементы с фиксированными координатами.\n\n";

    private readonly ConcurrentDictionary<string, Project> _projects = new();
    private readonly string _storePath;
    private readonly UserStore _users;
    // Песочница container-пользователей: их проекты живут только под Sandbox:ProjectsRoot.
    // null — в тестах (все пользователи считаются local)
    private readonly Execution.SandboxManager? _sandbox;
    // Домашняя папка владельца ({база по среде}/{username} либо override из конфига)
    private readonly UserHomeResolver _homes;
    private readonly Lock _saveLock = new();

    public ProjectManager(IConfiguration config, UserStore users, AppSettingsService appSettings,
        Execution.SandboxManager? sandbox = null, UserHomeResolver? homes = null)
    {
        _users = users;
        _sandbox = sandbox;
        _homes = homes ?? UserHomeResolver.WithoutOverrides(appSettings, sandbox);
        _storePath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        Load();
    }

    // Тайлы фонов: data/project-backgrounds/{id}/tile-{guid}.svg (ADR-008 §6).
    // В бэкап едут по общему правилу — исключений и своего способа копирования не требуют.
    public string BackgroundsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "project-backgrounds");

    // Container-пользователь заперт в корне песочницы: путь вне него claude не увидит
    // (в контейнер монтируется только Sandbox:ProjectsRoot), а FileService увидел бы хост —
    // расхождение недопустимо
    private void EnsureRootAllowed(string userId, string rootPath)
    {
        if (_sandbox is null) return;
        if (_users.GetById(userId)?.ExecutionEnvironment != ExecutionEnvironments.Container) return;
        var sandboxRoot = _sandbox.Options.ProjectsRoot;
        // Сравнение вложенности — общим хелпером: голый StartsWith пропускал бы соседа
        // «C:\Sandbox-old» при корне «C:\Sandbox»
        if (string.IsNullOrWhiteSpace(sandboxRoot)
            || !UserHomeResolver.IsInside(rootPath, sandboxRoot))
            throw new ArgumentException(
                "Проекты изолированного пользователя должны находиться внутри папки песочницы (Sandbox:ProjectsRoot)");
    }

    public IReadOnlyCollection<Project> GetAll() => _projects.Values.ToList();

    public IReadOnlyCollection<Project> GetByOwner(string userId) =>
        _projects.Values.Where(p => p.OwnerId == userId).ToList();

    public Project? GetById(string id) => _projects.GetValueOrDefault(id);

    public Project? GetByName(string name) =>
        _projects.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // Одна папка — один проект НА ВЛАДЕЛЬЦА. Иначе появляются проекты-близнецы (пользователь
    // подключил ту же папку второй раз, например записав путь иначе — «c:\\GIT\\x» вместо
    // «c:\GIT\x»), а датасет знаний в Dify общий на RootPath: два проекта начинают спорить за
    // одну базу. У РАЗНЫХ владельцев общая папка допустима — на этом держатся каскады
    // «соседей по папке» (GetByRootPath).
    private void EnsureRootFree(string userId, string rootPath, string? exceptProjectId = null)
    {
        var key = WorkspaceKnowledgeStore.NormalizePath(rootPath);
        var taken = _projects.Values.FirstOrDefault(p =>
            p.OwnerId == userId
            && p.Id != exceptProjectId
            && WorkspaceKnowledgeStore.NormalizePath(p.RootPath) == key);
        if (taken is not null)
            throw new ArgumentException($"Эта папка уже подключена как проект «{taken.Name}»");
    }

    public Project Create(string name, string? rootPath, string userId, string username, bool createDirectory = false, string? groupId = null, string? color = null)
    {
        // Путь не задан — «Новый проект»: папку под него придумываем сами.
        // Проверка стоит прямо в if (а не через заранее вычисленный флаг): так компилятор
        // видит, что ниже rootPath уже не null, и GetFullPath обходится без подавления
        var autoPath = false;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            autoPath = true;
            // Домашняя папка владельца: у изолированных — в корне песочницы, у остальных —
            // в DefaultProjectsPath; прослойка {username} может быть снята override'ом конфига
            var env = _sandbox is not null
                ? _users.GetById(userId)?.ExecutionEnvironment
                : ExecutionEnvironments.Local;
            var home = _homes.Resolve(username, env)
                ?? throw new ArgumentException(env == ExecutionEnvironments.Container
                    ? UserHomeResolver.NotConfiguredMessage(env)
                    : "Укажите путь к папке или задайте папку по умолчанию в настройках");
            rootPath = Path.Combine(home, name);
            createDirectory = true;
        }
        // Путь приводим к каноничному виду СРАЗУ: иначе «c:\GIT\x» и «c:\\GIT\\x» лягут в стор
        // как разные проекты, хотя это одна папка (Windows схлопывает двойные разделители сам)
        rootPath = Path.GetFullPath(rootPath);
        EnsureRootAllowed(userId, rootPath);
        EnsureRootFree(userId, rootPath);

        // «Новый проект» обязан получить НОВУЮ папку. Раньше домашняя папка была отдельной
        // ({база}/{логин}), и совпасть с рабочей репой не могла; с override домашней папкой
        // может быть общий корень вроде C:\GIT — и совпадение имени тихо подцепило бы чужую
        // репу со всем содержимым. Явный отказ вместо молчаливого «Новый = Существующий».
        if (autoPath && Directory.Exists(rootPath))
            throw new ArgumentException(
                $"Папка «{rootPath}» уже существует. Чтобы работать с ней, добавьте её как существующую.");

        if (createDirectory)
            Directory.CreateDirectory(rootPath);
        else if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Папка не найдена: {rootPath}");

        var project = new Project
        {
            Name = name,
            RootPath = rootPath,
            OwnerId = userId,
            GroupId = string.IsNullOrEmpty(groupId) ? null : groupId,
            Icon = new ProjectIcon { Color = string.IsNullOrEmpty(color) ? null : color },
            // Новый проект — кандидат на предложение каркаса (знакомство v2); созданные
            // до фичи проекты остаются с null и предложение не получают никогда
            PresetKey = ProjectPreset.Pending,
        };
        _projects[project.Id] = project;
        Save();
        return project;
    }

    public Project Update(string id, string? name, string? rootPath, string? systemPrompt = null,
        bool? showHiddenFiles = null, List<PermissionRule>? permissionRules = null, string? groupId = null,
        string? color = null, List<string>? mcpServersOn = null)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");

        if (name is not null) project.Name = name;
        if (rootPath is not null)
        {
            rootPath = Path.GetFullPath(rootPath);
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Папка не найдена: {rootPath}");
            if (project.OwnerId is { } ownerId)
            {
                EnsureRootAllowed(ownerId, rootPath);
                // сам проект из проверки исключаем: сохранение без смены папки — не дубль
                EnsureRootFree(ownerId, rootPath, exceptProjectId: project.Id);
            }
            project.RootPath = rootPath;
        }
        if (systemPrompt is not null) project.SystemPrompt = systemPrompt;
        if (showHiddenFiles is not null) project.ShowHiddenFiles = showHiddenFiles.Value;
        if (permissionRules is not null) project.PermissionRules = permissionRules.Count == 0 ? null : permissionRules;
        // groupId: null = не менять; "" = убрать из группы; иначе — привязать к группе
        if (groupId is not null) project.GroupId = groupId.Length == 0 ? null : groupId;
        // color: null = не менять; "" = сброс цвета (дефолтный фолбэк на фронте); иначе — ключ палитры.
        // Смена цвета картинку НЕ сбрасывает — она приоритетнее инициалов при Kind==Image.
        if (color is not null) project.Icon.Color = color.Length == 0 ? null : color;
        // mcpServersOn: allow-смысл. null = не менять; пустой список = «в проекте не включён никто».
        // Ключи нормализуем как в реестре (нижний регистр) — сравнение при доставке идёт по ним
        if (mcpServersOn is not null)
            project.McpServersOn = mcpServersOn.Count == 0
                ? null
                : [.. mcpServersOn.Select(k => k.Trim().ToLowerInvariant())
                    .Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)];
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Дефолт-персона проекта («руководитель», фича default-personas-onboarding); null — сброс.
    // Отдельным методом, а не параметром Update — настройка узкая, приходит из make-default.
    public Project SetDefaultPersona(string id, string? personaId)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.DefaultPersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Сессия незавершённого онбординга проекта (null — онбординг завершён/сброшен)
    public Project SetOnboardingSession(string id, string? sessionId)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.OnboardingSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Область документации для панели «Документы». Отдельным методом, а не параметром
    // Update: настройка узкая и приходит из самой панели, а Update и без того на девяти
    // параметрах. null у оси = вернуть её к дефолту; пустой список = «ничего отсюда».
    // Исключения устроены иначе: null = «не трогать» (старый бандл фронта, не знающий
    // оси, не должен стирать их своим null), [] = «убрать все».
    public Project SetDocsScope(string id, IReadOnlyList<string>? folders,
        IReadOnlyList<string>? rootFiles, IReadOnlyList<string>? types, string? home,
        IReadOnlyList<string>? excludeFolders = null)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        List<string>? normalizedFolders = folders is null ? null : [.. Docs.DocsIndexService.NormalizeFolders(folders)];
        project.DocsFolders = normalizedFolders;
        project.DocsRootFiles = rootFiles is null ? null : [.. Docs.DocsIndexService.NormalizeRootFiles(rootFiles)];
        project.DocsTypes = types is null ? null : [.. Docs.DocsIndexService.NormalizeTypes(types)];
        // Пустая строка — «вернуть авто-выбор README», как у color/groupId выше
        project.DocsHome = home is null ? project.DocsHome : Docs.DocsIndexService.NormalizeHome(home);
        // Исключения нормализуются против ИТОГОВЫХ папок: правка folders здесь же
        // выкидывает исключения, ставшие неактуальными
        if (excludeFolders is not null)
            project.DocsExcludeFolders =
                [.. Docs.DocsIndexService.NormalizeExcludeFolders(excludeFolders,
                    normalizedFolders ?? [.. Docs.DocsIndexService.DefaultScope.Folders])];
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Установить подобранный значок (ADR-009 §6): Kind = Glyph, Glyph = {Name|Paths, SetAt}.
    // Значок обязан пройти валидацию ДО вызова (контроллер, ProjectIconGlyphService.ValidateGlyph) —
    // инвариант «валидация на входе в стор» (§11.3).
    public Project SetIconGlyph(string id, ProjectGlyph glyph)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.Icon.Kind = ProjectIconKind.Glyph;
        project.Icon.Glyph = glyph;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Переключить режим отображения иконки (инициалы ↔ значок) БЕЗ стирания значка —
    // это «путь назад» на инициалы с сохранённым значком (и обратно). Переход в Glyph
    // допустим только при наличии Glyph (иначе показывать нечего) — гарантирует контроллер.
    public Project SetIconKind(string id, ProjectIconKind kind)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.Icon.Kind = kind;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Установить значок фоновой миграцией (ADR-009 §10). Отличается от SetIconGlyph двумя
    // вещами: UpdatedAt НЕ трогаем — по нему сортируется список проектов, и массовая
    // миграция перетасовала бы его целиком (та же причина, что у методов фона ниже); а
    // проверка «значка ещё нет» идёт под тем же локом, что и запись — выбор пользователя,
    // случившийся между отбором кандидатов миграции и обработкой, не перетирается.
    public bool TrySetIconGlyphMigrated(string id, ProjectGlyph glyph)
    {
        lock (_saveLock)
        {
            var project = _projects.GetValueOrDefault(id);
            if (project is null || project.Icon.Glyph is not null) return false;
            project.Icon.Kind = ProjectIconKind.Glyph;
            project.Icon.Glyph = glyph;
            JsonFileStore.Save(_storePath, _projects.Values.ToList());
            return true;
        }
    }

    // === Фон проекта (ADR-008) ===
    // Состояние живёт в самой записи проекта — отдельного стора «что уже прогнали» нет,
    // поэтому повторный запуск массового прогона это no-op. UpdatedAt здесь НЕ трогаем:
    // по нему идёт сортировка списка проектов, а фоновая генерация 39 фонов перетасовала
    // бы его целиком.

    /// <summary>
    /// Взять проект в работу: Pending + StartedAt под тем же локом, что и Save. Два тика
    /// прогона или тик и кнопка не заберут один проект дважды. Протухший Pending (сервер
    /// упал в середине) перезабирается.
    /// </summary>
    /// <param name="candidatesOnly">
    /// Режим массового прогона (ADR-008 §10): забирать только проекты, которых генерация
    /// НИКОГДА не касалась (<c>Background == null</c>), плюс протухший Pending, плюс Failed.
    /// Failed сюда попадает единственным путём — транзиентный возврат по явному включению
    /// флага (<c>ProjectBackgroundBackfill.IsCandidate</c> с <c>allowTransientRetry</c>);
    /// какой именно Failed подходит, решает IsCandidate, здесь — только механика захвата.
    /// Generated/Standard не проходят — защита от перетирания чужой успешной работы.
    /// Проверка идёт под тем же локом, что и захват, — иначе кнопка «Вернуть стандартный»,
    /// нажатая между выбором кандидатов и их обработкой, была бы перетёрта прогоном.
    /// </param>
    public bool TryBeginBackground(string id, TimeSpan? staleAfter = null, bool candidatesOnly = false)
    {
        var stale = staleAfter ?? TimeSpan.FromMinutes(10);
        lock (_saveLock)
        {
            var project = _projects.GetValueOrDefault(id);
            if (project is null) return false;
            var current = project.Background;
            if (current is { Kind: ProjectBackgroundKind.Pending }
                && current.StartedAt is { } started
                && DateTime.UtcNow - started < stale)
                return false;
            if (candidatesOnly && current is not null
                               and not { Kind: ProjectBackgroundKind.Pending }
                               and not { Kind: ProjectBackgroundKind.Failed })
                return false;

            project.Background = new ProjectBackground
            {
                Kind = ProjectBackgroundKind.Pending,
                TileFile = current?.TileFile,   // прежний тайл живёт, пока новый не готов
                StartedAt = DateTime.UtcNow,
                GeneratedAt = current?.GeneratedAt,
                Attempts = (current?.Attempts ?? 0) + 1,
            };
            JsonFileStore.Save(_storePath, _projects.Values.ToList());
        }
        return true;
    }

    /// <summary>Успешная генерация: ссылка на новый тайл, прежний файл удаляется.</summary>
    public Project SetBackgroundGenerated(string id, string tileFile)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        DeleteBackgroundAsset(id, project.Background?.TileFile, keep: tileFile);
        project.Background = new ProjectBackground
        {
            Kind = ProjectBackgroundKind.Generated,
            TileFile = tileFile,
            GeneratedAt = DateTime.UtcNow,
            Attempts = project.Background?.Attempts ?? 1,
        };
        Save();
        return project;
    }

    /// <summary>«Вернуть стандартный»: файл удаляется, автопрогон такой проект не трогает.</summary>
    public Project SetBackgroundStandard(string id)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        DeleteBackgroundAsset(id, project.Background?.TileFile, keep: null);
        project.Background = new ProjectBackground
        {
            Kind = ProjectBackgroundKind.Standard,
            Attempts = project.Background?.Attempts ?? 0,
        };
        Save();
        return project;
    }

    /// <summary>Неудача генерации: прежний тайл (если был) остаётся, повтор — только руками.</summary>
    public Project SetBackgroundFailed(string id, string reason)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        var previous = project.Background;
        // Был удачный тайл — остаёмся на нём: неудачная перегенерация не должна отбирать
        // у пользователя уже работающий фон
        project.Background = previous is { Kind: ProjectBackgroundKind.Pending, TileFile: not null }
            ? new ProjectBackground
            {
                Kind = ProjectBackgroundKind.Generated,
                TileFile = previous.TileFile,
                GeneratedAt = previous.GeneratedAt,
                Attempts = previous.Attempts,
                FailReason = reason,
            }
            : new ProjectBackground
            {
                Kind = ProjectBackgroundKind.Failed,
                Attempts = previous?.Attempts ?? 1,
                FailReason = reason,
            };
        Save();
        return project;
    }

    // Удалить прежний тайл фона (кроме keep); ошибки удаления не критичны
    private void DeleteBackgroundAsset(string projectId, string? file, string? keep)
    {
        if (string.IsNullOrEmpty(file) || file == keep) return;
        try { File.Delete(Path.Combine(BackgroundsDir, projectId, file)); } catch { /* не критично */ }
    }

    /// <summary>Сохраняет git-настройки проекта (remote, режим авто-коммита, override промпта коммита).</summary>
    /// <remarks>Конвенция строковых полей: null = «не менять», "" = «очистить» (сбросить в null).</remarks>
    public Project UpdateGitSettings(string id, string? remoteUrl = null, bool? autoCommit = null,
        bool? autoPush = null, string? commitPromptOverride = null)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        if (remoteUrl is not null) project.GitRemoteUrl = remoteUrl.Length == 0 ? null : remoteUrl;
        if (autoCommit is not null) project.GitAutoCommit = autoCommit.Value;
        if (autoPush is not null) project.GitAutoPush = autoPush.Value;
        if (commitPromptOverride is not null)
            project.CommitPromptOverride = commitPromptOverride.Length == 0 ? null : commitPromptOverride;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Зафиксировать исход каркаса знакомства v2: ключ применённого пресета или отказ
    // (ProjectPreset.None). Валидацию ключа делает вызывающий код (эндпоинт резолвит
    // его по каталогу) — здесь сеттер, как у остальных точечных обновлений.
    public Project SetPresetKey(string id, string key)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.PresetKey = key;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Кастомные колонки Kanban-доски проекта; пустой список/null → дефолтные 3
    public Project UpdateBoardColumns(string id, List<BoardColumn>? columns)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.BoardColumns = columns is null || columns.Count == 0 ? null : columns;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    public Project UpdateTags(string id, List<ProjectTag> registry)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.TagRegistry = registry ?? [];
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Все проекты, чей RootPath указывает на ту же папку (датасет знаний общий per-RootPath):
    // каскад удаления и события синка знаний должны учитывать соседей по папке
    public IReadOnlyList<Project> GetByRootPath(string rootPath)
    {
        var key = WorkspaceKnowledgeStore.NormalizePath(rootPath);
        return _projects.Values
            .Where(p => WorkspaceKnowledgeStore.NormalizePath(p.RootPath) == key)
            .ToList();
    }

    // Подмести ключ сервера из McpServersOn всех проектов владельца — вызывается при
    // удалении сервера из реестра и смене его ключа: новый сервер под старым ключом
    // молча унаследовал бы чужие права. Возвращает число тронутых проектов.
    public int PurgeMcpKey(string ownerId, string serverKey)
    {
        var key = serverKey.Trim().ToLowerInvariant();
        var touched = 0;
        foreach (var p in _projects.Values.Where(p => p.OwnerId == ownerId))
        {
            if (p.McpServersOn is not { Count: > 0 } on) continue;
            var kept = on.Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == on.Count) continue;
            p.McpServersOn = kept.Count == 0 ? null : kept;
            p.UpdatedAt = DateTime.UtcNow;
            touched++;
        }
        if (touched > 0) Save();
        return touched;
    }

    // Отвязывает все проекты от удаляемой группы (вызывается при удалении группы)
    public void ClearGroup(string groupId)
    {
        var changed = false;
        foreach (var p in _projects.Values.Where(p => p.GroupId == groupId))
        {
            p.GroupId = null;
            changed = true;
        }
        if (changed) Save();
    }

    // Части эффективного системного промпта в порядке отправки:
    // builtin — встроенная константа, user — промпт проекта, auto — автодополнения (Dify, теги).
    // Единственный источник состава промпта: и реальная отправка (BuildSystemPrompt),
    // и просмотр на UI (/effective-prompt) собираются отсюда.
    public static List<SystemPromptPart> GetSystemPromptParts(string? userPrompt, bool hasDify,
        Dictionary<string, List<string>>? documentTags = null)
    {
        var parts = new List<SystemPromptPart> { new("builtin", BuiltInSystemPrompt) };

        if (!string.IsNullOrWhiteSpace(userPrompt))
            parts.Add(new("user", userPrompt));

        if (hasDify)
        {
            var combined = string.Join("\n\n", parts.Select(p => p.Content));
            if (!combined.Contains("mcp__dify__search_knowledge"))
                parts.Add(new("auto",
                    "В этом проекте настроена база знаний Dify. Используй инструмент mcp__dify__search_knowledge для поиска по ней при ответе на вопросы о документации проекта. dataset_id уже настроен — указывать его не нужно.\n\n" +
                    "Если пользователь просит найти, поискать или проверить информацию — используй MCP-сервер Dify (search_knowledge) в первую очередь, до ответа из памяти."));

            var tagInstruction = BuildTagInstruction(documentTags);
            if (!string.IsNullOrEmpty(tagInstruction))
                parts.Add(new("auto", tagInstruction));
        }

        return parts;
    }

    public static string BuildSystemPrompt(string? userPrompt, bool hasDify,
        Dictionary<string, List<string>>? documentTags = null) =>
        string.Join("\n\n", GetSystemPromptParts(userPrompt, hasDify, documentTags).Select(p => p.Content));

    private static string BuildTagInstruction(Dictionary<string, List<string>>? documentTags)
    {
        if (documentTags is null || documentTags.Count == 0) return "";

        // Инвертируем: tag → список путей
        var byTag = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, tags) in documentTags)
            foreach (var tag in tags)
            {
                if (!byTag.TryGetValue(tag, out var list))
                    byTag[tag] = list = [];
                list.Add(path);
            }

        if (byTag.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("Теги документов в базе знаний:");
        foreach (var (tag, paths) in byTag.OrderBy(x => x.Key))
            sb.AppendLine($"  тег \"{tag}\": {string.Join(", ", paths)}");
        sb.Append("Если пользователь просит искать по тегу, вызови mcp__dify__search_knowledge, " +
                  "затем оставь только результаты, где segment.document.name входит в список выше для нужного тега.");
        return sb.ToString();
    }

    public bool Delete(string id)
    {
        var removed = _projects.TryRemove(id, out _);
        if (removed)
        {
            // Ассетов у иконки больше нет (ADR-009 §6) — чистим только тайлы фона:
            // осиротевших файлов не остаётся, сборщика сирот не требуется
            try
            {
                var dir = Path.Combine(BackgroundsDir, id);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* не критично */ }
            Save();
        }
        return removed;
    }

    private void Load()
    {
        var list = JsonFileStore.Load<List<Project>>(_storePath,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (list is not null)
            foreach (var p in list)
                _projects[p.Id] = p;

        // Миграция: проекты без OwnerId → первый пользователь
        var firstUser = _users.GetFirst();
        if (firstUser is not null)
        {
            var needsSave = false;
            foreach (var p in _projects.Values.Where(p => p.OwnerId is null))
            {
                p.OwnerId = firstUser.Id;
                needsSave = true;
            }
            if (needsSave) Save();
        }

        // Миграция: очищаем SystemPrompt от встроенных частей — теперь хранится только пользовательский текст
        var needsPromptMigration = false;
        foreach (var p in _projects.Values)
        {
            var original = p.SystemPrompt;

            // Убираем Dify-инструкцию из хранимого промпта (теперь добавляется динамически)
            if (p.SystemPrompt?.Contains("mcp__dify__search_knowledge") == true)
            {
                p.SystemPrompt = null;
            }

            // Старый дефолтный промпт теперь является встроенным — убираем из хранимого значения
            if (p.SystemPrompt == BuiltInSystemPrompt)
                p.SystemPrompt = null;

            // Встроенный fal-ai промпт мог попасть в пользовательскую часть — зачищаем
            var builtIn = BuiltInSystemPrompt.TrimEnd();
            if (p.SystemPrompt?.Contains(builtIn) == true)
            {
                if (p.SystemPrompt.Length <= builtIn.Length + 50)
                {
                    p.SystemPrompt = null;
                }
                else
                {
                    p.SystemPrompt = p.SystemPrompt
                        .Replace("\n\n" + builtIn, "")
                        .Replace(builtIn + "\n\n", "")
                        .Replace(builtIn, "")
                        .Trim();
                    if (string.IsNullOrWhiteSpace(p.SystemPrompt)) p.SystemPrompt = null;
                }
            }

            if (p.SystemPrompt != original)
                needsPromptMigration = true;
        }
        if (needsPromptMigration) Save();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            JsonFileStore.Save(_storePath, _projects.Values.ToList());
        }
    }
}

// Kind: builtin | user | auto
public record SystemPromptPart(string Kind, string Content);
