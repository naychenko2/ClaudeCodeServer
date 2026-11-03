namespace ClaudeHomeServer.Services.Notes.Wiki;

// Дом источника определяет раздел назначения (ADR-035, решение 9 «Дом источника определяет
// раздел»; ADR-036, решение 1 таблица, решение 3): раздел выбирает код, а не модель и не агент.
// Чистая функция без I/O: отказ — результат с причиной, не исключение (его формулирует вызывающий).
// Fail-closed: неизвестный/пустой/null дом, невалидный projectId — отказ, а не молчание и не
// «common»: молчаливый «common» открыл бы текст проекта всем чатам (тот риск, ради которого
// правило дома и заведено).
public static class WikiSourceHome
{
    /// <summary>Решение: SectionKey — ключ раздела назначения; null — отказ, и тогда Reason —
    /// причина для человека и для журнала.</summary>
    public readonly record struct Decision(string? SectionKey, string? Reason);

    /// <summary>home — ключ дома источника: "personal" (личный vault и любой источник вне
    /// проекта — их дом по умолчанию, ADR-036, решение 1) или "projects/{id}" (заметки notes/
    /// проекта и файлы проекта). null/пустой/неизвестный — отказ.
    /// target — null или ровно "common": явный выбор человека (ADR-036, решение 3). "common"
    /// разрешён только источнику вне проекта: у проектного источника target нет, в «Общее»
    /// он идёт повышением (решение 5).
    /// Функция возвращает раздел и только его: закрытость операций с моделью для «Личного»
    /// до Ф2 и запрет записи в раздел сферы — вызывающего (инgest, WikiSections.IsWritable),
    /// здесь отказа нет.</summary>
    public static Decision Decide(string? home, string? target = null)
    {
        if (target is not null && target != WikiSections.Common)
            return Reject($"target '{target}': единственный явный target — 'common'");
        if (string.IsNullOrWhiteSpace(home))
            return Reject("дом источника неизвестен");
        if (home == WikiSections.Common)
            return Reject("'common' — не дом источника: в «Общее» попадают target-ом или повышением");
        if (home == WikiSections.Personal)
            // Источник вне проекта: по умолчанию «Личное», с явным выбором человека — «Общее»
            // (такой пакет всегда идёт во «Входящие» — решает человек, не эта функция).
            return new(target ?? WikiSections.Personal, null);
        // Проектный дом: id проверяется тем же IsValidId, что и ключ раздела («projects/»,
        // «projects/a..b», «projects/a/b», «projects/a b» — не дом), через публичный разбор
        // WikiSections — второй таблицы допустимых id здесь не заводим.
        if (WikiSections.TryProjectId(home, out _))
            return target == WikiSections.Common
                ? Reject("проектный источник не идёт в 'common': в «Общее» только повышением")
                : new(home, null);
        // "spheres/{id}" и прочее: в Ф0–Ф1 сферы — не дом источников (раздел сферы — следующая
        // фича); новый дом добавляется вместе с тем, что его приносит.
        return Reject($"'{home}' — не дом источника");
    }

    private static Decision Reject(string reason) => new(null, reason);
}
