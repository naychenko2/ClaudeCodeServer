using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Шов для записи значка миграцией (Этап 5, волна C, шаг 2): ProjectIconMigration
// в отдельной сборке пишет единственный проектный атрибут `Icon.Glyph`,
// через Core-интерфейс. Прежде ProjectIcons прямо ссылался на полный
// ProjectManager из Main, что делало вынос невозможным — пятая сторона цикла.
//
// Контракт минимальный: одна операция. `bool` — лок под тем же замком, что
// и проверка `Icon.Glyph is not null` (см. `ProjectManager.TrySetIconGlyphMigrated`,
// строка 270), выбор пользователя между отбором и обработкой не перетирает.
//
// Реализация-форвардер `ProjectIconMigratorAdapter` живёт в Main рядом с
// `ProjectManager.cs`.
public interface IProjectIconMigrator
{
    // Возвращает false, если проект не найден или значок уже стоит.
    bool TrySetIconGlyphMigrated(string id, ProjectGlyph glyph);
}
