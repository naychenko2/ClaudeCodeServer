using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Нить идёт за файлом (ADR-019 §1): файл картинки переименовали или перенесли через файловый
// API — пути нитей в чатах проектов этой папки переписываются следом, на папку — префиксом.
// Сессию не трогает: UpdatedAt не двигается, в ленту ничего не пишется. Переименования мимо
// файлового API (mv в ходе агента) трекер не видит. Корень «Личного» (ADR-040 Ф2-9, «Мои файлы»)
// в проектах не числится: его нити — личные чаты (ProjectId == null) владельца этой папки.
public sealed class ImageThreadPathTracker(
    IProjectManager projects,
    ISessionDirectory directory,
    ImageThreadStore store,
    ILogger<ImageThreadPathTracker> logger,
    IProjectFiles? files = null,
    PersonalFolderResolver? personal = null) : IHostedService
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
        var from = Normalize(oldRel);
        var to = Normalize(newRel);
        if (from.Length == 0 || to.Length == 0 || from == to) return;

        var owners = projects.GetByRootPath(root).ToDictionary(p => p.Id, p => p.OwnerId);
        if (owners.Count == 0 && personal is null) return;
        var personalOwned = new Dictionary<string, bool>();

        foreach (var session in directory.GetAll())
        {
            string? owner;
            if (session.ProjectId is { } pid) owners.TryGetValue(pid, out owner);
            else owner = IsPersonalRootOf(session.OwnerId, root, personalOwned) ? session.OwnerId : null;
            if (string.IsNullOrEmpty(owner)) continue;
            if (!File.Exists(store.StatePath(owner, session.Id))) continue;
            store.RewritePaths(owner, session.Id, p => Move(p, from, to));
        }
    }

    // Корень — папка «Личного» этого владельца; ответ по владельцу запоминается на один проход
    private bool IsPersonalRootOf(string ownerId, string root, Dictionary<string, bool> known)
    {
        if (personal is null || string.IsNullOrEmpty(ownerId)) return false;
        if (known.TryGetValue(ownerId, out var cached)) return cached;
        var own = personal.Find(ownerId)?.RootPath;
        return known[ownerId] = own is not null && PathGuards.IsInside(root, own) && PathGuards.IsInside(own, root);
    }

    // Путь файла или путь внутри переименованной папки — на новое место; прочее как было
    internal static string Move(string path, string from, string to)
    {
        if (path == from) return to;
        return path.StartsWith(from + "/", StringComparison.Ordinal) ? to + path[from.Length..] : path;
    }

    // Пути нитей хранятся от корня через «/» без ведущих «./» и хвостовых «/»; файловый API
    // принимает что прислали — приводим к той же форме
    internal static string Normalize(string rel)
    {
        var p = rel.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.Trim('/');
    }
}
