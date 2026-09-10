using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Memory;

// Причина пропуска прогона autolearn — счётчик пропусков в сервисах пишет её в лог,
// чтобы после выката было видно, что именно гасит фильтр.
public enum AutolearnSkipReason { None, TempChat, Delegated, LowContent }

// Общий гейт «содержательности хода» для авто-извлечения памяти (persona-memory-autolearn,
// team-memory-autolearn): экономит one-shot LLM-вызов на технических/пустых ходах — пинг-чатах,
// временных чатах, ходах чата-исполнителя задачи. Ручные пути записи памяти (Remember/явная
// команда) этот гейт не проходят и не должны — он только на автоматическом извлечении.
public static class AutolearnGate
{
    // Синхронная часть (не требует истории): временный чат либо ход чата-исполнителя задачи
    // (TaskExecutionService) — оба не несут долгоживущих фактов, ради которых стоит звать LLM.
    public static AutolearnSkipReason CheckSession(Session session) =>
        session.ExpiresAfterMinutes is not null ? AutolearnSkipReason.TempChat
        : session.TaskExecution ? AutolearnSkipReason.Delegated
        : AutolearnSkipReason.None;

    // Асинхронная часть (после получения истории): длина реплик ПОСЛЕДНЕГО хода ниже порога —
    // короткие/технические ответы не стоят прогона LLM.
    public static AutolearnSkipReason CheckContent(IReadOnlyList<StoredMessage> history, int minChars) =>
        LastTurnLength(history) < minChars ? AutolearnSkipReason.LowContent : AutolearnSkipReason.None;

    // Длина реплик последнего хода: текст последнего сообщения пользователя + всё, что Claude
    // ответил после него (без сабагентов/thinking/tool-меток — то же деление ролей, что и
    // в SessionSummaryService.BuildTranscript), но только по хвосту истории — сам последний ход.
    // Пользовательского сообщения в истории нет (редкий случай) — считаем по всей истории.
    public static int LastTurnLength(IReadOnlyList<StoredMessage> history)
    {
        var start = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i] is StoredUserMessage) { start = i; break; }
        }

        var total = 0;
        for (var i = start; i < history.Count; i++)
        {
            total += history[i] switch
            {
                StoredUserMessage u => u.Text.Trim().Length,
                StoredTextMessage { ParentToolUseId: null } t => t.Text.Trim().Length,
                _ => 0,
            };
        }
        return total;
    }
}
