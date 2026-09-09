using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Адаптер ProjectManager → IProjectBackgroundWriter (Core).
// Шов для вертикали Backgrounds (Этап 5, волна C, шаг 2): Backgrounds физически
// не может жить в отдельной сборке (ProjectManager в Main), а полный
// ProjectManager держать в Core — лишняя поверхность (40+ методов жизненного
// цикла, миграций, квот). Через узкий интерфейс вертикаль получает ровно те
// шесть операций, что вызывает сама.
//
// Регистрация: один синглтон в Program.cs рядом с `AddSingleton<ProjectManager>`.
// ProjectManager — singleton, поэтому адаптер тоже singleton; состояние не
// хранит, всё форвардит.
public sealed class ProjectBackgroundWriterAdapter : IProjectBackgroundWriter
{
    private readonly ProjectManager _projects;

    public ProjectBackgroundWriterAdapter(ProjectManager projects)
    {
        _projects = projects;
    }

    public bool TryBeginBackground(string id, bool candidatesOnly) =>
        _projects.TryBeginBackground(id, candidatesOnly: candidatesOnly);

    public Project SetBackgroundGenerated(string id, string tileFile) =>
        _projects.SetBackgroundGenerated(id, tileFile);

    public Project SetBackgroundStandard(string id) =>
        _projects.SetBackgroundStandard(id);

    public Project SetBackgroundFailed(string id, string reason) =>
        _projects.SetBackgroundFailed(id, reason);

    public Project Update(string id, string? name, string? rootPath, string? color) =>
        _projects.Update(id, name, rootPath, color: color);

    public string BackgroundsDir => _projects.BackgroundsDir;
}
