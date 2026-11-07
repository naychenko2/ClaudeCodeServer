using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

// Маршрут запуска claude CLI для чата: провайдер и модель места, корень профиля CLI
// (CLAUDE_CONFIG_DIR) с учётом среды владельца, рабочая папка глазами CLI, база деревьев,
// поиск/перенос/удаление транскрипта и авто-фейловер пула подписок. Вынесено из
// SessionManager (S3-2, docs/research/session-manager-split-2026-11.md §1); тела перенесены
// дословно, SessionManager держит делегирующий фасад.
// Ровно десять зависимостей — потолок класса ядра (SessionCoreShapeGuardTests, правило 2):
// новая зависимость здесь — сигнал резать класс, а не поднимать лимит (§2).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051)
// и DI нужен публичный конструктор; обход фасада закрывает сторож формы ядра (правило 4).
public sealed class CliRoute
{
    private readonly LlmProviderRegistry _providers;
    private readonly ClaudeSubscriptionPool _pool;
    private readonly ModelAssignmentResolver _assignments;
    private readonly UserStore _users;
    private readonly UserHomeResolver _homes;
    private readonly ILauncherFactory _launchers;
    private readonly SandboxManager _sandbox;
    private readonly ProjectManager _projects;
    private readonly SessionStore _store;
    private readonly ArchivedTranscriptStore _archive;
    private readonly ILogger<CliRoute> _log;

    public CliRoute(
        LlmProviderRegistry providers,
        ClaudeSubscriptionPool pool,
        ModelAssignmentResolver assignments,
        UserStore users,
        UserHomeResolver homes,
        ILauncherFactory launchers,
        SandboxManager sandbox,
        ProjectManager projects,
        SessionStore store,
        ArchivedTranscriptStore archive,
        ILogger<CliRoute> log)
    {
        _providers = providers;
        _pool = pool;
        _assignments = assignments;
        _users = users;
        _homes = homes;
        _launchers = launchers;
        _sandbox = sandbox;
        _projects = projects;
        _store = store;
        _archive = archive;
        _log = log;
    }

    // Транскрипт чата на диске сервера: локальный проект ведёт его на устройстве.
    internal bool TranscriptOnServer(Session info) =>
        info.ProjectId is null || _projects.GetById(info.ProjectId) is not { } p || ProjectCapabilities.TranscriptOnServer(p);

    public string? GetChatRoot(string sessionId, string ownerId)
    {
        var s = _store.GetById(sessionId);
        if (s is null || _store.ResolveOwnerId(s) != ownerId) return null;
        if (s.ProjectId is null) return ResolveChatRoot(ownerId);
        var root = _projects.GetById(s.ProjectId)?.RootPath;
        return root is null ? null : EffectiveRoot(s, root);
    }

    // Рабочая папка чата вне проекта: {домашняя папка владельца}/Chats (создаётся при отсутствии)

    // Выбрать подписку Claude для новой сессии: если модель Claude (не сторонний провайдер),
    // выбираем из пула подписок (least-loaded из способных обслужить модель — пин Opus
    // не должен попасть на аккаунт без Opus); для сторонних — по модели.
    internal string ResolveSubscriptionProvider(string? model)
    {
        var provider = _providers.ResolveByModel(model);
        if (provider is not null)
            return provider.Key;
        return _pool.Pick(model);
    }

    // Модель места по назначению (слоты «сильная/средняя/слабая» + таблица назначений + per-user слоты).
    // Подставляется, только когда модель НЕ задана явно и это НЕ resume: у транскрипта
    // resumed-сессии уже зафиксированы своя модель и провайдер, и подмена здесь сменила бы
    // провайдер и упёрлась в guard смены провайдера (400).
    // Родной Claude сводится к семейству и тут: модель чата — точка записи.
    internal string? ResolveDefaultModel(string usageKey, string? model, string? resumeSessionId, string? ownerId) =>
        _providers.CanonicalizeModel(
            !string.IsNullOrEmpty(resumeSessionId) ? model : _assignments.Resolve(usageKey, model, ownerId));

    // Место применения по признакам сессии — тот же порядок, что у ClaudeSession.UsageKey:
    // исполнитель задач специфичнее персоны, персона специфичнее обычного чата.
    internal static string UsageKeyFor(bool taskExecution, string? taskId, string? personaId) =>
        taskExecution || taskId is not null ? LocalActionCatalog.TasksExecutor
        : !string.IsNullOrWhiteSpace(personaId) ? LocalActionCatalog.ChatPersona
        : LocalActionCatalog.ChatNew;

