namespace ClaudeHomeServer.Protocol;

// Контракт маршрутов файлов и git проекта (ADR-016, задача 4.2а): его обслуживают две стороны —
// серверные FilesController/GitController и localhost-API агента устройства, а фронт ходит к
// обеим одним клиентом. Формы запросов и ответов — общие типы: расхождение формы держит
// компилятор, а контракт-тест (ClaudeHomeServer.Tests, Contract/) — коды отказов и то, что
// осталось анонимным.

public record SaveContentRequest(string Content);
public record PathRequest(string Path);
// Создание файла: Content == null → пустой файл (старый контракт { path } работает);
// существующий путь → 409, без тихой перезаписи
public record CreateFileRequest(string Path, string? Content = null);
public record RenameRequest(string OldPath, string NewPath);
public record GitPathRequest(string Path);
public record GitCommitRequest(string Message, bool Amend = false);

/// <summary>Дифф файла: <c>files/diff</c> и <c>git/diff</c>. null — изменений нет.</summary>
public sealed record DiffResponse(string? Diff);

/// <summary>Ответ <c>git/commit</c>: sha созданного коммита.</summary>
public sealed record CommitResponse(string Sha);

/// <summary>Маршрут относительно <c>api/projects/{projectId}/</c>: метод и шаблон, как в RoutePattern.</summary>
public sealed record ProjectApiRoute(string Method, string Template)
{
    public override string ToString() => $"{Method} {Template}";
}

/// <summary>
/// Какие маршруты файлов и git есть у агента устройства. Источник правды для матрицы
/// возможностей локального проекта и фронта (4.4): действие на маршруте из
/// <see cref="Unsupported"/> у локального проекта прячется. Каждый маршрут серверных
/// контроллеров обязан стоять ровно в одном списке — это сторожит контракт-тест: новый
/// серверный маршрут без решения «есть ли он у агента» красит сборку.
/// </summary>
public static class DeviceAgentRoutes
{
    public static readonly IReadOnlyList<ProjectApiRoute> Shared =
    [
        new("GET", "files"),
        new("GET", "files/tree"),
        new("GET", "files/search"),
        new("GET", "files/content"),
        new("PUT", "files/content"),
        new("GET", "files/diff"),
        new("POST", "files/revert"),
        new("POST", "files/create"),
        new("POST", "files/mkdir"),
        new("POST", "files/rename"),
        new("DELETE", "files"),
        new("GET", "files/stream"),

        new("GET", "git/status"),
        new("GET", "git/diff"),
        new("GET", "git/log"),
        new("GET", "git/branches"),
        new("POST", "git/stage"),
        new("POST", "git/unstage"),
        new("POST", "git/discard"),
        new("POST", "git/commit"),
    ];

    public static readonly IReadOnlyList<ProjectApiRoute> Unsupported =
    [
        // Загрузка, скачивание по ссылке, связь с чатами сервера
        new("POST", "files/upload"),
        new("POST", "files/save-from-url"),
        new("POST", "files/changed-by"),
        // OnlyOffice живёт рядом с сервером и тянет файл по адресу сервера
        new("GET", "files/office-download"),
        new("GET", "files/office-config"),
        new("POST", "files/office-callback"),
        new("POST", "files/office-discard"),
        new("POST", "files/office-force-save"),
        new("GET", "files/office-version"),
        // ИИ-действия над документами идут через модели сервера
        new("POST", "files/document/convert"),
        new("POST", "files/document/summary"),
        new("POST", "files/document/extract"),
        new("POST", "files/document/to-markdown"),
        new("POST", "files/document/tags"),

        // git: история и ревизии
        new("GET", "git/unpushed"),
        new("GET", "git/commits/{sha}"),
        new("GET", "git/commits/{sha}/diff"),
        new("GET", "git/commits/{sha}/file"),
        new("POST", "git/commits/{sha}/restore-file"),
        new("POST", "git/commits/{sha}/revert"),
        new("GET", "git/file-log"),
        new("GET", "git/blame"),
        // git: массовые операции и хунки
        new("POST", "git/stage-all"),
        new("POST", "git/discard-all"),
        new("POST", "git/stage-hunk"),
        new("POST", "git/unstage-hunk"),
        new("POST", "git/save-now"),
        // git: stash
        new("GET", "git/stash"),
        new("GET", "git/stash/{index:int}"),
        new("POST", "git/stash"),
        new("POST", "git/stash/{index:int}/pop"),
        new("DELETE", "git/stash/{index:int}"),
        // git: ветки (запись), remote и Forgejo
        new("POST", "git/checkout"),
        new("POST", "git/branches"),
        new("POST", "git/fetch"),
        new("POST", "git/pull"),
        new("POST", "git/push"),
        new("POST", "git/sync"),
        new("POST", "git/init"),
        new("GET", "git/remote"),
        new("POST", "git/remote"),
        new("POST", "git/remote/server"),
        new("GET", "git/forgejo-credentials"),
        new("POST", "git/forgejo-credentials/reset"),
        new("PUT", "git/auto-commit"),
        // git: ИИ-помощь и её настройки
        new("POST", "git/ai/commit-message"),
        new("POST", "git/ai/detect-commit-style"),
        new("POST", "git/ai/stash-name"),
        new("GET", "git/commit-prompt"),
        new("PUT", "git/commit-prompt"),
    ];
}
