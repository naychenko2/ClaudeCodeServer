using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Obsidian-совместимая база заметок. Источник правды — .md файлы на диске:
//   • личный vault пользователя:  {dataDir}/notes/{userId}/
//   • notes/ внутри каждого проекта, которым владеет пользователь
// Единый per-owner граф агрегирует все источники владельца (Модель 3 из плана).
// Класс не хранит состояние заметок — сканирует файлы на каждый запрос
// (заметок немного; кэш-инвалидация — возможная оптимизация позже).
public sealed class NotesService
{
    private readonly ProjectManager _projects;
    private readonly ILogger<NotesService> _logger;
    private readonly string _dataDir;

    private const string PersonalKey = "personal";
    private const string PersonalLabel = "Личный";

    // [[Target]] | [[Target|подпись]] | [[Target#заголовок]] | [[Папка/Target]]
    private static readonly Regex WikiLink = new(@"\[\[([^\[\]]+?)\]\]", RegexOptions.Compiled);

    public NotesService(ProjectManager projects, IConfiguration config, ILogger<NotesService> logger)
    {
        _projects = projects;
        _logger = logger;
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // --- Источники владельца ---

    private sealed record Source(string Key, string Label, string RootDir);

    private IReadOnlyList<Source> SourcesFor(string userId)
    {
        var list = new List<Source>
        {
            new(PersonalKey, PersonalLabel, Path.Combine(_dataDir, "notes", userId)),
        };
        foreach (var p in _projects.GetByOwner(userId))
            if (!string.IsNullOrWhiteSpace(p.RootPath))
                list.Add(new(p.Id, p.Name, Path.Combine(p.RootPath, "notes")));
        return list;
    }

    // Источники для выбора «куда создать заметку»
    public IReadOnlyList<NoteSourceDto> GetSources(string userId) =>
        SourcesFor(userId).Select(s => new NoteSourceDto(s.Key, s.Label)).ToList();

    // --- Сканирование и парсинг ---

    private sealed class RawNote
    {
        public required string Id;
        public required string SourceKey;
        public required string SourceLabel;
        public required string RelPath;
        public required string FullPath;
        public required string Title;
        public required string Content;
        public required List<string> Tags;
        // Сырые цели ссылок + строка-контекст, где ссылка встретилась
        public required List<(string Target, string Snippet)> RawLinks;
        public required string CreatedAt;
        public required string UpdatedAt;
    }

    private List<RawNote> Scan(string userId)
    {
        var notes = new List<RawNote>();
        foreach (var src in SourcesFor(userId))
        {
            if (!Directory.Exists(src.RootDir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(src.RootDir, "*.md", SearchOption.AllDirectories); }
            catch (Exception ex) { _logger.LogWarning(ex, "Сканирование заметок {Dir}", src.RootDir); continue; }

            foreach (var full in files)
            {
                string text;
                try { text = File.ReadAllText(full, Encoding.UTF8); }
                catch { continue; }

                var rel = NormalizeRel(Path.GetRelativePath(src.RootDir, full));
                var (title, tags) = ParseFrontmatter(text, Path.GetFileNameWithoutExtension(full));
                var links = ParseLinks(text);
                notes.Add(new RawNote
                {
                    Id = EncodeId(src.Key, rel),
                    SourceKey = src.Key,
                    SourceLabel = src.Label,
                    RelPath = rel,
                    FullPath = full,
                    Title = title,
                    Content = text,
                    Tags = tags,
                    RawLinks = links,
                    CreatedAt = SafeTime(() => File.GetCreationTimeUtc(full)),
                    UpdatedAt = SafeTime(() => File.GetLastWriteTimeUtc(full)),
                });
            }
        }
        return notes;
    }

    // Минимальный разбор YAML-frontmatter: только title и tags (без внешней либы).
    private static (string Title, List<string> Tags) ParseFrontmatter(string text, string fallbackTitle)
    {
        var title = fallbackTitle;
        var tags = new List<string>();
        if (!text.StartsWith("---")) return (title, tags);

        using var reader = new StringReader(text);
        var first = reader.ReadLine();
        if (first is null || first.Trim() != "---") return (title, tags);

        var lines = new List<string>();
        string? line;
        var closed = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim() == "---") { closed = true; break; }
            lines.Add(line);
        }
        if (!closed) return (title, tags);

        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var m = Regex.Match(l, @"^title:\s*(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) { title = m.Groups[1].Value.Trim().Trim('"', '\''); continue; }

