using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

// Источники расхода токенов (спека Spend Analytics v2): ходы чатов/задач, фоновые one-shot,
// генерации fal.ai (токенов нет — счётчик), генерации Higgsfield (кредиты, не токены — счётчик),
// бесплатные модели (токены есть, стоимость 0), синтез речи Yandex SpeechKit (токенов нет,
// счётчик запросов и рубли), веб-поиск через Perplexity Sonar (токены есть — источник обычный).
public static class SpendSources
{
    public const string ChatTurn = "chat-turn";
    public const string OneShot = "one-shot";
    public const string Fal = "fal";
    public const string Glif = "glif";
    public const string Free = "free";
    public const string Tts = "tts";
    public const string WebSearch = "websearch";
    public const string Higgsfield = "higgsfield";

    // Источники без токенов: у них расход меряется счётчиком вызовов, а не токенами, поэтому
    // в рейтингах «по токенам» им делать нечего (иначе они вечно висят внизу с нулём).
    public static bool IsTokenless(string source) => source is Fal or Glif or Tts or Higgsfield;

    // Провайдеры «freellm*» (в т.ч. «freellmapi» и «freellmapi-direct» — локальный прокси на
    // свою GPU) бесплатны. Правило в одном месте Core: его зовут и IsFree (источник расхода),
    // и SpendCostTiers (ценовой класс), иначе источник и класс разъедутся.
    public static bool IsFreeProviderKey(string provider) =>
        provider.StartsWith("freellm", StringComparison.Ordinal);

    // Бесплатный исполнитель: локальный провайдер (флаг IsLocal в конфиге — свой движок
    // на своей машине), локальная модель (Ollama или llama-server), провайдер «freellm*»
    // (с «-direct» и без) или модель ":free" через CLI. Прямой адаптер платного провайдера
    // («minimax-direct», «kimi-direct») бесплатным НЕ считается. Ключи локальных провайдеров
    // машин-specific (напр. "local-qwen"), поэтому признак берётся из конфига, а не из
    // перечисления здесь.
    public static bool IsFree(string provider, string? model, bool isLocal = false) =>
        isLocal
        || provider is "ollama" or "llama-server"
        || IsFreeProviderKey(provider)
        || (model is not null && model.EndsWith(":free", StringComparison.OrdinalIgnoreCase));

    // Дополнительные подписки Claude (sub-*) — тот же провайдер claude, отдельной осью не нужны
    public static string NormalizeProvider(string provider) =>
        provider.StartsWith("sub-", StringComparison.Ordinal) ? "claude" : provider;
}

// Значения SpendRecord.Initiator
public static class SpendInitiators
{
    public const string Human = "human";
    public const string Agent = "agent";
}

