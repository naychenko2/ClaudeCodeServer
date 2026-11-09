using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Sessions;

// Удаление чата (S3-5, docs/research/session-manager-split-2026-11.md §1 «жизненный цикл», §2):
// вынуть запись из реестра, погасить процесс и дочищать за чатом всё, что он держал на диске:
// историю, транскрипт CLI, архивную копию, снимки промпта, своё worktree и слоты ревью его
// дерева. Тело перенесено из SessionManager дословно; фасад держит делегирующую обёртку
// прежней публичной сигнатуры и свой наблюдатель OnSessionDeleted (он приходит в роль
// колбэком вызова — порядок «SaveSessions → наблюдатель → события подсистем → рассылка»
// сохраняется).
//
// Зависимостей семь: стор (реестр, SaveSessions, рассылка), ChatHistoryService (history.json),
// маршрут (транскрипт и его архивная копия), worktree (снос своего дерева и уборка снимка
// графа), снимки промпта, ленивый ReviewTreePool (его настоящий цикл: ReviewTreePool →
// SessionManager — вместо IServiceProvider, решение Александра 05.11, задача 1dda7103; при
// отсутствии пула — null, как было с GetService) и шина событий хода.
//
// Отклонения от таблицы плана §2 (каждое — с комментарием у места; отдельным решением они не
// подтверждены — на проверке у Александра):
// — processHost в конструктор не взят: гашение процесса удаления — прямое
//   entry.Process.DisposeAsync(), хосту здесь делать нечего;
// — projects и worktree-логика — внутри ChatWorktree.RemoveOwnWorktreeAsync (у него
//   проекты уже есть), здесь остаётся только колбэк сноса слотов ревью.
// Класс public по той же причине, что и роли S3-2…S3-4: его принимает публичный конструктор
// фасада (internal-тип в публичном конструкторе — CS0051). Вовне он не назван ни одним типом:
// граница держит сторож правила 4.
public sealed class ChatDeletion
{
    private readonly SessionStore _store;
    private readonly ChatHistoryService _history;
    private readonly CliRoute _route;
    private readonly ChatWorktree _worktree;
    private readonly PromptSnapshotStore? _promptSnapshots;
    private readonly Lazy<ReviewTreePool?> _reviewTreePool;
    // Не readonly, как поле фасада: ленивая шина ниже создаёт локальный экземпляр, когда
    // роль собрана без подсистемы Turn (юниты без DI).
    private Turn.ITurnEventBus? _turnEvents;
    private readonly ILogger<ChatDeletion> _log;

    public ChatDeletion(SessionStore store, ChatHistoryService history, CliRoute route,
        ChatWorktree worktree, PromptSnapshotStore? promptSnapshots, Lazy<ReviewTreePool?> reviewTreePool,
        Turn.ITurnEventBus? turnEvents, ILogger<ChatDeletion> log)
    {
        _store = store;
        _history = history;
        _route = route;
        _worktree = worktree;
        _promptSnapshots = promptSnapshots;
        _reviewTreePool = reviewTreePool;
        _turnEvents = turnEvents;
        _log = log;
    }

    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;
    private Turn.ITurnEventBus TurnEvents => _turnEvents ??= new Turn.TurnEventBus();

