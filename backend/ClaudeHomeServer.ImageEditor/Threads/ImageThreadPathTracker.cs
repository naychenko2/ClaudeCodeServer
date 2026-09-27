using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Нить идёт за файлом (ADR-019 §1): файл картинки переименовали или перенесли через файловый
// API — пути нитей в чатах проектов этой папки переписываются следом, на папку — префиксом.
// Сессию не трогает: UpdatedAt не двигается, в ленту ничего не пишется. Переименования мимо
// файлового API (mv в ходе агента) трекер не видит — так же, как у чатов картинки v2.
public sealed class ImageThreadPathTracker(
    IProjectManager projects,
    ISessionDirectory directory,
    ImageThreadStore store,
    ILogger<ImageThreadPathTracker> logger,
    IProjectFiles? files = null) : IHostedService
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
        if (kind != FileMutationKind.Rename || newRel is null) return;
        try { Retarget(root, rel, newRel); }
        catch (Exception ex) { logger.LogWarning(ex, "Перенос путей нитей картинок при переименовании {Old}", rel); }
    }

    // Все проекты этой папки: у соседей по папке файл переехал так же, нити каждого — свои
    internal void Retarget(string root, string oldRel, string newRel)
    {
        var from = ImageChatPathTracker.Normalize(oldRel);
        var to = ImageChatPathTracker.Normalize(newRel);
        if (from.Length == 0 || to.Length == 0 || from == to) return;

        var owners = projects.GetByRootPath(root).ToDictionary(p => p.Id, p => p.OwnerId);
        if (owners.Count == 0) return;

        foreach (var session in directory.GetAll())
        {
            if (session.ProjectId is not { } pid || !owners.TryGetValue(pid, out var owner) || string.IsNullOrEmpty(owner)) continue;
            if (!File.Exists(store.StatePath(owner, session.Id))) continue;
            store.RewritePaths(owner, session.Id, p => ImageChatPathTracker.Move(p, from, to));
        }
    }
}
