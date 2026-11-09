namespace ClaudeHomeServer.Services.Llm;

// Узкий шов под методы `LlmProviderRegistry` (в т.ч. `IsLocal(string?)`), которые
// расход-аналитика (Spend) дёргает у реестра провайдеров. Контракт повторяет сигнатуры дословно, чтобы адаптер в Main был
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

    // Модель родного Claude (подписки) — по ФОРМЕ id, а не по наличию аккаунтов
    // (см. LlmProviderRegistry.IsNativeClaudeModel). Нужна rekey-проходу расхода, чтобы
    // не переключить запись настоящей подписки с моделью claude-* к стороннему провайдеру,
    // перехватившему префикс «claude-» (ghostcli).
    bool IsNativeClaudeModel(string? model);

    // Ключ провайдера модели по каталогу/префиксу (см. LlmProviderRegistry.ResolveByModel).
    // null — ни у одного провайдера такой модели нет: правды о маршруте хода нет, запись
    // трогать нельзя.
    string? ResolveProviderKeyByModel(string? model);
}
