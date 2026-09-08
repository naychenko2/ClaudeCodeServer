namespace ClaudeHomeServer.Models;

/// <summary>
/// Сырой коммит из git log + проект, к которому он относится — сырье для
/// продуктовой сводки (не отдается на фронт напрямую).
/// </summary>
/// <param name="Sha">Полный sha коммита.</param>
/// <param name="Author">Отображаемое имя автора (после применения алиасов Changelog:AuthorAliases).</param>
/// <param name="Email">Email автора из git (%ae) — по нему матчатся алиасы.</param>
/// <param name="Date">Дата автора в ISO-8601 (%aI), с таймзоной.</param>
/// <param name="Subject">Первая строка сообщения коммита.</param>
/// <param name="Body">Тело сообщения коммита (может быть пустым).</param>
/// <param name="Project">Имя проекта, в котором сделан коммит.</param>
public record GitCommitRaw(
    string Sha,
    string Author,
    string Email,
    DateTimeOffset Date,
    string Subject,
    string Body,
    string Project = "");

/// <summary>
/// Пункт продуктовой сводки за день: что нового и чем это полезно пользователю.
/// Пишет Claude по коммитам (без технических деталей и кода).
/// </summary>
/// <param name="Type">Категория: "feature" | "improvement" | "fix" | "other".</param>
/// <param name="Area">Область/раздел продукта, к которому относится изменение (напр. «Артефакты сессии», «Календарь»). Для группировки внутри дня.</param>
/// <param name="Emoji">Тематическая эмодзи под смысл пункта (чтобы «на глаз» понять, о чем речь).</param>
/// <param name="Title">Что появилось/изменилось — человеческим языком.</param>
/// <param name="Benefit">Чем это полезно пользователю / что улучшилось.</param>
/// <param name="Score">Оценка значимости изменения 1-5 (5 — хит, 1-2 — по мелочи).</param>
/// <param name="ScoreReason">Краткое обоснование оценки — почему круто / почему по мелочи.</param>
/// <param name="Authors">Отображаемые имена авторов пункта.</param>
/// <param name="Projects">Проекты, которых касается пункт.</param>
public record ChangelogItem(
    string Type,
    string Area,
    string Emoji,
    string Title,
    string Benefit,
    int Score,
    string ScoreReason,
    List<string> Authors,
    List<string> Projects);

/// <summary>
/// Во сколько обошлась сборка сводки дня: время ожидания, токены и стоимость.
/// Пишется при генерации, живёт в кеше рядом со сводкой и показывается внизу дня.
/// </summary>
/// <param name="DurationMs">Полное время вызова claude, включая старт CLI (столько ждал бы пользователь).</param>
/// <param name="InputTokens">Входные токены без кеша.</param>
/// <param name="CacheCreationTokens">Вход, записанный в кеш промптов (тарифицируется как обычный вход).</param>
/// <param name="CacheReadTokens">Вход, прочитанный из кеша (дешевле обычного).</param>
/// <param name="OutputTokens">Сгенерированные токены.</param>
/// <param name="CostUsd">Стоимость в долларах: по ценам провайдера, а для подписки — оценка по тарифам API (null, если цен нет).</param>
/// <param name="Model">Модель, которой реально считался вызов.</param>
/// <param name="GeneratedAt">Когда сводка была собрана.</param>
public record ChangelogGeneration(
    long DurationMs,
    long InputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long OutputTokens,
    double? CostUsd,
    string? Model,
    DateTimeOffset GeneratedAt);

/// <summary>Продуктовая сводка изменений за один день (по всем проектам).</summary>
/// <param name="Date">День в формате yyyy-MM-dd (локальная дата коммитов).</param>
/// <param name="Items">Пункты сводки (сгенерированы Claude, закешированы).</param>
/// <param name="Degraded">Сводку собрать не удалось — пункты сырые (subject'ы коммитов), а не осмысленная сводка.</param>
/// <param name="DegradedReason">Человеческое объяснение, что сломалось и как чинить (null, если всё хорошо).</param>
/// <param name="Generation">Расход на сборку этой сводки (null у старых записей кеша и когда метрик не дали).</param>
public record ChangelogDay(
    string Date,
    List<ChangelogItem> Items,
    bool Degraded = false,
    string? DegradedReason = null,
    ChangelogGeneration? Generation = null);

/// <summary>
/// Заглушка дня для мгновенного списка (без LLM): дата, сколько коммитов,
/// есть ли уже готовая сводка в кеше.
/// </summary>
public record DaySummaryStub(
    string Date,
    int CommitCount,
    bool Cached);

/// <summary>
/// Кандидат на фоновый прогрев сводки (для ChangelogWarmupService, на фронт не отдается).
/// </summary>
/// <param name="Date">День в формате yyyy-MM-dd (локальная дата коммитов).</param>
/// <param name="Cached">В кеше есть актуальная сводка (хеш sha-набора совпадает) — греть не надо.</param>
/// <param name="LastCommitAt">Время последнего коммита дня — для «остыва» (день с сыплющимися коммитами не греем).</param>
public record WarmupCandidate(
    string Date,
    bool Cached,
    DateTimeOffset LastCommitAt);

/// <summary>
/// Статус настройки источника changelog — чтобы фронт при пустом разделе отличал
/// «не настроено, донастрой инстанс» от «настроено, но изменений пока нет».
/// </summary>
/// <param name="Configured">Источник задан и пригоден (репа с .git либо есть хоть один проект).</param>
/// <param name="Mode">Режим источника: "repo" (фиксированная репа продукта) | "projects" (агрегация проектов).</param>
/// <param name="Detail">Человеческая подсказка, что не так / что донастроить (null, если всё ок).</param>
public record ChangelogStatus(
    bool Configured,
    string Mode,
    string? Detail);
