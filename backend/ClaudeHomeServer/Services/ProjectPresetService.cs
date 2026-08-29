using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Docs;

namespace ClaudeHomeServer.Services;

// Пропуск шага применения пресета: путь, которого он касался, и причина.
public record PresetSkip(string Path, string Reason);

// Честный отчёт о применении, а не мнимая атомарность: файловая система, .docs и
// projects.json общей транзакции не имеют, и обещать её нельзя (п.3 плана знакомства v2).
public record PresetApplyResult(IReadOnlyList<string> Created, IReadOnlyList<PresetSkip> Skipped);

/// <summary>
/// Применение пресета каркаса к проекту (знакомство v2, п.2-3). Только добавляет —
/// существующие папки, файлы, область документации и доска не перезаписываются никогда:
/// проект часто заводят на живой папке с рабочим CLAUDE.md или настроенной доской.
///
/// Порядок записи: папки и файлы → .docs → колонки доски → PresetKey ПОСЛЕДНИМ.
/// Пока PresetKey = "pending", повтор безопасен (skip-if-exists добирает недостающее);
/// смерть процесса посередине оставляет "pending" и не портит ничего записанного.
/// </summary>
public sealed class ProjectPresetService(FileService files, DocsIndexService docs, ProjectManager projects)
{
    // Точка проверки порядка записи для тестов: вызывается после всех шагов применения,
    // ДО простановки PresetKey. Продуктового кода сюда нет.
    internal Action<Project>? BeforePresetKeyCommit { get; set; }

    // Строка о последствиях пропуска .docs: без области «Начало» панели не переключилось,
    // и человек не поймёт этого из молчаливого skipped (требование п.3 плана)
    private const string DocsSkipNote =
        "Статус.md в область документации не добавлен, «Начало» панели не переключилось";

    public PresetApplyResult Apply(Project project, PresetDefinition preset)
    {
        var created = new List<string>();
        var skipped = new List<PresetSkip>();
        var root = project.RootPath;

        // Папки: CreateDirectory идемпотентен, но отчёт обязан отличать созданное от бывшего
        foreach (var folder in preset.Folders)
        {
            if (Directory.Exists(FullPath(root, folder)))
            {
                skipped.Add(new PresetSkip(folder, "папка уже существует"));
                continue;
            }
            try
            {
                files.CreateDirectory(root, folder);   // SafeJoin: имена приходят из каталога
                created.Add(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new PresetSkip(folder, $"не удалось создать папку: {e.Message}"));
            }
        }

        // Файлы: существующий пропускается — FileService.CreateFile/WriteFile затирают
        foreach (var file in preset.Files)
        {
            if (File.Exists(FullPath(root, file.Path)))
            {
                skipped.Add(new PresetSkip(file.Path, "файл уже существует — не перезаписан"));
                continue;
            }
            try
            {
                // WriteFile родительских папок не создаёт (в отличие от CreateFile,
                // который безусловно затирает файл) — создаём заранее. Название проекта
                // подставляется здесь же: каталог хранит токен-подсказку, файл на диске
                // получает реальную шапку
                var parent = Path.GetDirectoryName(file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent))
                    files.CreateDirectory(root, parent);
                files.WriteFile(root, file.Path, PresetCatalog.Materialize(file.Content, project.Name));
                created.Add(file.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new PresetSkip(file.Path, $"не удалось записать файл: {e.Message}"));
            }
        }

        // Область документации — в .docs репозитория, а не в Project.Docs* (файл сильнее
        // настройки, едет вместе с папкой, версионируется; типы документов со свойствами
        // доступны только через него). Правило бинарное: нет файла/пуст → пишем; что-то
        // валидное есть → skipped целиком, частичной записи WriteScopeFile не умеет.
        try
        {
            var read = docs.ReadScopeFile(root);
            if (read.Broken)
                skipped.Add(new PresetSkip(DocsIndexService.ScopeFileName,
                    $"файл не разобран, не перезаписан; {DocsSkipNote}"));
            else if (read.Scope is not null)
                skipped.Add(new PresetSkip(DocsIndexService.ScopeFileName,
                    $"область уже настроена — не перезаписана; {DocsSkipNote}"));
            else
            {
                var status = docs.WriteScopeFile(root, preset.DocsScope, preset.DocTypes);
                if (status == DocsIndexService.ScopeFileWriteStatus.Ok)
                    created.Add(DocsIndexService.ScopeFileName);
                else
                    skipped.Add(new PresetSkip(DocsIndexService.ScopeFileName,
                        status == DocsIndexService.ScopeFileWriteStatus.Broken
                            ? $"файл не разобран, запись отменена; {DocsSkipNote}"
                            : $"не удалось записать; {DocsSkipNote}"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new PresetSkip(DocsIndexService.ScopeFileName,
                $"не удалось записать: {e.Message}; {DocsSkipNote}"));
        }

        // Колонки доски: UpdateBoardColumns заменяет список целиком — пишем только в null
        if (project.BoardColumns is not null)
        {
            skipped.Add(new PresetSkip("Доска задач", "колонки уже настроены — не перезаписаны"));
        }
        else
        {
            projects.UpdateBoardColumns(project.Id,
                [.. preset.BoardColumns.Select(c => new BoardColumn { Name = c.Name, Category = c.Category, Role = c.Role })]);
            created.Add("Доска задач");
        }

        // PresetKey — последняя запись: частичный успех выше фиксирует его, повтор даёт 409
        BeforePresetKeyCommit?.Invoke(project);
        projects.SetPresetKey(project.Id, preset.Key);
        return new PresetApplyResult(created, skipped);
    }

    private static string FullPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
