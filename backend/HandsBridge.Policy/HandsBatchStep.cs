namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Один шаг пакета <c>hands_batch</c> (D1c): инструмент, его аргументы и пауза в миллисекундах,
/// которая выдерживается ПОСЛЕ этого шага, перед следующим. <c>args</c> гейт не разбирает — его
/// проверяет гейт самого инструмента при исполнении шага.
/// </summary>
public sealed record HandsBatchStep(string Tool, int? DelayMs, object? Args);