// Одна запись расхода: ход чата, фоновый one-shot, генерация fal.ai или вызов бесплатной
// модели. Детальные записи живут в data/spend/turns-*.jsonl последние Spend:DetailDays дней,
// старше — сворачиваются в дневные агрегаты DailySpendRow (SpendStore.RollupOlderThan).
public sealed class SpendRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    // Владелец траты; "" — системный вызов (changelog, каталог моделей и т.п.)
    public string OwnerId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? PersonaId { get; init; }
    // Кто подал ход (актор хода общего проекта, S2a-6b): null — запись до этого поля,
    // серверный ход (one-shot, директива) или актор не известен. Ключ хранилища остаётся
    // OwnerId (владелец-арендатор): ActorUserId — разрез, не ключ. В срезе чтения
    // «нет поля → владелец» (тот же порядок, что у SenderUserId в К4): старые записи
    // относятся владельцу. Аддитивное nullable-поле: старые turns-*.jsonl и daily.json
    // читаются штатно, новый формат читается старым кодом — BackupSchema.Version не двигается.
    public string? ActorUserId { get; init; }
    // set (не init): разовый rekey-проход в SpendStore меняет провайдера записей,
    // записанных с Claude-ключом и не-Claude моделью (см. RekeyNonClaudeModelsOnClaudeKeys)
    public string Provider { get; set; } = "claude";
    public string? Model { get; init; }
    // set (не init): проход переклассификации в SpendStore меняет источник записей,
    // записанных до учёта признака IsLocal (см. ReclassifyLocalAsFree)
    public string Source { get; set; } = SpendSources.ChatTurn;
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    // Стоимость собирается про запас (метрика фичи — токены, UI деньги не показывает)
    public double? CostUsd { get; init; }
    // Стоимость в РУБЛЯХ — расход на сервисы Яндекса (озвучка SpeechKit тарифицируется в них).
    // Отдельное поле, а не пересчёт в доллары: курс пришлось бы выдумывать, и врал бы он тем
    // сильнее, чем старше запись. Валюты не складываются нигде — ни здесь, ни в агрегатах.
    public double? CostRub { get; init; }
    // Стоимость в КРЕДИТАХ поставщика (Higgsfield в редакторе картинок, ADR-017 §4) — третья
    // валюта по образцу CostRub: с долларами и рублями не складывается нигде
    public double? CostCredits { get; init; }
    // Счётчик вызовов без токенов: генерации fal.ai/glif и запросы синтеза речи (SpeechKit
    // тарифицируется ЗА ЗАПРОС, так что это ровно единицы тарификации); у остальных 0
    public int Generations { get; init; }
    public long DurationMs { get; init; }
    // Время запросов к API за этот ход — приращение накопительного счётчика CLI между итогами
    // одного процесса (ClaudeSession.TurnApiDelta), без работы инструментов. Это знаменатель
    // сессионной скорости ответа: токены хода ÷ время ответов модели. DurationMs для этого не
    // годится — там полное время хода, включая работу инструментов.
    // null — поле не пришло (сторонние потоки его не шлют), приращение нулевое или запись до
    // этого поля: такой ход в скорость не идёт, знаменатель не выдумывается.
    // Прежнее имя DurationApiMs (20.11, до фикса приращения) из модели снято намеренно: строки
    // с тем именем несут накопительный счётчик процесса, а не время хода, — в знаменатель они
    // не читаются (прочитать их старым именем = делить токены хода на время всей сессии).
    // Аддитивное nullable-поле: старые turns-*.jsonl читаются штатно, BackupSchema.Version не
    // двигается. В дневные агрегаты не сворачивается.
    public long? TurnApiMs { get; init; }
    // Подпись операции: ключ фонового действия (changelog, notes.tags…) или endpoint fal
    public string? Label { get; init; }
    // Кто запустил платную генерацию: SpendInitiators.*. Носители значения: ходы чата —
    // главный после R-S-SPEND-ИНИЦ: ядро выводит признак из защёлки SessionEntry.TurnFromHuman
    // (human — ход поднял человек; agent — автономный ход CLI или подача агента); медиальные
    // операции (редактор картинок, ADR-018 §2; генерация и догонящие заявки картинок, видео-
    // и аудиоредактор) и генерация аватаров — по тому, кто подал операцию: REST из UI — human,
    // MCP-инструменты агента — agent; агентские генерации хода через внешний MCP
    // (fal/glif/higgsfield, S-SPEND-8) — всегда agent. Source (Free и пр.) о наличии инициатора
    // не говорит: ход чата на локальной модели (SpendMapping, инициатор из защёлки) и сборка
    // видео (FilmAssembler, human/agent) пишут Source = Free с названным инициатором. None
    // (значения нет) пишут именно эти места: фоновые one-shot — OneShotClaudeRunner:424 и
    // CloudCheapClient:323 (Source OneShot или Free) и прямые вызовы локальных движков —
    // OllamaClient:140 и LlamaServerClient:601 (Source Free): инициатор там смешанный —
    // автоматика, кнопка человека или инструмент агента; None = не назван и в разрезе
    // человек/агент не сворачивается (решение aaf97b9c). Прочие Free-записи в разрезе остаются.
    // null — не размечено либо запись до этого поля. В дневные агрегаты не сворачивается.
    public string? Initiator { get; init; }

    [JsonIgnore]
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

    [JsonIgnore]
    public DateOnly Date => DateOnly.FromDateTime(Timestamp);
}

// Дневной агрегат по полному составному ключу разрезов — из него считается любой pivot
// за пределами детального окна (кроме уровня «ход»).
public sealed class DailySpendRow
{
    public string Date { get; init; } = "";
    public string OwnerId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? PersonaId { get; init; }
    // Тот же разрез, что у детальной записи (S2a-6b): без него свёрнутый день не даёт
    // владельцу разбивку по участникам, а участнику — его записей за окном детального окна.
    // Агрегаты до появления поля читаются с null и относятся владельцу (см. SpendRecord).
    public string? ActorUserId { get; init; }
    // set (не init): rekey-проход в SpendStore меняет провайдера агрегата
    // (см. RekeyNonClaudeModelsOnClaudeKeys)
    public string Provider { get; set; } = "";
    public string? Model { get; init; }
    // set (не init): проход переклассификации в SpendStore меняет источник агрегата
    public string Source { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public double CostUsd { get; set; }
    public double CostRub { get; set; }
    public double CostCredits { get; set; }
    public int Generations { get; set; }
    // Количество свёрнутых записей (ходов/вызовов)
    public int Turns { get; set; }
}
