using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Документ изменился между выделением и созданием — verify-guard отказал (наружу 409)
public sealed class AnnotationConflictException(string message) : InvalidOperationException(message);

// Комментарии к MD-документам (флаг doc-annotations): создание с посимвольной
// верификацией выделения, вставка блочного якоря ^id (только в источниках заметок),
// каскадный резолвер привязки и смена статуса. Сам комментарий — обычная .md-заметка
// с frontmatter annotates/anchor_quote/anchor_heading/status; весь стек заметок
// (поиск, теги, backlinks, realtime) переиспользуется как есть.
public sealed partial class NotesService
{
    private const string AnnotationsFolder = "Комментарии";
    private const int MinQuoteAnchor = 24;      // короче — цитата не годится в якорь
    private const int MaxQuoteStored = 300;

    private static readonly Regex BlockIdAtEol = new(@"[ \t]\^([A-Za-z0-9-]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    // --- Резолв документа-цели ---

    // Документ: scope=personal → путь в личном vault; scope=projectId → путь от корня
    // проекта (любые .md). ^id пишем только туда, где живут заметки (vault и notes/):
    // в прочих файлах проекта чужой git-шум недопустим.
    internal (string FullPath, bool CanWriteBlockId) ResolveDoc(string userId, string scope, string relPath)
    {
        var rel = NormalizeRel(relPath);
        if (!rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
            !rel.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Комментировать можно только markdown-документы");

        if (scope == PersonalKey)
            return (FileService.SafeJoinPublic(Path.Combine(_dataDir, "notes", userId), rel), true);

        var project = _projects.GetById(scope)
            ?? throw new KeyNotFoundException($"Проект {scope} не найден");
        if (project.OwnerId != userId)
            throw new UnauthorizedAccessException("Проект не принадлежит пользователю");
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("У проекта нет корневой папки");
        var canWrite = rel.StartsWith("notes/", StringComparison.OrdinalIgnoreCase);
        return (FileService.SafeJoinPublic(project.RootPath, rel), canWrite);
    }

    // --- Создание комментария ---

    public NoteDetail Annotate(string userId, AnnotateRequest req)
    {
        var scope = req.Doc.Scope.Trim();
        var relPath = NormalizeRel(req.Doc.Path);
        var (docFull, canWriteId) = ResolveDoc(userId, scope, relPath);
        if (!File.Exists(docFull)) throw new KeyNotFoundException("Документ не найден");

        var anchor = ResolveSelectionAnchor(docFull, canWriteId, req.Selection);

        // Заметка-комментарий: источник = область документа, папка «Комментарии»
        var sourceKey = scope;                       // personal | projectId — совпадает с ключом источника
        var title = !string.IsNullOrWhiteSpace(req.Title) ? req.Title!.Trim() : AutoTitle(req.Comment, relPath);
        var annotates = $"{scope}/{relPath}" + (anchor.BlockId is not null ? $"#^{anchor.BlockId}" : "");
        var content = BuildAnnotationContent(title, annotates, anchor.Quote, anchor.Heading,
            req.Tags, req.Selection.Text ?? "", req.Comment);
        var note = Create(userId, new CreateNoteRequest(title, content, sourceKey, Folder: AnnotationsFolder));

        Invalidate(userId);   // документ мог получить ^id — блочный якорь виден сразу
        return note;
    }

    private sealed record SelectionAnchor(string? BlockId, string? Heading, string Quote);

    // Резолв выделения в якорь: verify-before-write (посимвольная сверка, иначе
    // единственное вхождение; провал → 409 без порчи), якорный параграф, ^id
    // (существующий переиспользуется; новый пишется только в источниках заметок),
    // путь заголовков и нормализованная цитата. Общее ядро Annotate и Repin.
    private SelectionAnchor ResolveSelectionAnchor(string docFull, bool canWriteId, AnnotateSelection selection)
    {
        var doc = File.ReadAllText(docFull, Encoding.UTF8);
        var selText = selection.Text ?? "";
        if (selText.Trim().Length < 3)
            throw new ArgumentException("Выделение слишком короткое");

        var start = selection.Start;
        var ok = start >= 0 && selection.End > start && selection.End <= doc.Length
                 && string.CompareOrdinal(doc, start, selText, 0, selText.Length) == 0
                 && selection.End - start == selText.Length;
        if (!ok)
        {
            var first = doc.IndexOf(selText, StringComparison.Ordinal);
            if (first >= 0 && doc.IndexOf(selText, first + 1, StringComparison.Ordinal) < 0)
                start = first;
            else
            {
                // Выделение из рендера ≠ источник: переносы строк абзаца становятся
                // пробелами (wrap/CRLF), инлайн-разметка (**жирный**, `код`, [ссылки](url),
                // [[вики|подписи]]) в DOM не видна. Третья ступень — канонизация «как
                // рендер» с картой офсетов, единственное вхождение; selText заменяется
                // РЕАЛЬНЫМ срезом документа. Провал → честный 409.
                var (norm, map) = NormalizeForMatch(doc);
                var q = NormalizeForMatch(selText).Norm;
                var idx = q.Length >= 3 ? norm.IndexOf(q, StringComparison.Ordinal) : -1;
                if (idx >= 0 && norm.IndexOf(q, idx + 1, StringComparison.Ordinal) < 0)
                {
                    start = map[idx];
                    var rawEnd = map[Math.Min(idx + q.Length - 1, map.Count - 1)] + 1;
                    selText = doc[start..rawEnd];
                }
                else
                    throw new AnnotationConflictException("Документ изменился — выделите фрагмент заново");
            }
        }

        // Якорный блок: параграф (непрерывные непустые строки), содержащий начало выделения
        var lines = SplitLinesWithOffsets(doc);
        var selLine = LineAt(lines, start);
        var (_, blockLast) = BlockBounds(lines, selLine);

        // ^id: в конце последней строки блока; существующий переиспользуем
        string? blockId = null;
        var lastLineText = lines[blockLast].Text;
        var existing = BlockIdAtEol.Match(lastLineText);
        if (existing.Success) blockId = existing.Groups[1].Value;
        else if (canWriteId)
        {
            blockId = Guid.NewGuid().ToString("N")[..6];
            var insertAt = lines[blockLast].Start + lastLineText.TrimEnd('\r').Length;
            doc = doc[..insertAt] + " ^" + blockId + doc[insertAt..];
            File.WriteAllText(docFull, doc, new UTF8Encoding(false));
        }

        var heading = HeadingPathAbove(lines, selLine);
        var quote = NormalizeWs(selText);
        if (quote.Length > MaxQuoteStored) quote = quote[..MaxQuoteStored];
        return new SelectionAnchor(blockId, heading, quote);
    }

    // Перепривязка комментария к новому выделению («место изменилось»/«сирота» при
    // живом документе): пересчитывает ^id/заголовки/цитату, статус и тред не трогает.
    public NoteDetail RepinAnnotation(string userId, string id, AnnotateSelection selection)
    {
        var model = GetModel(userId);
        if (!model.ById.TryGetValue(id, out var note))
            throw new KeyNotFoundException("Комментарий не найден");
        var ann = note.Annotation
            ?? throw new InvalidOperationException("Заметка не является комментарием к документу");
        if (ann.IsReply)
            throw new InvalidOperationException("Перепривязывается корневой комментарий, а не ответ");

        var (docFull, canWriteId) = ResolveDoc(userId, ann.DocScope, ann.DocPath);
        if (!File.Exists(docFull)) throw new KeyNotFoundException("Документ не найден");
        var anchor = ResolveSelectionAnchor(docFull, canWriteId, selection);

        var annotates = $"{ann.DocScope}/{ann.DocPath}" + (anchor.BlockId is not null ? $"#^{anchor.BlockId}" : "");
        var content = File.ReadAllText(note.FullPath, Encoding.UTF8);
        content = SetFrontmatterField(content, "annotates", $"\"{Escape(annotates)}\"");
        content = SetFrontmatterField(content, "anchor_quote", $"\"{Escape(anchor.Quote)}\"");
        if (!string.IsNullOrWhiteSpace(anchor.Heading))
            content = SetFrontmatterField(content, "anchor_heading", $"\"{Escape(anchor.Heading!)}\"");
        File.WriteAllText(note.FullPath, content, new UTF8Encoding(false));

        Invalidate(userId);
        return GetDetail(userId, id)
            ?? throw new InvalidOperationException("Комментарий перепривязан, но не читается");
    }

    private static string AutoTitle(string? comment, string relPath)
    {
        var c = NormalizeWs(comment ?? "");
        if (c.Length >= 3)
            return c.Length > 50 ? c[..50].TrimEnd() + "…" : c;
        return $"Комментарий — {Path.GetFileNameWithoutExtension(relPath)}";
    }

    private static string BuildAnnotationContent(string title, string annotates, string quote,
        string? heading, IReadOnlyList<string>? tags, string selText, string? comment)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"title: {Escape(title)}\n");
        sb.Append($"annotates: \"{Escape(annotates)}\"\n");
        sb.Append($"anchor_quote: \"{Escape(quote)}\"\n");
        if (!string.IsNullOrWhiteSpace(heading))
            sb.Append($"anchor_heading: \"{Escape(heading!)}\"\n");
        sb.Append("status: open\n");
        var clean = (tags ?? []).Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Distinct().ToList();
        if (clean.Count > 0)
            sb.Append($"tags: [{string.Join(", ", clean)}]\n");
        sb.Append("---\n\n");
        foreach (var line in selText.Replace("\r", "").Split('\n'))
            sb.Append("> ").Append(line).Append('\n');
        sb.Append('\n');
        if (!string.IsNullOrWhiteSpace(comment))
            sb.Append(comment!.Trim()).Append('\n');
        return sb.ToString();
    }

