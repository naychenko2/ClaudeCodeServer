namespace ClaudeHomeServer.Services.Llm;

// Валидатор значения маршрута локального действия для формы «конкретная модель»
// (ADR-009 §1, форма 8). Дальше синтаксических форм (local/claude/default, tier:*,
// preset:*) разбирает сам LocalActionOverridesStore/Router; сюда контроллер попадает,
// только когда значение не опознано как служебный литерал — то есть это id модели.
//
// Контракт (утверждённые тексты — docs/features/model-route-format-validation.md):
//   • модель есть в каталоге под этим именем           → null (годится);
//   • модель есть только как «direct:»                 → noProvider (дефект с прода:
//     модель прямого вызова записана без префикса транспорта);
//   • модели нет вовсе (ни голой, ни direct:)          → unknownModel (опечатка).
//
// Pure-функция по списку Value каталога: вынесена из контроллера ради прямого unit-теста
// (через HTTP кейс noProvider не смоделировать — каталог тестового хоста не содержит
// direct-моделей: все провайдеры с пустым ApiKey). Соответствует рекомендации ADR-009 §6
// о едином валидаторе маршрута на записи; дорогая проверка каталога остаётся на вызове.
public static class LocalActionRouteValidator
{
    // null — модель годится как значение маршрута.
    public static string? ClassifyModelRoute(string model, IEnumerable<string> catalogValues)
    {
        var set = new HashSet<string>(catalogValues, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return UnknownModel(model);

        // Обычная модель настроенного провайдера — есть в каталоге под этим именем.
        if (set.Contains(model)) return null;

        // Модель прямого вызова записана без поставщика: голому id в каталоге соответствует
        // только «direct:<id>». Это и есть дефект с прода (MiniMax-M3 без префикса).
        if (set.Contains(CloudCheapClient.RoutePrefix + model))
            return NoProvider(model);

        return UnknownModel(model);
    }

    // Тексты — дословно из docs/features/model-route-format-validation.md (ключи
    // route.noProvider.api / route.unknownModel). {model} подставляется как есть,
    // в кавычках-ёлочках — чтобы человек узнал в сообщении то, что у него записано.
    private static string NoProvider(string model) =>
        $"Модель «{model}» указана без поставщика — по одному названию непонятно, " +
        $"через кого её вызывать. Выберите её в списке раздела «Модели и расход».";

    private static string UnknownModel(string model) =>
        $"Модель «{model}» не найдена среди доступных. " +
        $"Выберите её в списке раздела «Модели и расход».";
}
