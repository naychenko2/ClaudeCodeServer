using ClaudeHomeServer.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.TriggerSources;

// Файл-триггер: новый/изменённый файл по glob в папке. Poll-snapshot на тике
// (НЕ FileWatcherService — тот ref-counted по UI-коннектам и живёт только с открытым клиентом):
// обходим дерево корня с отсевом тяжёлых папок (FileService.TreeExcludes), применяем glob
// через Matcher+InMemoryDirectoryInfo, дифф по LastWriteTicks. Снапшот обновляем синхронно с детекцией
// → следующий тик видит только новые изменения (встроенный дедуп).
//
// Корень резолвится AutomationRootResolver: projectId → project.RootPath, либо folder →
// подпапка основной папки пользователя (глобальный агент без проекта).
// Args: projectId | folder, glob ("src/**/*.ts", по умолчанию "**/*"), kinds:["created","changed"]
public sealed class FileTriggerSource(AutomationRootResolver roots, ILogger<FileTriggerSource> log) : ITriggerSource
{
    public AutomationTriggerType Type => AutomationTriggerType.File;

    // Cap защиты от огромных деревьев (снапшот не разрастается бесконечно)
    private const int MaxEntries = 20000;

    public Task<IReadOnlyList<TriggerEvent>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        var args = TriggerArgs.Of(ctx.Rule.Trigger);
        var glob = args.GetString("glob");
        if (string.IsNullOrWhiteSpace(glob)) glob = "**/*";
        var kinds = args.GetStringList("kinds") ?? ["created", "changed"];
        var watchCreated = kinds.Contains("created", StringComparer.OrdinalIgnoreCase);
        var watchChanged = kinds.Contains("changed", StringComparer.OrdinalIgnoreCase);

        var (root, label) = roots.Resolve(args, ctx.User);
        if (root is null || !Directory.Exists(root))
            return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var cur = BuildSnapshot(root, glob);
        var prev = ctx.State.FileSnapshot;
        // Первое наблюдение: только базовый снапшот, без эмита — иначе «созданным» считался бы
        // весь проект и новое правило сразу запускало бы дорогой ход (guard как у GitCommit)
        if (prev is null)
        {
            ctx.State.FileSnapshot = cur;
            return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());
        }

        var created = new List<string>();
        var changed = new List<string>();
        foreach (var (rel, ticks) in cur)
        {
            if (!prev.TryGetValue(rel, out var oldTicks)) { if (watchCreated) created.Add(rel); }
            else if (oldTicks != ticks) { if (watchChanged) changed.Add(rel); }
        }
        ctx.State.FileSnapshot = cur;

        if (created.Count == 0 && changed.Count == 0)
            return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var bits = new List<string>();
        if (created.Count > 0) bits.Add($"новых {created.Count}");
        if (changed.Count > 0) bits.Add($"изменённых {changed.Count}");
        var summary = $"Файлы в {label} изменились: {string.Join(", ", bits)}";
        var details = new Dictionary<string, string>();
        AddCapped(details, "created", created);
        AddCapped(details, "changed", changed);
        return Task.FromResult<IReadOnlyList<TriggerEvent>>(new[]
        {
            new TriggerEvent(ctx.Rule.Id, AutomationTriggerType.File, summary, details),
        });
    }

    private static void AddCapped(Dictionary<string, string> d, string key, List<string> items)
    {
        if (items.Count == 0) return;
        const int cap = 15;
        d[key] = string.Join("\n", items.Take(cap)) + (items.Count > cap ? $"\n…и ещё {items.Count - cap}" : "");
    }

    // Обход дерева (отсев FileService.TreeExcludes + cap), затем glob-отбор через Matcher.
    private Dictionary<string, long> BuildSnapshot(string root, string glob)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var all = new Dictionary<string, long>(StringComparer.Ordinal);  // rel → ticks
        var abs = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && all.Count < MaxEntries)
        {
            var dir = stack.Pop();
            string[] subdirs; string[] files;
            try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var s in subdirs)
            {
                if (FileService.TreeExcludes.Contains(Path.GetFileName(s))) continue;
                stack.Push(s);
            }
            foreach (var f in files)
            {
                if (all.Count >= MaxEntries) break;
                var rel = Path.GetRelativePath(rootFull, f).Replace('\\', '/');
                try { all[rel] = File.GetLastWriteTimeUtc(f).Ticks; abs.Add(f); }
                catch { /* файл мог исчезнуть между enumerable и чтением */ }
            }
        }
        if (all.Count >= MaxEntries)
            log.LogWarning("File-триггер: достигнут cap {Cap} файлов в {Root}", MaxEntries, root);

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(glob);
        var matched = matcher.Execute(new InMemoryDirectoryInfo(rootFull, abs));
        var cur = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var m in matched.Files)
            if (all.TryGetValue(m.Path, out var t)) cur[m.Path] = t;
        return cur;
    }
}