    // Кавычки внутри значений frontmatter ломают наш простой парсер — заменяем
    private static string Escape(string s) => s.Replace('"', '\'').Replace("\n", " ");

    // Derived-поля комментариев при построении модели (не персистятся):
    // IsReply — annotates указывает на другую заметку-комментарий (тред);
    // DocMissing — документ-цель удалён (для ghost-узла в дереве).
    private void ComputeAnnotationDerived(string userId, List<RawNote> notes)
    {
        // Ключи целей — канонический документный путь + легаси-вариант без notes/
        var annPaths = new Dictionary<(string Scope, string Path), RawNote>();
        foreach (var n in notes)
            if (n.Annotation is not null)
            {
                annPaths[(n.SourceKey, DocPathOf(n.SourceKey, n.RelPath).ToLowerInvariant())] = n;
                annPaths[(n.SourceKey, n.RelPath.ToLowerInvariant())] = n;
            }
        if (annPaths.Count == 0) return;

        var docExists = new Dictionary<(string, string), bool>();
        foreach (var n in notes)
        {
            var a = n.Annotation;
            if (a is null) continue;
            var key = (a.DocScope, a.DocPath.ToLowerInvariant());
            var isReply = annPaths.ContainsKey(key);
            if (!docExists.TryGetValue(key, out var exists))
            {
                try { exists = File.Exists(ResolveDoc(userId, a.DocScope, a.DocPath).FullPath); }
                catch { exists = false; }
                docExists[key] = exists;
            }
            if (isReply || !exists)
                n.Annotation = a with { IsReply = isReply, DocMissing = !exists };
        }
    }

