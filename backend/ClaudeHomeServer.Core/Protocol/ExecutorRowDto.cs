namespace ClaudeHomeServer.Protocol;

// Строка исполнителя в ответе quote (ADR-023 §Д2.1, решение Р2): «Чем» строки контекста и панели читается
// отсюда, фронт строки сам не собирает. Форма зеркалит ExecutorRow фронта (ExecutorList.tsx).
// Group — auto | local | cloud; Sub у «Авто» несёт «сейчас: …»; Reason — почему серая (встаёт вместо Sub).
public sealed record ExecutorRowDto(
    string Id,
    string Group,
    string Name,
    string? Sub,
    string Price,
    bool Disabled = false,
    bool Locked = false,
    string? Reason = null);
