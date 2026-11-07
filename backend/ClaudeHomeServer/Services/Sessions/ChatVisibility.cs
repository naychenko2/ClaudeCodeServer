using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

// Видимость чата (S3-3, docs/research/session-manager-split-2026-11.md §1): тумблер приватности
// с переносом вложений между раскладками, сохранение вложения в нужную раскладку, перепись путей
// вложений в сообщениях по карте переноса, вышибание чужих соединений из session-группы и
// флаг «исключить из паспортов». Вынесено из SessionManager; фасад держит тонкие обёртки с
// прежними сигнатурами.
// Замки не меняют ключа: VisibilityLock — составляющая записи SessionEntry (по сессии), а не
// поле этой роли — тот же замок, что у оставшейся в фасаде записи Participants (см. его
// комментарий); PendingLock — тот же, под которым работает очередь.
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051);
// сам конструктор internal: все типы его параметров публичны (SessionStore публичный), поэтому
// internal-ность здесь не вынуждена CS0051 (в отличие от ProviderMigration, где причина —
// internal-тип Lazy<ITeamNotifier>) — роль собирается только фасадом (запасная сборка) и
// регистрацией ядра, вовне она не строится. Обход фасада закрывает сторож формы ядра (правило 4).
// Зависимости (5): store, chatHistory, projects, broadcaster, шов Lazy<IFeatureChatLinks>.
// В таблице S3-0 их шесть (route, queue вместо broadcaster): EffectiveRoot и
// MapQueuedAttachedPaths — статические методы CliRoute и PendingQueueService, экземпляр роли
// под них не нужен; зато нужен broadcaster — EvictForeignViewersAsync вышибает соединения и
// шлёт событие проекта напрямую через него (у стора такого шва нет).
// Шов «видимость → привязка к фиче»: ленивый и опциональный — настоящий цикл
// FeatureChatLinks(ISessionDirectory, TaskManager) → SessionManager (решение Александра, 05.11),
// поэтому связь названа типом Lazy<…>. Параметр опционален: без DI (юниты) он null и ведёт себя
// как прежний null _services — привязка к фиче не проверяется (boundToFeature = false).
public sealed class ChatVisibility
{
    private readonly SessionStore _store;
    private readonly ChatHistoryService _history;
    private readonly ProjectManager _projects;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly Lazy<IFeatureChatLinks>? _featureLinks;
    private readonly ILogger<ChatVisibility> _log;

    internal ChatVisibility(
        SessionStore store,
        ChatHistoryService history,
        ProjectManager projects,
        ISessionBroadcaster broadcaster,
        Lazy<IFeatureChatLinks>? featureLinks,
        ILogger<ChatVisibility> log)
    {
        _store = store;
        _history = history;
        _projects = projects;
        _broadcaster = broadcaster;
        _featureLinks = featureLinks;
        _log = log;
    }

