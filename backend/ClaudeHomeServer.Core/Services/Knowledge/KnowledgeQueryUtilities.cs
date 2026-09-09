namespace ClaudeHomeServer.Services.Knowledge;

// Жёсткий потолок длины поискового запроса в Dify v1: query длиннее 250 символов
// отбивается 400 "String should have at most 250 characters". Обрезаем сами —
// иначе recall заметок и памяти персон падает на любом длинном ходе.
//
// Этап 5, шаг 6: константы и нормализация переехали в Core (из `KnowledgeService.TrimQuery`)
// — контрибьютор NotesRecallContributor в вертикали Notes вызывает их на каждый ход,
// и тянуть вертикаль Knowledge ради одной статической утилиты неправильно.
// Дубликата в `KnowledgeService` больше нет — KnowledgeService.cs ссылается сюда.
public static class KnowledgeQueryUtilities
{
    public const int MaxQueryLength = 250;

    // Нормализация запроса перед отправкой в Dify: trim + обрезка до потолка.
    public static string TrimQuery(string? query)
    {
        var q = query?.Trim() ?? "";
        return q.Length > MaxQueryLength ? q[..MaxQueryLength] : q;
    }
}