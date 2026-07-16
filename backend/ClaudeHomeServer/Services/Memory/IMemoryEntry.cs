namespace ClaudeHomeServer.Services.Memory;

// Общий контракт записи долгой памяти для ядра скоринга (MemoryScorerCore). Персона и команда
// хранят разные сущности (PersonaMemoryEntry / TeamMemoryEntry) с общим набором полей, по которым
// считается retention/скоринг: идентификатор, текст, важность (двигается reinforcement'ом), дата
// создания и типизированная категория (TType — свой enum у каждого стека).
public interface IMemoryEntry<out TType>
{
    string Id { get; }
    string Text { get; }
    double Salience { get; set; }
    DateTime CreatedAt { get; }
    TType Type { get; }
}
