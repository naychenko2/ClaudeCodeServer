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
        // Хвосты ревью R-E4 (минор 1): формулировка «что нужно от тебя» вместо описания симптома
        // или действия самого человека; тексты согласованы продактом, каждая в лимите ask.
        [ExecutorStopClassifier.ToolCallLeakedReason] = "Исполнитель сбился — перезапустите шаг",
        [ExecutorStopClassifier.ToolCallLeakStuckReason] = "Вызовы инструмента уходят текстом раз за разом — нужно решение",
        [ExecutorStopClassifier.InterruptedByUserReason] = "Исполнитель остановлен вами — запустите снова или оставьте",
        [ExecutorStopClassifier.DeviceWaitExpiredReason] = "Устройство не вышло в онлайн за потолок ожидания",
        [ExecutorStopClassifier.StageStallStopReason] = "Исполнитель молчал дольше порога этапа и остановлен — перезапустите шаг",
        [ExecutorStopClassifier.FeatureHeldReason] = "Фича на паузе — снимите паузу, когда будете готовы",
        [ExecutorStopClassifier.FeatureHeldExpiredReason] = "Чат прерванного исполнителя удалён — шаг нужно перезапустить",
    };

    public static string PhraseFor(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var phrase) ? phrase : Fallback;
}
