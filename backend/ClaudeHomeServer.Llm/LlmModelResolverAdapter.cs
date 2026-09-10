namespace ClaudeHomeServer.Services.Llm;

// Адаптер Core-интерфейса `IModelResolver` на живой `LlmProviderRegistry` (этап 5, шаг 3,
// разрыв цикла Spend → Llm). Регистрация — рядом с LlmProviderRegistry в LlmSubsystem.cs,
// DI подхватывает `LlmProviderRegistry` автоматически по конструктору.
//
// Вся логика — форвард одного метода. Менять контракт здесь нельзя: Spend рассчитывает
// на стабильный семантический эквивалент `ResolveModelOrDefault` (документация метода
// лежит в LlmProviderRegistry.cs).
public sealed class LlmModelResolverAdapter(LlmProviderRegistry inner) : IModelResolver
{
    public string ResolveModelOrDefault(string? model, string? providerKey)
        => inner.ResolveModelOrDefault(model, providerKey);
}
