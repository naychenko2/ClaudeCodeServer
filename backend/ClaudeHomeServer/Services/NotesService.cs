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
    private readonly FeatureFlagService _flags;
    private readonly ILogger<NotesService> _logger;
    private readonly string _dataDir;
    // Корень профиля Claude Code (~/.claude): в projects/{slug}/memory лежит память проекта
    private readonly string _claudeProfileDir;

    private const string PersonalKey = "personal";
    private const string PersonalLabel = "Личный";
    // Префикс ключа источника «Память Claude»: memory:{projectId}
    private const string MemoryPrefix = "memory:";

    // [[Target]] | [[Target|подпись]] | [[Target#заголовок]] | [[Папка/Target]]
    private static readonly Regex WikiLink = new(@"\[\[([^\[\]]+?)\]\]", RegexOptions.Compiled);
    // Inline-тег #тег (не заголовок «# » — требуется хотя бы один символ сразу за #)
    private static readonly Regex InlineTag = new(@"(?<=^|\s)#([\p{L}\p{N}_][\p{L}\p{N}_/-]*)", RegexOptions.Compiled);

    // Кэш построенной модели per-owner: сканирование всех файлов дорого, а за один
    // ход UI дёргает список/деталь/граф подряд. TTL короткий — внешние правки (Obsidian)
    // подхватятся быстро; свои мутации инвалидируют кэш явно.
    private readonly ConcurrentDictionary<string, (Model Model, DateTime At)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    public NotesService(ProjectManager projects, FeatureFlagService flags,
        IConfiguration config, ILogger<NotesService> logger)
    {
        _projects = projects;
        _flags = flags;
        _logger = logger;
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
        _claudeProfileDir = config["ClaudeUserProfileDir"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    private Model GetModel(string userId)
    {
        if (_cache.TryGetValue(userId, out var e) && DateTime.UtcNow - e.At < CacheTtl)
            return e.Model;
        var model = Build(userId);
        _cache[userId] = (model, DateTime.UtcNow);
        return model;
    }

    private void Invalidate(string userId) => _cache.TryRemove(userId, out _);

    // --- Источники владельца ---

    private sealed record Source(string Key, string Label, string RootDir, bool ReadOnly = false);

    private IReadOnlyList<Source> SourcesFor(string userId)
    {
        var list = new List<Source>
        {
            new(PersonalKey, PersonalLabel, Path.Combine(_dataDir, "notes", userId)),
        };
        foreach (var p in _projects.GetByOwner(userId))
            if (!string.IsNullOrWhiteSpace(p.RootPath))
                list.Add(new(p.Id, p.Name, Path.Combine(p.RootPath, "notes")));

        // Память Claude Code по проектам (read-only) — за фич-флагом и только если
        // директория памяти реально существует и непуста (иначе источник не показываем)
        if (_flags.IsEnabled(userId, FeatureFlagKeys.NotesMemorySource))
            foreach (var p in _projects.GetByOwner(userId))
            {
                if (string.IsNullOrWhiteSpace(p.RootPath)) continue;
                var memDir = ProjectMemoryDir(p.RootPath);
                if (HasMarkdown(memDir))
                    list.Add(new(MemoryPrefix + p.Id, $"Память: {p.Name}", memDir, ReadOnly: true));
            }
        return list;
    }

    // Директория памяти Claude Code для проекта: {~/.claude}/projects/{slug}/memory
    private string ProjectMemoryDir(string rootPath) =>
        Path.Combine(_claudeProfileDir, "projects", ProjectMemorySlug(rootPath), "memory");

    // Слуг директории проекта у Claude Code: все не-буквенно-цифровые символы пути → '-'
    // (C:\Sources\ClaudeCodeServer → C--Sources-ClaudeCodeServer)
    internal static string ProjectMemorySlug(string rootPath) =>
        Regex.Replace(rootPath.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");

    private static bool HasMarkdown(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        try { return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    // Источники для выбора «куда создать заметку»
    public IReadOnlyList<NoteSourceDto> GetSources(string userId) =>
        SourcesFor(userId).Select(s => new NoteSourceDto(s.Key, s.Label, s.ReadOnly)).ToList();

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
                // templates/ — шаблоны, не заметки: в список/граф не попадают
                if (rel.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)) continue;
                // MEMORY.md — индекс памяти Claude, как заметка бессмыслен
                if (src.ReadOnly && rel.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)) continue;

                var fileName = Path.GetFileNameWithoutExtension(full);
                var fm = ParseFrontmatter(text);
                var tags = fm.Tags;
                // Явный title: приоритетен; для памяти без него — читаемый description, иначе имя файла
                var title = fm.Title
                    ?? (src.ReadOnly ? ReadonlyTitle(fm.Description, fm.Name, fileName) : fileName);
                // Inline-теги #тег из тела + теги из frontmatter (без дублей)
                foreach (var it in InlineTag.Matches(text).Select(m => m.Groups[1].Value))
                    if (!tags.Contains(it, StringComparer.OrdinalIgnoreCase)) tags.Add(it);
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

    // Минимальный разбор YAML-frontmatter: title/description/name (null если поля нет)
    // и tags (без внешней либы). description/name нужны для читаемых заголовков заметок
    // памяти Claude — там нет title:, но есть русский description.
    private static (string? Title, string? Description, string? Name, List<string> Tags) ParseFrontmatter(string text)
    {
        var tags = new List<string>();
        if (!text.StartsWith("---")) return (null, null, null, tags);

        using var reader = new StringReader(text);
        var first = reader.ReadLine();
        if (first is null || first.Trim() != "---") return (null, null, null, tags);

        var lines = new List<string>();
        string? line;
        var closed = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim() == "---") { closed = true; break; }
            lines.Add(line);
        }
        if (!closed) return (null, null, null, tags);

        string? title = null, description = null, name = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var m = Regex.Match(l, @"^title:\s*(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) { title = m.Groups[1].Value.Trim().Trim('"', '\''); continue; }

            var d = Regex.Match(l, @"^description:\s*(.+)$", RegexOptions.IgnoreCase);
            if (d.Success) { description = d.Groups[1].Value.Trim().Trim('"', '\''); continue; }

            var n = Regex.Match(l, @"^name:\s*(.+)$", RegexOptions.IgnoreCase);
            if (n.Success) { name = n.Groups[1].Value.Trim().Trim('"', '\''); continue; }

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
        return (title, description, name, tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    // Заголовок read-only заметки (память Claude): у файлов памяти нет title:, а имя
    // файла — английский слаг. Берём читаемый description (обрезая), иначе name/имя файла.
    internal static string ReadonlyTitle(string? description, string? name, string fileName)
    {
        var d = description?.Trim();
        if (!string.IsNullOrEmpty(d))
        {
            if (d.Length > 70)
            {
                var cut = d[..70];
                var sp = cut.LastIndexOf(' ');
                d = (sp > 35 ? cut[..sp] : cut).TrimEnd() + "…";
            }
            return d;
        }
        return string.IsNullOrWhiteSpace(name) ? fileName : name!.Trim();
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
        // id заметки -> несвязанные упоминания (заголовки других заметок в тексте без [[…]])
        public required Dictionary<string, List<NoteBacklinkDto>> Unlinked;
        // нормализованное имя -> заметки (для публичного резолва [[имени]])
        public required Dictionary<string, List<RawNote>> ByName;
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
                var ns = segments.Length > 1 ? string.Join('/', segments[..^1]).Trim() : null;
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

        // Несвязанные упоминания: заголовок другой заметки встречается в тексте как
        // отдельное слово, но резолвленной ссылки на неё нет. Заголовки < 3 символов
        // не триггерим (шум). O(n²) по числу заметок — приемлемо для базы знаний.
        var unlinked = new Dictionary<string, List<NoteBacklinkDto>>();
        foreach (var n in notes)
        {
            var linked = new HashSet<string>(outLinks[n.Id].Where(l => l.Resolved).Select(l => l.TargetId));
            var lower = n.Content.ToLowerInvariant();
            foreach (var m in notes)
            {
                if (m.Id == n.Id || linked.Contains(m.Id)) continue;
                var t = Norm(m.Title);
                if (t.Length < 3) continue;
                var at = FindWord(lower, t);
                if (at < 0) continue;
                if (!unlinked.TryGetValue(n.Id, out var lst)) unlinked[n.Id] = lst = new();
                lst.Add(new NoteBacklinkDto(m.Id, m.Title, m.SourceKey, m.SourceLabel, SnippetAround(n.Content, at)));
            }
        }

        return new Model
        {
            Notes = notes, ById = byId, OutLinks = outLinks,
            Backlinks = backlinks, Ghosts = ghosts, Edges = edges, Unlinked = unlinked,
            ByName = byName,
        };
    }

    // Индекс вхождения needle как отдельного слова (границы — не буквы/цифры), иначе -1
    private static int FindWord(string haystack, string needle)
    {
        var from = 0;
        while (true)
        {
            var i = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (i < 0) return -1;
            var before = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            var afterIdx = i + needle.Length;
            var after = afterIdx >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIdx]);
            if (before && after) return i;
            from = i + 1;
        }
    }

    // Разрешение коллизий имён: 1 кандидат — берём; несколько — по namespace
    // (источник в записи [[Проект/Заметка]]), иначе предпочитаем тот же источник.
    // Разрешение [[Имя]] и [[Префикс/Имя]]. Префикс — источник («Проект»), папка
    // внутри источника («Идеи/Черновики», как в Obsidian) или «Источник/Папка».
    private static RawNote? Resolve(Dictionary<string, List<RawNote>> byName, string key, string? ns, RawNote? from)
    {
        if (!byName.TryGetValue(key, out var candidates) || candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        if (ns is not null)
        {
            var p = Norm(ns);
            var byNs = candidates.FirstOrDefault(c =>
            {
                var dir = Norm(DirOf(c.RelPath));
                var label = Norm(c.SourceLabel);
                return label == p || c.SourceKey == ns
                    || dir == p || dir.EndsWith("/" + p, StringComparison.Ordinal)
                    || (dir.Length > 0 && label + "/" + dir == p);
            });
            if (byNs is not null) return byNs;
        }
        var sameSource = from is null ? null : candidates.FirstOrDefault(c => c.SourceKey == from.SourceKey);
        return sameSource; // null → неоднозначно, считаем неразрешённой
    }

    // Папка относительного пути ("Идеи/Черновик.md" → "Идеи"; корень → "")
    private static string DirOf(string relPath)
    {
        var i = relPath.LastIndexOf('/');
        return i < 0 ? "" : relPath[..i];
    }

    // Публичный резолв заметки по имени [[X]] / [[Проект/X]] (+ фрагмент по якорю
    // #Заголовок или #^blockid) — для hover-preview и embed-вставок.
    public (NoteDetail Note, string? Fragment)? ResolveByName(string userId, string name, string? anchor)
    {
        var model = GetModel(userId);
        var segments = name.Split('/');
        var shortName = segments[^1].Split('#')[0].Trim();
        var ns = segments.Length > 1 ? string.Join('/', segments[..^1]).Trim() : null;
        var raw = Resolve(model.ByName, Norm(shortName), ns, null);
        if (raw is null) return null;
        var fragment = !string.IsNullOrWhiteSpace(anchor) ? ExtractFragment(raw.Content, anchor!) : null;
        return (ToDetail(model, raw), fragment);
    }

    // Фрагмент по якорю: "^id" — параграф с блочной меткой ^id;
    // иначе — секция markdown от заголовка до следующего того же/высшего уровня.
    internal static string? ExtractFragment(string content, string anchor)
    {
        anchor = anchor.TrimStart('#').Trim();
        if (anchor.StartsWith('^'))
        {
            var id = Regex.Escape(anchor[1..].Trim());
            foreach (var p in content.Split("\n\n"))
                if (Regex.IsMatch(p, $@"\^{id}\s*$", RegexOptions.Multiline))
                    return p.Trim();
            return null;
        }

        var lines = content.Split('\n');
        var norm = Norm(anchor);
        int start = -1, level = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^(#{1,6})\s+(.+?)\s*#*\s*$");
            if (!m.Success) continue;
            if (start < 0)
            {
                if (Norm(m.Groups[2].Value) == norm) { start = i; level = m.Groups[1].Length; }
            }
            else if (m.Groups[1].Length <= level)
                return string.Join('\n', lines[start..i]).Trim();
        }
        return start >= 0 ? string.Join('\n', lines[start..]).Trim() : null;
    }

    // Абсолютный путь вложения (картинки и т.п.) внутри vault источника — для отдачи
    // в <img>; владение источником проверяет ResolveRoot, traversal — SafeJoin.
    public string ResolveAttachmentPath(string userId, string sourceKey, string relativePath) =>
        FileService.SafeJoinPublic(ResolveRoot(userId, sourceKey, forWrite: false), relativePath);

    // --- Публичное API ---

    public IReadOnlyList<NoteSummary> GetSummaries(string userId, string? source, string? query)
    {
        var notes = GetModel(userId).Notes;
        IEnumerable<RawNote> q = notes;
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(n => n.SourceKey == source);
        if (!string.IsNullOrWhiteSpace(query))
        {
            // Операторы в запросе: tag:идея source:Личный — остальное полнотекст
            var (tags, sources, text) = ParseQuery(query);
            foreach (var tag in tags)
                q = q.Where(n => n.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
            foreach (var s in sources)
                q = q.Where(n => n.SourceKey.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                                 n.SourceLabel.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (text.Length > 0)
                q = q.Where(n =>
                    n.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    n.Content.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    n.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }
        return q.OrderByDescending(n => n.UpdatedAt)
                .Select(ToSummary).ToList();
    }

    // Разбор операторов запроса: tag:x source:y (можно несколько), остаток — полнотекст
    internal static (List<string> Tags, List<string> Sources, string Text) ParseQuery(string query)
    {
        var tags = new List<string>();
        var sources = new List<string>();
        var rest = new List<string>();
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                tags.Add(token[4..].TrimStart('#'));
            else if (token.StartsWith("source:", StringComparison.OrdinalIgnoreCase) && token.Length > 7)
                sources.Add(token[7..]);
            else rest.Add(token);
        }
        return (tags, sources, string.Join(' ', rest));
    }

    public NoteDetail? GetDetail(string userId, string id)
    {
        var model = GetModel(userId);
        if (!model.ById.TryGetValue(id, out var n)) return null;
        return ToDetail(model, n);
    }

    public IReadOnlyList<NoteBacklinkDto> GetBacklinks(string userId, string id)
    {
        var model = GetModel(userId);
        return model.Backlinks.TryGetValue(id, out var bl) ? bl : Array.Empty<NoteBacklinkDto>();
    }

    public NoteGraph GetGraph(string userId)
    {
        var model = GetModel(userId);
        var degree = new Dictionary<string, int>();
        foreach (var (s, t) in model.Edges)
        {
            degree[s] = degree.GetValueOrDefault(s) + 1;
            degree[t] = degree.GetValueOrDefault(t) + 1;
        }

        var nodes = new List<NoteGraphNode>();
        foreach (var n in model.Notes)
            nodes.Add(new NoteGraphNode(n.Id, n.Title, n.SourceKey, n.SourceLabel,
                degree.GetValueOrDefault(n.Id), false, n.Tags));
        foreach (var (gid, name) in model.Ghosts)
            nodes.Add(new NoteGraphNode(gid, name, "", "", degree.GetValueOrDefault(gid), true));

        var edges = model.Edges.Select(e => new NoteGraphEdge(e.Item1, e.Item2)).ToList();
        return new NoteGraph(nodes, edges);
    }

    // Папка внутри источника: сегменты чистятся по одному, traversal исключён.
    // "Идеи/Черновики" → "Идеи/Черновики"; пусто/мусор → "" (корень).
    internal static string SanitizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        var segments = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeFileName)
            .Where(s => s.Length > 0 && s != "." && s != "..")
            .ToArray();
        return string.Join('/', segments);
    }

    public NoteDetail Create(string userId, CreateNoteRequest req)
    {
        var sourceKey = string.IsNullOrWhiteSpace(req.Source) ? PersonalKey : req.Source!;
        var rootDir = ResolveRoot(userId, sourceKey);

        var baseName = SanitizeFileName(req.Title);
        if (baseName.Length == 0) baseName = "Без названия";
        var folder = SanitizeFolder(req.Folder);
        var prefix = folder.Length > 0 ? folder + "/" : "";
        var relPath = prefix + baseName + ".md";
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Разрешаем коллизию имени файла суффиксом
        var n = 2;
        while (File.Exists(full))
        {
            relPath = $"{prefix}{baseName}-{n++}.md";
            full = FileService.SafeJoinPublic(rootDir, relPath);
        }

        var content = req.Content
            ?? (req.TemplateId is not null ? RenderTemplate(userId, req.TemplateId, req.Title) : null)
            ?? $"# {req.Title}\n";
        File.WriteAllText(full, content, new UTF8Encoding(false));

        Invalidate(userId);
        var id = EncodeId(sourceKey, NormalizeRel(relPath));
        return GetDetail(userId, id)
            ?? throw new InvalidOperationException("Заметка создана, но не читается");
    }

    // --- Шаблоны и daily notes ---

    private string TemplatesDir(string userId) =>
        Path.Combine(_dataDir, "notes", userId, "templates");

    public IReadOnlyList<NoteTemplateDto> GetTemplates(string userId)
    {
        var dir = TemplatesDir(userId);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.md")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new NoteTemplateDto(n, n))
            .ToList();
    }

    // Контент шаблона с подстановкой {{title}}, {{date}}, {{time}}; null — шаблона нет
    private string? RenderTemplate(string userId, string templateId, string title, string? date = null)
    {
        var file = Path.Combine(TemplatesDir(userId), SanitizeFileName(templateId) + ".md");
        if (!File.Exists(file)) return null;
        var text = File.ReadAllText(file, Encoding.UTF8);
        var now = DateTime.Now;
        return text
            .Replace("{{title}}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{{date}}", date ?? now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{time}}", now.ToString("HH:mm"), StringComparison.OrdinalIgnoreCase);
    }

    // Дневниковая заметка Journal/{date}.md в личном vault: get-or-create.
    // Дату присылает клиент (его таймзона); без даты — серверная локальная.
    public NoteDetail GetOrCreateDaily(string userId, string? date)
    {
        var day = string.IsNullOrWhiteSpace(date) ? DateTime.Now.ToString("yyyy-MM-dd") : date!.Trim();
        var rootDir = ResolveRoot(userId, PersonalKey);
        var rel = $"Journal/{SanitizeFileName(day)}.md";
        var full = FileService.SafeJoinPublic(rootDir, rel);
        var id = EncodeId(PersonalKey, NormalizeRel(rel));

        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var content = RenderTemplate(userId, "Daily", day, day) ?? $"# {day}\n\n";
            File.WriteAllText(full, content, new UTF8Encoding(false));
            Invalidate(userId);
        }
        return GetDetail(userId, id)
            ?? throw new InvalidOperationException("Дневниковая заметка не читается");
    }

    // «Связать» несвязанное упоминание: обернуть первое словесное вхождение заголовка
    // в [[…]]; при отличии регистра исходный текст сохраняется как алиас.
    public NoteDetail? LinkMention(string userId, string id, string targetTitle)
    {
        var model = GetModel(userId);
        if (!model.ById.TryGetValue(id, out var note)) return null;

        var content = File.ReadAllText(note.FullPath, Encoding.UTF8);
        var at = FindWord(content.ToLowerInvariant(), Norm(targetTitle));
        if (at < 0) return GetDetail(userId, id);   // упоминание уже исчезло — не ошибка

        var original = content.Substring(at, targetTitle.Length);
        var replacement = original.Equals(targetTitle, StringComparison.Ordinal)
            ? $"[[{targetTitle}]]"
            : $"[[{targetTitle}|{original}]]";
        content = content[..at] + replacement + content[(at + targetTitle.Length)..];
        File.WriteAllText(note.FullPath, content, new UTF8Encoding(false));

        Invalidate(userId);
        return GetDetail(userId, id);
    }

    // Перенос заметки: в папку и/или другой источник (личный vault ↔ notes/ проекта).
    // Ссылки [[…]] не трогаем — они резолвятся по заголовку и переезд переживают;
    // меняется только id (источник + путь).
    public NoteDetail? Move(string userId, string id, string? folder, string? targetSource = null)
    {
        var (sourceKey, relPath) = DecodeId(id);
        var rootDir = ResolveRoot(userId, sourceKey);
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        if (!File.Exists(full)) return null;

        // Целевой источник: пусто = текущий; владение проверяет ResolveRoot
        var newSource = string.IsNullOrWhiteSpace(targetSource) ? sourceKey : targetSource!;
        var newRootDir = ResolveRoot(userId, newSource);

        var fileName = Path.GetFileName(relPath);
        var target = SanitizeFolder(folder);
        var newRel = NormalizeRel(target.Length > 0 ? $"{target}/{fileName}" : fileName);
        if (newSource == sourceKey && string.Equals(newRel, NormalizeRel(relPath), StringComparison.OrdinalIgnoreCase))
            return GetDetail(userId, id);   // уже там

        var newFull = FileService.SafeJoinPublic(newRootDir, newRel);
        if (File.Exists(newFull))
            throw new InvalidOperationException($"В папке «{(target.Length > 0 ? target : "корень")}» уже есть «{fileName}»");
        Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
        try { File.Move(full, newFull); }
        catch (IOException)
        {
            // Разные тома (личный vault и проект на разных дисках) — копия + удаление
            File.Copy(full, newFull);
            File.Delete(full);
        }

        Invalidate(userId);
        return GetDetail(userId, EncodeId(newSource, newRel));
    }

    // Переименование/перенос папки целиком (Directory.Move — атомарно, вложения
    // переезжают вместе). Ссылки [[…]] не трогаем (резолв по заголовку).
    // Возвращает маппинг id заметок старый → новый (id содержит путь).
    public IReadOnlyList<MovedNoteId> MoveFolder(string userId, string sourceKey, string path, string newPath)
    {
        var rootDir = ResolveRoot(userId, sourceKey);
        var oldFolder = SanitizeFolder(path);
        var newFolder = SanitizeFolder(newPath);
        if (oldFolder.Length == 0) throw new ArgumentException("Не задана папка");
        if (newFolder.Length == 0) throw new ArgumentException("Не задан новый путь");
        if (newFolder.Equals(oldFolder, StringComparison.OrdinalIgnoreCase))
            return [];
        if (newFolder.StartsWith(oldFolder + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нельзя перенести папку внутрь самой себя");

        var oldFull = FileService.SafeJoinPublic(rootDir, oldFolder);
        var newFull = FileService.SafeJoinPublic(rootDir, newFolder);
        if (!Directory.Exists(oldFull)) throw new KeyNotFoundException("Папка не найдена");
        if (Directory.Exists(newFull) || File.Exists(newFull))
            throw new InvalidOperationException($"«{newFolder}» уже существует");

        // Заметки папки до переезда — для маппинга id
        var before = GetModel(userId).Notes
            .Where(n => n.SourceKey == sourceKey &&
                        (n.RelPath.StartsWith(oldFolder + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.RelPath).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
        Directory.Move(oldFull, newFull);
        Invalidate(userId);

        return before.Select(rel =>
        {
            var newRel = newFolder + rel[oldFolder.Length..];
            return new MovedNoteId(EncodeId(sourceKey, rel), EncodeId(sourceKey, newRel));
        }).ToList();
    }

    public NoteDetail? Update(string userId, string id, UpdateNoteRequest req)
    {
        var (sourceKey, relPath) = DecodeId(id);
        var rootDir = ResolveRoot(userId, sourceKey);
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        if (!File.Exists(full)) return null;

        // Заголовок, по которому на заметку ссылаются сейчас (для авто-обновления ссылок)
        var model = GetModel(userId);
        var oldTitle = model.ById.TryGetValue(id, out var cur) ? cur.Title : Path.GetFileNameWithoutExtension(full);
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

        // Авто-обновление входящих ссылок при смене заголовка: во всех заметках,
        // ссылавшихся на старый заголовок, заменяем [[Старый]] → [[Новый]] (как Obsidian).
        if (!string.IsNullOrWhiteSpace(req.Title) && Norm(req.Title!) != Norm(oldTitle)
            && model.Backlinks.TryGetValue(id, out var inbound))
        {
            var sources = inbound.Select(b => b.SourceId).Distinct();
            foreach (var srcId in sources)
            {
                if (srcId == id || !model.ById.TryGetValue(srcId, out var srcNote)) continue;
                try
                {
                    var updated = RewriteLinks(File.ReadAllText(srcNote.FullPath, Encoding.UTF8), oldTitle, req.Title!);
                    File.WriteAllText(srcNote.FullPath, updated, new UTF8Encoding(false));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Обновление ссылок в {File}", srcNote.FullPath); }
            }
        }

        Invalidate(userId);
        return GetDetail(userId, effectiveId);
    }

    // Заменяет [[Старый]] / [[Старый|подпись]] / [[Старый#якорь]] / [[Папка/Старый]]
    // на [[Новый…]] с сохранением подписи и якоря; не-совпадающие ссылки не трогает.
    private static string RewriteLinks(string content, string oldTitle, string newTitle)
    {
        var oldNorm = Norm(oldTitle);
        return WikiLink.Replace(content, m =>
        {
            var inner = m.Groups[1].Value;
            var cut = inner.Length;
            var pipe = inner.IndexOf('|'); if (pipe >= 0) cut = Math.Min(cut, pipe);
            var hash = inner.IndexOf('#'); if (hash >= 0) cut = Math.Min(cut, hash);
            var name = inner[..cut];
            var rest = inner[cut..];               // подпись/якорь сохраняем
            var lastSeg = name.Split('/')[^1].Trim();
            return Norm(lastSeg) == oldNorm ? $"[[{newTitle}{rest}]]" : m.Value;
        });
    }

    public bool Delete(string userId, string id)
    {
        var (sourceKey, relPath) = DecodeId(id);
        var rootDir = ResolveRoot(userId, sourceKey);
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        if (!File.Exists(full)) return false;
        File.Delete(full);
        Invalidate(userId);
        return true;
    }

    // --- Проекции ---

    private NoteSummary ToSummary(RawNote n) =>
        new(n.Id, n.Title, n.SourceKey, n.SourceLabel, n.RelPath, n.Tags, n.CreatedAt, n.UpdatedAt);

    private NoteDetail ToDetail(Model model, RawNote n) =>
        new(n.Id, n.Title, n.SourceKey, n.SourceLabel, n.RelPath, n.Content, n.Tags,
            model.OutLinks.GetValueOrDefault(n.Id) ?? new(),
            model.Backlinks.GetValueOrDefault(n.Id) ?? new(),
            model.Unlinked.GetValueOrDefault(n.Id) ?? new(),
            n.CreatedAt, n.UpdatedAt);

    // --- Разрешение источника и валидация владения ---

    private string ResolveRoot(string userId, string sourceKey, bool forWrite = true)
    {
        if (sourceKey == PersonalKey)
            return Path.Combine(_dataDir, "notes", userId);

        // Память Claude Code — только чтение (вложения-картинки); запись запрещена
        if (sourceKey.StartsWith(MemoryPrefix, StringComparison.Ordinal))
        {
            if (forWrite)
                throw new UnauthorizedAccessException("Источник «Память Claude» доступен только для чтения");
            var projectId = sourceKey[MemoryPrefix.Length..];
            var proj = _projects.GetById(projectId)
                ?? throw new KeyNotFoundException($"Источник {sourceKey} не найден");
            if (proj.OwnerId != userId)
                throw new UnauthorizedAccessException("Проект не принадлежит пользователю");
            if (string.IsNullOrWhiteSpace(proj.RootPath))
                throw new InvalidOperationException("У проекта нет корневой папки");
            return ProjectMemoryDir(proj.RootPath);
        }

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
