using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Prompts;

// Подсказка про контекст чата (материалы, закреплённые пользователем кнопкой «в контекст
// чата»): состав меняется прямо в идущем разговоре, поэтому секция пересобирается каждый
// ход из живого состава (Func-провайдер), а не из снимка при создании адаптера.
//
// Отдельный класс-функция по образцу VoicePrompts: сборка секций внутри ClaudeSession шва
// для юнита не имеет, а текст обещает модели инструмент — молча разойтись с реальностью
// он не должен.
public static class ChatContextPrompts
{
    /// <summary>
    /// Секция для системного промпта хода. null — материалов нет: обещать «загляни в
    /// контекст» пустому чату незачем (и вредно — модель пойдёт звать инструмент впустую).
    /// Вызывать только когда wsp-сервер смонтирован и флаг владельца включён —
    /// иначе context_list в составе нет, а подсказка обещала бы недоступное.
    /// </summary>
    public static string? SectionFor(IReadOnlyList<SessionContextEntry>? entries)
    {
        if (entries is not { Count: > 0 }) return null;

        var files = entries.Count(e => e.Type == SessionContextTypes.File);
        var urls = entries.Count(e => e.Type == SessionContextTypes.Url);
        var tasks = entries.Count(e => e.Type == SessionContextTypes.Task);
        var parts = new List<string>();
        if (files > 0) parts.Add($"файлов — {files}");
        if (urls > 0) parts.Add($"ссылок — {urls}");
        if (tasks > 0) parts.Add($"задач — {tasks}");

        return $"К этому чату пользователь приложил материалы ({entries.Count}: {string.Join(", ", parts)}) — " +
            "это его ручной выбор, а не история разговора: именно про них он говорит «этот файл», " +
            "«эта задача», «та ссылка». Начни ход с mcp__wsp__context_list — он вернёт список с " +
            "подсказкой, чем раскрыть каждую запись, — и сверяйся с материалами по ходу работы. " +
            "Содержимое читай по надобности, а не всё подряд: список короткий, файлы бывают большими.";
    }
}