            var t = Regex.Match(l, @"^tags:\s*(.*)$", RegexOptions.IgnoreCase);
            if (t.Success)
            {
                var inline = t.Groups[1].Value.Trim();
                if (inline.StartsWith("["))
                    tags.AddRange(inline.Trim('[', ']').Split(',')
                        .Select(x => x.Trim().Trim('"', '\'')).Where(x => x.Length > 0));
                else
                    // список в столбик: последующие строки "  - тег"
                    for (var j = i + 1; j < lines.Count; j++)
                    {
                        var item = Regex.Match(lines[j], @"^\s*-\s*(.+)$");
                        if (!item.Success) break;
                        tags.Add(item.Groups[1].Value.Trim().Trim('"', '\''));
                    }
            }
        }
        return (title, tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<(string, string)> ParseLinks(string text)
    {
        var result = new List<(string, string)>();
        foreach (Match m in WikiLink.Matches(text))
        {
            var inner = m.Groups[1].Value;
            // Отрезаем подпись [[Target|подпись]] и якорь [[Target#heading]]
            var target = inner.Split('|')[0].Split('#')[0].Trim();
            if (target.Length == 0) continue;
            result.Add((target, SnippetAround(text, m.Index)));
        }
        return result;
    }

    // Строка-контекст вокруг ссылки (для панели backlinks)
    private static string SnippetAround(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        var end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        var line = text[start..end].Trim();
        return line.Length > 120 ? line[..117] + "…" : line;
    }

    // --- Модель графа (строится один раз из скана) ---

    private sealed class Model
    {
        public required List<RawNote> Notes;
        public required Dictionary<string, RawNote> ById;
        // id заметки -> разрешённые исходящие ссылки
        public required Dictionary<string, List<NoteLinkDto>> OutLinks;
        // id цели -> входящие ссылки
        public required Dictionary<string, List<NoteBacklinkDto>> Backlinks;
        // ghost id -> отображаемое имя
        public required Dictionary<string, string> Ghosts;
        // множество рёбер (source|target) для дедупликации
        public required HashSet<(string, string)> Edges;
    }

    private Model Build(string userId)
    {
        var notes = Scan(userId);
        var byId = notes.ToDictionary(n => n.Id);

        // Индекс имя -> заметки (для резолва [[...]])
        var byName = new Dictionary<string, List<RawNote>>();
        foreach (var n in notes)
        {
            var key = Norm(n.Title);
            if (!byName.TryGetValue(key, out var l)) byName[key] = l = new();
            l.Add(n);
        }

        var outLinks = new Dictionary<string, List<NoteLinkDto>>();
        var backlinks = new Dictionary<string, List<NoteBacklinkDto>>();
        var ghosts = new Dictionary<string, string>();
        var edges = new HashSet<(string, string)>();

        foreach (var n in notes)
        {
            var outs = new List<NoteLinkDto>();
            var seen = new HashSet<string>();
            foreach (var (rawTarget, snippet) in n.RawLinks)
            {
                var segments = rawTarget.Split('/');
                var name = segments[^1].Trim();
                var ns = segments.Length > 1 ? segments[0].Trim() : null;
                var key = Norm(name);
                if (key.Length == 0) continue;

                var resolved = Resolve(byName, key, ns, n);
                string targetId; string targetTitle; bool isResolved;
                if (resolved is not null)
                {
                    targetId = resolved.Id; targetTitle = resolved.Title; isResolved = true;
                    if (!backlinks.TryGetValue(targetId, out var bl)) backlinks[targetId] = bl = new();
                    bl.Add(new NoteBacklinkDto(n.Id, n.Title, n.SourceKey, n.SourceLabel, snippet));
                }
                else
                {
                    targetId = "ghost:" + key; targetTitle = name; isResolved = false;
                    ghosts[targetId] = name;
                }
                if (seen.Add(targetId))
                {
                    outs.Add(new NoteLinkDto(targetId, targetTitle, isResolved));
                    edges.Add((n.Id, targetId));
                }
            }
            outLinks[n.Id] = outs;
        }

        return new Model
        {
            Notes = notes, ById = byId, OutLinks = outLinks,
            Backlinks = backlinks, Ghosts = ghosts, Edges = edges,
        };
    }

    // Разрешение коллизий имён: 1 кандидат — берём; несколько — по namespace
    // (источник в записи [[Проект/Заметка]]), иначе предпочитаем тот же источник.
    private static RawNote? Resolve(Dictionary<string, List<RawNote>> byName, string key, string? ns, RawNote from)
    {
        if (!byName.TryGetValue(key, out var candidates) || candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        if (ns is not null)
        {
            var nsKey = Norm(ns);
            var byNs = candidates.FirstOrDefault(c => Norm(c.SourceLabel) == nsKey || c.SourceKey == ns);
            if (byNs is not null) return byNs;
        }
        var sameSource = candidates.FirstOrDefault(c => c.SourceKey == from.SourceKey);
        return sameSource; // null → неоднозначно, считаем неразрешённой
    }

    // --- Публичное API ---

    public IReadOnlyList<NoteSummary> GetSummaries(string userId, string? source, string? query)
    {
        var notes = Scan(userId);
        IEnumerable<RawNote> q = notes;
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(n => n.SourceKey == source);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            q = q.Where(n =>
                n.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                n.Content.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                n.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }
        return q.OrderByDescending(n => n.UpdatedAt)
                .Select(ToSummary).ToList();
    }

    public NoteDetail? GetDetail(string userId, string id)
    {
        var model = Build(userId);
        if (!model.ById.TryGetValue(id, out var n)) return null;
        return ToDetail(model, n);
    }

    public IReadOnlyList<NoteBacklinkDto> GetBacklinks(string userId, string id)
    {
        var model = Build(userId);
        return model.Backlinks.TryGetValue(id, out var bl) ? bl : Array.Empty<NoteBacklinkDto>();
    }

    public NoteGraph GetGraph(string userId)
    {
        var model = Build(userId);
        var degree = new Dictionary<string, int>();
        foreach (var (s, t) in model.Edges)
        {
            degree[s] = degree.GetValueOrDefault(s) + 1;
            degree[t] = degree.GetValueOrDefault(t) + 1;
        }

        var nodes = new List<NoteGraphNode>();
        foreach (var n in model.Notes)
            nodes.Add(new NoteGraphNode(n.Id, n.Title, n.SourceKey, n.SourceLabel,
                degree.GetValueOrDefault(n.Id), false));
        foreach (var (gid, name) in model.Ghosts)
            nodes.Add(new NoteGraphNode(gid, name, "", "", degree.GetValueOrDefault(gid), true));

        var edges = model.Edges.Select(e => new NoteGraphEdge(e.Item1, e.Item2)).ToList();
        return new NoteGraph(nodes, edges);
    }

    public NoteDetail Create(string userId, CreateNoteRequest req)
    {
        var sourceKey = string.IsNullOrWhiteSpace(req.Source) ? PersonalKey : req.Source!;
        var rootDir = ResolveRoot(userId, sourceKey);
        Directory.CreateDirectory(rootDir);

        var baseName = SanitizeFileName(req.Title);
        if (baseName.Length == 0) baseName = "Без названия";
        var relPath = baseName + ".md";
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        // Разрешаем коллизию имени файла суффиксом
        var n = 2;
        while (File.Exists(full))
        {
            relPath = $"{baseName}-{n++}.md";
            full = FileService.SafeJoinPublic(rootDir, relPath);
        }

        var content = req.Content ?? $"# {req.Title}\n";
        File.WriteAllText(full, content, new UTF8Encoding(false));

        var id = EncodeId(sourceKey, NormalizeRel(relPath));
        return GetDetail(userId, id)
            ?? throw new InvalidOperationException("Заметка создана, но не читается");
    }

    public NoteDetail? Update(string userId, string id, UpdateNoteRequest req)
    {
        var (sourceKey, relPath) = DecodeId(id);
        var rootDir = ResolveRoot(userId, sourceKey);
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        if (!File.Exists(full)) return null;

        var effectiveId = id;

        // Переименование файла при смене заголовка (Obsidian: файл = имя заметки)
        if (!string.IsNullOrWhiteSpace(req.Title))
        {
            var currentTitle = Path.GetFileNameWithoutExtension(full);
            var newBase = SanitizeFileName(req.Title!);
            if (newBase.Length > 0 && !newBase.Equals(currentTitle, StringComparison.Ordinal))
            {
                var dir = Path.GetDirectoryName(relPath) ?? "";
                var newRel = NormalizeRel(Path.Combine(dir, newBase + ".md"));
                var newFull = FileService.SafeJoinPublic(rootDir, newRel);
                if (!File.Exists(newFull))
                {
                    File.Move(full, newFull);
                    full = newFull;
                    effectiveId = EncodeId(sourceKey, newRel);
                }
            }
        }

        if (req.Content is not null)
            File.WriteAllText(full, req.Content, new UTF8Encoding(false));

        return GetDetail(userId, effectiveId);
    }

    public bool Delete(string userId, string id)
    {
        var (sourceKey, relPath) = DecodeId(id);
        var rootDir = ResolveRoot(userId, sourceKey);
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        if (!File.Exists(full)) return false;
        File.Delete(full);
        return true;
    }

    // --- Проекции ---

    private NoteSummary ToSummary(RawNote n) =>
        new(n.Id, n.Title, n.SourceKey, n.SourceLabel, n.RelPath, n.Tags, n.CreatedAt, n.UpdatedAt);

    private NoteDetail ToDetail(Model model, RawNote n) =>
        new(n.Id, n.Title, n.SourceKey, n.SourceLabel, n.RelPath, n.Content, n.Tags,
            model.OutLinks.GetValueOrDefault(n.Id) ?? new(),
            model.Backlinks.GetValueOrDefault(n.Id) ?? new(),
            n.CreatedAt, n.UpdatedAt);

    // --- Разрешение источника и валидация владения ---

    private string ResolveRoot(string userId, string sourceKey)
    {
        if (sourceKey == PersonalKey)
            return Path.Combine(_dataDir, "notes", userId);

        var project = _projects.GetById(sourceKey)
            ?? throw new KeyNotFoundException($"Источник {sourceKey} не найден");
        if (project.OwnerId != userId)
            throw new UnauthorizedAccessException("Проект не принадлежит пользователю");
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("У проекта нет корневой папки");
        return Path.Combine(project.RootPath, "notes");
    }

    // --- Утилиты ---

    private static string EncodeId(string sourceKey, string relPath)
    {
        var raw = Encoding.UTF8.GetBytes($"{sourceKey}|{relPath}");
        return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static (string SourceKey, string RelPath) DecodeId(string id)
    {
        var b64 = id.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
        string raw;
        try { raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { throw new ArgumentException("Некорректный id заметки"); }
        var sep = raw.IndexOf('|');
        if (sep < 0) throw new ArgumentException("Некорректный id заметки");
        return (raw[..sep], raw[(sep + 1)..]);
    }

    private static string NormalizeRel(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in title.Trim())
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim().TrimEnd('.');
    }

    private static string SafeTime(Func<DateTime> f)
    {
        try { return f().ToString("o"); } catch { return DateTime.UtcNow.ToString("o"); }
    }
}
