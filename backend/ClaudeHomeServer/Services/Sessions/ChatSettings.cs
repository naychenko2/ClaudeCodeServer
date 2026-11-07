using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

// Тип отказа «глобальная персона в общем проекте» — часть контракта фасада (его ловят
// контроллеры по SessionManager.GlobalPersonaInSharedException), объявлен вложенным в
// SessionManager; здесь обращение через alias, чтобы тела остались дословными (тот же приём,
// что с QueuedMessage в PendingQueueService).
using GlobalPersonaInSharedException = ClaudeHomeServer.Services.SessionManager.GlobalPersonaInSharedException;

namespace ClaudeHomeServer.Services.Sessions;

// Настройки и жизненный цикл чата (S3-3, docs/research/session-manager-split-2026-11.md §1):
// закрепление и ручная группировка, темы проекта, архив (ручной, автоправило, пачки и откат),
// срок и его заморозка паузой, уведомления/озвучка/прочитанность/пометка вики, состав
// контекста, переадресация моделей, кэш сводок, собеседник и участники группового чата,
// правка имени/модели (UpdateAsync со сменой провайдера через ProviderMigration) и зона
// персоны. Вынесено из SessionManager; фасад держит тонкие обёртки с прежними сигнатурами.
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051);
// сам конструктор internal: все типы его параметров публичны (SessionStore публичный), поэтому
// internal-ность здесь не вынуждена CS0051 (в отличие от ProviderMigration, где причина —
// internal-тип Lazy<ITeamNotifier>) — роль собирается только фасадом (запасная сборка) и
// регистрацией ядра, вовне она не строится. Обход фасада закрывает сторож формы ядра (правило 4).
// Инвариант всей роли: UpdatedAt не двигают настройки (закрепление, темы, архив, срок,
// уведомления, озвучка, прочитанность, пометка, контекст, сводки) — признак архива и
// непрочитанность производные от UpdatedAt; активность (ход, смена собеседника) двигает.
// Замки не меняют ключа: _subjectsLock — лока записи тем (был полем фасада, отношение
// «один экземпляр на процесс» сохраняет синглтон роли); VisibilityLock — составляющая
// записи SessionEntry (по сессии), тот же замок, что у тумблера видимости.
// Рассылка событий архивации/контекста идёт через store.BroadcastSessionMessageAsync:
// это ровно та же склейка (session-группа + project_/user_-группа по адресату чата), что
// была в BroadcastChatArchivedAsync/SetContextAsync напрямую через broadcaster — отдельная
// зависимость от вещателя роли не требует.
public sealed class ChatSettings
{
    private readonly SessionStore _store;
    private readonly SessionHistory _history;
    private readonly CliRoute _route;
    // Смена провайдера (S3-3, вынесена отдельно): UpdateAsync — единственный, кто зовёт
    // MigrateProviderAsync и ловит его ProviderUnchangedException (S3-0 §1: guard смены
    // провайдера живёт в роли миграции, а не здесь).
    private readonly ProviderMigration _providerMigration;
    private readonly ProjectManager _projects;
    private readonly LlmProviderRegistry _llmProviders;
    private readonly ModelAssignmentResolver _assignments;
    private readonly ClaudeSubscriptionPool _subscriptionPool;
    private readonly PersonaManager _personas;
    private readonly Spheres.ISphereDirectory? _spheres;
    private readonly ILogger<ChatSettings> _log;

    // Замок записи тем: CAS «снимок прохода → запись» против ручного переноса держится здесь
    private readonly Lock _subjectsLock = new();

    internal ChatSettings(
        SessionStore store,
        SessionHistory history,
        CliRoute route,
        ProviderMigration providerMigration,
        ProjectManager projects,
        LlmProviderRegistry llmProviders,
        ModelAssignmentResolver assignments,
        ClaudeSubscriptionPool subscriptionPool,
        PersonaManager personas,
        Spheres.ISphereDirectory? spheres,
        ILogger<ChatSettings> log)
    {
        _store = store;
        _history = history;
        _route = route;
        _providerMigration = providerMigration;
        _projects = projects;
        _llmProviders = llmProviders;
        _assignments = assignments;
        _subscriptionPool = subscriptionPool;
        _personas = personas;
        _spheres = spheres;
        _log = log;
    }

    private Spheres.ISphereDirectory SphereDir => _spheres ?? NoSphereDirectory.Instance;

    // Кто архивировал: не "user" и не "rule" — откат прохода автоправила такие чаты не вернёт
    public const string LegacyImageChatArchivedBy = "image-editor-v3";

    // Чаты, подпадающие под автоправило архивации «без сообщений дольше N дней» (план v4,
    // флаг chat-auto-archive) — ЕДИНАЯ точка отбора для счётчика превью
    // (GET /api/chats/archive-preview) и тика правила (ChatArchiveService.TickAsync):
    // превью обязано считать той же функцией, что архивирует, иначе счётчик покажет «3»,
    // а исчезнет 200 (пре-мортем №2). nowUtc — параметром, чтобы превью и тик сходились
    // при одном моменте времени; фронт этот отбор повторить не может в принципе
    // (HasTurnInFlight и живость агентов — серверные).
    //
    // projectId != null — чаты проекта (владение проектом контроллер проверил отдельно,
    // у проектных сессий OwnerId null); null — чаты вне проекта владельца ownerId
    // (личный дефолт правила). Потолок пачки и ArchivedBy="rule" — забота тика, не отбора.
    public IReadOnlyList<Session> GetArchiveRuleCandidates(string ownerId, string? projectId, int days, DateTime nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromDays(days);
        var result = new List<Session>();
        foreach (var entry in _store.Sessions.Values)
        {
            var info = entry.Info;
            if (projectId is null)
            {
                if (info.ProjectId is not null || info.OwnerId != ownerId) continue;
            }
            else if (info.ProjectId != projectId) continue;
            // S2d-4h: приватный чат (PrivateToUserId != null, включая пустой — тот же
            // fail-closed, что у SessionAccess.CanSee) не входит в машинные обходы ни в какой
            // форме: правило проекта не действует над приватным чатом — ни чужим, ни своим;
            // свой архивирует автор ручкой (ручная архивация через эту выборку не идёт).
            if (info.PrivateToUserId is not null) continue;
            if (!MatchesArchiveRule(info, _store.TaskLookup, cutoff)) continue;
            // Живость в чистый предикат не входит: она — свойство entry/адаптера, не Session
            if (SessionStore.HasTurnInFlight(entry) || entry.Process is { HasTrackedBg: true }) continue;
            result.Add(info);
        }
        return result;
    }

    // Чистая часть предиката правила (порог + исключения; живость хода/фоновых агентов —
    // в GetArchiveRuleCandidates). internal static для юнит-тестов, образец — ShouldExpire
    // в ChatExpiryService. Исключения ровно три: закреплённые («чат нужен»), временные
    // (ими управляет свой срок) и чат живой задачи-исполнителя (выполненной — можно).
    //
    // Исключения по онбордингу (OnboardingKind) и по штабу в работе (TeamImplement не в
    // Idle) сняты 28.08.2026: они были бессрочными, и брошенное знакомство или штаб,
    // остывший полгода назад, не уходили в архив никогда — мусор копился (12 «вечных»
    // чатов у владельца на момент решения). Проверки активности в них не было, а сам порог
    // «без активности N дней» её и означает; архив при этом ничего не удаляет, и первая же
    // запись в чат возвращает его из архива автоматически (IsArchived — производный от
    // UpdatedAt <= ArchivedAt).
    internal static bool MatchesArchiveRule(Session s, ITaskLookup? tasks, DateTime cutoff) =>
        !s.IsArchived
        && !s.IsPinned
        && s.ExpiresAfterMinutes is null
        && s.UpdatedAt <= cutoff
        && (s.TaskId is null || SessionTaskLinks.IsTaskDone(s, tasks));

