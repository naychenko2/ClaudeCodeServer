using System.Globalization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Разовая чистка данных темы чата под свободный выбор lucide-иконки.
/// Прежняя реализация хранила в <see cref="Session.Topic"/> ключ темы из каталога
/// (bug/code/design…) и клеила эмодзи прямо в <see cref="Session.Name"/>. Теперь Topic хранит
/// ИМЯ компонента lucide-react (PascalCase: Cat, Bug, User), а имя должно быть чистым текстом.
/// </summary>
/// <remarks>
/// Идемпотентна: повторный прогон — no-op. Делает два дела: снимает ведущий эмодзи с имени
/// (маппинга эмодзи→lucide-имя нет, поэтому значок не восстанавливаем — проставится заново
/// batch-прогоном) и обнуляет устаревшие ключевые Topic (они с маленькой буквы, не PascalCase).
/// </remarks>
public static class ChatTopicMigration
{
    /// <summary>
    /// Правит переданные сессии НА МЕСТЕ (вызывающий сохраняет их сам).
    /// Возвращает true, если хоть одна сессия изменилась — тогда нужен SaveSessions().
    /// </summary>
    public static bool Apply(IEnumerable<Session> sessions)
    {
        var changed = false;
        foreach (var session in sessions)
        {
            // Имя задано человеком — не трогаем вовсе: эмодзи там мог быть осознанным выбором
            if (!session.NameLocked && TitleExtraction.HasEmoji(session.Name))
            {
                var clean = StripLeadingEmoji(session.Name);
                // Пустой остаток — имя не трогаем, иначе чат станет безымянным
                if (!string.IsNullOrEmpty(clean) && clean != session.Name)
                {
                    session.Name = clean;
                    changed = true;
                }
            }

            // Устаревший ключ темы (с маленькой буквы — bug/code/docs/chat) не является
            // PascalCase-именем lucide-компонента: фронт icons["bug"] ничего не найдёт.
            // Обнуляем — значок проставится заново batch-прогоном уже lucide-именем
            if (!string.IsNullOrEmpty(session.Topic) && !IsPascalCase(session.Topic))
            {
                session.Topic = null;
                changed = true;
            }
        }
        return changed;
    }

    // Имя без ведущего эмодзи. Первая графема (ZWJ-последовательность — один элемент)
    // срезается целиком, остаток обрезается от пробелов. null — после значка ничего не осталось.
    private static string? StripLeadingEmoji(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Trim();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        if (!enumerator.MoveNext()) return null;
        var rest = text[((string)enumerator.Current).Length..].TrimStart();
        return rest.Length == 0 ? null : rest;
    }

    // PascalCase-имя компонента lucide: первая заглавная, дальше буквы/цифры. Старые ключи
    // тем (bug, code) — с маленькой буквы — отсекаются, новые lucide-имёна (Cat, Bug) — проходят.
    private static bool IsPascalCase(string value)
        => value.Length >= 2 && char.IsUpper(value[0]) && value.All(char.IsLetterOrDigit);
}
