using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Записка этого чата уже собирается (повторный клик) → 409 у контроллера
public sealed class HandoffInProgressException() : Exception("Перенос этого чата уже идёт");

// Модель не собрала записку (упала/таймаут/пустой ответ) → 502 у контроллера
public sealed class HandoffGenerationException(string message) : Exception(message);

// Итог переноса: новый чат + черновик композера (передаточная записка, не автоотправка)
public sealed record ChatHandoffResult(Session Session, string Draft);

// Перенос в новый чат, холодный путь (docs/features/chat-handoff.md): по транскрипту чата
// дешёвый раннер (место chat-handoff) пишет передаточную записку, сервер создаёт чат-преемник
// без resume и отдаёт записку черновиком. Новый чат знает, о чём был разговор, и НЕ платит
// холодную перезапись всей истории — поэтому не ветвление и не --fork-session: те тащат
// историю целиком.
//
// Старый чат только читается: ни UpdatedAt, ни архив, ни сводки не трогаем.
//
// Образец — ChatDigestService (тот же класс сервиса в спине): _inFlight против двойного
// клика, очередь «1 поток на владельца». Отказа «десктопным чатам», как у digest, тут нет:
// такого вида чата больше не существует (Session.DesktopChat удалён вместе с «руками» ADR-008).
public class ChatHandoffService(
    SessionManager sessions, IProjectManager projects, ICheapTextRunner cheap,
    ILogger<ChatHandoffService> logger)
{
    // Вдвое больше, чем у сводки: записке нужны решения и их причины, а не две строки итога.
    // При переполнении — голова (цель) + хвост (где остановились), см. SessionTranscript
    public const int TranscriptBudget = 60_000;

    public const string NothingToHandOff = "В чате ещё нет сообщений — переносить нечего";

    // Окно локали (16k) и её 1024 токена вывода записку по транскрипту не вывозят, а
    // обрезанная передача хуже честного отказа — модель не зовём вовсе
    public const string LocalRouteRefusal =
        "Перенос в новый чат назначен на локальную модель, а её окна не хватает на передачу разговора. "
        + "Назначьте месту «Перенос в новый чат» облачного исполнителя в «Поставщиках моделей».";

    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new();

    public async Task<ChatHandoffResult> HandoffAsync(string userId, string sessionId, CancellationToken ct)
    {
        var source = sessions.GetOwned(sessionId, userId)
            ?? throw new KeyNotFoundException("Чат не найден");

        // Отказы — до модели: платить за записку, которую некуда положить, незачем
        sessions.EnsureHandoffAllowed(source);
        if (cheap.UsesLocal(LocalActionCatalog.ChatHandoff))
            throw new InvalidOperationException(LocalRouteRefusal);

        if (!_inFlight.TryAdd(sessionId, 0))
            throw new HandoffInProgressException();

        try
        {
            var ownerId = ResolveOwnerId(source) ?? throw new KeyNotFoundException("Чат не найден");
            var gate = _ownerGates.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Пока стояли в очереди, чат могли удалить
                source = sessions.GetOwned(sessionId, userId)
                    ?? throw new KeyNotFoundException("Чат не найден");

                var history = await sessions.GetHistoryAsync(sessionId);
                var transcript = SessionTranscript.Build(history, TranscriptBudget);
                if (string.IsNullOrWhiteSpace(transcript))
                    throw new InvalidOperationException(NothingToHandOff);

                string note;
                try
                {
                    note = await cheap.RunAsync(LocalActionCatalog.ChatHandoff,
                        BuildPrompt(source.Name, transcript), ownerId: ownerId, ct: ct);
                }
                catch (InvalidOperationException ex)
                {
                    throw new HandoffGenerationException(ex.Message);
                }
                if (string.IsNullOrWhiteSpace(note))
                    throw new HandoffGenerationException("Модель вернула пустую передаточную записку");

                var name = $"{(string.IsNullOrWhiteSpace(source.Name) ? "Чат" : source.Name)} (продолжение)";
                var successor = await sessions.CreateHandoffChatAsync(source, ownerId, name);
                var draft = BuildDraft(source, note.Trim());
                logger.LogInformation("Чат {SessionId} перенесён в {NewId} (записка {Length} символов)",
                    sessionId, successor.Id, draft.Length);
                return new ChatHandoffResult(successor, draft);
            }
            finally { gate.Release(); }
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
        }
    }

    // Владелец: у проектной сессии OwnerId null (живёт у проекта) — как у ChatDigestService
    private string? ResolveOwnerId(Session session) =>
        session.ProjectId is not null
            ? projects.GetById(session.ProjectId)?.OwnerId
            : session.OwnerId;

    // Единственный промпт передачи: им же будет пользоваться горячий путь (фаза C).
    // Ссылку на старый чат пишет код (BuildDraft), а не модель — id она бы исказила.
    public static string BuildPrompt(string? sessionName, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ниже — транскрипт чата с Claude Code. Разговор переносится в новый чат, который");
        sb.AppendLine("не увидит этой истории. Составь передаточную записку, по которой новый собеседник");
        sb.AppendLine("продолжит работу без потери контекста.");
        sb.AppendLine();
        sb.AppendLine("Разделы — markdown-заголовками третьего уровня, строго в этом порядке:");
        sb.AppendLine("### Цель");
        sb.AppendLine("### Решения и их причины");
        sb.AppendLine("### От чего отказались");
        sb.AppendLine("### Изменённые файлы");
        sb.AppendLine("### Где остановились");
        sb.AppendLine("### Следующий шаг");
        sb.AppendLine("### Открытые вопросы");
        sb.AppendLine();
        sb.AppendLine("Правила: по-русски, кратко, списками. Не выдумывай того, чего нет в транскрипте: " +
                      "нечего сказать по разделу — напиши «нет». Пути файлов и идентификаторы — как в " +
                      "транскрипте. Без вступлений и заключений — только записка.");
        if (!string.IsNullOrWhiteSpace(sessionName))
            sb.AppendLine($"Название чата: {sessionName}");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }

    // Черновик нового чата: записка модели + ссылка на старый чат последней строкой. У
    // временного чата — предупреждение: ссылка умрёт вместе с ним (новый чат не временный).
    internal static string BuildDraft(Session source, string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Продолжаем разговор из чата «{(string.IsNullOrWhiteSpace(source.Name) ? "Чат" : source.Name)}». Передаточная записка:");
        sb.AppendLine();
        sb.AppendLine(note);
        sb.AppendLine();
        if (source.ExpiresAfterMinutes is not null)
            sb.AppendLine("Исходный чат временный: после его удаления ссылка ниже перестанет работать.");
        sb.Append($"Исходный чат: {source.Id} — детали через chats_history.");
        return sb.ToString();
    }
}