    // Тумблер видимости чата (S2d-7a, PUT /api/chats/{id}/visibility). Вызывающий уже прошёл
    // CanSee (404) и отказ читателю (403) в контроллере; здесь — общий проект, режим чата, автор
    // и «сообщений нет» атомарно под VisibilityLock. Порядок отказов: режим (TeamOrGroup) идёт
    // раньше авторского (QA-D45) — у штаба и группы нет автора, которого касалась бы эта
    // операция. true: только автор, только общий проект (есть участники),
    // только пока в чате нет сообщений; ExcludeFromDossiers = true. false: только автор, в любой
    // момент и после этого вернуть приватность нельзя (chat_has_messages); ExcludeFromDossiers
    // остаётся true — прошлое приватного периода не раскрываем.
    public ChatVisibilityResult SetChatVisibility(string sessionId, string userId, bool makePrivate)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry))
            return new(ChatVisibilityOutcome.NotFound, null);
        var info = entry.Info;
        var notAuthor = string.IsNullOrEmpty(userId) || info.CreatedByUserId != userId;

        // Чат, уже входящий в фичу, приватным не делается (S2d-7b-2). Шов берётся лениво (не из
        // конструктора: ядро не зависит от Tasks) и считается ДО замка видимости — чтобы не брать
        // замки задач под ним; привязать приватный чат к фиче потом не даёт дверь привязки.
        // !notAuthor: за пределами авторского пути ниже результат не читается, чужой вызов
        // не должен платить за счёт по задачам.
        var boundToFeature = makePrivate && !notAuthor && info.PrivateToUserId != userId
            && _featureLinks is not null
            && _store.ResolveOwnerId(info) is { } chatOwner
            && _featureLinks.Value.IsBoundToFeature(chatOwner, info.Id);

        Dictionary<string, string>? moves = null;
        lock (entry.VisibilityLock)
        {
            if (makePrivate)
            {
                var project = info.ProjectId is not null ? _projects.GetById(info.ProjectId) : null;
                if (project?.Members is not { Count: > 0 })
                    return new(ChatVisibilityOutcome.NotSharedProject, null);
                // Штаб «Командной реализации» и групповой чат — режимы общего контура (S2d-7b-3).
                // QA-D45 (D4): отказ режима идёт раньше авторского — у штаба и группы нет автора,
                // которого касалась бы эта операция, и участник своего группового чата получал 403
                // not_chat_author вместо 409 private_chat_mode. Существование не утекает:
                // вызывающий уже прошёл CanSee в контроллере (постороннему — 404).
                if (info.PrivateToUserId != userId && IsTeamOrGroup(info))
                    return new(ChatVisibilityOutcome.TeamOrGroup, null);
                if (notAuthor)
                    return new(ChatVisibilityOutcome.NotAuthor, null);
                if (boundToFeature)
                    return new(ChatVisibilityOutcome.BoundToFeature, null);
                // Уже приватный — идемпотентно: повторный true не должен падать на «сообщениях»
                if (info.PrivateToUserId != userId)
                {
                    // «Пусто» = нет записей ленты (карточки модулей и штаба шлются мимо LastMessage и
                    // MessageCount) и нет прежних признаков хода; Accumulator null — лента не загружена,
                    // тогда её следы ловит персистентная отметка MessageClaimed (ставит AppendStoredAsync).
                    if (info.MessageClaimed == true || info.MessageCount > 0 || info.LastMessage is not null
                        || info.LastUserMessageAt is not null || entry.Accumulator?.GetAll().Count > 0)
                        return new(ChatVisibilityOutcome.HasMessages, null);
                    // Вложения, загруженные до перевода, переезжают в закрытое поддерево автора ДО флага
                    // и под тем же замком, что Upload: файла в неверной раскладке не остаётся.
                    // Не вышло — откат и отказ, флаг не ставится (иначе содержимое приватного чата осталось бы общим).
                    if (!TryRelocateAttachments(info, userId, toPrivate: true, out moves))
                        return new(ChatVisibilityOutcome.AttachmentsMoveFailed, null);
                    info.PrivateToUserId = userId;
                }
                info.ExcludeFromDossiers = true;
            }
            else
            {
                // Снять приватность — по-прежнему только автор: здесь отказа режима нет
                // (приватный чат не бывает штабом/группой), поэтому авторский остаётся первым.
                if (notAuthor)
                    return new(ChatVisibilityOutcome.NotAuthor, null);
                // Чат побывал общим (или остаётся им): вернуть приватность нельзя — его могли
                // видеть другие участники. Метка та же, что у первого сообщения: единый отказ
                // chat_has_messages на повторном true.
                if (info.PrivateToUserId is not null)
                {
                    // Обратный перенос: иначе участники общего чата получают 404 на картинки. Отказ
                    // не страшен для утечки (файлы остаются закрытыми), но флаг не снимаем — состояние целое.
                    if (!TryRelocateAttachments(info, userId, toPrivate: false, out moves))
                        return new(ChatVisibilityOutcome.AttachmentsMoveFailed, null);
                    // Пути в сообщениях едут за файлами: иначе автор и участники получают 404 на картинки ленты.
                    if (moves is { Count: > 0 }) RewriteMessageAttachedPaths(entry, moves);
                    info.MessageClaimed = true;
                }
                info.PrivateToUserId = null;
            }
        }
        _store.SaveSessions();
        return new(ChatVisibilityOutcome.Ok, info) { AttachmentMoves = moves };
    }

    // Чат — штаб «Командной реализации» (роль штаба) или групповой (есть участники-персоны).
    private static bool IsTeamOrGroup(Session info) =>
        info.TeamImplement is not null || info.Participants is { Count: > 0 };

    // Сохранить вложение чата и записать его в учёт (S2d-6b-4). Раскладку (общая или private/{автор})
    // выбирает ЗДЕСЬ, под VisibilityLock, — тем же замком, под которым SetChatVisibility переносит файлы:
    // upload либо успевает до переноса (и файл переедет вместе с флагом), либо идёт после и пишет
    // уже в новую раскладку. write получает итоговый путь и пишет файл (под замком — вызывающий
    // передал готовые байты, ожидания ввода-вывода по сети нет). null — имя не прошло санацию
    // или чата нет.
    public string? SaveChatAttachment(string sessionId, string callerId, string? fileName, Action<string> write)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        var info = entry.Info;
        string? rel;
        lock (entry.VisibilityLock)
        {
            rel = info.ProjectId is not null && info.PrivateToUserId is { } author && author == callerId
                ? Access.PrivateAttachments.Build(author, Guid.NewGuid(), fileName)
                : AttachmentsGitExclude.AttachmentPath(fileName);
            if (rel is null) return null;
            write(rel);
            (info.Attachments ??= []).Add(rel);
        }
        // Учёт должен пережить рестарт раньше автосейва: иначе перенос при включении приватности
        // не увидит файл и оставит его в общей раскладке (по образцу ClaimFirstMessage).
        _store.SaveSessions();
        return rel;
    }

    // Переписывает пути вложений в сообщениях чата (память и history.json) по карте переноса.
    // Активный аккумулятор — в памяти плюс снимок на диск; неактивный чат — прямо файл истории.
    // Отложенные сообщения очереди (entry.Pending и pending.json) — тем же проходом: иначе
    // отложенное сообщение уйдёт по старому private/… и AttachmentInliner молча пропустит
    // перенесённый файл. Сбой записи не откатывает перенос: логируем, состояние флага и файлов
    // остаётся целым.
    private void RewriteMessageAttachedPaths(SessionEntry entry, IReadOnlyDictionary<string, string> map)
    {
        var info = entry.Info;
        try
        {
            // Очередь — под PendingLock, тем же замком, под которым EnqueuePendingAsync ставит
            // новые сообщения: параллельная постановка не потеряется и не задублируется.
            // Снятое из очереди (его уже доставляет drain — RemoveAt под этим же замком) сюда
            // не попадает: его в очереди уже нет.
            lock (entry.PendingLock) entry.Pending.ReplaceWhere(q => PendingQueueService.MapQueuedAttachedPaths(q, map));
            if (entry.Accumulator is { } acc)
            {
                if (acc.RewriteAttachedPaths(map))
                    acc.SaveSnapshotAsync(_history).GetAwaiter().GetResult();
                return;
            }
            var key = SessionHistory.HistoryKeyOf(info);
            var stored = _history.LoadAsync(key).GetAwaiter().GetResult();
            if (TurnAccumulator.RewriteAttachedPaths(stored, map))
                _history.SaveAsync(key, stored).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось переписать пути вложений в сообщениях чата {Id} при снятии приватности", info.Id);
        }
    }

    // Перенос учтённых вложений чата между раскладками. Зовётся ТОЛЬКО под VisibilityLock.
    // Файла на диске нет (удалён вручную) — пропускаем и снимаем с учёта; ошибка переноса — откат уже
    // перенесённых и false. Возвращает карту «старый путь → новый» для композера (черновик ссылается на старые).
    private bool TryRelocateAttachments(Session info, string authorId, bool toPrivate, out Dictionary<string, string>? moves)
    {
        moves = null;
        if (info.Attachments is not { Count: > 0 } list) return true;
        var rootPath = info.ProjectId is not null ? _projects.GetById(info.ProjectId)?.RootPath : null;
        if (rootPath is null) return true;
        var root = CliRoute.EffectiveRoot(info, rootPath);
        var done = new List<(string From, string To)>();
        var next = new List<string>();
        try
        {
            foreach (var rel in list)
            {
                var target = toPrivate ? Access.PrivateAttachments.ToPrivate(authorId, rel) : Access.PrivateAttachments.ToShared(rel);
                if (target is null) { next.Add(rel); continue; }
                var from = SafePath.Join(root, rel);
                var to = SafePath.Join(root, target);
                if (!File.Exists(from)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, overwrite: false);
                done.Add((from, to));
                next.Add(target);
                (moves ??= [])[rel] = target;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Перенос вложений чата {Id} при смене видимости не удался, откат", info.Id);
            foreach (var (from, to) in done)
                try { File.Move(to, from, overwrite: false); }
                catch (Exception rex) { _log.LogWarning(rex, "Откат переноса вложения чата {Id} не удался: {To} остался на месте", info.Id, to); }
            moves = null;
            return false;
        }
        info.Attachments = next;
        return true;
    }

    // Вторая половина перевода в приватный (S2d-7a): чужие соединения снимаются с session-группы и
    // группа проекта получает событие без содержимого. Порядок важен: сначала вышибаем, потом шлём
    // событие — клиент участника после него уже не получит ни одного события чата. Звать после Ok от
    // SetChatVisibility(makePrivate: true); вещатель без шва вышибания — только событие.
    public async Task EvictForeignViewersAsync(string sessionId, string authorId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        var info = entry.Info;
        if (info.ProjectId is not string pid || info.PrivateToUserId != authorId) return;
        var project = _projects.GetById(pid);
        if (project is null) return;
        var others = new HashSet<string>(project.Members?.Select(m => m.UserId) ?? []);
        if (project.OwnerId is { } ownerId) others.Add(ownerId);
        others.Remove(authorId);
        if (_broadcaster is ISessionGroupEvictor evictor)
            foreach (var connectionId in await evictor.EvictUsersFromSessionGroup(sessionId, others))
                _store.RemoveViewer(sessionId, connectionId);
        await _broadcaster.ToProject(pid, new ChatVisibilityChangedMessage(true) { SessionId = sessionId });
    }

    // Opt-out «Истории решений» (ADR-004 §6): тумблер «Не сохранять решения из этого чата».
    // Персистится в sessions.json; DossierCaptureService проверяет его при захвате коммита.
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    // S2d-6c: пока чат приватный, false не принимается — иначе паспорта приватного разговора
    // молча попали бы в общую «Историю решений» проекта. Признак приватности (PrivateToUserId,
    // включая пустую строку у старых записей) читается ПОД VisibilityLock — тем же замком, под
    // которым SetChatVisibility ставит приватность: чтение до замка пропустило бы быстрое
    // «не приватный» в запись после тумблера. false после снятия приватности — явное согласие
    // автора, принимается. Отказ — false, флаг не меняется; исключений наружу нет.
    public bool TrySetExcludeFromDossiers(string sessionId, bool value)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;
        lock (entry.VisibilityLock)
        {
            if (!value && entry.Info.PrivateToUserId is not null) return false;
            entry.Info.ExcludeFromDossiers = value;
        }
        _store.SaveSessions();
        return true;
    }
}