    // Закрепить/открепить чат.
    // UpdatedAt намеренно НЕ обновляется (как в SetExpiry): закрепление — настройка, а не
    // активность. Закреплённые поднимает наверх сам флаг (chatTree.ts/chatGroups.ts), а
    // отметка времени метила бы чат непрочитанным и выводила бы его из архива (признак
    // архива производный: UpdatedAt <= ArchivedAt).
    public bool SetPinned(string sessionId, bool pinned)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;
        entry.Info.IsPinned = pinned;
        _store.SaveSessions();
        return true;
    }

    // Ручная группировка чатов (drag-and-drop в списке): назначить родителя либо вынести
    // в корень (parentId == null). Единственная точка записи ParentOverrideId/ParentDetached —
    // поля взаимоисключающие, снаружи их не трогает никто.
    //
    // Чат не «переезжает»: UpdatedAt намеренно НЕ обновляется. Корни сортируются по активности
    // поддерева (chatTree.ts), и отметка времени от перетаскивания перекидывала бы чат наверх
    // списка, будто в нём был ход.
    public Session? SetParent(string sessionId, string? parentId, string ownerId)
    {
        if (_store.GetOwned(sessionId, ownerId) is not { } chat) return null;
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;

        if (parentId is null)
        {
            entry.Info.ParentOverrideId = null;
            // Гасим авто-связь только у чата, у которого она есть, — иначе обычный чат
            // навсегда унёс бы бессмысленный флаг в sessions.json.
            entry.Info.ParentDetached = chat.TaskId is not null;
            _store.SaveSessions();
            return entry.Info;
        }

        if (parentId == sessionId)
            throw new InvalidOperationException("Чат нельзя вложить в самого себя");
        if (_store.GetOwned(parentId, ownerId) is not { } parent)
            throw new InvalidOperationException("Родительский чат не найден");
        // Разные списки рендерятся на разных экранах: ребёнок из чужого скоупа не нашёл бы
        // родителя в своей выборке и молча всплыл бы в корень (chatTree.ts: byId.has(pid)).
        if (parent.ProjectId != chat.ProjectId)
            throw new InvalidOperationException(
                "Чат можно группировать только внутри одного проекта или списка чатов вне проектов");
        if (IsDescendantOf(parent.Id, sessionId))
            throw new InvalidOperationException("Нельзя вложить чат в его собственный дочерний чат");

        entry.Info.ParentOverrideId = parentId;
        entry.Info.ParentDetached = false;
        _store.SaveSessions();
        return entry.Info;
    }

    // --- Темы чатов проекта ---

    // Единственная точка записи Session.Subject* (и модель, и человек). Как SetPinned/SetParent,
    // UpdatedAt НЕ двигает и из архива не выводит: тема — настройка, а не активность, иначе
    // разбор поднимал бы чаты наверх списка и метил непрочитанными. Чат чужого проекта
    // пропускается. Запись модели (Ai) идёт по CAS: чат не закреплён человеком, SubjectId
    // равен снимку прохода, тема ещё есть в реестре (её могли удалить посреди прохода). Ручной
    // перенос в тему ПЕРВЫМ делом переводит тему в human под замком реестра (MarkSubjectHuman
    // проверяет и существование): уборка пустых тем либо уже не снесёт её, либо успела снести
    // раньше — тогда перенос отбивается KeyNotFoundException, и ни одна сессия не тронута.
    // sessions.json пишется ОДИН раз на вызов, а не на каждую запись. Возвращает id чатов,
    // у которых что-то поменялось.
    public IReadOnlyList<string> ApplySubjects(string projectId, IReadOnlyList<ChatSubjects.SubjectWrite> writes, DateTime nowUtc)
    {
        var changed = new List<string>();
        lock (_subjectsLock)
        {
            foreach (var w in writes)
            {
                if (w.Kind != ChatSubjects.SubjectWriteKind.Human || w.SubjectId is not { } subjectId) continue;
                if (!_store.Sessions.TryGetValue(w.SessionId, out var target) || target.Info.ProjectId != projectId) continue;
                if (!_projects.MarkSubjectHuman(projectId, subjectId))
                    throw new KeyNotFoundException("Тема не найдена");
            }
            foreach (var w in writes)
            {
                if (!_store.Sessions.TryGetValue(w.SessionId, out var entry)) continue;
                var s = entry.Info;
                if (s.ProjectId != projectId) continue;
                if (ApplySubjectWrite(s, w, projectId, nowUtc)) changed.Add(s.Id);
            }
        }
        if (changed.Count > 0) _store.SaveSessions();
        return changed;
    }

    // Человек удалил тему, в которую сам переносил чаты: закрепление за несуществующей темой
    // бессмысленно — чат молча висел бы в «Без темы», недоступный автоматике навсегда. Такие
    // чаты возвращаются автоматике (как кнопкой «Вернуть автоматике»). «Убрал из темы»
    // (human при SubjectId == null) — осознанный выбор, его не трогаем. Реестр читается ПОД
    // замком тем: перенос в тему, заведённую между PUT реестра и этим вызовом, не снимется.
    // Зовётся после замены реестра; заодно лечит висячие закрепления, оставшиеся из прошлого.
    public IReadOnlyList<string> ReleaseOrphanedHumanSubjects(string projectId)
    {
        var changed = new List<string>();
        lock (_subjectsLock)
        {
            var registry = _projects.GetSubjects(projectId).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in _store.Sessions.Values)
            {
                var s = entry.Info;
                if (s.ProjectId != projectId || s.SubjectSource != SubjectSources.Human
                    || s.SubjectId is not { } id || registry.Contains(id))
                    continue;
                ApplySubjectWrite(s, new ChatSubjects.SubjectWrite(s.Id, ChatSubjects.SubjectWriteKind.Release), projectId, DateTime.UtcNow);
                changed.Add(s.Id);
            }
        }
        if (changed.Count > 0) _store.SaveSessions();
        return changed;
    }

    private bool ApplySubjectWrite(Session s, ChatSubjects.SubjectWrite w, string projectId, DateTime nowUtc)
    {
        switch (w.Kind)
        {
            case ChatSubjects.SubjectWriteKind.Ai:
                if (s.SubjectSource == SubjectSources.Human) return false;
                var accepted = w.SubjectId is { } aiId
                    && s.SubjectId == w.ExpectedSubjectId
                    && _projects.HasSubject(projectId, aiId);
                if (accepted)
                {
                    s.SubjectId = w.SubjectId;
                    s.SubjectSource = SubjectSources.Ai;
                    s.SubjectAssignedAt = nowUtc;
                }
                // Модель ответила по чату — попытка засчитана, даже если запись не прошла CAS
                s.SubjectTriedAt = nowUtc;
                return true;

            case ChatSubjects.SubjectWriteKind.Tried:
                if (s.SubjectSource == SubjectSources.Human) return false;
                s.SubjectTriedAt = nowUtc;
                return true;

            case ChatSubjects.SubjectWriteKind.Human:
                // Тема проверена и переведена в human до цикла записи (ApplySubjects)
                s.SubjectId = w.SubjectId;
                s.SubjectSource = SubjectSources.Human;
                s.SubjectAssignedAt = nowUtc;
                return true;

            case ChatSubjects.SubjectWriteKind.Release:
                s.SubjectId = null;
                s.SubjectSource = null;
                s.SubjectAssignedAt = null;
                s.SubjectTriedAt = null;
                return true;

            case ChatSubjects.SubjectWriteKind.ResetAi:
                if (s.SubjectSource == SubjectSources.Human) return false;
                s.SubjectId = null;
                s.SubjectSource = null;
                s.SubjectAssignedAt = null;
                s.SubjectTriedAt = null;
                return true;

            default:
                return false;
        }
    }

    // Отбор чатов проекта для разбора по темам — единая точка для фонового тика и кнопки.
    // Кандидат: корневой чат проекта (дочерний — нет; ставший корнем — сам кандидат), не в
    // архиве, сейчас не идёт ход, не закреплён человеком, без темы или с висячей темой (её нет
    // в реестре). Фон дополнительно требует: была активность после прошлой попытки модели,
    // чат остыл (idle) и дорос до minMessages, активность не раньше нижней границы since
    // (залежи — только кнопкой). Кнопка (manual) снимает эти три условия.
    public IReadOnlyList<Session> GetSubjectCandidates(string projectId, DateTime nowUtc, bool manual,
        DateTime? since = null, TimeSpan? idle = null, int minMessages = 0)
    {
        var registry = _projects.GetSubjects(projectId).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var cutoff = nowUtc - (idle ?? TimeSpan.Zero);
        return _store.Sessions.Values
            .Select(e => e.Info)
            .Where(s => s.ProjectId == projectId
                // S2d-4h: приватный чат не кандидат разбора ни фоном, ни кнопкой — его название
                // и реплики истории не уходят в модель (тот же признак, что у SessionAccess.CanSee)
                && s.PrivateToUserId is null
                && !s.IsArchived
                && s.Status is not (SessionStatus.Working or SessionStatus.Waiting)
                && SessionTaskLinks.ParentSessionId(s, _store.TaskLookup) is null
                && s.SubjectSource != SubjectSources.Human
                && (s.SubjectId is null || !registry.Contains(s.SubjectId))
                && (manual
                    || ((s.SubjectTriedAt is null || s.UpdatedAt > s.SubjectTriedAt)
                        && s.UpdatedAt <= cutoff
                        && s.MessageCount >= minMessages
                        && (since is null || s.UpdatedAt >= since))))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    // Является ли candidate потомком ancestor по эффективной иерархии (ParentSessionId,
    // т.е. с учётом ручных override). Счётчик шагов — страховка от цикла, уже лежащего
    // в данных: инварианты SetParent новых циклов не создают, но старый sessions.json
    // мог приехать из бэкапа, а обход вверх по кольцу здесь бы завис.
    private bool IsDescendantOf(string candidateId, string ancestorId)
    {
        var cur = _store.GetById(candidateId);
        for (var steps = 0; cur is not null && steps < 256; steps++)
        {
            if (SessionTaskLinks.ParentSessionId(cur, _store.TaskLookup) is not { } pid) return false;
            if (pid == ancestorId) return true;
            cur = _store.GetById(pid);
        }
        return false;
    }

    // Убрать чат в архив (archived = true) / вернуть (false). Единственная точка записи
    // полей архива — как SetParent для группировки. by — "user" (пункт меню) | "rule"
    // (автоправило), batchId — идентификатор прохода правила (откат возвращает ровно одну
    // пачку; у ручной архивации null).
    //
    // UpdatedAt/LastReadAt намеренно НЕ трогает (как SetExpiry): по ним сортируется список
    // и считается непрочитанность, а архивация — не активность; возврат не должен всплывать
    // чат наверх и метить его непрочитанным. Признак архива производный (Session.IsArchived),
    // поэтому «снять архив» — это сброс полей, и повторная активность снимет его и без
    // мутатора. Попутно копируем транскрипт в data/archived-transcripts (архивация) или
    // возвращаем его в профиль (возврат) — best-effort, сбой файловой части не роняет вызов.
    //
    // SaveSessions сознательно НЕ зовём: ручная архивация сохранит стор сразу после вызова,
    // а проход автоправила пишет файл ОДИН раз на всю пачку (до 200 чатов за тик — иначе
    // каждая архивация перезаписывала бы sessions.json целиком и дёргала sweep).
    public Session? SetArchived(string sessionId, bool archived, string by, string? batchId = null)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        if (archived)
        {
            entry.Info.ArchivedAt = DateTime.UtcNow;
            entry.Info.ArchivedBy = by;
            entry.Info.ArchiveBatchId = batchId;
            _route.ArchiveTranscriptCopy(entry.Info);
        }
        else
        {
            entry.Info.ArchivedAt = null;
            entry.Info.ArchivedBy = null;
            entry.Info.ArchiveBatchId = null;
            _route.RestoreTranscriptCopy(entry.Info);
        }
        return entry.Info;
    }

    // Разовая миграция чатов картинки v2 на v3 (ADR-019, решение 2): чат уходит в архив
    // (ArchivedAt, UpdatedAt не трогаем — по общему правилу IsArchived) и получает маркер
    // SessionImageChat.MigratedAt. Идемпотентна: чат с маркером пропускается — и тот, что
    // человек вернул из архива сам, повторно не архивируется. Уже архивный чат получает только
    // маркер. ArchivedAt не раньше UpdatedAt: иначе чат с меткой из будущего остался бы в списке.
    // Транскрипт копируется в архив, как при ручной архивации, --resume не ломается.
    // Возвращает число мигрированных чатов; 0 — sessions.json не переписывается.
    public int ArchiveLegacyImageChats(DateTime nowUtc)
    {
        var migrated = 0;
        foreach (var entry in _store.Sessions.Values)
        {
            if (entry.Info.ImageChat is not { MigratedAt: null } chat) continue;
            if (!entry.Info.IsArchived)
            {
                entry.Info.ArchivedAt = entry.Info.UpdatedAt > nowUtc ? entry.Info.UpdatedAt : nowUtc;
                entry.Info.ArchivedBy = LegacyImageChatArchivedBy;
                entry.Info.ArchiveBatchId = null;
                _route.ArchiveTranscriptCopy(entry.Info);
            }
            chat.MigratedAt = nowUtc;
            migrated++;
        }
        if (migrated > 0) _store.SaveSessions();
        return migrated;
    }

    // Ручная архивация/возврат из архива (PUT /api/chats/{id}/archived, шаг 2 плана
    // «Архив чатов»): обёртка над SetArchived для точки входа из UI — гейт живости,
    // снятие срока временного чата, персист и событие ленты chat_archived. Автоправило
    // (ChatArchiveService) зовёт SetArchived напрямую: у него свой гейт отбора и одна
    // SaveSessions() на всю пачку.
    //
    // Гейт живости — только на архивацию: возврат ничего не рвёт, а «вернуть из архива,
    // пока идёт ход» — ровно то, что происходит при снятии архива активностью. 409 на
    // живом ходе отсюда уезжает InvalidOperationException (конвенция RestartWave и др.).
    public async Task<Session?> SetArchivedAsync(string sessionId, string ownerId, bool archived)
    {
        if (_store.GetOwned(sessionId, ownerId) is not { } info) return null;
        if (archived && _store.Sessions.TryGetValue(sessionId, out var entry))
        {
            if (SessionStore.HasTurnInFlight(entry))
                throw new InvalidOperationException(
                    "В чате идёт ход — дождитесь его завершения или прервите его, затем уберите чат в архив");
            if (entry.Process is { HasTrackedBg: true })
                throw new InvalidOperationException(
                    "В чате работают фоновые агенты — дождитесь их завершения, затем уберите чат в архив");
            // Временный чат: архив бессрочен до возврата, срок снимаем ДО записи признака
            // архива — иначе чат умер бы по таймеру ChatExpiryService уже в архиве
            // (SetExpiry заодно обнуляет ExpiryAnchor). SetExpiry пишет стор сам —
            // промежуточное состояние «срок снят, архива нет» безопасно.
            if (info.ExpiresAfterMinutes is not null) SetExpiry(sessionId, null);
        }
        var updated = SetArchived(sessionId, archived, by: "user");
        if (updated is null) return null;
        _store.SaveSessions();
        await BroadcastChatArchivedAsync(sessionId, archived);
        return updated;
    }

    // Уведомить клиентов об архивации/возврате чата (адресация как у BroadcastChatDeletedAsync):
    // project-группа для проектной сессии, user-группа для чата вне проекта. Стор сам находит
    // запись по sessionId (session-группа + ToChatAudience по ней) — параметра с записью
    // здесь не нужно (R-S3-3, замечание 3: после перехода на стор он стал мёртвым).
    private async Task BroadcastChatArchivedAsync(string sessionId, bool archived)
        => await _store.BroadcastSessionMessageAsync(sessionId, new ChatArchivedMessage(archived));

    // Проход автоправила архивации (ChatArchiveService, шаг 6 плана v4): заархивировать
    // пачку одним batchId — ОДНОЙ SaveSessions на весь проход (до 200 чатов; иначе каждая
    // архивация переписывала бы sessions.json целиком и дёргала sweep) и событием
    // chat_archived каждому чату. Гейт живости не повторяем: отбор кандидатов
    // (GetArchiveRuleCandidates) уже его отработал; кому-то стать живым между отбором и
    // записью — допустимая гонка, чат вернёт из архива собственная активность.
    public async Task ArchiveBatchAsync(IReadOnlyCollection<string> sessionIds, string batchId)
    {
        var archived = new List<string>();
        foreach (var id in sessionIds)
        {
            var updated = SetArchived(id, archived: true, by: "rule", batchId);
            if (updated is not null) archived.Add(id);
        }
        if (archived.Count == 0) return;
        _store.SaveSessions();
        foreach (var id in archived)
            await BroadcastChatArchivedAsync(id, archived: true);
    }

    // Откат пачки автоправила из уведомления/раздела «Архив»: вернуть РОВНО чаты прохода
    // batchId (ArchivedBy="rule" и чат ещё в архиве), а не всю историю правила. Одна
    // SaveSessions на пачку, событие возврата каждому. Владелец — по GetOwned: батч-id
    // приходит из URL, чужой не должен возвращать чужие чаты (даже угаданный).
    public async Task<int> RestoreArchiveBatchAsync(string ownerId, string batchId)
    {
        var restored = new List<string>();
        foreach (var s in _store.Sessions.Values.Select(e => e.Info)
                     .Where(s => s.ArchiveBatchId == batchId && s.ArchivedBy == "rule" && s.IsArchived)
                     .ToList())
        {
            if (_store.GetOwned(s.Id, ownerId) is null) continue;
            var updated = SetArchived(s.Id, archived: false, by: "user");
            if (updated is not null) restored.Add(s.Id);
        }
        if (restored.Count == 0) return 0;
        _store.SaveSessions();
        foreach (var id in restored)
            await BroadcastChatArchivedAsync(id, archived: false);
        return restored.Count;
    }

    // Включить/выключить временность чата: minutes > 0 — авто-удаление через N минут
    // после последней активности, null — обычный чат.
    //
    // UpdatedAt намеренно НЕ обновляется (как в SetParent): по нему сортируется список и
    // считается непрочитанность, а смена настройки хранения — не активность чата. Отсчёт
    // срока при этом не должен стартовать в прошлом, поэтому включение ставит ExpiryAnchor —
    // дедлайн считается от него, если он позже последней активности.
    public Session? SetExpiry(string sessionId, int? minutes)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.ExpiresAfterMinutes = minutes;
        entry.Info.ExpiryAnchor = minutes is null ? null : DateTime.UtcNow;
        _store.SaveSessions();
        return entry.Info;
    }

    // Заморозить срок чата паузой фичи (W4-HOLD-E/J, хвост N): снимаемое значение сохраняется
    // в HoldFrozenExpiresAfterMinutes — возврат после снятия паузы восстанавливает срок самого
    // чата, а не задачи (R-HOLD-J п.3). Идемпотентна: повторная пауза той же записи не трогает
    // уже снятый срок. UpdatedAt, как в SetExpiry, не двигается: настройка хранения — не активность.
    public Session? FreezeExpiryForHold(string sessionId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        // W4-HOLD-N (хвост R-N п.1): значение сохраняется только если срок снят сейчас.
        // Вторая пауза той же записи видит ExpiresAfterMinutes == null: безусловное присваивание
        // затёр бы сохранённый срок null — после снятия он не вернулся бы, чат стал бы вечным.
        if (entry.Info.ExpiresAfterMinutes is not null)
            entry.Info.HoldFrozenExpiresAfterMinutes = entry.Info.ExpiresAfterMinutes;
        entry.Info.ExpiresAfterMinutes = null;
        entry.Info.ExpiryAnchor = null;
        _store.SaveSessions();
        return entry.Info;
    }

    // Вернуть замороженный срок: якорь — «сейчас» (как SetExpiry), сохранённое значение гасится.
    // Нечего возвращать (чат был вечным или не замораживался) — состояние не меняется.
    public Session? RestoreFrozenExpiry(string sessionId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Info.HoldFrozenExpiresAfterMinutes is null) return entry.Info;
        var frozen = entry.Info.HoldFrozenExpiresAfterMinutes;
        entry.Info.HoldFrozenExpiresAfterMinutes = null;
        return SetExpiry(sessionId, frozen);
    }

    // Запись модуля в ленту вне хода (ADR-019 §2, шов IChatFeed): история + живая пара
    // module_record. Активность, как тихие строки v2: UpdatedAt двигается, чат выходит из
    // архива. Timestamp ставит сервер, если модуль его не задал. false — чата нет.
    public async Task<bool> AppendModuleRecordAsync(string sessionId, StoredModuleRecord record)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;
        var ts = record.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stored = new StoredModuleRecord
        {
            Module = record.Module, RecordType = record.RecordType, Data = record.Data,
            Fallback = record.Fallback, Timestamp = ts,
        };
        await _history.AppendStoredAsync(sessionId, stored,
            new ModuleRecordMessage(stored.Module, stored.RecordType, stored.Data, stored.Fallback, ts), inTurn: true);
        entry.Info.UpdatedAt = DateTime.UtcNow;
        _store.SaveSessions();
        return true;
    }

    // Заглушить/включить уведомления по чату (браузерные «нужно решение» / «ход завершён»).
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    public Session? SetNotificationsMuted(string sessionId, bool muted)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.NotificationsMuted = muted;
        _store.SaveSessions();
        return entry.Info;
    }

    // Включить/выключить голосовой режим чата и/или сменить стиль озвучки.
    // Один сеттер на оба поля намеренно: каждый дёргает SaveSessions(), то есть перезапись
    // всего списка — два раздельных дали бы две записи файла на один PUT.
    // null-аргумент = поле не трогаем: стиль приезжает и БЕЗ флага (устройство выправляет
    // чужой стиль у чата с уже включённой озвучкой), а такой запрос не должен её гасить.
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    public Session? SetVoiceMode(string sessionId, bool? on, string? style = null)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        if (on is bool value) entry.Info.VoiceMode = value;
        if (style is not null) entry.Info.VoiceStyle = VoiceStyles.Normalize(style);
        _store.SaveSessions();
        return entry.Info;
    }

    // Отметить чат прочитанным (синк непрочитанности между устройствами).
    // UpdatedAt намеренно не трогаем (как в SetExpiry): прочтение — не активность чата.
    // false — сессии нет или она не принадлежит owner'у (контроллер отдаёт 404).
    // Идемпотентность: LastReadAt уже >= UpdatedAt → true без записи.
    // Запись — дебаунсом (ScheduleDebouncedSave), а не синхронным SaveSessions: отметка
    // прочитанности — лёгкая мутация, а SaveSessions сериализует ВЕСЬ sessions.json (на
    // 600 сессий ~0,9с в проде) и fsync'ит файл — в запросе это незачем. In-memory
    // состояние обновлено синхронно, поэтому ответ корректен сразу; на диск отметка
    // уходит одним пакетом (быстрые повторные отметки сливаются) или автосейвом.
    public bool MarkRead(string sessionId, string ownerId)
    {
        if (_store.GetOwned(sessionId, ownerId) is not { } s) return false;
        if (s.LastReadAt >= s.UpdatedAt) return true;
        s.LastReadAt = DateTime.UtcNow;
        _store.ScheduleDebouncedSave();
        return true;
    }

    // Пометка человека для отбора чатов в вики (ADR-035 §12а): WikiMarks.Important —
    // разбирается всегда, WikiMarks.Skip — не разбирается никогда (ставится и откатом по
    // источнику, §12г мера 5: иначе следующая ночь вернёт откатанное). null — снять пометку.
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: пометка — настройка чата,
    // а не активность (по UpdatedAt идут сортировка и непрочитанность).
    // Реализация шва ISessionDirectory (по нему зовёт вертикаль Notes — в SessionManager
    // напрямую ей нельзя, ADR-014). false — неизвестная пометка (валидация на входе) или
    // сессии нет; отказовый вызов стор не переписывает.
    public bool SetWikiMark(string sessionId, string? mark)
    {
        if (mark is not null && mark != WikiMarks.Important && mark != WikiMarks.Skip) return false;
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;
        entry.Info.WikiMark = mark;
        _store.SaveSessions();
        return true;
    }

    // Сменить состав контекста чата (фича chat-context): идемпотентная замена списка
    // материалов целиком — «добавить материал» это PUT со старым составом + новая запись.
    // Валидацию записей делает контроллер ДО этого вызова; сюда приезжают уже чистые
    // данные. UpdatedAt не трогаем по той же причине, что в SetExpiry: контекст —
    // настройка чата, а не активность (по UpdatedAt идут сортировка и непрочитанность).
    // Broadcast — в session/project/user-группы (BroadcastSessionMessageAsync): событие
    // внеходовое, вторая вкладка браузера должна увидеть состав без перезагрузки.
    public async Task<Session?> SetContextAsync(string sessionId, IReadOnlyList<SessionContextEntry> entries)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.Context = [.. entries];
        _store.SaveSessions();
        await _store.BroadcastSessionMessageAsync(sessionId, new ContextUpdatedMessage(entry.Info.Context));
        return entry.Info;
    }

    // Контекст чата, принадлежащего пользователю: для GET-эндпоинта и (вслед за ним)
    // тула context_list. null — чужая/несуществующая сессия.
    public IReadOnlyList<SessionContextEntry>? GetContext(string sessionId, string ownerId) =>
        _store.GetOwned(sessionId, ownerId) is { } s ? s.Context : null;

    // Разовая переадресация закреплённых моделей (миграция каталога провайдера): id из карты
    // заменяется, всё остальное — включая незнакомые модели и «preset:{id}» — остаётся как есть.
    // Возвращает число изменённых чатов; 0 — стор на диск не переписывается.
    // Идёт через живой реестр, а не файл: иначе первый же SaveSessions вернул бы старые id.
    public int RemapModels(IReadOnlyDictionary<string, string> map) =>
        RemapModels(id => map.TryGetValue(id, out var next) ? next : null);

    // То же с произвольным сведением: map(id) → новое значение или null (не менять).
    public int RemapModels(Func<string, string?> map)
    {
        var changed = 0;
        foreach (var info in _store.Sessions.Values.Select(e => e.Info))
        {
            if (info.Model is null || map(info.Model.Trim()) is not { } next || next == info.Model) continue;
            info.Model = next;
            changed++;
        }
        if (changed > 0) _store.SaveSessions();
        return changed;
    }

    // Гейты режима по модели (подготовка к S3-7, вердикт архитектора 07.11): эти две проверки
    // уже делаются здесь — сравнение провайдеров через ProviderKey в SetPersona, гейт
    // план-фазы — в ProviderMigration. Приём хода не получает одиннадцатую зависимость
    // LlmProviderRegistry — зовёт их через настройки.
    /// <summary>Поддерживает ли провайдер модели режим «План» (тот же гейт, что на ходе и в SetMode).</summary>
    internal bool SupportsPlanMode(string? model) => _llmProviders.CapabilitiesFor(model).SupportsPlanMode;

    /// <summary>Ключ провайдера модели (сравнение провайдеров — тот же свод, что в SetPersona).</summary>
    internal string ProviderKey(string? model) => _llmProviders.ProviderKey(model);

    // Запомнить заметку-итог сессии (SessionSummaryService) — для обновления при повторной генерации
    public void SetSummaryNoteId(string sessionId, string noteId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.SummaryNoteId = noteId;
        _store.SaveSessions();
    }

    // Кэш сводки карточки архива (ChatDigestService, место chat-digest): текст и момент
    // сборки — при UpdatedAt > ArchiveSummaryAt сводка не актуальна (см. Session).
    // UpdatedAt намеренно не двигается: сборка сводки — не активность чата, иначе она
    // сама снимала бы архив и поднимала чат в списке. null — сбросить кэш.
    public void SetArchiveSummary(string sessionId, string? summary)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.ArchiveSummary = summary;
        entry.Info.ArchiveSummaryAt = summary is null ? null : DateTime.UtcNow;
        _store.SaveSessions();
    }

    /// <summary>Видна ли персоне работа в проекте (зона Global/Project/Sphere) — единая проверка чатов.</summary>
    public bool PersonaVisibleIn(Persona persona, string? projectId) =>
        PersonaZone.VisibleIn(persona, projectId, SphereDir);

    /// <summary>Отказ хода, если проект вышел из сферы персоны; null — ход разрешён (см. <see cref="PersonaZone.OutOfZoneRefusal"/>).</summary>
    public string? OutOfZoneRefusal(Persona persona, string? projectId) =>
        PersonaZone.OutOfZoneRefusal(persona, projectId, SphereDir);

    // Гейт приёма хода: чат остался без персоны или проект вышел из её сферы — ход не стартует
    // (S3-7: проверка опущена к владельцу зоны; приём хода — только точка вызова. Резолв
    // владельца и персоны — тот же путь, что у SetParticipants выше).
    internal string? ZoneRefusalForTurn(SessionEntry entry) =>
        entry.Info.PersonaId is { } personaId && _store.ResolveOwnerId(entry.Info) is { } ownerId
        && _personas.Get(personaId, ownerId) is { } persona
            ? OutOfZoneRefusal(persona, entry.Info.ProjectId)
            : null;

    // Отказ 400 (InvalidOperationException), если персона не видна в проекте чата
    internal void EnsurePersonaVisible(Persona persona, string? projectId)
    {
        if (!PersonaVisibleIn(persona, projectId))
            throw new InvalidOperationException(
                $"Персона «{persona.Name}» не работает в этом проекте: он вне её сферы");
    }

    // Обновить состав участников группового чата. Активный спикер сохраняется,
    // если остался в составе, иначе — новая ведущая. Адаптер пересоздаётся
    // (состав участников зашит в подсказку @упоминаний и групповой слой промпта).
    public Session? SetParticipants(string sessionId, string ownerId, IReadOnlyList<string> personaIds)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        if (_store.ResolveOwnerId(entry.Info) != ownerId) return null;

        var participants = ValidateParticipants(ownerId, personaIds, entry.Info.ProjectId);
        // Новый участник чата общего проекта не глобальная персона (доработка R-S2a-8): та же общая
        // проверка рамки, что при создании группового чата, — по фактическому проекту этого чата.
        EnsurePersonasVisibleInFrame(entry.Info.ProjectId, participants);
        // S2d-7b-3: проверка приватности и запись состава — одна критическая секция под тем же
        // замком, под которым SetChatVisibility ставит приватность и сверяет IsTeamOrGroup:
        // приватный чат с составом невозможен ни при каком порядке параллельных запросов.
        lock (entry.VisibilityLock)
        {
            if (entry.Info.PrivateToUserId is not null)
                throw new PrivateChatModeException(Controllers.FeatureChatDoor.PrivateModeReason);
            entry.Info.Participants = participants.Select(p => p.Id).ToList();
        }
        var speaker = participants.FirstOrDefault(p => p.Id == entry.Info.PersonaId) ?? participants[0];
        SwitchSpeaker(entry, speaker);
        return entry.Info;
    }

    // Участники группового чата: 2-4 уникальные персоны, все принадлежат владельцу
    internal List<Persona> ValidateParticipants(string ownerId, IReadOnlyList<string> personaIds,
        string? projectId = null)
    {
        var ids = (personaIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count is < 2 or > 8)
            throw new InvalidOperationException("В групповом чате участвуют от 2 до 8 персон");
        var result = ids.Select(id => _personas.Get(id, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}")).ToList();
        // Участник сферы обязан быть виден в проекте чата (у группового чата вне проекта — нигде)
        foreach (var p in result.Where(PersonaZone.IsSpherePersona)) EnsurePersonaVisible(p, projectId);
        return result;
    }

    /// <summary>
    /// Персона, назначаемая чату, обязана быть видна в рамке этого чата (доработка R-S2a-8):
    /// рамка чата общего проекта узкая для всех, включая арендатора (TurnFrame, решение 29.10),
    /// а глобальная персона — личное пространство арендатора: её характер и память утекали бы
    /// в общий чат. Метод проверяет ПЕРЕДАННЫЙ набор персон, а не итоговое состояние сессии:
    /// каждая точка назначения обязана сама собрать набор и позвать его до записи. Точки входа:
    /// REST — создание чата с явной personaId (SessionsController.Create), чат от лица персоны
    /// (CreatePersonaChatAsync), групповой чат (CreateGroupChatAsync, SetParticipants), смена
    /// собеседника (SetPersona), продолжение легаси-чата (ChatContinueService); MCP —
    /// chats_create с projectId и явной personaId (WorkspaceToolset). CreateAsync рамку не
    /// проверяет — новый путь назначения обязан звать этот метод, а не писать свою проверку. Вердикт — по свойствам проекта, без актора (как в PluginDirSource):
    /// исключение приватного чата (S2d-1) здесь не реализовано — признака приватности в модели
    /// ещё нет, проверка встанет вместе с ним (см. комментарий TurnFrame.Resolve).
    /// </summary>
    public void EnsurePersonasVisibleInFrame(string? projectId, IEnumerable<Persona?> personas)
    {
        if (projectId is null) return; // чат вне проекта — личное пространство арендатора (Wide)
        if (_projects.GetById(projectId) is not { } project) return; // проект исчез — не забота этой проверки
        if (Access.TurnFrame.Resolve(project, null, project.OwnerId, null) != Access.TurnFrameKind.Narrow) return;
        foreach (var persona in personas)
            if (persona is not null && persona.Scope == PersonaScope.Global)
                throw new GlobalPersonaInSharedException(
                    $"Персона «{persona.Name}» — глобальная: в общем проекте она не может быть собеседником чата");
    }

    /// <summary>
    /// Продолжение чата общего проекта с глобальной персоной (R-S2a-8): отказ ДО платного хода
    /// передачи — иначе CreatePersonaChatAsync отказал бы уже после него. Чат вне проекта и
    /// личный проект проходят (рамка Wide).
    /// </summary>
    public void EnsureContinuationPersonaAllowed(Session source, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(source.PersonaId)) return;
        EnsurePersonasVisibleInFrame(source.ProjectId, new[] { _personas.Get(source.PersonaId, ownerId) });
    }

    // Назначить/сменить собеседника чату (единый селектор): персону (personaId) ИЛИ
    // стандартного .md-агента Claude (agentName) — взаимоисключающе. Оба пустые = снять.
    // Разрешено и ПО ХОДУ разговора: персона-слой строится на каждый ход, транскрипт
    // продолжается через --resume с новым системным слоем. Модель/усилие подтягиваются
    // из персоны; у начатой сессии — только при том же провайдере: смена собеседника
    // транскрипт не перевозит (в отличие от явной смены модели), а уводить ход в чужой
    // профиль CLI молча нельзя — модель персоны просто не применяется.
    public Session? SetPersona(string sessionId, string ownerId, string? personaId, string? agentName = null)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        if (_store.ResolveOwnerId(entry.Info) != ownerId) return null;

        Persona? persona = null;
        if (!string.IsNullOrEmpty(personaId))
        {
            persona = _personas.Get(personaId, ownerId)
                ?? throw new KeyNotFoundException("Персона не найдена");
            EnsurePersonaVisible(persona, entry.Info.ProjectId);
            // Глобальной персоне отказывает рамка, а не зона: VisibleIn для Global всегда
            // открыт (PersonaZone.cs:24) — работает общая проверка рамки EnsurePersonasVisibleInFrame
            // (доработка R-S2a-8): личный проект и чат вне проекта Wide (поведение прежнее),
            // общий проект Narrow — включая случай самого арендатора (решение 29.10).
            EnsurePersonasVisibleInFrame(entry.Info.ProjectId, new[] { persona });
        }

        SwitchSpeaker(entry, persona, agentName);
        return entry.Info;
    }

    // Запомнить персону, созданную в ходе онбординг-сессии (через personas_create из чата
    // мастера/наставника). Финализация (FinalizeOnboardingAsync) досевает профиль дефолта
    // только ей: выбранная существующая персона прав не получает.
    public void SetOnboardingCreatedPersona(string sessionId, string ownerId, string personaId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        if (_store.ResolveOwnerId(entry.Info) != ownerId) return;
        if (entry.Info.OnboardingCreatedPersonaId == personaId) return;
        entry.Info.OnboardingCreatedPersonaId = personaId;
        _store.SaveSessions();
    }

    // Пометить онбординг-сессию финализированной (PersonasController.FinalizeOnboardingAsync):
    // повторный make-default из живой сессии после этого — no-op без второго события в ленте.
    public void SetOnboardingFinalized(string sessionId, string ownerId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        if (_store.ResolveOwnerId(entry.Info) != ownerId) return;
        if (entry.Info.OnboardingFinalized) return;
        entry.Info.OnboardingFinalized = true;
        _store.SaveSessions();
    }

    // Роутинг спикера группового чата перед ходом: @упоминание участника переключает
    // активного спикера (SwitchSpeaker + speaker_changed клиентам). Во время активного
    // хода (Working/Waiting) состав не трогаем — переключение подействует со следующего.
    // S3-7: опущено из приёма хода к владельцу смены спикера (SwitchSpeaker здесь же);
    // точка вызова — приём хода (фасадная обёртка и приватные форвардеры в SessionManager сняты).
    internal async Task RouteGroupSpeakerAsync(string sessionId, SessionEntry entry, string text)
    {
        if (entry.Info.Participants is not { Count: > 1 } participantIds) return;
        if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting) return;
        var ownerId = _store.ResolveOwnerId(entry.Info);
        if (ownerId is null) return;

        var participants = participantIds
            .Select(id => _personas.Get(id, ownerId))
            .OfType<Persona>()
            .ToList();
        if (participants.Count == 0) return;

        var route = GroupChatRouter.Resolve(text, participants, entry.Info.PersonaId);
        if (!route.Switched) return;

        var speaker = participants.First(p => p.Id == route.SpeakerPersonaId);
        SwitchSpeaker(entry, speaker);

        var label = string.IsNullOrWhiteSpace(speaker.Role) ? speaker.Name : $"{speaker.Role} ({speaker.Name})";
        // Только в session-группу: клиент открытого чата состоит и в user_/project_-группе,
        // рассылка в обе дублировала разделитель «Теперь отвечает» в ленте
        await _store.BroadcastAsync(sessionId, new SpeakerChangedMessage(speaker.Id, label));
    }

    // Общее ядро смены собеседника/спикера (SetPersona и роутинг группового чата):
    // PersonaId и модель/усилие персоны (у начатой сессии — только при том же провайдере),
    // флаг PersonaSwitched, сброс адаптера (новый слой подхватится при следующем ходе), Save.
    internal void SwitchSpeaker(SessionEntry entry, Persona? persona, string? agentName = null)
    {
        var started = entry.Info.ClaudeSessionId is not null;
        // .md-агент и персона взаимоисключающие: назначение одного сбрасывает другого
        var newAgentName = persona is null && !string.IsNullOrWhiteSpace(agentName)
            ? agentName.Trim()
            : null;
        // Спикер реально сменился, только если изменилась личность собеседника (персона ИЛИ
        // .md-агент). Повторное назначение той же персоны — не смена (иначе PersonaSwitched
        // взводился бы навсегда и в ленту лез разделитель «теперь отвечает чужие прошлые ответы»).
        var switching = started &&
            (entry.Info.PersonaId != persona?.Id || entry.Info.AgentName != newAgentName);

        entry.Info.PersonaId = persona?.Id;
        entry.Info.AgentName = newAgentName;
        if (persona is not null)
        {
            // Модель персоны: своя сильнее её уровня (уровень уже развёрнут в модель по
            // слотам владельца) — дальше по тексту работаем только с этим значением.
            // Владелец — ТОЛЬКО через ResolveOwnerId: у проектной сессии Session.OwnerId
            // равен null (он живёт у проекта), и личные слоты тиров молча подменялись бы
            // глобальными — см. комментарий у GetActiveTurnDelegation
            var personaModel = _assignments.PersonaModel(persona, _store.ResolveOwnerId(entry.Info),
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona));
            if (!started)
            {
                // ?? — а не присваивание в лоб: у персоны без своей модели чат остаётся на той,
                // что уже подставлена при создании (глобальная «модель по умолчанию»).
                // Раньше здесь её затирало в null, и назначение персоны в свежий чат молча
                // возвращало ход к дефолту CLI (заметнее всего в MCP chats_create + personaId)
                entry.Info.Model = personaModel ?? entry.Info.Model;
                entry.Info.Effort = persona.Effort ?? entry.Info.Effort;
            }
            else if (_llmProviders.ProviderKey(personaModel) == _llmProviders.ProviderKey(entry.Info.Model)
                && _subscriptionPool.SupportsModel(entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey, personaModel))
            {
                // Тот же провайдер И подписка сессии способна обслужить модель персоны —
                // модель применяется со следующего хода; иначе оставляем модель сессии
                // (характер всё равно её): пин Opus на аккаунте без Opus валил бы ход
                entry.Info.Model = personaModel ?? entry.Info.Model;
                entry.Info.Effort = persona.Effort ?? entry.Info.Effort;
            }
        }
        if (switching) entry.Info.PersonaSwitched = true;
        // Сдвиг без гейта архива — осознанно (решение 2026-10-09): смена собеседника — это
        // АКТИВНОСТЬ (новый собеседник, PersonaSwitched, разделитель в ленте), а не настройка
        // чата, как SetPinned. Поэтому архивный чат при смене персоны выходит из архива;
        // сторож — ChatArchiveFlagTests. Сдвиг намеренно безусловный и по `switching` не
        // гейтится: двигаем и при повторном выборе той же персоны, и при назначении в
        // неначатый чат (switching == false) — решение Гриши 2026-10-09
        entry.Info.UpdatedAt = DateTime.UtcNow;

        // Адаптер несёт контекст прежнего собеседника (memory-MCP, привязки) — пометим
        // устаревшим. Уборка ЛЕНИВАЯ (EnsureProcessAsync перед следующим ходом), а не
        // немедленная: мгновенный dispose обрывал бы активный ход и убивал доживающих
        // фоновых агентов молча
        if (entry.Process is not null) entry.AdapterStale = true;
        _store.SaveSessions();
    }

    // Редактирование названия и модели. Модель применяется со следующего хода
    // (процесс claude пересоздаётся в RunTurnAsync), Info — общая ссылка с адаптером.
    //
    // PATCH-семантика: null = «поле не передано, не трогать». Иначе частичные апдейты
    // (MCP chats_update только с name; PUT {pinned} из togglePin) затирали бы модель/имя.
    //
    // ownerId — владелец, от чьего имени идёт правка (UserId контроллера). Единственная
    // мутирующая точка, где его раньше не было: смена провайдера уходит в
    // MigrateProviderAsync, а тот проверяет владение — брать владельца из самой сессии
    // означало бы сверять её саму с собой.
    public async Task<Session?> UpdateAsync(string sessionId, string ownerId, string? name, string? model,
        string? effort, List<string>? tags = null)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;

        if (model is not null)
        {
            // Родной Claude храним семейством (opus/fable…): версию выбирает CLI, окно — сервер
            var newModel = _llmProviders.CanonicalizeModel(string.IsNullOrWhiteSpace(model) ? null : model.Trim());
            // Провайдера резолвим по ЭФФЕКТИВНЫМ моделям: пустая означает «по назначению места»,
            // а назначение может указывать на модель стороннего провайдера. По сырому null
            // возврат glm-чата к «По умолчанию» (при назначении на glm) выглядел бы как переезд
            // на claude и упирался бы в guard, а переход claude-чата на «По умолчанию» с чужой
            // моделью, наоборот, проскакивал бы мимо guard и ломал транскрипт.
            // Ключ владельца — тот, от чьего имени идёт правка (тот же, что проверяет
            // MigrateProviderAsync), а не entry.Info.OwnerId: у проектной сессии он пуст по
            // контракту (Models/Session.cs:382), и резолв уходил на общий слот инстанса, тогда
            // как следующий ход резолвит модель по владельцу хода (LlmSessionContext.OwnerId —
            // владелец проекта, SessionManager.cs:7242). Разъезд концов и есть ложная миграция:
            // «По умолчанию» в чате проекта выглядело бы переездом на claude.
            var usageKey = CliRoute.UsageKeyFor(entry.Info.TaskExecution, entry.Info.TaskId, entry.Info.PersonaId);
            var effectiveNew = _assignments.Resolve(usageKey, newModel, ownerId);
            var effectiveCur = _assignments.Resolve(usageKey, entry.Info.Model, ownerId);

            // Смена провайдера: контекст сессии живёт у провайдера (транскрипт эндпоинта),
            // и «переехавшая» сессия молча потеряла бы его. Прежний запрет «нельзя у начатой
            // сессии» держался ровно на этом, но с тех пор в продукте завелась миграция
            // транскрипта — та же, что кнопка «Продолжить на …» даёт живому разговору.
            // Значит вопрос не в том, начат ли чат, а в том, есть ли что переносить: этим
            // занимается MigrateProviderAsync.
            var newProvKey = _llmProviders.ResolveByModel(effectiveNew)?.Key;
            var curProvKey = _llmProviders.ResolveByModel(effectiveCur)?.Key;
            var migrated = false;
            if (newProvKey != curProvKey)
            {
                // Единственная причина отказа — ход прямо сейчас в полёте: смена провайдера
                // на лету увела бы ход в другой профиль CLI посреди прогона. Проверка стоит
                // ЗДЕСЬ, а не внутри MigrateProviderAsync: кнопка «Продолжить на …» аварийная
                // (приходит при исчерпании лимита, когда ход ещё формально жив), и такая
                // проверка сломала бы её.
                if (SessionStore.HasTurnInFlight(entry))
                    throw new InvalidOperationException(
                        "Идёт ответ ассистента — дождитесь его завершения, затем смените модель");

                try
                {
                    // effectiveNew = null — выбрали «По умолчанию», а назначение места модель не
                    // даёт: это переезд на родной Claude без закреплённой модели, а не «модель не
                    // указана». MigrateProviderAsync такой вызов понимает.
                    await _providerMigration.MigrateProviderAsync(sessionId, ownerId, effectiveNew);
                    // Сброс на «По умолчанию» не должен закреплять модель: MigrateProviderAsync
                    // кладёт в Info.Model то, что ему передали (эффективную), и чат переставал бы
                    // следовать назначению места. Provider внутри уже выставлен.
                    if (newModel is null) entry.Info.Model = null;
                    migrated = true;
                }
                catch (ProviderMigration.ProviderUnchangedException)
                {
                    // Здесь провайдеров сравнивали по ЭФФЕКТИВНЫМ моделям, а миграция — по сырым
                    // (Info.Model ?? Info.Provider), и после переставленного назначения места эти
                    // две картины расходятся: чат с Model = null числится на claude (или на своём
                    // аккаунте пула), а по назначению эффективно уезжает на glm; выбор opus
                    // миграция видит как claude → claude. По сырым полям переносить нечего, так
                    // что это не повод валить 400 весь PATCH (вместе с name/effort/tags) —
                    // просто закрепляем модель обычной веткой ниже. Настоящая неизвестная
                    // модель сюда не попадает: у неё свой тип (InvalidOperationException) и
                    // честный 400 наружу.
                }
            }
            if (!migrated)
            {
                // В Info.Model кладём именно то, что выбрали: null = «следовать настройке»
                // (резолвится на каждом ходу, смена настройки подхватывается сама)
                entry.Info.Model = newModel;
                // Провайдер ВСЕГДА выводится из модели (комментарий LlmProviderRegistry: модель —
                // единственный источник правды, Provider не персистится как самостоятельное значение).
                // Инцидент 14.08.2026: пересчёт был только «в стороннего», и смена glm→opus[1m]
                // (пилюля модели в NewChatSetup до первого хода) оставляла пару (Claude-модель, ключ
                // glm) — CLI стартовал в профиле glm с моделью Anthropic → мгновенный 401.
                if (effectiveNew is not null && _llmProviders.ResolveByModel(effectiveNew) is { } newProv)
                    entry.Info.Provider = newProv.Key;
                // Родной Claude (ResolveByModel == null, в т.ч. пул подписок): ключ — из пула
                // (Pick сам отфильтрует исчерпанные/auth-dead, при пустом пуле — PrimaryKey),
                // модель меняем на лету у живого хода — применится к его последующим round-trip'ам.
                else if (effectiveNew is not null)
                {
                    // Чат уже сидит на аккаунте пула, который эту модель тянет — Provider НЕ
                    // трогаем: транскрипт лежит в профиле именно этого аккаунта, а Pick при
                    // равных тарифах тай-брейкает случайно (LeastLoaded, deterministic: false).
                    // Переезд без переноса .jsonl и без AdapterStale не виден сразу (живой
                    // адаптер держит старый корень), но после рестарта --resume идёт в чужой
                    // профиль — «No conversation found» и разговор с нуля. Pick нужен ровно
                    // там, где текущий ключ модель не тянет (пин Opus на плане без Opus) либо
                    // это возврат со стороннего провайдера/чат вообще без аккаунта пула.
                    var cur = entry.Info.Provider;
                    if (cur is null || !_subscriptionPool.All.Any(s => s.Key == cur)
                        || !_subscriptionPool.SupportsModel(cur, effectiveNew))
                        entry.Info.Provider = _subscriptionPool.Pick(effectiveNew);
                    entry.Process?.TrySetModelLive(effectiveNew);
                }
            }
        }

        if (name is not null)
        {
            var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            entry.Info.Name = trimmed;
            // Явно заданное имя (вручную/MCP chats_update) — лочим от авто-переименования;
            // очистка имени (пустое) снимает лок, чтобы авто-заголовок мог сработать снова.
            entry.Info.NameLocked = trimmed is not null;
        }
        if (effort is not null)
            entry.Info.Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        if (tags is not null)
            entry.Info.Tags = tags;

        // Отметка времени — только когда что-то реально правили. PUT приходит и с одними
        // настройками (срок хранения, тумблер уведомлений — их применяет контроллер до этого
        // вызова, сюда все поля доезжают как null): безусловный UpdatedAt поднимал бы чат
        // в списке и метил непрочитанным просто за смену настройки.
        // Архивный чат из архива не выводим: правка имени/модели/тегов доступна и в разделе
        // «Архив», а признак архива производный — UpdatedAt снимает его сам.
        if (name is not null || model is not null || effort is not null || tags is not null)
            if (!entry.Info.IsArchived)
                entry.Info.UpdatedAt = DateTime.UtcNow;
        _store.SaveSessions();
        return entry.Info;
    }

    /// <summary>Явно перевести чат с окна 1M на базовое (200K): срезать суффикс [1m] у модели.</summary>
    /// Единственный путь, которым суффикс окна снимается по воле человека — кнопка «продолжить в
    /// стандартном окне» под карточкой отказа Window1MUnavailable. Автоматического среза больше
    /// нет нигде в ходе чата (он был тихой миной для длинных разговоров), поэтому решение
    /// «мне хватит 200K» принимает пользователь, а сервер только исполняет.
    ///
    /// Модель берём ЭФФЕКТИВНУЮ (Info.Model может быть пуста — тогда модель приходит от слота
    /// назначения места), а закрепляем в чате базовый алиас явно: иначе назначение места на
    /// следующем ходу вернуло бы окно 1M и человек снова упёрся бы в ту же карточку.
    /// Не тир-алиас с окном — снимать нечего, отказ (InvalidOperationException → 400).
    public async Task<Session?> DropWindow1MAsync(string sessionId, string ownerId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        var usageKey = CliRoute.UsageKeyFor(entry.Info.TaskExecution, entry.Info.TaskId, entry.Info.PersonaId);
        var effective = _assignments.Resolve(usageKey, entry.Info.Model, entry.Info.OwnerId);
        if (!LlmProviderRegistry.IsClaudeTierWindowAlias(effective))
            throw new InvalidOperationException("У чата не выбрано окно 1M — переключать нечего");
        return await UpdateAsync(sessionId, ownerId,
            name: null, model: LlmProviderRegistry.StripClaudeWindowAlias(effective), effort: null);
    }
}
