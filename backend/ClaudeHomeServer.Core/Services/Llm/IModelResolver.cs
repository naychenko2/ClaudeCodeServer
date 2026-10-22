namespace ClaudeHomeServer.Services.Llm;

// Узкий шов под методы `ResolveModelOrDefault(string?, string?)` и `IsLocal(string?)`,
// которые расход-аналитика (Spend) дёргает у `LlmProviderRegistry`. Контракт повторяет
// сигнатуры дословно (см. `LlmProviderRegistry.cs:175`), чтобы адаптер в Main был
// однострочным форвардером и резолв не разъехался с подпиской.
//
// Этап 5, шаг 3 (разрыв цикла Spend → Llm): без этого шва Spend держит прямую ссылку
// на `LlmProviderRegistry` (тип живёт в Services/Llm, и Spend без ProjectReference
// на Main не собирается — независимый csproj вертикали невозможен). Введение
// Core-интерфейса отвязывает Spend от Llm.
public interface IModelResolver
{
    string ResolveModelOrDefault(string? model, string? providerKey);

    // Локальный ли провайдер по конфиг (IsLocal) — для классификации расхода как бесплатного
    bool IsLocal(string? providerKey);
}
