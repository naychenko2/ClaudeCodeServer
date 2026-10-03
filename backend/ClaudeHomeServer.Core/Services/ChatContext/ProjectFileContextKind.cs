using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ChatContext;

// Встроенный вид «файл проекта» (ADR-023): Ref = {path} — относительный путь от корня проекта.
// Основным объектом не бывает: референсы не принимает, исполнителя не описывает.
public sealed class ProjectFileContextKind : IContextKindProvider
{
    public const string Kind = "project-file";

    public IReadOnlyList<string> Kinds { get; } = [Kind];

    public string? Validate(ContextScope scope, string kind, JsonObject reference)
    {
        if (kind != Kind) return $"Вид «{kind}» не принадлежит провайдеру project-file";
        // Личный чат: файлов проекта нет, до RootPath не доходим
        if (scope.Project is null) return "В личном чате нет файлов проекта";
        // Защита в глубину: локальный проект (ADR-016) — отказ до диска, даже если вызывающий не проверил сам
        if (ProjectCapabilityGuard.Refusal(scope.Project, ProjectCapabilityArea.FileBound) is { } refusal) return refusal;
        if (PathOf(reference) is not { } path) return "Не указан путь файла";
        var root = scope.Project.RootPath;
        if (ProjectLinkGuard.ResolveInside(root, path) is not null) return null;
        // Отказ двух родов: лексически путь вне корня — или внутри, но идёт через ссылку
        return LiesInsideLexically(root, path)
            ? "Путь идёт через символическую ссылку"
            : "Путь вне проекта";
    }

    private static bool LiesInsideLexically(string root, string path)
    {
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\')) return false;
        try { SafePath.Join(root, path); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public ContextItemSummary Describe(ContextScope scope, ContextItem item)
    {
        var path = PathOf(item.Ref) ?? "";
        var label = Path.GetFileName(path.Replace('\\', '/'));
        var exists = scope.Project is not null
            && ProjectCapabilityGuard.Allows(scope.Project, ProjectCapabilityArea.FileBound)
            && !string.IsNullOrEmpty(path)
            && ProjectLinkGuard.ResolveInside(scope.Project.RootPath, path) is { } full
            && File.Exists(full);
        return new ContextItemSummary(label.Length > 0 ? label : path, null, null, Missing: !exists);
    }

    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => [];

    public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;

    private static string? PathOf(JsonObject reference) =>
        reference["path"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
