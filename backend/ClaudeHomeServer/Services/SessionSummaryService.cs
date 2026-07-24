using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Итог по этой сессии уже генерируется (повторный клик) → 409 у контроллера
public sealed class SummaryInProgressException() : Exception("Итог по этой сессии уже генерируется");

// Ошибка генерации конспекта (LLM упал/таймаут) → 502 у контроллера
public sealed class SummaryGenerationException(string message) : Exception(message);

// «Итог сессии»: по явному запросу пользователя собирает транскрипт сессии,
// one-shot вызовом claude --print строит конспект и сохраняет его заметкой
// (проектная сессия → notes/Сессии проекта, чат вне проекта → личный vault).
// Повторный вызов обновляет ту же заметку (Session.SummaryNoteId), а не плодит дубли.
public class SessionSummaryService(
    SessionManager sessions, ProjectManager projects, NotesService notes,
    NotesKnowledgeService kb, Llm.ICheapTextRunner cheap,
    IHubContext<SessionHub> hub, PushService push,
    NotificationService notif, IConfiguration config,
    ILogger<SessionSummaryService> logger)
{
    // Бюджет транскрипта в символах: длиннее — сокращаем (голова + хвост)
    private const int TranscriptBudget = 30_000;

    // Сессии с генерацией в полёте — защита от параллельных кликов
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public async Task<NoteDetail> SummarizeAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = sessions.GetById(sessionId)
            ?? throw new KeyNotFoundException("Сессия не найдена");
        var ownerId = session.ProjectId is not null
            ? projects.GetById(session.ProjectId)?.OwnerId
            : session.OwnerId;
        if (ownerId is null || ownerId != userId)
            throw new UnauthorizedAccessException("Сессия принадлежит другому пользователю");

        if (!_inFlight.TryAdd(sessionId, 0))
            throw new SummaryInProgressException();
        try
        {
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
                    config["Notes:SummaryModel"] ?? config["Notes:AiModel"] ?? "haiku", ct: ct);
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
                && notes.GetDetail(ownerId, session.SummaryNoteId) is not null;
            if (isUpdate)
            {
                note = notes.Update(ownerId, session.SummaryNoteId!, new UpdateNoteRequest(Content: summary))
                    ?? throw new SummaryGenerationException("Заметка-итог не обновилась");
            }
            else
            {
                note = notes.Create(ownerId, new CreateNoteRequest(
                    Title: BuildTitle(session),
                    Content: summary,
                    Source: session.ProjectId,   // null → личный vault (чат вне проекта)
                    Folder: "Сессии"));
                sessions.SetSummaryNoteId(sessionId, note.Id);
            }

            kb.QueueSync(ownerId);
            await hub.Clients.Group("user_" + ownerId).SendAsync(
                "message", new NotesChangedMessage(isUpdate ? "updated" : "created", note.Id), ct);

            await notif.SendNotificationMessageAsync(ownerId, new NotificationMessage(
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
    // и файлах; thinking и метаданные пропускаются. Переполнение бюджета — голова (цель
    // сессии) + хвост (развязка), середина сокращается.
    internal static string BuildTranscript(IReadOnlyList<StoredMessage> messages, int budget)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            switch (m)
            {
                case StoredUserMessage u when !string.IsNullOrWhiteSpace(u.Text):
                    sb.AppendLine("Пользователь:");
                    sb.AppendLine(u.Text.Trim());
                    sb.AppendLine();
                    break;
                // Текст сабагента (ParentToolUseId != null) — не реплика Claude в диалоге
                case StoredTextMessage { ParentToolUseId: null } t when !string.IsNullOrWhiteSpace(t.Text):
                    sb.AppendLine("AI:");
                    sb.AppendLine(t.Text.Trim());
                    sb.AppendLine();
                    break;
                case StoredToolUseMessage tu when !string.IsNullOrEmpty(tu.Name):
                    sb.AppendLine($"[инструмент {tu.Name}]");
                    break;
                case StoredFileChangedMessage f:
                    sb.AppendLine($"[изменён файл {f.Path} +{f.Added}/-{f.Removed}]");
                    break;
            }
        }
        var text = sb.ToString().Trim();
        if (text.Length <= budget) return text;
        var head = budget / 5;
        var tail = budget - head;
        return text[..head] + "\n\n[…транскрипт сокращён…]\n\n" + text[^tail..];
    }

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
