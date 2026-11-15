using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ChatContext;

// Встроенный вид «файл Личного» (Ф-9): Ref = {path} — относительный путь от корня «Личного» этого
// владельца. Парный к project-file, но область у него своя: вид живёт только личным чатом (у чата
// проекта «Личное» — папка владельца, а не проекта), а корень берётся единственной точкой
// PersonalFolderResolver по OwnerId вызывающего — чужой владелец чужой папки не получает по
// построению, своего файла он не увидит даже по верному пути. Основным объектом не бывает
// (CanBePrimary по умолчанию false): только референс; из редакторов его берёт звук, роль «Кусок»
// склейки (AudioContextRoles.Piece). В контекст кладутся только файлы медиальных подпапок
// (images/audio/video) — тот же источник правил, что у панели «Личного» и входов Start/Concat:
// не-медийный путь отказывается здесь, а не на склейке (хвост ревью Р-Ф-9).
public sealed class PersonalFileContextKind(PersonalFolderResolver? personal = null) : IContextKindProvider
{
    public const string Kind = "personal-file";

    private const string NonMediaRefusal = "В контекст кладут только файлы папок «images», «audio» или «video»";

    public IReadOnlyList<string> Kinds { get; } = [Kind];

    public string? Validate(ContextScope scope, string kind, JsonObject reference)
    {
        if (kind != Kind) return $"Вид «{kind}» не принадлежит провайдеру personal-file";
        // Не личный чат (и нет папки, и нет смысла): «Личное» в контекст чата проекта не кладётся
        if (RootOf(scope) is not { } root) return Refusal(scope);
        if (PathOf(reference) is not { } path) return "Не указан путь файла";
        // Владелец проверяется как у проектных путей: чужое private не подтверждаем даже существованием
        if (ProjectLinkGuard.ResolveInsideAs(root, path, scope.ActorId) is not null)
            // Внутри «Личного» годится только медиальная подпапка: тот же источник правил, что у панели
            // «Личного» и входов Start/Concat. Не-медийный путь (docs/x.txt) редактор не прочитает —
            // отказ здесь, а не на склейке
            return PersonalMediaFolders.AllowsAll([path]) ? null : NonMediaRefusal;
        if (PrivateAttachments.IsForeignResolved(root, path, scope.ActorId)) return "Путь вне «Личного»";
        // Отказ двух родов: лексически путь вне корня — или внутри, но идёт через ссылку
        return LiesInsideLexically(root, path)
            ? "Путь идёт через символическую ссылку"
            : "Путь вне «Личного»";
    }

    // Корень «Личного» вызывающего: единственная точка (ADR-040), Find ничего не создаёт — без папки
    // отказ идёт до обращения к диску. Проектный чат — тот же отказ, что без папки
    private string? RootOf(ContextScope scope) =>
        scope.Session.ProjectId is null ? personal?.Find(scope.OwnerId)?.RootPath : null;

    private static string Refusal(ContextScope scope) =>
        scope.Session.ProjectId is not null
            ? "Файлы «Личного» доступны только в личном чате"
            : PersonalFolderResolver.UnavailableText;

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
        var exists = RootOf(scope) is { } root
            && path.Length > 0
            && ProjectLinkGuard.ResolveInsideAs(root, path, scope.ActorId) is { } full
            && File.Exists(full);
        return new ContextItemSummary(label.Length > 0 ? label : path, null, null, Missing: !exists);
    }

    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => [];

    public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;

    private static string? PathOf(JsonObject reference) =>
        reference["path"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
