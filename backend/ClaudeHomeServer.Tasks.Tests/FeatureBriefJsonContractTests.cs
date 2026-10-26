using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Волна 6, шаг B10 (минор ревью R-B78, находка 2): JSON-имена полей контракта brief закреплены
// бэк-тестом.
//
// Пробел: фронтовый литерал (featurePage.ts) сверяется с ФРОНТОВЫМ TS-типом, тесты FeatureBriefTests
// смотрят C#-свойства — JSON-ключи не смотрит никто. Переименование JSON-имени на бэке
// (JsonPropertyName или самое свойство) → фронт читает undefined, подпись «в очереди N мин» молча
// исчезает, tsc молчит (поле опциональное). Этот тест СЕРИАЛИЗУЕТ Brief теми же опциями, что веб-
// ответ, и проверяет точный набор JSON-ключей контракта на всех уровнях: Brief и вложенные записи.
//
// Опции: база веб-ответа — MVC (Program.cs:169 AddJsonOptions: Web-дефолты + конвертеры string-enum
// и FeatureNote) и TasksController.FeatureJsonOptions (TasksController.cs:531) — та же база
// JsonSerializerDefaults.Web, она и задаёт имена ключей. Конвертеры на имена ключей Brief не влияют:
// enums в Brief нет (kind/planState — строки), BriefNote — не FeatureNote (конвертер для хранимого типа).
//
// Мутация (проверена прогоном, см. отчёт задачи): [property: JsonPropertyName("queueWaitingMinutes")]
// у Brief.QueueWaitMinutes — тест красный (отсутствует "queueWaitMinutes", лишний "queueWaitingMinutes");
// возврат — зелёный. Тот же исход при переименовании самого свойства: camelCase дал бы
// "queueWaiting" — тоже красный.
public class FeatureBriefJsonContractTests
{
    private static readonly DateTime NOW = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    // Та же база опций, что у веб-ответа (см. шапку): naming policy camelCase решает имена ключей.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // Полный Brief строится напрямую (не через Build): все поля заполнены — чтобы в JSON оказались
    // ключИ ВСЕХ уровней контракта, включая queue (заглушка Build всегда null — её форму закрывает
    // только заполненная фикстура).
    private static FeatureBrief.Brief FullBrief() => new(
        FeatureBrief.Kinds.Queued,
        new FeatureBrief.BriefNow("review", "t1", "gleb", "B10", "back", NOW.AddMinutes(-30)),
        "queue",
        new FeatureBrief.BriefDone(2, 6, 1, "cut"),
        [new FeatureBrief.BriefNext("qa", "vera")],
        17,
        new FeatureBrief.BriefEstimate(100, 76, 124, "history", 5),
        new FeatureBrief.BriefQueue(2, 5),
        25,
        new FeatureBrief.BriefNote("ревью началось", NOW.AddHours(-2), "gleb", true, "review", false),
        NOW.AddHours(-3),
        NOW);

    // Точный набор ключей JSON-объекта в данной точке контракта: отсутствие ожидаемого имени или
    // лишнее — красный (контракт меняется только вместе с этим тестом и фронтовым зеркалом).
    private static void AssertKeys(JsonNode? node, string path, params string[] expected)
    {
        node.Should().BeAssignableTo<JsonObject>($"узел '{path}' сериализован объектом");
        node!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(expected,
            $"JSON-ключи '{path}' — ровно контракт фронтового типа (featurePage.ts)");
    }

    // Перечень — record Brief (FeatureBrief.cs:72-92) ↔ FeatureBrief (featurePage.ts:103-124) и
    // вложенные записи BriefNow/BriefDone/BriefNext/BriefEstimate/BriefQueue/BriefNote
    // (FeatureBrief.cs:51-69) ↔ FeatureBriefNow/Done/Next/Estimate/Queue/Note (featurePage.ts:50-99).
    [Fact]
    public void Brief_JsonКлючиРовноКонтрактаФронта_НаВсехУровнях()
    {
        var root = JsonSerializer.SerializeToNode(FullBrief(), WebJson);

        AssertKeys(root, "brief",
            "kind", "now", "reason", "done", "next", "elapsedMinutes",
            "estimate", "queue", "queueWaitMinutes", "note", "updatedAt", "computedAt");

        AssertKeys(root!["now"], "brief.now", "stage", "taskId", "personaId", "stepRef", "lane", "since");
        AssertKeys(root["done"], "brief.done", "stepsAccepted", "stepsTotal", "loops", "planState");
        AssertKeys(root["next"]![0], "brief.next[]", "stage", "personaId");
        AssertKeys(root["estimate"], "brief.estimate", "remainingMinutes", "p25", "p75", "source", "sampleSize");
        AssertKeys(root["queue"], "brief.queue", "position", "total");
        AssertKeys(root["note"], "brief.note", "text", "at", "byPersonaId", "byUser", "stageAtWrite", "stale");
    }
}
