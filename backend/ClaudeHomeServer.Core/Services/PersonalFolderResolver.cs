using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Единственная точка «папка личной области» для медиа-кода (ADR-040 §2.1): обёртка над
// IPersonalProjects, чтобы вертикали не держали стор «Личного» напрямую.
// Find — чтение без побочных эффектов (GET-пути и перечисления: папку не создаёт);
// EnsureAsync — только для пути, которому нужен диск (создаёт запись и папку лениво).
public sealed class PersonalFolderResolver(IPersonalProjects personal)
{
    /// <summary>Код отказа, когда папку «Личного» создать нельзя (нет домашней папки, нет места, нет прав).</summary>
    public const string UnavailableCode = "personal_scope_folder_unavailable";

    public const string UnavailableText = "Папка «Личного» недоступна";

    /// <summary>«Личное» владельца, если запись есть и папка на месте; иначе null. Ничего не создаёт.</summary>
    public Project? Find(string? ownerId) =>
        !string.IsNullOrEmpty(ownerId) && personal.GetPersonalOf(ownerId) is { } project && Directory.Exists(project.RootPath)
            ? project
            : null;

    /// <summary>«Личное» владельца: создаёт запись и папку при необходимости. null — папку получить нельзя.</summary>
    public async Task<Project?> EnsureAsync(string? ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ownerId)) return null;
        try
        {
            return await personal.EnsurePersonalAsync(ownerId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
