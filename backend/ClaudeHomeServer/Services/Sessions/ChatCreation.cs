using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;


namespace ClaudeHomeServer.Services.Sessions;

// Создание чата (S3-5, docs/research/session-manager-split-2026-11.md §1 «жизненный цикл», §2):
// обычный чат проекта, чат вне проекта, чат от лица персоны, групповой чат, а также
// способ породить чат из существующего — ветка по транскрипту. Тела перенесены из
// SessionManager дословно; фасад держит делегирующие обёртки
// прежних публичных сигнатур (на них сидят контроллеры и тулсеты). Класс internal: его
// никто не принимает в публичном конструкторе — фасад собирает его сам (переходный режим)
// или через AddSessionCore.
//
// Зависимостей девять: стор (реестр, видимость, рассылка), ChatHistoryService (чтение и
// запись истории ветки), маршрут (резолв модели/подписки, корни профиля, cwd, архивная
// копия), хост процесса (старт нового чата, гейт ожидания устройства), проекты, роль слоя
// персоны (резолв персоны — PersonaManager в конструкторы ролей ядра не входит), резолвер
// назначений моделей, настройки чатов (рамка видимости персоны, состав группы) и шина
// событий хода (SessionBranched).
//
// Отклонения от таблицы плана §2 (каждое — с комментарием у места; отдельным решением они не
// подтверждены — на проверке у Александра):
// — pluginDir в конструктор не взят: ни одно тело этой роли его не читает (--plugin-dir
//   относится к контексту запуска и живёт в TurnLaunchContextFactory); вместо него —
//   резолвер назначений и роль слоя персоны, которые здесь нужны;
// — EnsureSuccessorAllowed — здесь же, где его вызывающие (таблица плана их разделяла
//   только по файлам);
// — публичные вложенные типы фасада (ChatBranchResult, ChatBranchInclude) не переезжают:
//   на них смотрит контракт контроллера и тестов, здесь они используются (алиасы выше);
// — GetPersonaChats читает реестр стора напрямую (тот же фильтр, что был в фасаде).
// Класс public по той же причине, что и роли S3-2…S3-4: его принимает публичный конструктор
// фасада (internal-тип в публичном конструкторе — CS0051). Вовне он не назван ни одним типом:
// граница держит сторож правила 4.
public sealed class ChatCreation
{
    private readonly SessionStore _store;
    private readonly ChatHistoryService _history;
    private readonly CliRoute _route;
    private readonly TurnProcessHost _process;
    private readonly ProjectManager _projects;
    private readonly PersonaLayerBuilder _personaLayer;
    private readonly Llm.ModelAssignmentResolver _assignments;
    private readonly ChatSettings _settings;
    // Не readonly, как поле фасада: ленивая шина ниже создаёт локальный экземпляр, когда
    // роль собрана без подсистемы Turn (юниты без DI).
    private Turn.ITurnEventBus? _turnEvents;

    public ChatCreation(SessionStore store, ChatHistoryService history, CliRoute route,
        TurnProcessHost process, ProjectManager projects, PersonaLayerBuilder personaLayer,
        Llm.ModelAssignmentResolver assignments, ChatSettings settings, Turn.ITurnEventBus? turnEvents)
    {
        _store = store;
        _history = history;
        _route = route;
        _process = process;
        _projects = projects;
        _personaLayer = personaLayer;
        _assignments = assignments;
        _settings = settings;
        _turnEvents = turnEvents;
    }

    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;
    // Та же ленивая шина, что у фасада: без подсистемы Turn события создаются локально,
    // с ней — идёт общий синглтон (его передаёт конструктор фасада).
    private Turn.ITurnEventBus TurnEvents => _turnEvents ??= new Turn.TurnEventBus();

    // === Ветвление чата (docs/research/chat-branching-2026-09.md) ===

