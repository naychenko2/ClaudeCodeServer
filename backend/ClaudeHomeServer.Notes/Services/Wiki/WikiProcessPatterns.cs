using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Шаблоны реплик процесса (ADR-039, решение 1: «фильтр процесса — кодом до модели»).
// Реплика процесса — служебный статус хода (вердикт, отметка о коммите или карточке, путь
// worktree), а не позиция участника. Список константный: пополнение — отдельный шаг
// (за кадром S2-5), здесь ни одного шаблона не выводится из данных.
//
// Мера — строка: IsProcessReply true только когда КАЖДАЯ непустая строка реплики целиком
// (якоря ^…$) совпадает хотя бы с одним шаблоном. Смешанная реплика — позиция участника,
// остаётся как есть: одно служебное слово не превращает содержательный ответ в процесс.
// Понижение (а не отказ) делает WikiChatSlicer.Windows: сжатая строка процесса не может
// стать цитатой — QuoteMatcher проверяет дословность по тексту окна (WikiPlanValidator.cs:199).
public static class WikiProcessPatterns
{
    // Каждая — совпадение со всей строкой. Singleline: точка не ловит перевод строки,
    // многострочная реплика разбирается построчно самим IsProcessReply.
    private static readonly Regex[] Patterns =
    [
        // «Сверка: чисто» — строка-итог сверки (шаблон проверки из каталога работ).
        // Короткая строка: содержательная строка, начинающаяся с «Сверка:», — позиция
        // участника, её шаблон не трогает.
        new(@"^Сверка:\s*[\w\d/:%+–—-]{1,40}\s*$", RegexOptions.CultureInvariant),
        // «Реши, что дальше…» — строка просьбы решения постановщику (TaskExecutionService.
        // BuildDelegatorReactionPrompt); встречается в эхох авто-ходов.
        new(@"^Реши, что дальше.*$", RegexOptions.CultureInvariant),
        // «Голос обновлён» — отметка об обновлении голоса фичи.
        new(@"^Голос обновлён.*$", RegexOptions.CultureInvariant),
        // «Персона-исполнитель … завершила …» — строка-статус завершения исполнителя
        // (тот же оборот, что в BuildDelegatorReactionPrompt).
        new(@"^Персона-исполнитель\b.*заверши.*$", RegexOptions.CultureInvariant),
        // Вердикт витка ревью: «Вердикт: accepted» / «returned» / «accepted_with_remarks»
        // (тот же состав значений, что в verification вердиктов трекера).
        new(@"^Вердикт:\s*(?:returned|accepted(?:_with_remarks)?)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        // Строка-отчёт о коммите: id 7–40 hex (длина git-хэша) с явной меткой
        // («Коммит: 161c49c81», «коммит d341879bf») или с глаголом коммита в строке.
        new(@"^.*\b(?:коммит|commit)\b.*\b[0-9a-f]{7,40}\b.*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        // Id карточки (8 hex) в строке-статусе: «Задача 599005ea заверена»,
        // «Карточка 2ff5b90d принята». Статус задаёт глагол, id — только опознаёт строку.
        new(@"^.*\b(?:задача|карточка)\b.*\b[0-9a-f]{8}\b.*(?:завершена|заверена|закрыта|закрышена|принята|принят|вернута|отменена|выполнена)\b.*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        // Путь worktree проекта: «/…/ClaudeCodeServer-…» — строка-адрес рабочего дерева.
        new(@"^.*[/\\]ClaudeCodeServer[-/].*$", RegexOptions.CultureInvariant),
    ];

    /// <summary>Истинно, если реплика — только процесс: каждая её непустая строка целиком
    /// совпадает хотя бы с одним шаблоном. Пустая или одна содержательная строка — ложь:
    /// смешанная реплика остаётся позицией участника.</summary>
    public static bool IsProcessReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var sawLine = false;
        foreach (var raw in Regex.Split(text, "\r?\n"))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            sawLine = true;
            if (!MatchesAny(raw.Trim())) return false;
        }
        return sawLine;
    }

    private static bool MatchesAny(string line)
    {
        foreach (var p in Patterns)
            if (p.IsMatch(line)) return true;
        return false;
    }
}
