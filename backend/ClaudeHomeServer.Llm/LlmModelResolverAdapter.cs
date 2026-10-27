namespace ClaudeHomeServer.Services.Llm;

// Адаптер Core-интерфейса `IModelResolver` на живой `LlmProviderRegistry` (этап 5, шаг 3,
// разрыв цикла Spend → Llm). Регистрация — рядом с LlmProviderRegistry в LlmSubsystem.cs,
// DI подхватывает `LlmProviderRegistry` автоматически по конструктору.
//
// Вся логика — форварды к реестру без своей семантики: Spend рассчитывает на точный
// эквивалент методов реестра (документация каждого — в LlmProviderRegistry.cs).
public sealed class LlmModelResolverAdapter(LlmProviderRegistry inner) : IModelResolver
{
    public string ResolveModelOrDefault(string? model, string? providerKey)
        => inner.ResolveModelOrDefault(model, providerKey);

    public bool IsLocal(string? providerKey)
        => inner.GetByKey(providerKey)?.IsLocal == true;

    public bool IsNativeClaudeModel(string? model)
        => LlmProviderRegistry.IsNativeClaudeModel(model);

    // null — модель не резолвится ни к одному провайдеру (у реестра «null → Claude» —
    // дефолт маршрута хода; для rekey нужна честная «правды нет», поэтому без ?? «claude»)
    public string? ResolveProviderKeyByModel(string? model)
        => inner.ResolveByModel(model)?.Key;
}
