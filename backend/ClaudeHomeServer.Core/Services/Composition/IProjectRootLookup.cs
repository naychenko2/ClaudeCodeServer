namespace ClaudeHomeServer.Services.Composition;

// Шов «найти проект по корневой папке». Узкий контракт: вернуть только то, что нужно
// вертикали (например, `CodeGraph` — лишь `RootPath` для повторной нормализации),
// а не полный `Project` со всем графом зависимостей модели (PermissionRule,
// BoardColumn, ProjectIcon и пр.) — этот граф живёт в Main и в Core не переезжает,
// чтобы не тащить модели вертикалей в спинку.
//
// Контракт повторяет форму `ProjectManager.GetByRootPath` (возвращает список), но
// отдаёт DTO `ProjectRootLocation` вместо `Project`:
//   - список — потому что одна корневая папка может быть зарегистрирована за
//     несколькими проектами разных владельцев (`EnsureRootFree` это явно запрещает
//     только в пределах одного владельца; см. CLAUDE.md «Соглашения»);
//   - DTO — потому что CodeGraph использует только `RootPath`, и расширение контракта
//     до полного `Project` создало бы второй соблазн утянуть и логику из Main.
//
// Реализация — `ProjectRootLookup` в Main, тонкая обёртка над `ProjectManager`.
public interface IProjectRootLookup
{
    IReadOnlyList<ProjectRootLocation> GetByRootPath(string path);
}

/// <summary>Минимальный DTO: путь, под которым проект зарегистрирован.</summary>
public sealed record ProjectRootLocation(string RootPath);