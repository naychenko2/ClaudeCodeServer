namespace ClaudeHomeServer.Services.Memory;

// Общий контракт операции консолидации памяти (merge/drop) для ядра MemoryConsolidationCore.
// Персона и команда используют свои concrete-записи (MemoryConsolidationOp / TeamMemoryConsolidationOp)
// с общим набором полей; отличается только тип категории (TType — свой enum у каждого стека).
public interface IMemoryConsolidationOp<TType> where TType : struct
{
    string Op { get; }
    List<string>? Ids { get; }
    string? Id { get; }
    TType? Type { get; }
    string? Text { get; }
    double? Salience { get; }
    bool IsMerge { get; }
    bool IsDrop { get; }
}