    // --- Треды: ответы на комментарий (реплика = заметка с annotates на корневую) ---

    public NoteDetail Reply(string userId, string rootId, ReplyRequest req)
    {
        var model = GetModel(userId);
        if (!model.ById.TryGetValue(rootId, out var root))
            throw new KeyNotFoundException("Комментарий не найден");
        if (root.Annotation is null)
            throw new InvalidOperationException("Отвечать можно только на комментарий к документу");
        if (root.Annotation.IsReply)
            throw new InvalidOperationException("Ответить можно только на корневой комментарий (тред плоский)");
        var comment = req.Comment?.Trim() ?? "";
        if (comment.Length == 0) throw new ArgumentException("Пустой ответ");

        var norm = NormalizeWs(comment);
        var title = norm.Length >= 3
            ? (norm.Length > 50 ? norm[..50].TrimEnd() + "…" : norm)
            : $"Ответ — {root.Title}";
        // Канонический документный путь (у проектов — от корня, с notes/): единый
        // формат annotates для комментариев и ответов
        var annotates = $"{root.SourceKey}/{DocPathOf(root.SourceKey, root.RelPath)}";

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"title: {Escape(title)}\n");
        sb.Append($"annotates: \"{Escape(annotates)}\"\n");
        var clean = (req.Tags ?? []).Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Distinct().ToList();
        if (clean.Count > 0) sb.Append($"tags: [{string.Join(", ", clean)}]\n");
        sb.Append("---\n\n").Append(comment).Append('\n');

