using System.ComponentModel;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Локальный детерминированный сбой старта процесса CLI: бюджет промпта на склейке
// (TurnPromptAssembler.ApplyBudget) превышен даже после срезки всех нестабильных секций,
// и мы не можем собрать --append-system-prompt в пределах 32 767 символов командной строки
// Windows. Исключение бросает ClaudeSession после ApplyBudget; общий catch в RunTurnAsync
// ловит его, кладёт [Win32:206]-совместимый маркер в Details ErrorMessage (см. шаг
// "маркер Win32-кода" в TurnErrorClassifier.cs), адаптер через TurnErrorClassifier
// получает FallbackErrorClass.PromptOverflow и фолбэк не запускается.
//
// Win32Exception здесь — технический «двойник» реального исключения старта процесса: те
// же 206 ERROR_FILENAME_EXCED_RANGE по той же причине, просто в этом случае мы знаем о
// превышении заранее (на стадии сборки) и Process.Start не вызываем. Текст для человека
// формирует TurnFailureText.PromptOverflow, кладётся в Text ErrorMessage.
//
// Источник: задача dc641949.
public sealed class PromptOverflowException : Win32Exception
{
    public int TotalCmdlineChars { get; }
    public int Budget { get; }

    public PromptOverflowException(int totalCmdlineChars, int budget)
        : base(206, // ERROR_FILENAME_EXCED_RANGE — нижние 16 бит HRESULT, .NET сам развернёт в 0x800700CE
            $"Промпт хода превысил лимит командной строки даже после срезки нестабильных секций: " +
            $"{totalCmdlineChars} символов при бюджете {budget} (лимит Windows 32 767).")
    {
        TotalCmdlineChars = totalCmdlineChars;
        Budget = budget;
        // NativeErrorCode = 206 ставится базовым конструктором Win32Exception(int, string?):
        // код передаётся в HRESULT-форму, а getter возвращает нижние 16 бит.
    }
}
