using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Сводка этого чата уже собирается (повторный клик) → 409 у контроллера
public sealed class DigestInProgressException() : Exception("Сводка этого чата уже собирается");

// Ошибка генерации сводки (LLM упал/таймаут, пустой чат) → 502 у контроллера
public sealed class DigestGenerationException(string message) : Exception(message);

// Сводка карточки архива (шаг 5 плана «Архив чатов» v4): по кнопке «Собрать сводку»
// собирает транскрипт тем же сборщиком, что «Итог сессии» (SessionSummaryService.
// BuildTranscript), и one-shot вызовом места chat-digest строит 2–3 предложения о чём был
// разговор. Результат кэшируется в Session.ArchiveSummary/ArchiveSummaryAt: свежая сводка
// отдаётся без обращения к модели («один вызов на чат»), активность чата её инвалидирует.
//
// Это НЕ вынос в заметки: «Сохранить в заметки» — отдельная кнопка через существующий
// POST /api/sessions/{id}/summary (SessionSummaryService, место session-summary), здесь
// этот маршрут не участвует вовсе.
//
// Десктопным чатам сводка НЕ строится: наружу, к настроенному для chat-digest провайдеру,
// уехали бы текстовые описания экрана и содержимого чужих окон (кадров в base64 в
// BuildTranscript нет — он собирает текст реплик и строки «[инструмент N]»/«[изменён
// файл ...]», но сами реплики описывают увиденное) — тот же класс запрета, что у
// межпровайдерного фолбэка десктопных чатов.
public class ChatDigestService(
    SessionManager sessions, ProjectManager projects, NotesService notes,
    ICheapTextRunner cheap, ILogger<ChatDigestService> logger)
{
    // Тот же бюджет транскрипта, что у «Итога сессии»: при переполнении голова + хвост
    private const int TranscriptBudget = 30_000;

    // Заглушка карточки без сводки, заметки и последней реплики (канон текстов)
    public const string NoMessagesText = "Сообщений нет";

    // Чаты со сборкой сводки в полёте — защита от параллельных кликов по образцу
    // SessionSummaryService._inFlight: второй клик получает 409, а не вторую оплату модели
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    // Очередь «1 поток на владельца»: сводки чатов одного владельца идут строго по одной,
    // всплеск кликов по разделу «Архив» не запускает пачку параллельных one-shot вызовов
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new();

    public async Task<Session> BuildDigestAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = sessions.GetOwned(sessionId, userId)
            ?? throw new KeyNotFoundException("Чат не найден");
        if (session.DesktopChat)
            throw new InvalidOperationException(
                "Сводка десктопного чата не строится: описания экрана не покидают грань десктопного агента");

        // Кэш: свежая сводка (построена после последней активности) отдаётся как есть
        if (FreshSummary(session) is not null) return session;

        if (!_inFlight.TryAdd(sessionId, 0))
            throw new DigestInProgressException();

        var ownerId = ResolveOwnerId(session) ?? throw new KeyNotFoundException("Чат не найден");
        var gate = _ownerGates.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                // Пока стояли в очереди, чат могли удалить, а сводка — стать свежей
                // (собрал запрос, стартовавший до снятия нашего _inFlight): тогда отдаём
                // кэш, модель не зовём
                session = sessions.GetOwned(sessionId, userId)
                    ?? throw new KeyNotFoundException("Чат не найден");
                if (FreshSummary(session) is not null) return session;

                var history = await sessions.GetHistoryAsync(sessionId);
                var transcript = SessionSummaryService.BuildTranscript(history, TranscriptBudget);
                if (string.IsNullOrWhiteSpace(transcript))
                    throw new DigestGenerationException("В чате ещё нет сообщений для сводки");

                string summary;
                try
                {
                    summary = await cheap.RunAsync(
                        LocalActionCatalog.ChatDigest, BuildPrompt(session.Name, transcript),
                        ownerId: ownerId, ct: ct);
                }
                catch (InvalidOperationException ex)
                {
                    throw new DigestGenerationException(ex.Message);
                }
                if (string.IsNullOrWhiteSpace(summary))
                    throw new DigestGenerationException("Модель вернула пустую сводку");

                var text = summary.Trim();
                sessions.SetArchiveSummary(sessionId, text);
                logger.LogInformation("Сводка карточки архива {SessionId} собрана ({Length} символов)",
                    sessionId, text.Length);
                return session;
            }
            finally { gate.Release(); }
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
        }
    }

    // Свежая (актуальная) сводка: построена ПОСЛЕ последней активности чата. Равные
    // таймстемпы считаются свежими — симметрия с производным признаком IsArchived.
    // При UpdatedAt > ArchiveSummaryAt сводка НЕ выдаётся за актуальную: после возврата
    // чата, новых сообщений и повторной архивации карточка не показывает устаревший итог.
    internal static string? FreshSummary(Session session) =>
        session.ArchiveSummary is { Length: > 0 } summary
        && session.ArchiveSummaryAt is { } at
        && session.UpdatedAt <= at
            ? summary
            : null;

    // Приоритет текста карточки архива (канон — docs/product/archive-chats.md): свежая
    // сводка → первые строки заметки-итога (Session.SummaryNoteId, вынесена кнопкой
    // «Сохранить в заметки») → lastMessage → «Сообщений нет». Ни один вариант не обращается
    // к модели — только явный сбор сводки по кнопке.
    public string CardText(Session session)
    {
        if (FreshSummary(session) is { } fresh) return fresh;
        if (session.SummaryNoteId is { Length: > 0 } noteId
            && ResolveOwnerId(session) is { } ownerId
            && notes.GetDetail(ownerId, noteId) is { } note
            && FirstLines(note.Content) is { } noteLines)
        {
            return noteLines;
        }
        if (!string.IsNullOrWhiteSpace(session.LastMessage)) return session.LastMessage.Trim();
        return NoMessagesText;
    }

    // Владелец чата: у проектной сессии OwnerId null (резолвится через проект), у чата вне
    // проекта — сам OwnerId. Тот же способ резолва, что у SessionSummaryService.
    private string? ResolveOwnerId(Session session) =>
        session.ProjectId is not null
            ? projects.GetById(session.ProjectId)?.OwnerId
            : session.OwnerId;

    // Первые содержательные строки заметки: YAML-frontmatter и пустоты пропускаем, берём до
    // трёх строк (длинная строка режется на 300 символах) — карточке нужен намёк на
    // содержание, а не весь конспект
    internal static string? FirstLines(string content)
    {
        var sb = new StringBuilder();
        var taken = 0;
        using var reader = new StringReader(StripFrontmatter(content));
        string? line;
        while ((line = reader.ReadLine()) is not null && taken < 3)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(trimmed);
            taken++;
        }
        var text = sb.ToString().Trim();
        if (text.Length > 300) text = text[..300].TrimEnd() + "…";
        return text.Length == 0 ? null : text;
    }

    // Тело заметки без YAML-frontmatter (незакрытый блок считаем телом, а не шапкой)
    private static string StripFrontmatter(string content)
    {
        if (!content.StartsWith("---")) return content;
        using var reader = new StringReader(content);
        if (reader.ReadLine() is null) return content;
        while (reader.ReadLine() is { } line)
            if (line.Trim() == "---")
                return reader.ReadToEnd().TrimStart();
        return content;
    }

    internal static string BuildPrompt(string? sessionName, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ниже — транскрипт чата с Claude Code. Составь по нему краткую сводку разговора.");
        sb.AppendLine("2–3 предложения по-русски: о чём шла речь и к чему пришли. " +
                      "Без списков, заголовков и вступлений — только текст сводки. " +
                      "Не выдумывай факты, которых нет в транскрипте.");
        if (!string.IsNullOrWhiteSpace(sessionName))
            sb.AppendLine($"Название чата: {sessionName}");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }
}