    // Новый чат, который начинается там же, где исходный: транскрипт исходного чата
    // режется по якорю (сообщению пользователя), копия префикса кладётся в новый файл
    // профиля под новым ClaudeSessionId, история ветки строится из истории оригинала по
    // той же границе. Оригинал НЕ изменяется ни одним мутатором (ни SaveSessions по нему, ни
    // UpdatedAt/ArchivedAt/ClaudeSessionId) — источник читается, не трогается.
    //
    // userMessageIndex — 0-based номер элемента StoredUserMessage в истории оригинала
    // (включая служебные ходы auto/staffNote/systemDirective — они тоже user_message).
    // anchorText — фрагмент текста этого сообщения от клиента; сервер сверяет его с
    // найденным по индексу и на расхождении отказывает 409 (индекс — договорённость двух
    // счётчиков, молча резать не там нельзя).
    internal async Task<ChatBranchResult> BranchAsync(string sessionId, string ownerId,
        int userMessageIndex, string anchorText, string include, string? name = null)
    {
        // S2d-7b-1: GetOwnedVisible, а не GetOwned — чужой приватный чат не ветвится даже владельцем
        // проекта (не копируем его транскрипт и не плодим чат-преемник с его содержимым)
        if (_store.GetOwnedVisible(sessionId, ownerId) is not { } source
            || !_sessions.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("Чат не найден");

        // §9.1 — идёт ход или живут фоновые агенты (копируем файл, в который пишет CLI)
        if (SessionStore.HasTurnInFlight(entry))
            throw new InvalidOperationException(
                "В чате идёт ход — дождитесь его завершения или прервите его, затем попробуйте снова");
        if (entry.Process is { HasTrackedBg: true })
            throw new InvalidOperationException(
                "В чате работают фоновые агенты — дождитесь их завершения, затем попробуйте снова");

        // Локальный проект: транскрипт-источник на устройстве, копировать его серверу неоткуда
        if (source.ProjectId is { } branchProjectId && _projects.GetById(branchProjectId) is { } branchProject)
            Composition.ProjectCapabilityGuard.EnsureAllowed(branchProject, Composition.ProjectCapabilityArea.Transcript);

        // §9.2 — нет ClaudeSessionId: на экране история есть, в памяти модели — нет
        if (source.ClaudeSessionId is not string csid)
            throw new InvalidOperationException("Чат ещё не обращался к модели: ветвить нечего");

        // §9.6, §9.8 — групповой чат, штаб, отдельное worktree: отказы общие для всех
        // способов породить из чата новый (ветвление, перенос в новый чат)
        EnsureSuccessorAllowed(source, "ветвить", "ветки");

        if (include != ChatBranchInclude.Turn && include != ChatBranchInclude.BeforePrompt)
            throw new InvalidOperationException($"Неизвестное значение include: «{include}»");

        // §5 — адресация шага: тот же номер, что считает клиент по StoredUserMessage истории
        var history = await _history.LoadAsync(csid);
        var userMessages = history.OfType<StoredUserMessage>().ToList();
        if (userMessageIndex < 0 || userMessageIndex >= userMessages.Count)
            throw new InvalidOperationException(
                $"Сообщение с индексом {userMessageIndex} не найдено в истории чата");
        var anchorMessage = userMessages[userMessageIndex];

        // Сверка текста с индексом — fail-closed: живая лента клиента и серверная история
        // расходятся штатно, молча резать не там нельзя (§6 документа-основания)
        var normalizedFound = Llm.TranscriptBrancher.Normalize(anchorMessage.Text);
        var normalizedProvided = Llm.TranscriptBrancher.Normalize(anchorText);
        if (normalizedProvided.Length == 0
            || !normalizedFound.Contains(normalizedProvided, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Индекс сообщения и присланный текст разошлись — обновите чат и попробуйте снова");

        // §Резолв источника: самая длинная копия среди всех профилей (147 из 487 чатов имеют
        // копии разной длины — короткая дала бы ветку без начала разговора), фолбэк — архив
        // заархивированного чата. §9.4 — не найдено нигде — 400 (переносить нечего ≠ копировать
        // нечего). Оригинал из архива не возвращаем — ArchivedAt не трогаем.
        var searchRoots = _route.TranscriptSearchRoots(source);
        var hostCwd = _route.TryResolveCwd(source);
        var sourceCwd = hostCwd is null ? null : _route.CwdForOwner(ownerId, hostCwd);

        string? srcPath = null;
        var bestLen = -1L;
        foreach (var file in Llm.TranscriptMigrator.FindAllTranscripts(searchRoots, sourceCwd, csid))
        {
            var len = new FileInfo(file).Length;
            if (len > bestLen) { bestLen = len; srcPath = file; }
        }
        srcPath ??= _route.FindArchivedCopyPath(csid);

        if (srcPath is null)
            throw new InvalidOperationException(
                "Память этого разговора уже убрана плановой уборкой CLI");

        // §9.7 — потолок размера, тот же, что у ArchivedTranscriptStore.MaxCopyBytes
        if (new FileInfo(srcPath).Length > _route.ArchivedCopyMaxBytes)
            throw new InvalidOperationException(
                "Транскрипт чата слишком большой для ветвления (потолок 512 МБ)");

        // Новый csid — свой, не общий с оригиналом (иначе два чата делили бы один транскрипт
        // и одну историю, и удаление одного унесло бы память другого)
        var newCsid = Guid.NewGuid().ToString();

        // Схема хранения (§3): профиль тот же, что у оригинала; cwd — ВЕТКИ, не оригинала
        // (ветка не наследует WorktreePath — гейт §9.8 гарантирует, что у source его и нет,
        // поэтому cwd ветки = корень проекта/чатов владельца)
        var branchConfigRoot = _route.ConfigRootFor(ownerId, source.Provider);
        var branchRootPath = source.ProjectId is { } sourcePid
            ? (_projects.GetById(sourcePid)?.RootPath
                ?? throw new InvalidOperationException("Проект чата не найден"))
            : _route.ResolveChatRoot(ownerId);
        var branchCwd = _route.CwdForOwner(ownerId, branchRootPath);

        var dstDir = Path.Combine(branchConfigRoot, "projects", Llm.TranscriptMigrator.FlattenCwd(branchCwd));
        Directory.CreateDirectory(dstDir);
        var dstPath = SafePath.Join(dstDir, newCsid + ".jsonl");

        // Якоря для резака: неслужебные сообщения истории до якоря (не включая его) +
        // сам якорь последним (TranscriptBrancher трактует последний элемент как якорь шага)
        var anchors = new List<string>();
        for (var i = 0; i < userMessageIndex; i++)
        {
            var m = userMessages[i];
            if (m.Auto == true || m.SystemDirective == true || m.StaffNote != null || m.ViaAgent == true)
                continue;
            anchors.Add(m.Text);
        }
        anchors.Add(anchorMessage.Text);

        // Точный якорь (шаг 5): uuid хвоста транскрипта, записанный на конце ЯКОРНОГО хода —
        // последний result этого хода, то есть до следующего сообщения пользователя. Есть —
        // резак берёт границу точным сравнением; нет (чат до этого поля) — текстовый путь.
        var anchorHistoryIndex = history.IndexOf(anchorMessage);
        string? anchorUuid = null;
        for (var i = anchorHistoryIndex + 1; i < history.Count; i++)
        {
            if (history[i] is StoredUserMessage) break;
            if (history[i] is StoredResultMessage { TranscriptTailUuid: { } uuid }) anchorUuid = uuid;
        }

        // §9.5 — границу не удалось сопоставить (отказ резака) — 409, резать наугад нельзя.
        // include резак обязан знать: у beforePrompt граница транскрипта — сам якорный промпт
        // (не включая), иначе лента ветки и её транскрипт расходятся — на экране вопроса нет,
        // а в памяти модели и вопрос, и прежний ответ на него.
        // anchorTimestampMs — главный ключ текстового пути резака (ResolveAnchorPrompt):
        // время отправки якорного сообщения, по лагу до записи промпта в транскрипт
        // различаются повторяющиеся тексты («продолжай», тики /loop). null — история до
        // этого поля, там резак работает по цепочке.
        var branchResult = Llm.TranscriptBrancher.Branch(srcPath, anchors, newCsid, dstPath, anchorUuid,
            include == ChatBranchInclude.BeforePrompt
                ? Llm.TranscriptBrancher.BranchInclude.BeforePrompt
                : Llm.TranscriptBrancher.BranchInclude.Turn,
            anchorMessage.Timestamp);
        if (!branchResult.Ok)
        {
            Console.Error.WriteLine($"[SessionManager] Ветвление чата {sessionId} отказано резаком: {branchResult.Reason}");
            throw new InvalidOperationException(
                "Не удалось найти этот шаг в памяти модели; попробуйте ветвиться от другого сообщения");
        }

        // Обрезанная история ветки: turn — по конец хода (следующий user_message или конец
        // файла), beforePrompt — до якорного сообщения (оно не входит, его текст — draft,
        // иначе транскрипт заканчивался бы висящим промптом и следующий ход подклеил бы второй)
        int cutAt;
        string? draft = null;
        if (include == ChatBranchInclude.BeforePrompt)
        {
            cutAt = anchorHistoryIndex;
            draft = anchorMessage.Text;
        }
        else
        {
            // Граница — следующий user_message ИЛИ собственная плашка источника: когда
            // ветвят саму ветку от её последнего хода, чужая плашка «Ветка от …» (§7 — она
            // всегда последняя запись истории) попала бы в копию и встала бы второй рядом
            // со свежей. Унаследованная плашка неверна: у новой ветки источник свой.
            cutAt = history.Count;
            for (var i = anchorHistoryIndex + 1; i < history.Count; i++)
                if (history[i] is StoredUserMessage or StoredBranchedFromMessage) { cutAt = i; break; }
        }

        // Резак отступил назад: хвост последнего хода префикса оказался непарным (прерванный
        // ход, кнопка «Стоп») и фактическая граница транскрипта ушла к НАЧАЛУ этого хода.
        // История обязана совпасть с ней шаг в шаг, иначе в ленте остался бы ход, которого
        // нет в памяти модели. 409 тут не отдаём (продуктовое решение): отказывать за то, что
        // разговор когда-то прервался, хуже, чем перенести незавершённый ввод в черновик —
        // тем же путём, что уже работает у beforePrompt.
        if (branchResult.AnchorTurnExcluded)
        {
            // Сообщение, начавшее исключённый ход: у turn это сам якорь, у beforePrompt —
            // предыдущее сообщение пользователя (якорный ход в ветку и так не входил).
            var droppedIndex = anchorHistoryIndex;
            if (include == ChatBranchInclude.BeforePrompt)
            {
                droppedIndex = -1;
                for (var i = anchorHistoryIndex - 1; i >= 0; i--)
                    if (history[i] is StoredUserMessage) { droppedIndex = i; break; }
                if (droppedIndex < 0)
                    throw new InvalidOperationException(
                        "Не удалось найти этот шаг в памяти модели; попробуйте ветвиться от другого сообщения");
            }
            cutAt = droppedIndex;
            draft = (history[droppedIndex] as StoredUserMessage)?.Text;
        }

        var branchHistory = history.Take(cutAt).ToList();
        // Плашка «Ветка от …» — последней записью, ровно в точке расхождения (§7)
        branchHistory.Add(new StoredBranchedFromMessage
        {
            SourceSessionId = sessionId,
            SourceName = source.Name ?? "Чат",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await _history.SaveAsync(newCsid, branchHistory);

        // Имя по умолчанию — «{имя оригинала} (ветка)»; явно переданное имя перебивает
        var branchName = string.IsNullOrWhiteSpace(name)
            ? $"{(string.IsNullOrWhiteSpace(source.Name) ? "Чат" : source.Name)} (ветка)"
            : name.Trim();

        // Создание чата штатной фабрикой через общий хелпер преемника: resumeSessionId = новый
        // csid — StartNewSessionAsync подхватит уже подготовленные транскрипт и историю.
        // Поля, специфичные именно для ветки (снимок cost-state родителя и ссылка на источник),
        // передаются configure.
        var branchSession = await CreateSuccessorChatAsync(source, ownerId, branchName, newCsid,
            b =>
            {
                // Накопленный cost-state CLI: транскрипт ветки — копия префикса родителя (с его
                // cost-state), поэтому --resume ветки восстановит накопленный итог родителя. Базой
                // для дельты первого хода ветки берём снимок родителя, иначе он засчитал бы всю
                // историю родителя. (См. Session.LastCostState / ClaudeSession.ComputeTurnDelta.)
                b.LastCostState = source.LastCostState;
                b.BranchedFromSessionId = sessionId;
            });

        // Подсистемы со своим состоянием по sessionId (нити редактора картинок) копируют его под
        // ветку (ADR-019 §2). Notification: сбой подписчика ветвление не роняет
        await TurnEvents.PublishAsync(new Turn.SessionBranched(
            new Turn.TurnContext(branchSession.Id, ownerId, 0, 0, branchSession.ProjectId), sessionId));

        return new ChatBranchResult(branchSession, draft);
    }

    // Отказы, общие для всех способов породить из чата новый (ветвление, перенос в новый чат):
    // состояние группового чата (спикеры) и штаба (волны) живёт в Session, а не в разговоре —
    // преемник унаследовал бы ленту без состояния; worktree-чат указывал бы преемника на
    // дерево, которое сносится вместе с чатом-источником. verb — «ветвить»/«переносить»,
    // successor — родительный падеж преемника («ветки»/«нового чата»).
    private static void EnsureSuccessorAllowed(Session source, string verb, string successor)
    {
        if (source.Participants is { Count: > 0 })
            throw new InvalidOperationException(
                $"Групповой чат нельзя {verb}: состав спикеров живёт в чате, а не в транскрипте");
        if (source.TeamImplement is not null)
            throw new InvalidOperationException(
                $"Чат в режиме «Командная реализация» нельзя {verb}: состояние волн живёт в чате, а не в транскрипте");
        if (source.WorktreePath is not null)
            throw new InvalidOperationException(
                $"Чат в отдельном рабочем дереве нельзя {verb}: контекст {successor} указывал бы на дерево, которое сносится вместе с чатом-источником");
    }

    // Новый чат-преемник штатной фабрикой (выбор по PersonaId/ProjectId источника) плюс
    // наследование полей §11 (docs/research/chat-branching-2026-09.md). Фабрики персоны/проекта
    // не берут все поля параметрами (персональная вовсе подставляет модель персоны, а не
    // источника), поэтому поля §11 переписываются на значения источника уже ПОСЛЕ создания.
    // resumeSessionId — csid заранее подложенных транскрипта и истории (ветвление) или null
    // (перенос: чистый разговор, csid выдаст первый ход). configure — поля конкретного способа
    // (у ветки — BranchedFromSessionId и снимок cost-state), сохраняются тем же SaveSessions.
    private async Task<Session> CreateSuccessorChatAsync(Session source, string ownerId, string name,
        string? resumeSessionId, Action<Session>? configure = null)
    {
        // Приватность преемника (S2d-7b-1): содержимое приватного чата не уходит в общий, ветка/перенос
        // остаются приватными у того же автора. Читаем один раз и синхронно — до первого await
        var privateAuthor = source.PrivateToUserId;
        var successor = !string.IsNullOrWhiteSpace(source.PersonaId)
            ? await CreatePersonaChatAsync(ownerId, source.PersonaId!, source.Mode,
                resumeSessionId: resumeSessionId, name: name, contextProjectId: source.ProjectId,
                createdByUserId: privateAuthor, privateToUserId: privateAuthor)
            : source.ProjectId is not null
                ? await CreateAsync(source.ProjectId, source.Mode, resumeSessionId: resumeSessionId,
                    name: name, model: source.Model, effort: source.Effort,
                    createdByUserId: privateAuthor, privateToUserId: privateAuthor)
                : await CreateChatAsync(ownerId, source.Mode, resumeSessionId: resumeSessionId,
                    name: name, model: source.Model, effort: source.Effort);

        if (!_sessions.TryGetValue(successor.Id, out var successorEntry)) return successor;
        var s = successorEntry.Info;
        // Наследуем: Model/Provider/Effort/Context/VoiceMode/VoiceStyle/NotificationsMuted —
        // тот же разговор тем же собеседником; ExcludeFromDossiers — иначе преемник обходит
        // явный opt-out «не сохранять решения» (Session.cs:380-382).
        s.Model = source.Model;
        s.Provider = source.Provider;
        s.Effort = source.Effort;
        s.Context = [.. source.Context.Select(c => new SessionContextEntry
            { Type = c.Type, Id = c.Id, Title = c.Title })];
        s.VoiceMode = source.VoiceMode;
        s.VoiceStyle = source.VoiceStyle;
        s.NotificationsMuted = source.NotificationsMuted;
        s.ExcludeFromDossiers = source.ExcludeFromDossiers;
        // НЕ наследуем: AutoAllowTools (человек переспросит один раз, а не тихая эскалация
        // прав), WorktreePath/WorktreeBranch/TaskId/TaskExecution/ExpiresAfterMinutes/
        // ArchivedAt/ArchiveBatchId/LastReadAt — фабрика их и так не выставляет.
        configure?.Invoke(s);
        _store.SaveSessions();
        return s;
    }

    // === Фабрики чатов ===

    internal async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null, string? createdByUserId = null, string? privateToUserId = null)
    {
        var project = _projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

        var defaultModel = _route.ResolveDefaultModel(CliRoute.UsageKeyFor(taskExecution, taskId, personaId),
            model, resumeSessionId, project.OwnerId);
        var session = new Session
        {
            ProjectId = projectId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim(),
            AgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim(),
            Provider = _route.ResolveSubscriptionProvider(defaultModel),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer).
            // Маршрутизация остаётся по вызывающему коду (задача), не по зоне персоны.
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
            TaskId = taskId,
            // Онбординг-сессия: задаётся ДО старта — BuildPersonaLayer читает поле при сборке слоя
            OnboardingKind = onboardingKind,
            // Автор чата — кто его создал (S2d-1c): id вызывающего из JWT контроллера. На видимость
            // не влияет (S2d-1); ключом хранилищ по-прежнему служит владелец проекта, не автор.
            CreatedByUserId = string.IsNullOrWhiteSpace(createdByUserId) ? null : createdByUserId.Trim(),
            // Приватность задаётся ДО регистрации сессии (S2d-7b-1): чат-преемник приватного чата не
            // должен ни на миг стать общим — иначе его увидели бы участники проекта
            // Пустая строка — «не видит никто» (fail-closed): не нормализуем её в null
            PrivateToUserId = privateToUserId,
        };

        await _process.StartNewSessionAsync(session, project.RootPath, project.SystemPrompt,
            () => _projects.GetById(projectId)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
        return session;
    }

    // Создание чата вне проекта: рабочая папка — {домашняя папка владельца}/Chats,
    // системный промпт — только встроенная часть (rawSystemPrompt=null), без проектных правил.
    internal async Task<Session> CreateChatAsync(string ownerId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? effort = null,
        string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null)
    {
        var rootPath = _route.ResolveChatRoot(ownerId);

        var defaultModel = _route.ResolveDefaultModel(CliRoute.UsageKeyFor(taskExecution, taskId, personaId),
            model, resumeSessionId, ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim(),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer)
            Provider = _route.ResolveSubscriptionProvider(defaultModel),
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
            TaskId = taskId,
            // Онбординг-сессия: задаётся ДО старта — BuildPersonaLayer читает поле при сборке слоя
            OnboardingKind = onboardingKind,
        };

        await _process.StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Создание чата от лица персоны. Маршрутизация по зоне:
    // проектная персона → сессия в её проекте (scope = проект); глобальная (или проект
    // недоступен) → чат вне проекта (scope = все данные владельца). Модель по умолчанию — из персоны.
    // contextProjectId — проект, ИЗ которого зовут глобальную персону («Поговорить» в проекте):
    // чат создаётся в нём, а не вне проекта (как давно позволяет смена собеседника SetPersona).
    internal async Task<Session> CreatePersonaChatAsync(string ownerId, string personaId,
        ClaudeMode mode, string? resumeSessionId = null, string? name = null,
        string? contextProjectId = null, string? automationRuleId = null,
        string? createdByUserId = null, string? privateToUserId = null)
    {
        var persona = _personaLayer.ResolvePersona(personaId, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {personaId}");

        // Проект сессии: у проектной персоны — её собственный; у глобальной — контекстный
        var targetProjectId = PersonaZone.IsProjectPersona(persona)
            ? persona.ProjectId
            : contextProjectId;
        // Персона сферы без проекта сферы чата не получает: её зона — только проекты сферы
        if (PersonaZone.IsSpherePersona(persona)) _settings.EnsurePersonaVisible(persona, targetProjectId);

        // Персона без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной».
        // Дефолт места (Strong) передаётся в резолв, чтобы ячейка персоны без явного уровня сработала.
        var personaModel = _route.ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(persona, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId, ownerId);

        if (!string.IsNullOrEmpty(targetProjectId)
            && _projects.GetById(targetProjectId) is { } project && project.OwnerId == ownerId)
        {
            // Глобальной персоне чат общего проекта не назначается (доработка R-S2a-8): общая проверка
            // рамки — здесь проект чата известен точно (иначе чат вышел бы вне проекта, где отказа нет).
            _settings.EnsurePersonasVisibleInFrame(project.Id, new[] { persona });
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = personaId,
                Mode = mode,
                ClaudeSessionId = resumeSessionId,
                Name = name,
                Model = personaModel,
                Provider = _route.ResolveSubscriptionProvider(personaModel),
                Effort = persona.Effort,
                AutomationRuleId = automationRuleId,
                // Преемник приватного чата (S2d-7b-1): автор и приватность — до регистрации сессии.
                // Чат вне проекта (ветка ниже) приватным не бывает: он и так виден только владельцу
                CreatedByUserId = string.IsNullOrWhiteSpace(createdByUserId) ? null : createdByUserId.Trim(),
                // Пустая строка — «не видит никто» (fail-closed): не нормализуем её в null
                PrivateToUserId = privateToUserId,
            };
            await _process.StartNewSessionAsync(projectSession, project.RootPath, project.SystemPrompt,
                () => _projects.GetById(project.Id)?.PermissionRules
                    ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
            return projectSession;
        }

        var rootPath = _route.ResolveChatRoot(ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            PersonaId = personaId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = personaModel,
            Provider = _route.ResolveSubscriptionProvider(personaModel),
            Effort = persona.Effort,
            AutomationRuleId = automationRuleId,
        };
        await _process.StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Создание группового чата (флаг persona-group-chats): 2-4 персоны владельца,
    // первая — ведущая (стартовый активный спикер). Зона — по ведущей, как в
    // CreatePersonaChatAsync: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    internal async Task<Session> CreateGroupChatAsync(string ownerId, IReadOnlyList<string> personaIds,
        ClaudeMode mode, string? name = null)
    {
        // Проект группового чата — проект ведущей проектной персоны; у остальных чат вне проекта,
        // и персоне сферы в нём не место
        var groupProjectId = _personaLayer.ResolvePersona(personaIds.FirstOrDefault() ?? "", ownerId) is { } l
            ? PersonaZone.OwnProjectId(l) : null;
        var participants = _settings.ValidateParticipants(ownerId, personaIds, groupProjectId);
        var leader = participants[0];
        var participantIds = participants.Select(p => p.Id).ToList();
        // Ведущая без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной»
        // (дефолт места передаётся в резолв, чтобы ячейка ведущей без уровня сработала).
        var leaderModel = _route.ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(leader, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId: null, ownerId);

        if (PersonaZone.OwnProjectId(leader) is { } leaderProjectId
            && _projects.GetById(leaderProjectId) is { } project && project.OwnerId == ownerId)
        {
            // Групповой чат общего проекта без глобальных участников (доработка R-S2a-8): та же
            // общая проверка рамки, что для собеседника — состав чата и есть его спикеры.
            // Здесь проект чата известен точно (иначе чат создался бы вне проекта, где отказа нет).
            _settings.EnsurePersonasVisibleInFrame(project.Id, participants);
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = leader.Id,
                Participants = participantIds,
                Mode = mode,
                Name = name,
                Model = leaderModel,
                Provider = _route.ResolveSubscriptionProvider(leaderModel),
                Effort = leader.Effort,
            };
            await _process.StartNewSessionAsync(projectSession, project.RootPath, project.SystemPrompt,
                () => _projects.GetById(project.Id)?.PermissionRules
                    ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
            return projectSession;
        }

        var rootPath = _route.ResolveChatRoot(ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            PersonaId = leader.Id,
            Participants = participantIds,
            Mode = mode,
            Name = name,
            Model = leaderModel,
            Effort = leader.Effort,
            Provider = _route.ResolveSubscriptionProvider(leaderModel),
        };
        await _process.StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Чаты владельца, ведущиеся от лица конкретной персоны (для раздела «Персоны») — список
    // человеческой ручки (S2d-4a): плюс VisibleTo, чужой приватный чат в нём не живёт
    internal IReadOnlyList<Session> GetPersonaChats(string ownerId, string personaId) =>
        _sessions.Values
            .Select(e => e.Info)
            .Where(s => s.PersonaId == personaId && _store.ResolveOwnerId(s) == ownerId && _store.VisibleTo(s, ownerId))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
}
