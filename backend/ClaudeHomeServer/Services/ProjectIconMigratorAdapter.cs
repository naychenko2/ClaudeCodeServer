using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Адаптер ProjectManager → IProjectIconMigrator (Core).
// Шов для вертикали ProjectIcons (Этап 5, волна C, шаг 2): миграция значков
// в отдельной сборке пишет Icon.Glyph через Core-интерфейс; полный ProjectManager
// держит этот метод, и концентрировать всё ради одной миграции в Core нерационально.
//
// Регистрация: один синглтон в Program.cs рядом с `AddSingleton<ProjectManager>`.
// ProjectManager — singleton, поэтому адаптер тоже singleton.
public sealed class ProjectIconMigratorAdapter : IProjectIconMigrator
{
    private readonly ProjectManager _projects;

    public ProjectIconMigratorAdapter(ProjectManager projects)
    {
        _projects = projects;
    }

    public bool TrySetIconGlyphMigrated(string id, ProjectGlyph glyph) =>
        _projects.TrySetIconGlyphMigrated(id, glyph);
}