    internal string ResolveChatRoot(string ownerId)
    {
        var user = _users.GetById(ownerId)
            ?? throw new KeyNotFoundException($"Пользователь не найден: {ownerId}");
        // Container-пользователи живут в отдельном корне (Sandbox:ProjectsRoot):
        // только он монтируется в песочницу — данные local-пользователей туда не попадают
        var home = _homes.Resolve(user)
            ?? throw new InvalidOperationException(
                UserHomeResolver.NotConfiguredMessage(user.ExecutionEnvironment));
        var path = Path.Combine(home, "Chats");
        Directory.CreateDirectory(path);
        return path;
    }

    // Корень профиля CLI (CLAUDE_CONFIG_DIR) для ключа провайдера сессии: подписка пула
    // (включая "claude", если задана с токеном) — claude-profiles/sub-{key}; ключ CLI-провайдера
    // — claude-profiles/{key}; иначе (локальный Claude — пул пуст, или provider не задан) —
    // пользовательский ~/.claude без оверрайда. Зеркалит выбор env хода в ClaudeSession
    // (BuildOAuthCliEnv / BuildCliEnv). ХОСТОВАЯ раскладка: у container-пользователя профиль
    // переписывается на песочный, поэтому ходить сюда напрямую нельзя — только через
    // ConfigRootFor(ownerId, key), который знает обе раскладки.
    private string ConfigRootForProvider(string? providerKey)
    {
        if (string.IsNullOrEmpty(providerKey))
            return _providers.UserProfileDir;
        if (_providers.GetByKey(providerKey) is not null)
            return _providers.GetProfileDir(providerKey);
        if (_pool.All.Any(s => s.Key == providerKey && s.Enabled))
            return _providers.GetProfileDir("sub-" + providerKey);
        return _providers.UserProfileDir;
    }

    // Корень профиля CLI С УЧЁТОМ СРЕДЫ владельца. У container-пользователя ход идёт в
    // песочнице, и DockerProcessRunner.RewriteProfileEnv подменяет профиль на
    // {ProfilesHostDir}/{ownerId}/{ключ}, где ключ — имя папки хостового профиля, а «без
    // оверрайда» (~/.claude) → "default". Ключ выводится именно из имени папки (та же
    // операция, что в RewriteProfileEnv), а не из providerKey: ConfigRootForProvider("claude")
    // отдаёт sub-claude, если запись «claude» задана в пуле с токеном, и наивный маппинг
    // «primary → default» разошёлся бы с реальной раскладкой хода.
    internal string ConfigRootFor(string? ownerId, string? providerKey)
    {
        var hostRoot = ConfigRootForProvider(providerKey);
        if (ownerId is null || _users.GetById(ownerId)?.ExecutionEnvironment != ExecutionEnvironments.Container)
            return hostRoot;
        var key = string.Equals(Path.GetFullPath(hostRoot), Path.GetFullPath(_providers.UserProfileDir),
                StringComparison.OrdinalIgnoreCase)
            ? "default"
            : Path.GetFileName(hostRoot.TrimEnd('\\', '/'));
        return Path.Combine(_sandbox.ProfilesHostDir, ownerId, key);
    }

    // База деревьев проекта: {home}/.worktrees/<slug проекта> — вне дерева главной репы и
    // внутри Sandbox:ProjectsRoot у container-пользователей. Одна формула для деревьев чатов
    // (SetWorktreeAsync) и слотов ревью (ReviewTreePool).
    internal string WorktreesBase(string? ownerId, Project project)
    {
        var user = ownerId is null ? null : _users.GetById(ownerId);
        var home = (user is null ? null : _homes.Resolve(user))
            ?? throw new InvalidOperationException("Не удалось определить домашнюю папку владельца");
        var projSlug = PersonaManager.Slugify(project.Name);
        if (projSlug.Length == 0) projSlug = project.Id[..Math.Min(8, project.Id.Length)];
        return Path.Combine(home, ".worktrees", projSlug);
    }

    // Рабочая папка ГЛАЗАМИ CLI: у container-пользователя процесс видит контейнерный путь
    // (/projects/…), по нему же CLI уплощает имя папки транскрипта. Путь вне монтирований
    // песочницы ToRuntime отвергает исключением (аналог SafeJoin) — вызывающий решает сам,
    // отдать его наружу (явная миграция → 400) или деградировать тихо (авто-фейловер).
    internal string CwdForOwner(string? ownerId, string hostCwd)
    {
        var launcher = _launchers.ForOwner(ownerId);
        return launcher.IsSandboxed ? launcher.Paths.ToRuntime(hostCwd) : hostCwd;
    }

    // Рабочая папка сессии: отдельное worktree чата приоритетнее корня проекта.
    // Единая точка подмены cwd — через неё идут обе funnel-точки LlmSessionContext.
    // internal — той же формулой DifyToolset резолвит дефолтный датасет проекта чата
    // (волна 4): расхождение формул означало бы разный состав tools/list и shape.
    internal static string EffectiveRoot(Session session, string fallbackRoot) =>
        session.WorktreePath ?? fallbackRoot;

