using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ImageEditor.Chats;

// Чат идёт за файлом (ADR-018 §1, §10.4): файл картинки переименовали или перенесли через
// файловый API — пути чатов картинок этого проекта переписываются следом. На папку действует
// префиксом. UpdatedAt не двигается, в ленту ничего не пишется; на Delete привязка остаётся —
// редактор сам предложит выбрать файл. Переименования мимо файлового API (mv в ходе агента)
// трекер не видит: их лечит «Привязать чат к этому файлу» (PUT …/chats/{id}/path).
//
// Hosted service ради одного: подписка должна жить с первого запроса, а не с первого резолва.
public sealed class ImageChatPathTracker(
    IProjectManager projects,
    ISessionDirectory directory,
    ILogger<ImageChatPathTracker> logger,
    IProjectFiles? files = null,
    IImageChatSessions? chats = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (files is not null) files.OnMutated += OnMutated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (files is not null) files.OnMutated -= OnMutated;
        return Task.CompletedTask;
    }

    private void OnMutated(string root, string rel, FileMutationKind kind, string? newRel)
    {
        if (kind != FileMutationKind.Rename || newRel is null || chats is null) return;
        try { Retarget(root, rel, newRel); }
        catch (Exception ex) { logger.LogWarning(ex, "Перенос путей чатов картинки при переименовании {Old}", rel); }
    }

    // Все проекты этой папки: у соседей по папке файл переехал так же, чаты каждого — свои
    internal void Retarget(string root, string oldRel, string newRel)
    {
        var from = Normalize(oldRel);
        var to = Normalize(newRel);
        if (from.Length == 0 || to.Length == 0 || from == to) return;

        var projectIds = projects.GetByRootPath(root).Select(p => p.Id).ToHashSet();
        if (projectIds.Count == 0) return;

        foreach (var session in directory.GetAll())
        {
            if (session.ImageChat is not { } chat || session.ProjectId is not { } pid || !projectIds.Contains(pid)) continue;
            var current = Move(chat.CurrentPath, from, to);
            var lineage = chat.Lineage.Select(p => Move(p, from, to)).ToList();
            if (current == chat.CurrentPath && lineage.SequenceEqual(chat.Lineage)) continue;
            chats!.RewritePaths(session.Id, current, lineage);
        }
    }

    // Путь файла или путь внутри переименованной папки — на новое место; прочее как было
    internal static string Move(string path, string from, string to)
    {
        if (path == from) return to;
        return path.StartsWith(from + "/", StringComparison.Ordinal) ? to + path[from.Length..] : path;
    }

    // Пути чатов хранятся от корня через «/» без ведущих «./» и хвостовых «/»; файловый API
    // принимает что прислали — приводим к той же форме
    internal static string Normalize(string rel)
    {
        var p = rel.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.Trim('/');
    }
}
