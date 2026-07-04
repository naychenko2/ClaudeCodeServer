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

/// <summary>Продуктовая сводка изменений за один день (по всем проектам).</summary>
/// <param name="Date">День в формате yyyy-MM-dd (локальная дата коммитов).</param>
/// <param name="Items">Пункты сводки (сгенерированы Claude, закешированы).</param>
public record ChangelogDay(
    string Date,
    List<ChangelogItem> Items);

/// <summary>
/// Заглушка дня для мгновенного списка (без LLM): дата, сколько коммитов,
/// есть ли уже готовая сводка в кеше.
/// </summary>
public record DaySummaryStub(
    string Date,
    int CommitCount,
    bool Cached);