    // Решение (Minor, волна 3, осознанно оставлено как есть): удаление чата-штаба НЕ отменяет
    // живые волны, НЕ трогает дочерние чаты исполнения (Session.ParentSessionId вычисляется из
    // Task.SourceSessionId — после удаления штаба они всплывают в корень дерева чатов) и не
    // закрывает под-задачи явно — эскалации незакрытых волн (TeamWaveService.RaiseEscalationAsync
    // → PublishTeamEscalationAsync) молча падают в лог: `_sessions.TryGetValue` не находит
    // удалённую запись и пишет предупреждение, дальше эскалация никуда не уходит.
    // Почему не чиним сейчас: полный каскад (остановить живые процессы исполнителей, решить,
    // что делать с их чатами — удалять, переносить в корень явной карточкой или оставлять как
    // есть, закрыть/пометить под-задачи) — самостоятельная фича с продуктовыми развилками
    // (UX «осиротевших» чатов), а не точечный фикс. Удаление чата с фоновой работой уже и
    // так ничего не отменяет каскадно нигде в системе (обычная задача с исполнителем — тот
    // же класс). Ниже — минимальная страховка: явный лог, чтобы находка не терялась молча.
    internal async Task DeleteAsync(string sessionId, Action<Session>? onDeleted)
    {
        if (!_sessions.TryRemove(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is { WaveNumber: > 0 } liveTeam && liveTeam.WaveNumber > liveTeam.ClosedWave)
            _log.LogWarning("Чат-штаб {SessionId} удалён посреди незакрытой волны {Wave} — " +
                "живые задачи и дочерние чаты исполнения не отменяются и не переносятся " +
                "(осознанное решение волны 3, см. комментарий у DeleteAsync)", sessionId, liveTeam.WaveNumber);
        // Стоп снапшотам ДО удаления файлов: финализация прогона (drain сабагентов,
        // поздние tool_result) доигрывается после dispose и пересоздала бы history.json
        entry.Accumulator?.MarkDeleted();
        if (entry.Process is not null)
            await entry.Process.DisposeAsync();
        // Дренаж очереди сессии до конца: ResultMessage последнего хода обязан дойти до всех
        // наблюдателей (иначе TaskExecutionService не закроет задачу исполнителя)
        // HTTP-путь удаления не ждёт зависшего наблюдателя: дренаж идёт в фоне, исход — в лог
        if (entry.Pipeline is { } pipeline)
            _ = Task.Run(async () =>
            {
                try
                {
                    await pipeline.CompleteAsync(TimeSpan.FromSeconds(30));
                    _log.LogDebug("Очередь удалённой сессии {SessionId} разобрана", sessionId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Дренаж очереди удалённой сессии {SessionId} упал", sessionId);
                }
            });

        // Дочищаем историю на диске — иначе data/sessions/{id} копится мусором
        if (entry.Info.ClaudeSessionId is string csid)
        {
            // И история, и транскрипт CLI могут быть ОБЩИМИ у двух чатов: сессия, созданная с
            // resumeSessionId (POST /sessions, /chats, /personas/{id}/chats), несет тот же
            // ClaudeSessionId. Пока на него ссылается другой чат, не трогаем ни то, ни другое:
            // иначе у него пропадет лента в UI (история) и вся память разговора (транскрипт —
            // его читает --resume). Свою запись из реестра мы вынули выше, поэтому Any видит
            // только чужие ссылки.
            if (_sessions.Values.Any(e => e.Info.ClaudeSessionId == csid))
                _log.LogInformation(
                    "История и транскрипт чата {SessionId} оставлены: на сессию {ClaudeSessionId} ссылается другой чат",
                    entry.Info.Id, csid);
            else
            {
                _history.Delete(csid);
                // Локальный проект: транскрипт на устройстве, архивной копии сервер не делал —
                // искать и удалять файл по своему пути значило бы задеть чужой
                if (_route.TranscriptOnServer(entry.Info))
                {
                    _route.DeleteTranscript(entry.Info, csid);
                    // Архивная копия транскрипта уходит вместе с чатом — иначе переписка
                    // переживёт его и в data, и в бэкапе. Гейт тот же (общий csid у чата-двойника)
                    _route.DeleteArchivedCopy(csid);
                }
            }
        }
        // Снимки промпта ключуются id ЧАТА, а не транскриптом, — гейт общего разговора выше
        // к ним не относится: у чата-двойника свои снимки, и его лента их не потеряет
        _promptSnapshots?.DeleteAll(sessionId);
        // История ЛОКАЛЬНЫХ голосовых ходов пишется под id чата (до первого CLI-хода у чата
        // нет ClaudeSessionId) — чистим всегда: id чата уникален, общим с другим чатом быть
        // не может. Убиратся и файл-дубль после перехода локаль→CLI (история тогда уже
        // пишется под csid, а старая папка оставалась бы мусором до удаления чата). Гейт
        // снизу — единственный экзотический случай:resume-чата с ClaudeSessionId, равным
        // id ЭТОГО чата (resumeSessionId валидируется белым списком, но может совпадать).
        var localHistoryKey = entry.Info.Id.ToString();
        if (!_sessions.Values.Any(e => e.Info.ClaudeSessionId == localHistoryKey))
            _history.Delete(localHistoryKey);

        // Отдельное worktree чата: своё (создано тумблером, WorktreeOwned) сносим вместе с
        // чатом; чужое/общее (привязано) или старое (без признака) — только ReleaseWorktreeGraph,
        // без git worktree remove: дерево делят другие живые чаты, и его снос унёс бы их работу.
        // Запись этого чата из реестра уже вынята выше (TryRemove), поэтому Any видит только чужие.
        // Тело сноса — в ChatWorktree (это его дело); колбэк здесь потому, что ленивый пул
        // слотов ревью — у этой роли.
        await _worktree.RemoveOwnWorktreeAsync(entry, async (wproj, wt) =>
        {
            if (_reviewTreePool.Value is { } pool)
                await pool.RemoveSlotsOfWaveAsync(_store.ResolveOwnerId(entry.Info), wproj, wt);
        });
        _store.SaveSessions();
        try { onDeleted?.Invoke(entry.Info); } catch { /* наблюдатель не должен ронять удаление */ }
        // Подсистемы сносят своё состояние чата (ADR-019 §2); сбой подписчика шина гасит сама
        await TurnEvents.PublishAsync(new Turn.SessionDeleted(
            new Turn.TurnContext(sessionId, _store.ResolveOwnerId(entry.Info), 0, 0, entry.Info.ProjectId)));
        await _store.BroadcastChatDeletedAsync(sessionId, entry.Info);
    }
}
