using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Итог по этой сессии уже генерируется (повторный клик) → 409 у контроллера
public sealed class SummaryInProgressException() : Exception("Итог по этой сессии уже генерируется");

// Ошибка генерации конспекта (LLM упал/таймаут) → 502 у контроллера
public sealed class SummaryGenerationException(string message) : Exception(message);

// Подсистема Notes выключена — конспект сохранять некуда. Отдельный тип, а не
// SummaryGenerationException: это состояние инстанса, а не сбой обработки, и клиенту
// повторять запрос бессмысленно. Контроллер отдаёт за него 503 + reason="notes_disabled" —
// ровно тот же наблюдаемый контракт, что у BriefingUnavailableException в «Утреннем брифе»
// (одна причина — один код отказа).
public sealed class SummaryUnavailableException(string message) : Exception(message);

// «Итог сессии»: по явному запросу пользователя собирает транскрипт сессии,
// one-shot вызовом claude --print строит конспект и сохраняет его заметкой
// (проектная сессия → notes/Сессии проекта, чат вне проекта → личный vault).
// Приватный чат (S2d-6a): сводит только его автор, заметка — в личный vault автора,
// в notes/ проекта такая заметка не появляется ни при каком вызове.
// Повторный вызов обновляет ту же заметку (Session.SummaryNoteId), а не плодит дубли.
public class SessionSummaryService(
    SessionManager sessions, ProjectManager projects,
    Llm.ICheapTextRunner cheap,
    ISessionBroadcaster broadcaster,
    NotificationService notif, IConfiguration config,
    ILogger<SessionSummaryService> logger, INoteAccessor? notes = null,
    // Подсистема Notes отключаемая: null, если выключена. Проверка notes is null стоит
    // ПЕРВОЙ в SummarizeAsync (после inFlight), чтобы при выключенной подсистеме не
    // платить за LLM-конспект, который некуда сохранить; kb тоже null и зовётся через `?.`.
    INoteSemanticIndex? kb = null)
{
    // Бюджет транскрипта в символах: длиннее — сокращаем (голова + хвост)
    private const int TranscriptBudget = 30_000;

    // Сессии с генерацией в полёте — защита от параллельных кликов
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public async Task<NoteDetail> SummarizeAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = sessions.GetById(sessionId)
            ?? throw new KeyNotFoundException("Сессия не найдена");

        // Приватность читается ОДИН РАЗ здесь — до _inFlight и до платного LLM-вызова:
        // всё решение ниже идёт по этому снимку. Если приватность снимут посреди генерации,
        // заметка всё равно уйдёт автору (обратный переход закрыт самим чатом:
        // chat_has_messages). Пустая строка — «не видит никто» (fail-closed, как в
        // SessionManager.CreateAsync): она не совпадает ни с одним userId.
        var author = session.PrivateToUserId;
        // Чужой приватный чат для вызывающего не существует: тот же отказ, что у read-ручек
        // чатов (404 «нет такого чата», а не 403 «нет права») — приватность не должна
        // различать «чужое» и «несуществующее».
        if (author is not null && author != userId)
            throw new KeyNotFoundException("Сессия не найдена");

        var ownerId = session.ProjectId is not null
            ? projects.GetById(session.ProjectId)?.OwnerId
            : session.OwnerId;
        // Гейт владельца остаётся только за общим чатом: приватный сводит его автор, и доступ
        // к проекту ему не нужен — заметка идёт в его личный vault, а не в notes/ проекта.
        if (author is null && (ownerId is null || ownerId != userId))
            throw new UnauthorizedAccessException("Сессия принадлежит другому пользователю");

        // Владелец заметки: приватный чат — автор (личный vault, Source: null); общий —
        // владелец, как раньше (проектный чат → notes/Сессии проекта, чат вне проекта →
        // его личный vault). Оплата LLM (ownerId ниже) не меняется: её платит владелец проекта.
        var noteOwner = author ?? ownerId!;

        if (!_inFlight.TryAdd(sessionId, 0))
            throw new SummaryInProgressException();
        try
        {
            // Подсистема Notes отключена — конспект сохранять некуда. Проверка стоит
            // ДО платного cheap.RunAsync: иначе пользователь платит за LLM-конспект,
            // который гарантированно будет выброшен (эталон — DailyBriefingService).
            if (notes is null)
                throw new SummaryUnavailableException(
                    "Итог сессии недоступен: подсистема «Заметки» отключена");

            var history = await sessions.GetHistoryAsync(sessionId);
            var transcript = BuildTranscript(history, TranscriptBudget);
            if (string.IsNullOrWhiteSpace(transcript))
                throw new InvalidOperationException("В сессии ещё нет сообщений для конспекта");

            string summary;
            try
            {
                summary = await cheap.RunAsync(
                    Llm.LocalActionCatalog.SessionSummary,
                    BuildPrompt(session.Name, transcript),
                    config["Notes:SummaryModel"] ?? config["Notes:AiModel"] ?? "haiku", ownerId, ct: ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new SummaryGenerationException(ex.Message);
            }
            if (string.IsNullOrWhiteSpace(summary))
                throw new SummaryGenerationException("Модель вернула пустой конспект");

            // Существующая заметка-итог обновляется (заголовок не трогаем — пользователь мог переименовать)
            NoteDetail note;
            var isUpdate = session.SummaryNoteId is not null
                && notes.GetDetail(noteOwner, session.SummaryNoteId) is not null;
            if (isUpdate)
            {
                note = notes.Update(noteOwner, session.SummaryNoteId!, new UpdateNoteRequest(Content: summary))
                    ?? throw new SummaryGenerationException("Заметка-итог не обновилась");
            }
            else
            {
                note = notes.Create(noteOwner, new CreateNoteRequest(
                    Title: BuildTitle(session),
                    Content: summary,
                    // Приватный чат — всегда личный vault автора (Source: null). Общий — как раньше:
                    // id проекта → notes/Сессии проекта, null (чат вне проекта) → личный vault.
                    Source: author is null ? session.ProjectId : null,
                    Folder: "Сессии"));
                sessions.SetSummaryNoteId(sessionId, note.Id);
            }

            kb?.QueueSync(noteOwner);
            await broadcaster.ToOwner(noteOwner,
                new NotesChangedMessage(isUpdate ? "updated" : "created", note.Id));

            await notif.SendNotificationMessageAsync(noteOwner, new NotificationMessage(
                Title: "Итог сессии готов",
                Body: note.Title,
                Url: "#/notes/" + Uri.EscapeDataString(note.Id),
                Kind: "claude",
                Tag: "Саммари"));

            logger.LogInformation("Итог сессии {SessionId} → заметка {NoteId} ({Action})",
                sessionId, note.Id, isUpdate ? "обновлена" : "создана");
            return note;
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
        }
    }

    // Транскрипт для LLM: реплики пользователя/Claude + однострочные пометки об инструментах
    // и файлах. Тело переехало в спину — `Services.SessionTranscript.Build` (Этап 5, волна 3:
    // функцию зовут вертикали Memory/Dossiers, а из отдельной сборки Main не виден). Здесь
    // остался тонкий форвардер ради семи вызывающих внутри Main — тот же приём, что
    // `FileService.SafeJoin` → `SafePath.Join`.
    internal static string BuildTranscript(IReadOnlyList<StoredMessage> messages, int budget) =>
        SessionTranscript.Build(messages, budget);

    internal static string BuildPrompt(string? sessionName, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ниже — транскрипт рабочей сессии с Claude Code. Составь конспект-заметку по нему.");
        sb.AppendLine("Структура: цель сессии; ключевые решения и их причины; что сделано и изменено; " +
                      "открытые вопросы и следующие шаги (если есть).");
        sb.AppendLine("Пиши по-русски, сжато, в markdown (заголовки ##, списки). " +
                      "Не выдумывай факты и [[wikilinks]], которых нет в транскрипте. " +
                      "Ответь ТОЛЬКО текстом заметки, без вступлений и пояснений.");
        if (!string.IsNullOrWhiteSpace(sessionName))
            sb.AppendLine($"Название сессии: {sessionName}");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }

    internal static string BuildTitle(Session session)
    {
        var name = string.IsNullOrWhiteSpace(session.Name) ? "чат" : session.Name.Trim();
        if (name.Length > 60) name = name[..60].TrimEnd() + "…";
        return $"Итог: {name} · {DateTime.Now:yyyy-MM-dd}";
    }
}
