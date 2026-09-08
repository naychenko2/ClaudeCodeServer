namespace ClaudeHomeServer.Services.Composition;

// Реализация шва IProjectManager (Core) поверх корневого ProjectManager в Main.
// Полный `ProjectManager` содержит десятки методов (жизненный цикл проекта,
// миграции, квоты, права, мутации фонов и иконок); отдельные .csproj (Notes,
// Knowledge) видят через шов только 4 метода чтения реестра.
//
// Полный `Project` возвращается как есть: существующие потребители (NotesService,
// ProjectKnowledgeSyncService) читают поля `RootPath`/`OwnerId`/`Name` напрямую,
// вводить узкий DTO на этом шаге — лишняя работа (потребует перелопатить
// несколько сервисов, которые сейчас читают `.RootPath` и `.OwnerId`).
// Если в будущем понадобится DTO — расширим шов, сейчас контракт узкий по числу
// методов, а не по форме возвращаемого типа.
//
// Раньше IProjectManager регистрировался фабрикой `sp => sp.GetRequiredService<ProjectManager>()`,
// что неявно делало один объект интерфейсом двух типов. Явный класс-адаптер:
//   - даёт точку для подмены в тестах (мутационная проверка адаптера);
//   - формализует, что IProjectManager — это узкая проекция ProjectManager
//     (только 4 из ~30 публичных методов), а не синоним.
public sealed class ProjectManagerAdapter : IProjectManager
{
    private readonly ProjectManager _projects;

    public ProjectManagerAdapter(ProjectManager projects)
    {
        _projects = projects;
    }

    public Models.Project? GetById(string id) =>
        _projects.GetById(id);

    public IReadOnlyCollection<Models.Project> GetByOwner(string userId) =>
        _projects.GetByOwner(userId);

    public IReadOnlyCollection<Models.Project> GetAll() =>
        _projects.GetAll();

    public IReadOnlyCollection<Models.Project> GetByRootPath(string rootPath) =>
        _projects.GetByRootPath(rootPath);
}
