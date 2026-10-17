namespace ClaudeHomeServer.Services.Llm;

// Шов: ClaudeSession сообщает о детектированной утечке вызова инструмента в текст, счётчик
// хода локальной модели (LocalModelTurnMeter) копит её в итог. Kind = "tail" | "whole_call".
// Реализация — LocalModelTurnMeter (singleton Llm); без него (тесты/не-локальная сессия)
// ClaudeSession просто не получает шов и счётчик не копит.
public interface IToolCallLeakSink
{
    void NoteLeak(string sessionId, string kind);
}