        return Create(userId, new CreateNoteRequest(title, sb.ToString(), root.SourceKey, Folder: AnnotationsFolder));
    }

    public IReadOnlyList<NoteReplyDto> GetReplies(string userId, string rootId)
    {
        var model = GetModel(userId);
        if (!model.ById.TryGetValue(rootId, out var root))
            throw new KeyNotFoundException("Комментарий не найден");
        var canonical = DocPathOf(root.SourceKey, root.RelPath);
        return model.Notes
            .Where(r => r.Annotation is { IsReply: true } a &&
                        a.DocScope.Equals(root.SourceKey, StringComparison.Ordinal) &&
                        (a.DocPath.Equals(canonical, StringComparison.OrdinalIgnoreCase) ||
                         a.DocPath.Equals(root.RelPath, StringComparison.OrdinalIgnoreCase)))   // легаси без notes/
            .OrderBy(r => r.CreatedAt, StringComparer.Ordinal)
            .Select(r => new NoteReplyDto(r.Id, r.Title, ExtractExcerpt(r.Content), r.CreatedAt, r.Tags))
            .ToList();
    }

    // --- Миграция привязки при переносе/переименовании документа ---

    // Документный путь заметки: для проектов annotates ссылается на путь ОТ КОРНЯ
    // проекта (заметки живут в notes/), для личного vault — путь внутри vault.
    internal static string DocPathOf(string sourceKey, string relPath) =>
        sourceKey == PersonalKey ? relPath : "notes/" + relPath;

    // Переписывает annotates всех комментариев/ответов со старого пути документа на
    // новый (точечно или по префиксу — для переноса папки); ^id сохраняется.
    public int RewriteAnnotationTargets(string userId, string oldScope, string oldPath,
        string newScope, string newPath, bool prefix = false)
    {
        var oldRel = NormalizeRel(oldPath);
        var newRel = NormalizeRel(newPath);
        if (oldScope == newScope && oldRel.Equals(newRel, StringComparison.OrdinalIgnoreCase)) return 0;
        var changed = 0;
        foreach (var n in GetModel(userId).Notes)
        {
            var a = n.Annotation;
            if (a is null || !a.DocScope.Equals(oldScope, StringComparison.Ordinal)) continue;
            string? updatedPath = null;
            if (a.DocPath.Equals(oldRel, StringComparison.OrdinalIgnoreCase)) updatedPath = newRel;
            else if (prefix && a.DocPath.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase))
                updatedPath = newRel + a.DocPath[oldRel.Length..];
            if (updatedPath is null) continue;

            var annotates = $"{newScope}/{updatedPath}" + (a.BlockId is not null ? $"#^{a.BlockId}" : "");
            try
            {
                var content = File.ReadAllText(n.FullPath, Encoding.UTF8);
                File.WriteAllText(n.FullPath, SetFrontmatterField(content, "annotates", $"\"{Escape(annotates)}\""),
                    new UTF8Encoding(false));
                changed++;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Перепись annotates в {File}", n.FullPath); }
        }
        if (changed > 0) Invalidate(userId);
        return changed;
    }

    // --- Комментарии документа с резолвом привязки ---

    public IReadOnlyList<DocAnnotationDto> GetDocAnnotations(string userId, string scope, string relPath)
    {
        var rel = NormalizeRel(relPath);
        string? doc = null;
        try
        {
            var (docFull, _) = ResolveDoc(userId, scope, rel);
            if (File.Exists(docFull)) doc = File.ReadAllText(docFull, Encoding.UTF8);
        }
        catch (ArgumentException) { /* не-markdown — комментариев нет */ }

        var model = GetModel(userId);
        // Ответы тредов: считаем по цели (пути корневой заметки-комментария)
        var replyCounts = new Dictionary<(string, string), int>();
        foreach (var r in model.Notes)
            if (r.Annotation is { IsReply: true } ra)
            {
                var k = (ra.DocScope, ra.DocPath.ToLowerInvariant());
                replyCounts[k] = replyCounts.GetValueOrDefault(k) + 1;
            }

        var result = new List<DocAnnotationDto>();
        foreach (var n in model.Notes)
        {
            var a = n.Annotation;
            if (a is null || a.IsReply || !a.DocScope.Equals(scope, StringComparison.Ordinal) ||
                !a.DocPath.Equals(rel, StringComparison.OrdinalIgnoreCase)) continue;
            var (state, s, e) = ResolveAnchor(doc, a);
            var replies = replyCounts.GetValueOrDefault((n.SourceKey, n.RelPath.ToLowerInvariant()));
            result.Add(new DocAnnotationDto(
                n.Id, n.Title, a.Status, state, s, e, a.BlockId, a.AnchorHeading,
                a.AnchorQuote ?? "", ExtractExcerpt(n.Content), n.Tags, n.UpdatedAt, replies));
        }
        return result.OrderBy(x => x.Start < 0 ? int.MaxValue : x.Start).ThenBy(x => x.Title).ToList();
    }

    // Каскад: ^blockid → путь заголовков → дословная цитата → сирота.
    // Возвращает derived-состояние и офсеты якорного блока для подсветки.
    internal static (string State, int Start, int End) ResolveAnchor(string? doc, NoteAnnotationInfo a)
    {
        if (doc is null) return ("orphan", -1, -1);
        var lines = SplitLinesWithOffsets(doc);

        // 1) Блочный якорь ^id (кэш) — точная привязка
        if (!string.IsNullOrEmpty(a.BlockId))
        {
            var re = new Regex(@"[ \t]\^" + Regex.Escape(a.BlockId!) + @"\s*$");
            for (var i = 0; i < lines.Count; i++)
                if (re.IsMatch(lines[i].Text))
                {
                    var (f, l) = BlockBounds(lines, i);
                    return ("exact", lines[f].Start, LineEnd(lines[l]));
                }
        }

        // 2) Дословная цитата (нормализованный whitespace, единственное вхождение)
        if (!string.IsNullOrEmpty(a.AnchorQuote) && a.AnchorQuote!.Length >= MinQuoteAnchor)
        {
            var (norm, map) = NormalizeWithMap(doc);
            var q = NormalizeWs(a.AnchorQuote!);
            var first = norm.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (first >= 0 && norm.IndexOf(q, first + 1, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var rawStart = map[first];
                var rawEnd = map[Math.Min(first + q.Length - 1, map.Count - 1)] + 1;
                var line = LineAt(lines, rawStart);
                var (f, l) = BlockBounds(lines, line);
                return ("exact", lines[f].Start, Math.Max(LineEnd(lines[l]), rawEnd));
            }
        }

        // 3) Путь заголовков: жив последний заголовок пути → раздел найден, но место изменилось
        if (!string.IsNullOrWhiteSpace(a.AnchorHeading))
        {
            var lastSeg = a.AnchorHeading!.Split('›')[^1].Trim();
            if (lastSeg.Length > 0)
                for (var i = 0; i < lines.Count; i++)
                {
                    var m = HeadingLine.Match(lines[i].Text);
                    if (m.Success && Norm(m.Groups[2].Value) == Norm(lastSeg))
                        return ("changed", lines[i].Start, LineEnd(lines[i]));
                }
        }

        return ("orphan", -1, -1);
    }

    // Комментарий-сирота? (для фильтра status:orphaned в списке заметок)
    private bool IsAnnotationOrphan(string userId, RawNote n)
    {
        var a = n.Annotation!;
        string? doc = null;
        try
        {
            var (full, _) = ResolveDoc(userId, a.DocScope, a.DocPath);
            if (File.Exists(full)) doc = File.ReadAllText(full, Encoding.UTF8);
        }
        catch { /* область недоступна → сирота */ }
        return ResolveAnchor(doc, a).State == "orphan";
    }

    // --- Статус ---

    public NoteDetail? SetAnnotationStatus(string userId, string id, string status)
    {
        var st = status?.Trim().ToLowerInvariant();
        if (st is not ("open" or "resolved"))
            throw new ArgumentException("Статус должен быть open или resolved");

        var (sourceKey, relPath) = DecodeId(id);
        var rootDir = ResolveRoot(userId, sourceKey);
        var full = FileService.SafeJoinPublic(rootDir, relPath);
        if (!File.Exists(full)) return null;

        var content = File.ReadAllText(full, Encoding.UTF8);
        if (ParseFrontmatter(content, "").Annotation is null)
            throw new InvalidOperationException("Заметка не является комментарием к документу");
        File.WriteAllText(full, SetFrontmatterField(content, "status", st!), new UTF8Encoding(false));
        Invalidate(userId);
        return GetDetail(userId, id);
    }

    // Замена (или вставка) скалярного поля frontmatter
    internal static string SetFrontmatterField(string content, string field, string value)
    {
        if (content.StartsWith("---"))
        {
            var endIdx = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIdx > 0)
            {
                var fm = content[..endIdx];
                var re = new Regex(@"(?m)^" + Regex.Escape(field) + @":\s*[^\n]*$");
                if (re.IsMatch(fm))
                    return re.Replace(fm, $"{field}: {value}", 1) + content[endIdx..];
                return fm + $"\n{field}: {value}" + content[endIdx..];
            }
        }
        return $"---\n{field}: {value}\n---\n{content}";
    }

    // Восстановление системных полей комментария в контенте, потерявшем их (merge-защита Update)
    internal static string ReinjectAnnotationFields(string content, NoteAnnotationInfo a)
    {
        var annotates = $"{a.DocScope}/{a.DocPath}" + (a.BlockId is not null ? $"#^{a.BlockId}" : "");
        var result = SetFrontmatterField(content, "annotates", $"\"{Escape(annotates)}\"");
        if (!string.IsNullOrEmpty(a.AnchorQuote))
            result = SetFrontmatterField(result, "anchor_quote", $"\"{Escape(a.AnchorQuote!)}\"");
        if (!string.IsNullOrEmpty(a.AnchorHeading))
            result = SetFrontmatterField(result, "anchor_heading", $"\"{Escape(a.AnchorHeading!)}\"");
        result = SetFrontmatterField(result, "status", a.Status);
        return result;
    }

    // --- Текстовые утилиты ---

    private readonly record struct DocLine(int Start, string Text);

    private static List<DocLine> SplitLinesWithOffsets(string doc)
    {
        var lines = new List<DocLine>();
        var pos = 0;
        while (pos <= doc.Length)
        {
            var nl = doc.IndexOf('\n', pos);
            if (nl < 0) { lines.Add(new DocLine(pos, doc[pos..])); break; }
            lines.Add(new DocLine(pos, doc[pos..nl]));
            pos = nl + 1;
        }
        return lines;
    }

    private static int LineEnd(DocLine l) => l.Start + l.Text.Length;

    private static int LineAt(List<DocLine> lines, int offset)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Start <= offset) return i;
        return 0;
    }

    // Границы параграфа: непрерывные непустые строки вокруг line
    private static (int First, int Last) BlockBounds(List<DocLine> lines, int line)
    {
        bool Empty(int i) => lines[i].Text.Trim().Length == 0;
        if (Empty(line)) return (line, line);
        var first = line; var last = line;
        while (first > 0 && !Empty(first - 1)) first--;
        while (last < lines.Count - 1 && !Empty(last + 1)) last++;
        return (first, last);
    }

    // Путь заголовков над строкой: «H1 › H2 › H3» (ближайшие заголовки каждого уровня)
    private static string? HeadingPathAbove(List<DocLine> lines, int line)
    {
        var stack = new List<(int Level, string Text)>();
        for (var i = 0; i <= Math.Min(line, lines.Count - 1); i++)
        {
            var m = HeadingLine.Match(lines[i].Text);
            if (!m.Success) continue;
            var level = m.Groups[1].Length;
            while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
            stack.Add((level, m.Groups[2].Value.Trim()));
        }
        return stack.Count == 0 ? null : string.Join(" › ", stack.Select(s => s.Text));
    }

    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    internal static string NormalizeWs(string s) => Ws.Replace(s, " ").Trim();

    // Канонизация текста «как рендер» — для сверки DOM-выделения с источником:
    // markdown-ссылки и вики-ссылки заменяются видимым текстом, маркеры эмфазиса/кода
    // выбрасываются, whitespace схлопывается. Карта — норм-индекс → исходный офсет.
    private static readonly Regex RenderLinks = new(
        @"\[\[(?<t>[^\]|]+)(?:\|(?<l>[^\]]+))?\]\]|!?\[(?<x>[^\]]*)\]\((?<u>[^)\s]*)\)",
        RegexOptions.Compiled);
    private static readonly char[] MarkupChars = ['*', '`', '_', '~', '\\'];

    internal static (string Norm, List<int> Map) NormalizeForMatch(string doc)
    {
        // Шаг 1: ссылки → видимый текст (позиции символов подписи — исходные)
        var buf = new StringBuilder(doc.Length);
        var map1 = new List<int>(doc.Length);
        var last = 0;
        foreach (Match m in RenderLinks.Matches(doc))
        {
            for (var i = last; i < m.Index; i++) { buf.Append(doc[i]); map1.Add(i); }
            var g = m.Groups["l"].Success ? m.Groups["l"] : m.Groups["t"].Success ? m.Groups["t"] : m.Groups["x"];
            for (var i = 0; i < g.Length; i++) { buf.Append(doc[g.Index + i]); map1.Add(g.Index + i); }
            last = m.Index + m.Length;
        }
        for (var i = last; i < doc.Length; i++) { buf.Append(doc[i]); map1.Add(i); }

        // Шаг 2: whitespace collapse + дроп маркеров эмфазиса/кода (симметрично для
        // обеих сторон сверки — подчёркивания в идентификаторах матчу не мешают)
        var sb = new StringBuilder(buf.Length);
        var map = new List<int>(buf.Length);
        var pendingSpace = false;
        for (var i = 0; i < buf.Length; i++)
        {
            var ch = buf[i];
            if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
            if (Array.IndexOf(MarkupChars, ch) >= 0) continue;
            if (pendingSpace) { sb.Append(' '); map.Add(map1[i]); pendingSpace = false; }
            sb.Append(ch); map.Add(map1[i]);
        }
        return (sb.ToString(), map);
    }

    // Нормализация whitespace с картой «нормализованный индекс → сырой индекс»
    internal static (string Norm, List<int> Map) NormalizeWithMap(string doc)
    {
        var sb = new StringBuilder(doc.Length);
        var map = new List<int>(doc.Length);
        var pendingSpace = false;
        for (var i = 0; i < doc.Length; i++)
        {
            var ch = doc[i];
            if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); map.Add(i - 1); pendingSpace = false; }
            sb.Append(ch); map.Add(i);
        }
        return (sb.ToString(), map);
    }

    // Первая строка тела комментария (после frontmatter, минус цитата-blockquote)
    private static string ExtractExcerpt(string content)
    {
        var body = content;
        if (body.StartsWith("---"))
        {
            var end = body.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var after = body.IndexOf('\n', end + 1);
                body = after > 0 ? body[(after + 1)..] : "";
            }
        }
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('>') || line.StartsWith('#')) continue;
            return line.Length > 160 ? line[..157] + "…" : line;
        }
        return "";
    }
}
