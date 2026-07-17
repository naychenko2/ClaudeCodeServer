using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TriggerSources;

// Резолв корня мониторинга для файловых триггеров проактивности (File/GitCommit) из args правила.
// Два режима (см. контракт Args в PersonaAutomation.cs):
//   • проект: args.projectId → project.RootPath (как исторически);
//   • папка (глобальный агент): args.folder — относительный подпуть в ОСНОВНОЙ ПАПКЕ пользователя
//     ({DefaultProjectsPath}/{username}, образец — SessionManager.ResolveChatRoot). Пустая строка = вся
//     домашняя папка. Guard от path traversal: результат обязан лежать внутри домашней папки.
// Единая точка, чтобы FileTriggerSource / GitCommitTriggerSource / PersonaAutomationService не
// дублировали резолв. label — человекочитаемая подпись корня для summary/контекста хода.
public sealed class AutomationRootResolver(ProjectManager projects, AppSettingsService appSettings)
{
    public (string? Root, string Label) Resolve(IReadOnlyDictionary<string, JsonElement> args, User user)
    {
        var projectId = args.GetString("projectId");
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var project = projects.GetById(projectId);
            if (project is not null && !string.IsNullOrWhiteSpace(project.RootPath))
                return (project.RootPath, $"«{project.Name}»");
            return (null, "");
        }

        // Режим «папка без проекта»: ключ folder присутствует (строка, возможно "").
        var folder = args.GetString("folder");
        if (folder is null) return (null, "");

        var basePath = appSettings.Get().DefaultProjectsPath;
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(user.Username))
            return (null, "");

        var home = Path.GetFullPath(Path.Combine(basePath, user.Username))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(home, folder.Trim().TrimStart('/', '\\')));
        // Guard: не выходить за домашнюю папку пользователя
        if (!full.Equals(home, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (null, "");

        var label = string.IsNullOrWhiteSpace(folder) ? "домашняя папка" : $"папка «{folder.Trim()}»";
        return (full, label);
    }
}
