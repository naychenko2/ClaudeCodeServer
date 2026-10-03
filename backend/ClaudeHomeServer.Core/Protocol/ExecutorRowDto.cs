namespace ClaudeHomeServer.Protocol;

// Строка исполнителя в ответе quote (ADR-023 §Д2.1, решение Р2): «Чем» строки контекста и панели читается
// отсюда, фронт строки сам не собирает и подпись Price не разбирает — цена лежит полями.
// Group — auto | local | cloud; Sub у «Авто» несёт «сейчас: …»; Reason — почему серая (встаёт вместо Sub).
// Free — бесплатно (локальная модель), тогда Amount = null; Amount и Unit — цена числом и единицей
// (Unit — валюта цены: free | usd | credits | rub; «за что» — штука, секунда, 1000 симв. — только в подписи Price; единица тарификации поставщика chars|sec|min в Unit не попадает, её исполнитель переводит в usd); EtaSeconds — время запуска у локальных.
// Price — готовая подпись для показа; Badges — «RU», лицензия, «тяжёлая» и т. п.
public sealed record ExecutorRowDto(
    string Id,
    string Group,
    string Name,
    string? Sub,
    string Price,
    bool Free = false,
    double? Amount = null,
    string? Unit = null,
    int? EtaSeconds = null,
    IReadOnlyList<ExecutorBadgeDto>? Badges = null,
    bool Disabled = false,
    bool Locked = false,
    string? Reason = null);

// Бейдж строки исполнителя; Tone — neutral | good | warn | info
public sealed record ExecutorBadgeDto(string Label, string Tone = "neutral");
