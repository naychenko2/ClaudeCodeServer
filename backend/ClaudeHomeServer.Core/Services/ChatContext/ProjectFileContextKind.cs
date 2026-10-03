using System.Text.Json.Nodes;
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
        if (PathOf(reference) is not { } path) return "Не указан путь файла";
        return ProjectLinkGuard.ResolveInside(scope.Project.RootPath, path) is null
            ? "Путь вне проекта или идёт через символическую ссылку"
            : null;
    }

    public ContextItemSummary Describe(ContextScope scope, ContextItem item)
    {
        var path = PathOf(item.Ref) ?? "";
        var label = Path.GetFileName(path.Replace('\\', '/'));
        var exists = scope.Project is not null
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
