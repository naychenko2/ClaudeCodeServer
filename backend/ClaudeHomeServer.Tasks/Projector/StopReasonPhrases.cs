namespace ClaudeHomeServer.Services.Tasks.Projector;

// FS-E4: словарь «код остановки → человеческая фраза» для brief.ask (источник stopped).
// Коды — public const string класса ExecutorStopClassifier (ClaudeHomeServer.Core, единственный
// источник значений); инвариант «у каждого кода есть фраза» держит тест-рефлексия по этим
// константам (FeatureBriefTests.Ask_КаждыйКодОстановкиИмеетСвоюФразу) — новый код без строки
// здесь даёт красный тест. Фразы формулируют «что нужно от человека» по смыслу кодов
// (комментарии классификаторов), каждая — в лимите текста ask (TaskManager.AwaitingAskMaxLength).
internal static class StopReasonPhrases
{
    // Код вне словаря (в том числе пометка остановки без кода): общая фраза, не сам код —
    // техническое имя причины наружу человеку не уходит.
    public const string Fallback = "Исполнитель остановился";

    private static readonly IReadOnlyDictionary<string, string> ByCode = new Dictionary<string, string>
    {
        [ExecutorStopClassifier.AuthFailedReason] = "Нужна авторизация — исполнитель не вошёл",
        [ExecutorStopClassifier.SubagentTruncatedReason] = "Сабагент оборвался на середине работы",
        [ExecutorStopClassifier.SubagentStuckReason] = "Сабагент обрывается раз за разом — нужно решение",
        [ExecutorStopClassifier.ToolCallLeakedReason] = "Модель вывела вызов инструмента текстом",
        [ExecutorStopClassifier.ToolCallLeakStuckReason] = "Вызовы инструмента уходят текстом раз за разом — нужно решение",
        [ExecutorStopClassifier.InterruptedByUserReason] = "Исполнителя остановили вручную",
        [ExecutorStopClassifier.DeviceWaitExpiredReason] = "Устройство не вышло в онлайн за потолок ожидания",
        [ExecutorStopClassifier.FeatureHeldReason] = "Фичу поставили на паузу",
        [ExecutorStopClassifier.FeatureHeldExpiredReason] = "Чат прерванного исполнителя удалён — шаг нужно перезапустить",
    };

    public static string PhraseFor(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var phrase) ? phrase : Fallback;
}