    // Рабочая папка сессии (для поиска транскрипта по уплощённому cwd);
    // null — папку определить не удалось (миграцию в этом случае не делаем)
    internal string? TryResolveCwd(Session s)
    {
        try
        {
            if (s.ProjectId is not null)
            {
                var root = _projects.GetById(s.ProjectId)?.RootPath;
                return root is null ? null : EffectiveRoot(s, root);
            }
            return s.OwnerId is null ? null : ResolveChatRoot(s.OwnerId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Авто-фейловер пула подписок: аккаунт чата исчерпан, а в пуле есть здоровая
    // альтернатива — тихо перевозим транскрипт в её профиль и меняем Session.Provider.
    // Для пользователя незаметно: та же модель, тот же эндпоинт, та же предоплаченная
    // подписка. Сторонние провайдеры сюда не входят — другая модель/качество/оплата,
    // это только явная миграция (MigrateProviderAsync + карточка-предложение).
    internal void TryPoolFailover(string sessionId, SessionEntry entry)
    {
        if (_providers.ResolveByModel(entry.Info.Model) is not null) return; // сторонний — не пул
        if (!_pool.HasExtra) return;
        var current = entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
        if (!_pool.IsExhausted(current)) return;
        var pick = _pool.Pick(entry.Info.Model);
        if (pick == current || _pool.IsExhausted(pick)) return; // переключаться некуда

        var ownerId = _store.ResolveOwnerId(entry.Info);
        // Локальный проект: транскрипт на устройстве, профиль CLI там один — переносить нечего
        if (entry.Info.ClaudeSessionId is not null && TranscriptOnServer(entry.Info))
        {
            var hostCwd = TryResolveCwd(entry.Info);
            if (hostCwd is null) return;
            // Транскрипт container-пользователя лежит под КОНТЕЙНЕРНЫМ cwd в песочном
            // профиле. Путь вне монтирований (проект переехал наружу) — не повод ронять
            // ход: фейловер здесь и так деградирует тихо, чат просто ждёт сброса окна
            string cwd;
            try { cwd = CwdForOwner(ownerId, hostCwd); }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[SessionManager] Фейловер пула отменён ({sessionId}): {ex.Message}");
                return;
            }
            if (!TranscriptMigrator.TryMigrate(ConfigRootFor(ownerId, current),
                    ConfigRootFor(ownerId, pick), cwd, entry.Info.ClaudeSessionId, out var error))
            {
                Console.Error.WriteLine($"[SessionManager] Фейловер пула отменён ({sessionId}): {error}");
                return;
            }
        }

        entry.Info.Provider = pick;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        _store.SaveSessions();
        Console.WriteLine($"[SessionManager] Чат {sessionId} переключён на подписку «{pick}» (лимит «{current}»)");
        DeferredRun.FireAndForget(_store.BroadcastAsync(sessionId, new ProviderSwitchedMessage(pick, Auto: true)),
            $"broadcast provider_switched ({sessionId})");
    }

    // Копия транскрипта при архивации чата (шаг 0 плана «Архив чатов»): копия нужна, чтобы
    // при возврате из архива восстановить переписку в профиле — оригинал мог быть убран при
    // удалении (DeleteTranscript): за время жизни чат мог мигрировать между профилями и
    // рабочими папками, а миграции исходники не удаляют. Сам стор валидирует csid белым
    // списком; best-effort — сбой не имеет права ронять архивацию.
    internal void ArchiveTranscriptCopy(Session info)
    {
        try
        {
            if (info.ClaudeSessionId is not string csid) return;
            // Локальный проект: транскрипт на устройстве, копировать с диска сервера нечего —
            // поиск по своему пути нашёл бы разве что чужой файл
            if (!TranscriptOnServer(info)) return;
            _archive.Archive(csid, TranscriptSearchRoots(info), TryResolveCwd(info));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Копия транскрипта при архивации чата {SessionId} не создана", info.Id);
        }
    }

    // Возврат копии при возврате чата: цель резолвится НА МОМЕНТ возврата — за время в
    // архиве могли смениться профиль провайдера (MigrateProviderAsync) и папка уплощённого
    // cwd (worktree, правка RootPath), поэтому исходный путь не запоминаем.
    internal void RestoreTranscriptCopy(Session info)
    {
        try
        {
            if (info.ClaudeSessionId is not string csid) return;
            // Локальный проект копию при архивации не делал — возвращать нечего
            if (!TranscriptOnServer(info)) return;
            var hostCwd = TryResolveCwd(info);
            if (hostCwd is null) return;
            var ownerId = _store.ResolveOwnerId(info);
            _archive.Restore(csid,
                ConfigRootFor(ownerId, info.Provider), CwdForOwner(ownerId, hostCwd));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Копия транскрипта при возврате чата {SessionId} не возвращена", info.Id);
        }
    }

    // Архивная копия транскрипта для ролей жизненного цикла (S3-5): поиск копии и потолок её
    // размера нужны ветвлению, удаление копии вместе с чатом — удалению. Сам
    // ArchivedTranscriptStore остаётся у маршрута (здесь же его Archive/Restore), а создающему
    // и удаляющему роли он в конструктор не нужен — достаточно этих трёх тонких записей.
    internal string? FindArchivedCopyPath(string csid) => _archive.FindCopyPath(csid);
    internal long ArchivedCopyMaxBytes => _archive.MaxCopyBytes;
    internal void DeleteArchivedCopy(string csid) => _archive.Delete(csid);

    // Корни профилей CLI для поиска транскрипта чата: все конфиг-корни провайдеров плюс
    // профили песочницы владельца (раскладка RewriteProfileEnv; берём ВСЕ папки владельца,
    // а не один ключ — чат мог мигрировать). Выделено из DeleteTranscript: тот же список
    // нужен и копии при архивации.
    internal IEnumerable<string> TranscriptSearchRoots(Session info)
    {
        var roots = new List<string>(_providers.GetAllConfigRoots());
        if (_store.ResolveOwnerId(info) is string ownerId)
        {
            var ownerProfiles = Path.Combine(_sandbox.ProfilesHostDir, ownerId);
            if (Directory.Exists(ownerProfiles))
                roots.AddRange(Directory.GetDirectories(ownerProfiles));
        }
        return roots;
    }

    // Транскрипт claude CLI удаленного чата ({профиль}/projects/{уплощенный cwd}/{csid}.jsonl).
    // Своя история (data/sessions/{csid}) уходит через ChatHistoryService, а этот файл раньше
    // не убирал никто — переписка удаленного чата лежала на диске до плановой уборки CLI
    // (~30 дней), хотя воспользоваться ей уже нельзя: --resume делать некому.
    //
    // Ищем во ВСЕХ профилях, а не только в текущем: переезды между профилями (TryMigrate при
    // смене провайдера) и между рабочими папками (TryRelocateCwd при worktree) намеренно
    // оставляют копии. Точность обеспечивает сам ключ — файл называется uuid'ом сессии, так
    // что чужие чаты, другие инстансы сервера и интерактивные сессии пользователя, живущие в
    // том же ~/.claude, задеть невозможно.
    //
    // Best-effort: удаление чата важнее уборки, поэтому любой сбой остается в логе.
    internal void DeleteTranscript(Session info, string claudeSessionId)
    {
        try
        {
            var removed = TranscriptMigrator.DeleteEverywhere(
                TranscriptSearchRoots(info), TryResolveCwd(info), claudeSessionId);
            if (removed > 0)
                _log.LogInformation("Транскрипт чата {SessionId} удален ({Count} файлов)", info.Id, removed);
            else
                // Штатных причин две: транскрипт уже вычистил сам CLI (плановая уборка ~30 дней)
                // либо ходов в чате не было. Но сюда же попадает «файл нашелся, а удалить не
                // дали» — поэтому не утверждаем, что убирать было нечего, и отправляем за
                // подробностями в лог TranscriptMigrator
                _log.LogInformation(
                    "Транскрипт чата {SessionId} не убран: не найден либо не удалился (подробности выше)",
                    info.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось убрать транскрипт чата {SessionId}", info.Id);
        }
    }

    // Рабочая папка хода для поиска транскрипта: дерево чата, иначе корень проекта.
    // Точность тут не критична — TranscriptProbe при промахе по конвенции сканирует
    // все проекты зарегистрированных корней, cwd лишь сужает первый поиск.
    private string ResolveTurnCwd(Session info) =>
        info.WorktreePath
        ?? (info.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null)
        ?? "";

    // Транскрипт resume-якоря чата: null — якоря нет или файл не найден (проверку
    // целостности тогда пропускаем). Обе точки RestartStuckTurnAsync — гейт
    // спасательной ветки и валидация перед resume — ищут файл одним путём.
    // У локального проекта всегда null: транскрипт на устройстве, его целость проверит сам CLI
    // на --resume и честно уронит ход (TurnFailureText.ResumeTranscriptMissing), а промах
    // поиска по серверному пути нельзя путать с «транскрипта нет».
    internal string? FindResumeTranscript(SessionEntry entry) =>
        entry.Info.ClaudeSessionId is { } csid && TranscriptOnServer(entry.Info)
            ? Llm.Claude.TranscriptProbe.FindMainTranscript(ResolveTurnCwd(entry.Info), csid)
            : null;
}
