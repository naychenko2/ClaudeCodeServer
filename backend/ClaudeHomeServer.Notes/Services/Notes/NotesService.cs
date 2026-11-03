using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services.Notes;

// Obsidian-совместимая база заметок. Источник правды — .md файлы на диске:
//   • личный vault смотрящего:  {dataDir}/notes/{userId}/
//   • notes/ внутри каждого проекта, к которому у смотрящего есть право чтения:
//     своего или общего (участник — через точку доступа, S1-3b)
// Единый граф строится по смотрящему (Модель 3 из плана): чужой личный vault в него не
// попадает ни с одной стороны — участник не видит «Личный» владельца, владелец — его «Личный».
// Класс не хранит состояние заметок — сканирует файлы на каждый запрос
// (заметок немного; кэш-инвалидация — возможная оптимизация позже).
public sealed partial class NotesService : ClaudeHomeServer.Services.Composition.INoteSummaryReader, INoteAccessor
{
    private readonly IProjectManager _projects;
    private readonly ILogger<NotesService> _logger;
    private readonly IProjectEventLogService? _events;
    // Точка доступа к проекту-источнику (Core-контракт, S1-3b): в ней роль по членству и
    // гейт «рубильник + флаг» общих проектов. НЕ параметр конструктора: прямая зависимость
    // замыкает DI-цикл NotesService → IProjectAccess → ProjectAccessService → ISessionDirectory
    // → SessionManager → PersonaBindingsService → INoteAccessor → форвардер (NotesSubsystem.cs:71)
    // → NotesService — на повторном GetOrAdd того же синглтона StartAsync хоста виснет
    // навсегда (бисект 2026-10-29: все тесты с WebApplicationFactory). Тот же приём, что
    // с реестром тулсетов в SessionManager (SessionManager.cs:829): в конструкторе — провайдер,
    // точка доступа — лениво при первом обращении, когда создание синглона уже завершено
    // и ре-энтри нет.
    // Вне DI (тестовые конструирования) точка передаётся напрямую internal-конструктором
    // или не передаётся вовсе — ProjectScope остаётся на владении, fail-closed.
    private readonly IServiceProvider? _services;
    private IProjectAccess? _access;
    private readonly string _dataDir;
    // S3-3c1: журнал правок человека (S3-3) — зовётся из *Core после УСПЕШНОЙ записи/удаления/
    // переименования заметки общего проекта человеком (byHuman). В DI он всегда (Program.cs:621-622);
    // опциональным оставлен как остальные необязательные зависимости: бесхостовые тесты собирают
    // сервис напрямую и подставляют фейк.
    private readonly Composition.IHumanEditJournal? _humanEdits;

    private const string PersonalKey = "personal";
    private const string PersonalLabel = "Личный";

    // [[Target]] | [[Target|подпись]] | [[Target#заголовок]] | [[Папка/Target]]
    private static readonly Regex WikiLink = new(@"\[\[([^\[\]]+?)\]\]", RegexOptions.Compiled);
    // Inline-тег #тег (не заголовок «# » — требуется хотя бы один символ сразу за #)
    private static readonly Regex InlineTag = new(@"(?<=^|\s)#([\p{L}\p{N}_][\p{L}\p{N}_/-]*)", RegexOptions.Compiled);
    // Inline-код `…` (без бэктика и перевода строки внутри): как и ```-заборы, «глушит»
    // ссылки/теги внутри — пример синтаксиса в коде не связь и не тег (Obsidian)
    private static readonly Regex InlineCode = new(@"`[^`\n]+`", RegexOptions.Compiled);

    // Кэш построенной модели per-owner: сканирование всех файлов дорого, а за один
    // ход UI дёргает список/деталь/граф подряд. TTL короткий — внешние правки (Obsidian)
    // подхватятся быстро; свои мутации инвалидируют кэш явно.
    private readonly ConcurrentDictionary<string, (Model Model, DateTime At)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    // Поколение инвалидации per-owner — guard против гонки check-then-act на кэше моделей (дефект S2c-4):
    // модель, собранная сканом, кладётся в кэш только если за скан поколение не сдвинулось — т.е. ни
    // одна запись не пришла, пока список источников перечислялся. Без guard'а скан, начатый до чужой
    // записи, кладёт устаревшую модель поверх свежей: её запись попадает в окно между чужим Invalidate
    // и чужим TryGetValue (в Create это время _events.Append), пострадавший читает чужую модель,
    // не видит свою только что записанную заметку и Create падает 500 «Заметка создана, но не
    // читается». Воспроизведение — NotesConcurrentCreateTests. Себе вернутую модель guard не
    // отменяет: скан всегда идёт после своей записи, своя заметка в модели есть всегда.
    private readonly ConcurrentDictionary<string, long> _generation = new();

    // Замок записи заметок — ОДНА система на весь сервис (S2c-2 К8 → S2c-5 → S2c-11 → S2c-12).
    // Ключ — id заметки (Update/Delete — «сравнить и записать»; Move — ещё и на id перемещаемой:
    // гонка переноса с удалением той же заметки, и на целевой путь: гонка двух переносов в одну
    // цель; LinkMention/SetAnnotationStatus/Repin — правка
    // той же заметки; Annotate/Repin — ещё на id заметки-документа, куда пишется ^id) или ключ
    // источника (Create — коллизия имени файла; MoveFolder/CreateFolder/DeleteFolder — структура
    // папок источника; MoveFolder/DeleteFolder — ещё id всех заметок папки: их правка держит
    // замок своего id, а не источника — без этих ключей гонка «папка ↔ заметка внутри» не
    // сериализована, F-6a). Многозаметочные каскады (RemoveWikilinksTo, RewriteAnnotationTargets)
    // берут по замку на каждую правимую заметку, ключи — по возрастанию, одним вызовом
    // (WithWriteGates): две операции не разойдутся по кругу. Вложенного WithWriteGates нет нигде:
    // внутренний вызов каскада из-под чужого замка (Update/Move/MoveFolder →
    // RewriteAnnotationTargets) идёт нелocking-ядром под уже взятым замком вызывающего.
    // Единственная запись заметочного файла мимо замка — GetOrCreateDaily: дневниковая заметка
    // личного vault (get-or-create своего файла, чужого писателя у него нет). Замок живёт, пока
    // сервис: заметок немного; после переименования старый ключ остаётся безвредным остатком
    // (сам id исчезает вместе со старым файлом).
    private readonly ConcurrentDictionary<string, object> _writeGates = new();

    /// <summary>
    /// Тестовый шов (F-6a доработка 1): вызывается под замком id сразу после проверки файла,
    /// до его чтения и записи — точка, в которую тест ставит гонку «перенос ↔ правка»
    /// детерминированно (NotesFolderWriteGatesTests). null — обычного пути нет: поле читается
    /// один раз на правку; в проде (через DI) не выставляется никогда.
    /// </summary>
    internal Action? TestProbeAfterFileExists;

    public NotesService(IProjectManager projects, IConfiguration config, ILogger<NotesService> logger,
        IProjectEventLogService? events = null, IServiceProvider? services = null,
        // S3-3c1: журнал правок человека — опционален по той же причине, что events/services:
        // бесхостовые тесты конструируют сервис напрямую.
        Composition.IHumanEditJournal? humanEdits = null)
    {
        _projects = projects;
        _logger = logger;
        _events = events;
        _services = services;
        _humanEdits = humanEdits;
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Тестовое конструирование вне DI: точка доступа передаётся напрямую, без провайдера.
    // internal — виден только тестовым сборкам (InternalsVisibleTo); DI выбирает публичный
    // конструктор, этот для него не кандидат.
    internal NotesService(IProjectManager projects, IConfiguration config, ILogger<NotesService> logger,
        IProjectEventLogService? events, IProjectAccess access,
        Composition.IHumanEditJournal? humanEdits = null) : this(projects, config, logger, events, (IServiceProvider?)null, humanEdits)
    {
        _access = access;
    }

    private Model GetModel(string userId)
    {
        if (_cache.TryGetValue(userId, out var e) && DateTime.UtcNow - e.At < CacheTtl)
            return e.Model;
        var gen = _generation.TryGetValue(userId, out var g) ? g : 0L;
        var model = Build(userId);
        // Скан шёл без чужих записей (поколение не сдвинулось) — модель свежая, кладём её в кэш.
        // Сдвинулось — она собрана до чужой записи и в кэше перетёрла бы свежую; себе возвращаем
        // как есть: своя заметка в ней есть всегда — скан начался после своей записи.
        if ((_generation.TryGetValue(userId, out var cur) ? cur : 0L) == gen)
            _cache[userId] = (model, DateTime.UtcNow);
        return model;
    }

    private void Invalidate(string userId)
    {
        _cache.TryRemove(userId, out _);
        _generation.AddOrUpdate(userId, 1, (_, v) => v + 1);
    }

    /// <summary>
    /// Инвалидация после записи в источник (S2c-11): свой ключ — как раньше, плюс ключ
    /// владельца-арендатора проекта (RequestScope.TenantOwnerId, не вызывающий): файлы notes/
    /// проекта читает и владелец, и без его ключа его модель per-user (TTL 2 с) держала бы
    /// снимок ДО правки редактора — владелец видел бы чужую запись с задержкой до TTL.
    /// Личный vault — только свой ключ: чужого арендатора у него нет.
    /// </summary>
    private void InvalidateWrite(string userId, string sourceKey)
    {
        Invalidate(userId);
        if (sourceKey == PersonalKey) return;
        var tenant = ProjectScope(userId, sourceKey, ProjectRight.Read)?.TenantOwnerId;
        if (!string.IsNullOrEmpty(tenant)) Invalidate(tenant!);
    }

    /// <summary>
    /// Критическая секция записи под замками _writeGates по названным ключам (id заметки /
    /// ключ источника). Многоключевые операции берут их в фиксированном порядке — по возрастанию
    /// ключа: если две операции спорят за одну пару ключей, обе идут к ней в одном порядке,
    /// цикла ожидания нет. Тело выполняется под всеми взятыми замками; исключение тела
    /// отпускает замки как обычно.
    /// </summary>
    private T WithWriteGates<T>(Func<T> body, params string[] keys)
    {
        var ordered = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return Take(0);

        T Take(int i)
        {
            if (i == ordered.Length) return body();
            var gate = _writeGates.GetOrAdd(ordered[i], _ => new object());
            lock (gate) { return Take(i + 1); }
        }
    }

    // Ключ замка источника: проект — его id (тот же ключ, что в id его заметок), личный vault —
    // id владельца: «personal» один на всех, а личное ключается личным id (как корень в
    // ResolveRoot) — чужая личная папка не должна ждать чужой записи.
    private static string SourceGateKey(string userId, string sourceKey) =>
        sourceKey == PersonalKey ? userId : sourceKey;

    // --- Источники смотрящего ---

    // ProjectRoot — корень проекта-источника (null у личного vault): по нему
    // резолвится привязка заметки к файлу (frontmatter file:, путь от корня проекта)
    private sealed record Source(string Key, string Label, string RootDir, string? ProjectRoot = null);

    // Источники, к которым у вызывающего есть право <right>. Read — то, что человек видит
    // (список, граф, папки, вложения): свои проекты плюс общие, где он участник при
    // включённом гейте. Write — то, в чём он может менять файлы: в S1 это только своё
    // (участник общего проекта читает, не пишет — право на запись откроют S2a/S2c).
    private IReadOnlyList<Source> SourcesFor(string userId, ProjectRight right)
    {
        var list = new List<Source>
        {
            new(PersonalKey, PersonalLabel, Path.Combine(_dataDir, "notes", userId)),
        };
        // Заметки проекта лежат в его папке: у локального проекта она на устройстве, сервер
        // её не читает — такой источник в списке не участвует (ADR-016 §4, search_unified).
        // Право на чужой проект решает точка доступа (ProjectScope): в общем проекте участник
        // видит его заметки только при роли и включённом гейте (S1-2). Список строится по
        // вызывающему: личный vault другого человека сюда не попадает ни с одной стороны.
        foreach (var p in _projects.GetAll())
            if (!string.IsNullOrWhiteSpace(p.RootPath)
                && Composition.ProjectCapabilityGuard.Allows(p, Composition.ProjectCapabilityArea.FileBound)
                && ProjectScope(userId, p.Id, right) is not null)
                list.Add(new(p.Id, p.Name, Path.Combine(p.RootPath, "notes"), p.RootPath));
        return list;
    }

    // Источники, которые человек видит: они же подсказка «куда создать». Источник,
    // доступный только на чтение (общий проект для участника), в списке остаётся —
    // его заметки человек видит; отказ на запись придёт из Create (403), а не из исчезновения
    // источника из списка.
    public IReadOnlyList<NoteSourceDto> GetSources(string userId) =>
        SourcesFor(userId, ProjectRight.Read).Select(s => new NoteSourceDto(s.Key, s.Label)).ToList();

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
        public string? ExpiresAt;
        public string? SourceSessionId;
        public NoteAnnotationInfo? Annotation;   // непусто = заметка-комментарий к документу
        public string? FileRef;      // frontmatter file: — привязка к файлу проекта
        public bool FileMissing;     // derived: привязанный файл отсутствует на диске
    }

    private List<RawNote> Scan(string userId)
    {
        var notes = new List<RawNote>();
        foreach (var src in SourcesFor(userId, ProjectRight.Read))
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
                var fm = ParseFrontmatter(text, Path.GetFileNameWithoutExtension(full));
                var tags = fm.Tags;
                // Inline-теги #тег из тела + теги из frontmatter (без дублей).
                // Тег в коде (`#x` в `…`/```-блоке) и чисто числовой (#807) — не тег.
                var mask = CodeMask(text);
                foreach (Match m in InlineTag.Matches(text))
                {
                    if (InCode(mask, m.Index)) continue;
                    var it = m.Groups[1].Value;
                    if (it.All(char.IsDigit)) continue;
                    if (!tags.Contains(it, StringComparer.OrdinalIgnoreCase)) tags.Add(it);
                }
                var links = ParseLinks(text, mask);
                // Привязка к файлу проекта: у личного vault поля нет (ProjectRoot null),
                // у проектной проверяем существование файла (аналог docMissing у комментариев).
                // SafeJoinPublic отсекает traversal — мусорный путь считается отсутствующим.
                var fileRef = src.ProjectRoot is null ? null : fm.File;
                var fileMissing = false;
                if (fileRef is not null)
                {
                    try { fileMissing = !File.Exists(SafePath.Join(src.ProjectRoot!, fileRef)); }
                    catch { fileMissing = true; }
                }
                notes.Add(new RawNote
                {
                    Id = EncodeId(src.Key, rel),
                    SourceKey = src.Key,
                    SourceLabel = src.Label,
                    RelPath = rel,
                    FullPath = full,
                    Title = fm.Title,
                    Content = text,
                    Tags = tags,
                    RawLinks = links,
                    CreatedAt = SafeTime(() => File.GetCreationTimeUtc(full)),
                    UpdatedAt = SafeTime(() => File.GetLastWriteTimeUtc(full)),
                    ExpiresAt = fm.ExpiresAt,
                    SourceSessionId = fm.SourceSessionId,
                    Annotation = fm.Annotation,
                    FileRef = fileRef,
                    FileMissing = fileMissing,
                });
            }
        }
        return notes;
    }

    // Результат разбора frontmatter (внутренний; Annotation собирается из annotates/anchor_*/status)
    internal sealed record NoteFm(
        string Title, List<string> Tags, string? ExpiresAt, string? SourceSessionId,
        NoteAnnotationInfo? Annotation, string? File = null);

    // Минимальный разбор YAML-frontmatter: title, tags, expires, source_session_id,
    // file (привязка к файлу проекта) и поля комментария к документу
    // (annotates/anchor_quote/anchor_heading/status) — без внешней либы.
    // file: парсится всегда (метод статический, источника не знает) — отбрасывание
    // у личных заметок делают проекции/скан по SourceKey.
    internal static NoteFm ParseFrontmatter(string text, string fallbackTitle)
    {
        var title = fallbackTitle;
        var tags = new List<string>();
        string? expires = null;
        string? sourceSessionId = null;
        string? fileRef = null;
        string? annotates = null, anchorQuote = null, anchorHeading = null, status = null;
        NoteFm Done() => new(title, tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            expires, sourceSessionId, BuildAnnotation(annotates, anchorQuote, anchorHeading, status), fileRef);
        if (!text.StartsWith("---")) return Done();

        using var reader = new StringReader(text);
        var first = reader.ReadLine();
        if (first is null || first.Trim() != "---") return Done();

        var lines = new List<string>();
        string? line;
        var closed = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim() == "---") { closed = true; break; }
            lines.Add(line);
        }
        if (!closed) return Done();

        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var m = Regex.Match(l, @"^title:\s*(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) { title = m.Groups[1].Value.Trim().Trim('"', '\''); continue; }

            var e = Regex.Match(l, @"^expires:\s*(.+)$", RegexOptions.IgnoreCase);
            if (e.Success) { expires = e.Groups[1].Value.Trim().Trim('"', '\''); continue; }
            var s = Regex.Match(l, @"^source_session_id:\s*(.+)$", RegexOptions.IgnoreCase);
            if (s.Success) { sourceSessionId = s.Groups[1].Value.Trim().Trim('"', '\''); continue; }
            // Привязка к файлу проекта: путь от корня проекта, нормализуем к forward slashes
            var f = Regex.Match(l, @"^file:\s*(.+)$", RegexOptions.IgnoreCase);
            if (f.Success) { fileRef = NormalizeRel(f.Groups[1].Value.Trim().Trim('"', '\'')); continue; }

            var a = Regex.Match(l, @"^annotates:\s*(.+)$", RegexOptions.IgnoreCase);
            if (a.Success) { annotates = a.Groups[1].Value.Trim().Trim('"', '\''); continue; }
            var aq = Regex.Match(l, @"^anchor_quote:\s*(.+)$", RegexOptions.IgnoreCase);
            if (aq.Success) { anchorQuote = aq.Groups[1].Value.Trim().Trim('"', '\''); continue; }
            var ah = Regex.Match(l, @"^anchor_heading:\s*(.+)$", RegexOptions.IgnoreCase);
            if (ah.Success) { anchorHeading = ah.Groups[1].Value.Trim().Trim('"', '\''); continue; }
            var st = Regex.Match(l, @"^status:\s*(.+)$", RegexOptions.IgnoreCase);
            if (st.Success) { status = st.Groups[1].Value.Trim().Trim('"', '\''); continue; }

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
        return Done();
    }

    // Сборка привязки из полей frontmatter. annotates: "<scope>/<путь>[#^id]".
    private static NoteAnnotationInfo? BuildAnnotation(string? annotates, string? quote, string? heading, string? status)
    {
        if (string.IsNullOrWhiteSpace(annotates)) return null;
        var val = annotates.Trim();
        string? blockId = null;
        var hash = val.IndexOf('#');
        if (hash >= 0)
        {
            var anchor = val[(hash + 1)..].Trim();
            if (anchor.StartsWith('^')) blockId = anchor[1..].Trim();
            val = val[..hash];
        }
        var slash = val.IndexOf('/');
        if (slash <= 0 || slash == val.Length - 1) return null;   // битое поле — не привязка
        var scope = val[..slash].Trim();
        var path = val[(slash + 1)..].Trim().Replace('\\', '/');
        var st = status?.Trim().ToLowerInvariant() is "resolved" ? "resolved" : "open";
        return new NoteAnnotationInfo(scope, path, st, blockId, quote, heading);
    }

    // Маска «вне кода»: true — позиция i внутри кода. Код = ```-забор (CommonMark,
    // через MarkdownFence — та же ограда, что у Docs) + inline-код `…`. Ссылка/тег/
    // упоминание в коде не считаются (Obsidian): иначе примеры синтаксиса дают «призраки»
    // графа и ложные теги, а переименование переписывает [[…]] внутри кода.
    internal static bool[] CodeMask(string text)
    {
        var mask = new bool[text.Length];
        var fence = new MarkdownFence();
        var offset = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (fence.Consume(line) || fence.InFence)
                for (var i = 0; i < raw.Length && offset + i < mask.Length; i++)
                    mask[offset + i] = true;
            offset += raw.Length + 1;   // +1 — на '\n', на котором резали
        }
        foreach (Match m in InlineCode.Matches(text))
            for (var i = m.Index; i < m.Index + m.Length && i < mask.Length; i++)
                mask[i] = true;
        return mask;
    }

    // Позиция совпадения внутри кода (по маске); вне диапазона — не код.
    internal static bool InCode(bool[] mask, int index) =>
        index >= 0 && index < mask.Length && mask[index];

    private static List<(string, string)> ParseLinks(string text, bool[] mask)
    {
        var result = new List<(string, string)>();
        foreach (Match m in WikiLink.Matches(text))
        {
            if (InCode(mask, m.Index)) continue;
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

    // Проверка: срок заметки истёк (expiresAt в прошлом)
    private static bool IsExpired(RawNote n, DateTime now) =>
        n.ExpiresAt is not null
        && DateTime.TryParse(n.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var exp)
        && exp <= now;

    private Model Build(string userId)
    {
        var now = DateTime.UtcNow;
        var notes = Scan(userId);
        // Истёкшие заметки исключаем из модели — они не видны в списке/графе,
        // а ссылки на них становятся «призрачными» (unresolved).
        notes.RemoveAll(n => IsExpired(n, now));
        ComputeAnnotationDerived(userId, notes);
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
            var mask = CodeMask(lower);
            foreach (var m in notes)
            {
                if (m.Id == n.Id || linked.Contains(m.Id)) continue;
                var t = Norm(m.Title);
                if (t.Length < 3) continue;
                var at = FindWord(lower, t, mask);
                if (at < 0) continue;
                if (!unlinked.TryGetValue(n.Id, out var lst)) unlinked[n.Id] = lst = new();
                lst.Add(new NoteBacklinkDto(m.Id, m.Title, m.SourceKey, m.SourceLabel, SnippetAround(n.Content, at)));
            }
        }

        return new Model
        {
            Notes = notes,
            ById = byId,
            OutLinks = outLinks,
            Backlinks = backlinks,
            Ghosts = ghosts,
            Edges = edges,
            Unlinked = unlinked,
            ByName = byName,
        };
    }

    // Индекс вхождения needle как отдельного слова (границы — не буквы/цифры), иначе -1.
    // Вхождения внутри кода (по маске) не считаются упоминанием.
    private static int FindWord(string haystack, string needle, bool[] mask)
    {
        var from = 0;
        while (true)
        {
            var i = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (i < 0) return -1;
            if (InCode(mask, i)) { from = i + 1; continue; }
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
    // в <img>: право ЧТЕНИЯ источника проверяет ResolveRoot, traversal — SafeJoin.
    // Участник общего проекта видит картинку из notes/ проекта, которую ему показал его
    // ![[…]] в прочитанной заметке проекта.
    public string ResolveAttachmentPath(string userId, string sourceKey, string relativePath) =>
        SafePath.Join(ResolveRoot(userId, sourceKey, ProjectRight.Read), relativePath);

    // --- Публичное API ---

    public IReadOnlyList<NoteSummary> GetSummaries(string userId, string? source, string? query)
    {
        var notes = GetModel(userId).Notes;
        IEnumerable<RawNote> q = notes;
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(n => n.SourceKey == source);
        if (!string.IsNullOrWhiteSpace(query))
        {
            // Операторы в запросе: tag:идея source:Личный status:open — остальное полнотекст
            var (tags, sources, statuses, text) = ParseQuery(query);
            foreach (var tag in tags)
                q = q.Where(n => n.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
            foreach (var s in sources)
                q = q.Where(n => n.SourceKey.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                                 n.SourceLabel.Equals(s, StringComparison.OrdinalIgnoreCase));
            // Ответы в тредах статусом не фильтруются: статус живёт у корневого комментария
            foreach (var st in statuses)
                q = st.Equals("orphaned", StringComparison.OrdinalIgnoreCase)
                    ? q.Where(n => n.Annotation is { IsReply: false } && IsAnnotationOrphan(userId, n))
                    : q.Where(n => n.Annotation is { IsReply: false } &&
                                   n.Annotation.Status.Equals(st, StringComparison.OrdinalIgnoreCase));
            if (text.Length > 0)
                q = q.Where(n =>
                    n.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    n.Content.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    n.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }
        return q.OrderByDescending(n => n.UpdatedAt)
                .Select(ToSummary).ToList();
    }

    // Разбор операторов запроса: tag:x source:y status:open|resolved|orphaned (можно
    // несколько), остаток — полнотекст
    internal static (List<string> Tags, List<string> Sources, List<string> Statuses, string Text) ParseQuery(string query)
    {
        var tags = new List<string>();
        var sources = new List<string>();
        var statuses = new List<string>();
        var rest = new List<string>();
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                tags.Add(token[4..].TrimStart('#'));
            else if (token.StartsWith("source:", StringComparison.OrdinalIgnoreCase) && token.Length > 7)
                sources.Add(token[7..]);
            else if (token.StartsWith("status:", StringComparison.OrdinalIgnoreCase) && token.Length > 7)
                statuses.Add(token[7..]);
            else rest.Add(token);
        }
        return (tags, sources, statuses, string.Join(' ', rest));
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

    // includeAnnotations=false (дефолт): комментарии к документам в граф не попадают
    // (решение панели: не шумят связями). true — тумблер в настройках графа: комментарии
    // и ответы становятся узлами со связями «комментарий → документ» и «ответ → корень».
    public NoteGraph GetGraph(string userId, bool includeAnnotations = false)
    {
        var model = GetModel(userId);
        var annIds = model.Notes.Where(n => n.Annotation is not null).Select(n => n.Id).ToHashSet();
        var edgesRaw = includeAnnotations
            ? new HashSet<(string, string)>(model.Edges)
            : new HashSet<(string, string)>(model.Edges.Where(e => !annIds.Contains(e.Item1) && !annIds.Contains(e.Item2)));

        // Рёбра привязки: комментарий → документ-заметка (или призрачный узел файла),
        // ответ → корневой комментарий (annotates ответа указывает на его файл)
        var ghostDocs = new Dictionary<string, string>();
        if (includeAnnotations)
        {
            var byDocPath = new Dictionary<(string, string), string>();
            foreach (var n in model.Notes)
            {
                byDocPath[(n.SourceKey, DocPathOf(n.SourceKey, n.RelPath).ToLowerInvariant())] = n.Id;
                // Легаси-формат annotates у ответов (путь заметки без notes/)
                byDocPath[(n.SourceKey, n.RelPath.ToLowerInvariant())] = n.Id;
            }
            foreach (var n in model.Notes)
            {
                var a = n.Annotation;
                if (a is null) continue;
                if (byDocPath.TryGetValue((a.DocScope, a.DocPath.ToLowerInvariant()), out var targetId))
                    edgesRaw.Add((n.Id, targetId));
                else
                {
                    var gid = $"doc:{a.DocScope}/{a.DocPath.ToLowerInvariant()}";
                    ghostDocs[gid] = Path.GetFileName(a.DocPath);
                    edgesRaw.Add((n.Id, gid));
                }
            }
        }

        var degree = new Dictionary<string, int>();
        foreach (var (s, t) in edgesRaw)
        {
            degree[s] = degree.GetValueOrDefault(s) + 1;
            degree[t] = degree.GetValueOrDefault(t) + 1;
        }

        var nodes = new List<NoteGraphNode>();
        foreach (var n in model.Notes)
        {
            var a = n.Annotation;
            if (a is not null && !includeAnnotations) continue;
            var kind = a is null ? null : a.IsReply ? "reply" : "comment";
            var status = a is { IsReply: false } ? a.Status : null;
            nodes.Add(new NoteGraphNode(n.Id, n.Title, n.SourceKey, n.SourceLabel,
                degree.GetValueOrDefault(n.Id), false, n.Tags, kind, status));
        }
        foreach (var (gid, name) in model.Ghosts)
            if (degree.GetValueOrDefault(gid) > 0)
                nodes.Add(new NoteGraphNode(gid, name, "", "", degree.GetValueOrDefault(gid), true));
        foreach (var (gid, name) in ghostDocs)
            nodes.Add(new NoteGraphNode(gid, name, "", "", degree.GetValueOrDefault(gid), true, null, "doc"));

        var edges = edgesRaw.Select(e => new NoteGraphEdge(e.Item1, e.Item2)).ToList();
        return new NoteGraph(nodes, edges);
    }

    // Папка внутри источника: сегменты чистятся по одному, traversal исключён.
    // "Идеи/Черновики" → "Идеи/Черновики"; пусто/мусор → "" (корень).
    internal static string SanitizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        // Быстрый путь: чистый относительный путь — тот, где общий путь ничего не меняет
        // (результат = NormalizeRel). Проверка — один проход строки, без аллокаций на сегмент:
        // ключ целевого пути в Move считается до взятия замка, и аллокация на сегмент глубокой
        // папки там дороже окна параллельной операции над файлом (замер: ~300 мкс на 300 сегментов
        // против ~100 мкс Exists+Delete такой же папки).
        if (IsCleanRelativePath(folder)) return NormalizeRel(folder);
        var segments = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeFileName)
            .Where(s => s.Length > 0 && s != "." && s != "..")
            .ToArray();
        return string.Join('/', segments);
    }

    // Ровно те случаи, где общий путь — тождество: нет обратных слэшей, ведущего/замыкающего
    // слэша, пустых сегментов и сегментов, начинающихся/кончающихся пробелом или точкой
    // (это же отсекает "." и ".."), недопустимых символов имени внутри сегментов. Всё остальное
    // — общий путь. Консервативно: «чистый» тут ⊆ «общий путь ничего не меняет».
    private static bool IsCleanRelativePath(string path)
    {
        if (path.IndexOf('\\') >= 0 || path[0] == '/' || path[^1] == '/') return false;
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/') continue;
            if (i == start) return false;
            if (path[start] == ' ' || path[start] == '.' || path[i - 1] == ' ' || path[i - 1] == '.') return false;
            for (var j = start; j < i; j++)
                if (Array.IndexOf(InvalidFileNameChars, path[j]) >= 0) return false;
            start = i + 1;
        }
        return true;
    }

    public NoteDetail Create(string userId, CreateNoteRequest req, bool byHuman = false)
    {
        var sourceKey = string.IsNullOrWhiteSpace(req.Source) ? PersonalKey : req.Source!;
        // S2c-11: создание — под замком источника: окно «проверить коллизию имени → записать
        // файл» не остаётся открытым для чужой записи того же файла. Журнал событий и чтение
        // созданной заметки — ЗА замком: Append — чужой код, способный стоять на внешнем
        // барьере (в тесте гонки — подменный журнал), а под замком такой стоп не держат:
        // барьер и замок на одном источнике дают цикл ожидания.
        var id = WithWriteGates(() => CreateCore(userId, req, sourceKey, byHuman), SourceGateKey(userId, sourceKey));
        // P1-4: лог note_changed в проектный лог (только для заметок проекта, не личных)
        if (sourceKey != PersonalKey)
            _events?.Append(sourceKey, userId, ProjectEventTypes.NoteChanged, "user",
                $"Создана заметка «{req.Title}»", id);
        return GetDetail(userId, id)
            ?? throw new InvalidOperationException("Заметка создана, но не читается");
    }

    // Файловая часть создания под замком источника: коллизия имени, запись, инвалидация.
    // Возвращает id созданной заметки.
    private string CreateCore(string userId, CreateNoteRequest req, string sourceKey, bool byHuman)
    {
        // S2c-7: дверь заметки — на WriteContent: редактор общего проекта создаёт заметку
        // в notes/ проекта; читатель — 403, посторонний — 404 (тот же разбор, что у ResolveRoot)
        var rootDir = ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);

        var baseName = SanitizeFileName(req.Title);
        if (baseName.Length == 0) baseName = "Без названия";
        var folder = SanitizeFolder(req.Folder);
        var prefix = folder.Length > 0 ? folder + "/" : "";
        var relPath = prefix + baseName + ".md";
        var full = SafePath.Join(rootDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Разрешаем коллизию имени файла суффиксом
        var n = 2;
        while (File.Exists(full))
        {
            relPath = $"{prefix}{baseName}-{n++}.md";
            full = SafePath.Join(rootDir, relPath);
        }

        var content = req.Content
            ?? (req.TemplateId is not null ? RenderTemplate(userId, req.TemplateId, req.Title) : null)
            ?? $"# {req.Title}\n";
        if (req.ExpiresAfterMinutes is > 0)
        {
            var expiresAt = DateTime.UtcNow.AddMinutes(req.ExpiresAfterMinutes.Value).ToString("o");
            content = InjectExpiresField(content, expiresAt);
        }
        if (!string.IsNullOrWhiteSpace(req.SourceSessionId))
            content = InjectSourceSessionId(content, req.SourceSessionId);
        // Привязка к файлу проекта: у личного vault поля нет (согласованно с чтением)
        if (!string.IsNullOrWhiteSpace(req.File) && sourceKey != PersonalKey)
            content = InjectFileField(content, NormalizeRel(req.File!.Trim()));
        File.WriteAllText(full, content, new UTF8Encoding(false));
        // S3-3c1: запись состоялась — итоговый путь (после разрешения коллизии имени) в журнал
        // правок человека, под замком источника: журнал обязан видеть тот же порядок, что файл.
        if (byHuman) RecordHumanEdit(userId, sourceKey, full);

        InvalidateWrite(userId, sourceKey);
        return EncodeId(sourceKey, NormalizeRel(relPath));
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
        var full = SafePath.Join(rootDir, rel);
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
    // S2c-12: это правка содержимого заметки — под тем же замком на её id, что Update/Delete:
    // гонка «связать» с правкой той же заметки сериализована, потерянной правки нет.
    public NoteDetail? LinkMention(string userId, string id, string targetTitle, bool byHuman = false) =>
        WithWriteGates(() => LinkMentionCore(userId, id, targetTitle, byHuman), id);

    private NoteDetail? LinkMentionCore(string userId, string id, string targetTitle, bool byHuman)
    {
        // Право записи — как в Move/Update: ResolveRoot с правым WriteContent (S2c-7).
        // Без этой проверки «связать» переписывало файл проекта, видимого читателю лишь
        // на чтение (отказ ловит NotesController → 403).
        var (sourceKey, _) = DecodeId(id);
        ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);

        var model = GetModel(userId);
        if (!model.ById.TryGetValue(id, out var note)) return null;

        var content = File.ReadAllText(note.FullPath, Encoding.UTF8);
        var lower = content.ToLowerInvariant();
        var at = FindWord(lower, Norm(targetTitle), CodeMask(lower));
        if (at < 0) return GetDetail(userId, id);   // упоминание уже исчезло — не ошибка

        var original = content.Substring(at, targetTitle.Length);
        var replacement = original.Equals(targetTitle, StringComparison.Ordinal)
            ? $"[[{targetTitle}]]"
            : $"[[{targetTitle}|{original}]]";
        content = content[..at] + replacement + content[(at + targetTitle.Length)..];
        File.WriteAllText(note.FullPath, content, new UTF8Encoding(false));
        // S3-3c1 доработка 1 (вердикт ea994f7c): «связать» — правка содержимого заметки,
        // под её замком — в журнал; исчезнувшее упоминание (выход выше, без записи) — молчит.
        if (byHuman) RecordHumanEdit(userId, sourceKey, note.FullPath);

        // S2c-11: это правка содержимого чужой заметки — сбрасывает и модель арендатора
        InvalidateWrite(userId, sourceKey);
        return GetDetail(userId, id);
    }

    // Перенос заметки: в папку и/или другой источник (личный vault ↔ notes/ проекта).
    // Ссылки [[…]] не трогаем — они резолвятся по заголовку и переезд переживают;
    // меняется только id (источник + путь).
    public NoteDetail? Move(string userId, string id, string? folder, string? targetSource = null, bool byHuman = false)
    {
        var (sourceKey, relPath) = DecodeId(id);
        // S2c-11: замок на id перемещаемой заметки — тот же ключ, что у Update/Delete: гонка
        // переноса редактора с удалением той же заметки сериализована, полуреального состояния
        // («исходный файл удалён, целевой создан») нет. Плюс замок на затронутые источники:
        // целевое место и структура папок. F-6a: и замок на целевой путь (тот же ключ, что
        // стоит на Update/Delete заметки в нём) — перенос в занятую цель сериализован с
        // операцией на этой цели, а не только с источником. Целевой id считается здесь же, по
        // тем же SanitizeFolder/NormalizeRel, что MoveCore: имена замка и файла расходятся
        // только при невалидном id (ArgumentException из DecodeId — до любой записи).
        // Ключи берутся в фиксированном порядке по возрастанию (WithWriteGates) — две
        // многоключевые операции не разойдутся по кругу.
        var newSource = string.IsNullOrWhiteSpace(targetSource) ? sourceKey : targetSource!;
        var fileName = Path.GetFileName(relPath);
        var target = SanitizeFolder(folder);
        var newRel = NormalizeRel(target.Length > 0 ? $"{target}/{fileName}" : fileName);
        var keys = new List<string> { id, EncodeId(newSource, newRel) };
        keys.Add(SourceGateKey(userId, sourceKey));
        if (newSource != sourceKey) keys.Add(SourceGateKey(userId, newSource));
        return WithWriteGates(() => MoveCore(userId, id, folder, targetSource, byHuman), [.. keys]);
    }

    private NoteDetail? MoveCore(string userId, string id, string? folder, string? targetSource, bool byHuman)
    {
        var (sourceKey, relPath) = DecodeId(id);
        // S2c-7: перенос ВНУТРИ источника — обычная дверь заметки, WriteContent: редактор
        // перекладывает свои заметки по папкам проекта
        var rootDir = ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);
        var full = SafePath.Join(rootDir, relPath);
        if (!File.Exists(full)) return null;

        // Целевой источник: пусто = текущий
        var newSource = string.IsNullOrWhiteSpace(targetSource) ? sourceKey : targetSource!;
        // S2c-5: перенос МЕЖДУ источниками — решение владельца: обе стороны проверяются
        // owner-Write. Дверь Move внутри источника (выше и ниже) — на WriteContent (S2c-7),
        // эта проверка остаётся на Write: редактор не вынесет заметку проекта в свой vault
        // и не внесёт свою в чужой проект.
        if (newSource != sourceKey)
        {
            EnsureSourceWrite(userId, sourceKey);
            ResolveRoot(userId, newSource, ProjectRight.Write);
        }
        var newRootDir = newSource == sourceKey
            ? rootDir
            : ResolveRoot(userId, newSource, ProjectRight.Write);

        var fileName = Path.GetFileName(relPath);
        var target = SanitizeFolder(folder);
        var newRel = NormalizeRel(target.Length > 0 ? $"{target}/{fileName}" : fileName);
        if (newSource == sourceKey && string.Equals(newRel, NormalizeRel(relPath), StringComparison.OrdinalIgnoreCase))
            return GetDetail(userId, id);   // уже там

        var newFull = SafePath.Join(newRootDir, newRel);
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

        // S3-3c2: человеческий перенос — в журнал ОБА пути переезда: старый (его файла больше
        // нет) и новый, каждый со root СВОЕГО источника-проекта (личный бок отсекает сам
        // RecordHumanEdit: vault вне журнала). Вызов под замком переноса и только после
        // состоявшегося File.Move: сюда не доходят ни отказанный перенос («уже есть», 404 —
        // проигравший не пишет ни одного пути), ни no-op «уже там» (правки файла не было).
        if (byHuman)
        {
            RecordHumanEdit(userId, sourceKey, full);
            RecordHumanEdit(userId, newSource, newFull);
        }

        // S2c-11: переезд между источниками — запись для обоих арендаторов: сбрасываются
        // исходный и целевой источники (свой ключ + ключ арендатора каждого)
        InvalidateWrite(userId, sourceKey);
        if (newSource != sourceKey) InvalidateWrite(userId, newSource);
        // Комментарии, привязанные к перенесённой заметке-документу, переезжают вместе с ней.
        // S2c-12: Inner — без своего замка: он был бы вложенным в только что взятый замок
        // переноса и сломал общий порядок ключей; правка комментариев идёт под ним.
        RewriteAnnotationTargetsInner(userId, sourceKey, DocPathOf(sourceKey, NormalizeRel(relPath)),
            newSource, DocPathOf(newSource, newRel));
        return GetDetail(userId, EncodeId(newSource, newRel));
    }

    // Переименование/перенос папки целиком (Directory.Move — атомарно, вложения
    // переезжают вместе). Ссылки [[…]] не трогаем (резолв по заголовку).
    // Возвращает маппинг id заметок старый → новый (id содержит путь).
    public IReadOnlyList<MovedNoteId> MoveFolder(string userId, string sourceKey, string path, string newPath, bool byHuman = false)
    {
        // S2c-11: перенос папки меняет id всех её заметок — под замком источника: гонка с
        // переносом отдельной заметки или созданием файла в той же папке сериализована.
        // F-6a (хвост 565100e3 п.6): плюс замки всех заметок папки — Update/Delete/Move заметки
        // внутри держат замок на её id, а не на источник: без этих замков гонка «перенос папки ↔
        // правка заметки внутри» не сериализована — правка уходит в «не найдена» или теряется
        // под переездом. Заметки папки перечисляются сканом диска ДО замка (поиск — только
        // чтение, замок не держится во время скана); их id и ключ источника берутся одним
        // WithWriteGates по возрастанию — тот же набор ключей при любом порядке запросов даёт
        // ту же очередь, цикла нет. Заметка, созданная между сканом и взятием замка, закрыта
        // замком источника: Create его берёт всегда.
        var oldFolder = SanitizeFolder(path);
        var noteIds = oldFolder.Length == 0
            ? []
            : FolderNoteIds(userId, sourceKey, oldFolder);
        return WithWriteGates(() => MoveFolderCore(userId, sourceKey, path, newPath, byHuman),
            [SourceGateKey(userId, sourceKey), .. noteIds]);
    }

    // id всех .md-заметок папки по скану диска (тот же EncodeId, что у id заметок модели):
    // ключи замка и маппинга id. Зовётся ДО взятия замка; под замком состав папки проверяется
    // Directory.Exists — повторный скан не нужен. Нечитаемый файл — не заметка: его переносит
    // Directory.Move, ключа замка у него нет (тот же разбор, что у пропуска битых файлов в Scan).
    private List<string> FolderNoteIds(string userId, string sourceKey, string folder)
    {
        var result = new List<string>();
        string rootDir;
        try { rootDir = ResolveRoot(userId, sourceKey, ProjectRight.Read); }
        catch { return result; }   // нет права/источника — отказ придёт из Core под замком
        var full = SafePath.Join(rootDir, folder);
        if (!Directory.Exists(full)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(full, "*.md", SearchOption.AllDirectories))
            {
                try { File.ReadAllText(file); } catch { continue; }
                result.Add(EncodeId(sourceKey, NormalizeRel(Path.GetRelativePath(rootDir, file))));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Перечисление заметок папки {Dir}", full); }
        return result;
    }

    private IReadOnlyList<MovedNoteId> MoveFolderCore(string userId, string sourceKey, string path, string newPath, bool byHuman)
    {
        // S2c-7: перенос папки — дверь содержимого заметок проекта, WriteContent
        var rootDir = ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);
        var oldFolder = SanitizeFolder(path);
        var newFolder = SanitizeFolder(newPath);
        if (oldFolder.Length == 0) throw new ArgumentException("Не задана папка");
        if (newFolder.Length == 0) throw new ArgumentException("Не задан новый путь");
        if (newFolder.Equals(oldFolder, StringComparison.OrdinalIgnoreCase))
            return [];
        if (newFolder.StartsWith(oldFolder + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нельзя перенести папку внутрь самой себя");

        var oldFull = SafePath.Join(rootDir, oldFolder);
        var newFull = SafePath.Join(rootDir, newFolder);
        if (!Directory.Exists(oldFull)) throw new KeyNotFoundException("Папка не найдена");
        if (Directory.Exists(newFull) || File.Exists(newFull))
            throw new InvalidOperationException($"«{newFolder}» уже существует");

        // Заметки папки — по скану диска под замком: после взятия замка источника их никто не
        // создаёт и не удаляет, а их id уже взяты (список выше) — состав под замком стабилен.
        // F-6a: не через модель — модель могла не увидеть заметку, созданную до этого окна;
        // маппинг обязан соответствовать тому, что реально переезжает. Нечитаемый .md — не
        // заметка и не в маппинге (тот же пропуск, что у модели и у списка замков выше).
        var before = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(oldFull, "*.md", SearchOption.AllDirectories))
            {
                try { File.ReadAllText(file); } catch { continue; }
                before.Add(NormalizeRel(Path.GetRelativePath(rootDir, file)));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Сканирование переносимой папки {Dir}", oldFull); }

        Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
        Directory.Move(oldFull, newFull);
        // S3-3c2: человеческий перенос папки — старый и новый путь КАЖДОГО её файла (тот же
        // порядок, что S3-3b в FilesController.RecordRenamedDir: путь папки в журнал не идёт —
        // её pathspec затянул бы чужую работу). Список берётся из скана под замком источника ДО
        // переноса; вызов — после состоявшегося Directory.Move: отказанный перенос (404 /
        // «уже существует») сюда не доходит — проигравший не пишет ни одного пути.
        if (byHuman)
            foreach (var rel in before)
            {
                RecordHumanEdit(userId, sourceKey, SafePath.Join(rootDir, rel));
                RecordHumanEdit(userId, sourceKey, SafePath.Join(rootDir, newFolder + rel[oldFolder.Length..]));
            }
        // S2c-11: содержимое папки переезжает — модель арендатора источника обязана быть сброшена
        InvalidateWrite(userId, sourceKey);
        // Привязки комментариев к документам внутри перенесённой папки — по префиксу.
        // S2c-12: Inner — без своего замка (вложенный сломал бы порядок), под замком источника.
        RewriteAnnotationTargetsInner(userId, sourceKey, DocPathOf(sourceKey, oldFolder),
            sourceKey, DocPathOf(sourceKey, newFolder), prefix: true);

        return before.Select(rel =>
        {
            var newRel = newFolder + rel[oldFolder.Length..];
            return new MovedNoteId(EncodeId(sourceKey, rel), EncodeId(sourceKey, newRel));
        }).ToList();
    }

    // --- Физические папки (в т.ч. пустые: дерево заметок строится из заметок,
    //     а пустая папка иначе бы «исчезла») ---

    // Все физические подпапки всех источников владельца (включая пустые, включая
    // промежуточные уровни). Скрытые (сегмент на «.») и templates/ пропускаются.
    public IReadOnlyList<NoteFolderDto> GetFolders(string userId)
    {
        var result = new List<NoteFolderDto>();
        foreach (var src in SourcesFor(userId, ProjectRight.Read))
        {
            if (!Directory.Exists(src.RootDir)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(src.RootDir, "*", SearchOption.AllDirectories); }
            catch (Exception ex) { _logger.LogWarning(ex, "Сканирование папок заметок {Dir}", src.RootDir); continue; }
            foreach (var dir in dirs)
            {
                var rel = NormalizeRel(Path.GetRelativePath(src.RootDir, dir));
                if (rel.Length == 0) continue;
                // Скрытые папки (.obsidian и т.п.) и шаблоны — не показываем
                if (rel.Split('/').Any(seg => seg.StartsWith('.'))) continue;
                if (rel.Equals("templates", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new NoteFolderDto(src.Key, rel));
            }
        }
        return result;
    }

    // Создать физическую папку (идемпотентно — дубликат не ошибка).
    public NoteFolderDto CreateFolder(string userId, string sourceKey, string path)
    {
        // S2c-11: папка — структура содержимого источника: под замком источника вместе с
        // созданием/переносом его заметок
        return WithWriteGates(() => CreateFolderCore(userId, sourceKey, path), SourceGateKey(userId, sourceKey));
    }

    private NoteFolderDto CreateFolderCore(string userId, string sourceKey, string path)
    {
        // S2c-7: папка — структура содержимого заметок проекта, дверь на WriteContent
        var rootDir = ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);
        var folder = SanitizeFolder(path);
        if (folder.Length == 0) throw new ArgumentException("Не задана папка");
        var full = SafePath.Join(rootDir, folder);
        if (File.Exists(full))
            throw new InvalidOperationException($"«{folder}» — уже файл, не папка");
        Directory.CreateDirectory(full);
        InvalidateWrite(userId, sourceKey);
        return new NoteFolderDto(sourceKey, folder);
    }

    // Удалить физическую папку рекурсивно (пустую или с заметками/вложениями).
    // Возвращает число удалённых .md-заметок.
    public int DeleteFolder(string userId, string sourceKey, string path, bool byHuman = false)
    {
        // S2c-11: та же дверь и тот же замок источника, что у CreateFolder.
        // F-6a (хвост 565100e3 п.6): плюс замки всех заметок папки — правка/удаление заметки
        // внутри (замок на её id) с удалением папки не сериализованы: правка могла писать в
        // файл под его удалением. Порядок захвата — тот же: одним WithWriteGates по возрастанию.
        var folder = SanitizeFolder(path);
        var noteIds = folder.Length == 0
            ? []
            : FolderNoteIds(userId, sourceKey, folder);
        return WithWriteGates(() => DeleteFolderCore(userId, sourceKey, path, byHuman),
            [SourceGateKey(userId, sourceKey), .. noteIds]);
    }

    private int DeleteFolderCore(string userId, string sourceKey, string path, bool byHuman)
    {
        // S2c-7: та же дверь, что у CreateFolder: WriteContent
        var rootDir = ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);
        var folder = SanitizeFolder(path);
        if (folder.Length == 0) throw new ArgumentException("Не задана папка");
        var full = SafePath.Join(rootDir, folder);
        if (!Directory.Exists(full)) throw new KeyNotFoundException("Папка не найдена");
        // S3-3c2: список файлов папки снимается ДО удаления (после него старых путей уже нет) —
        // тот же порядок, что S3-3b в FilesController: журналу идут ЕЁ ФАЙЛЫ, не путь папки.
        // Скан — под замком источника: после его взятия состав папки не меняется (те же
        // замки id, что список замков в DeleteFolder).
        List<string> mdFiles;
        try { mdFiles = [.. Directory.EnumerateFiles(full, "*.md", SearchOption.AllDirectories)]; }
        catch { mdFiles = []; }
        Directory.Delete(full, recursive: true);
        // S3-3c2: человеческий снос папки — каждый её файл своим путём, после состоявшегося
        // Directory.Delete: отказанный снос (404) сюда не доходит — проигравший не пишет путей.
        if (byHuman)
            foreach (var f in mdFiles) RecordHumanEdit(userId, sourceKey, f);
        // S2c-11: вместе с папкой удалены её заметки — модель арендатора обязана быть сброшена
        InvalidateWrite(userId, sourceKey);
        return mdFiles.Count;
    }

    // S2c-2 (К8): сохранение с If-Match — оптимистичная конкурентность. «Сравнить и записать»
    // целиком под замком на id заметки (вместе с инвалидацией кэша сводок в UpdateCore): окно
    // между проверкой хеша и записью не остаётся. Без If-Match — как раньше.
    public NoteDetail? Update(string userId, string id, UpdateNoteRequest req, string? ifMatch = null, bool byHuman = false) =>
        WithWriteGates(() => UpdateCore(userId, id, req, ifMatch, byHuman), id);

    private NoteDetail? UpdateCore(string userId, string id, UpdateNoteRequest req, string? ifMatch, bool byHuman)
    {
        var (sourceKey, relPath) = DecodeId(id);
        // S2c-7: правка содержимого заметки — дверь WriteContent (редактор общего проекта)
        var rootDir = ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);
        var full = SafePath.Join(rootDir, relPath);
        if (!File.Exists(full)) return null;
        // S3-3c1: пути, которые ЭТА правка действительно записала (для журнала правок человека):
        // переименование даёт СТАРЫЙ и НОВЫЙ путь (старого файла больше нет — журнал обязан
        // увидеть и его), запись содержимого/срока — текущий путь. Один вызов на путь: журнал
        // заменяет запись по пути сам, но список вызывающей стороны не должен расти от второй
        // записи того же файла в одной операции (содержимое + срок). Пусто — правки файла не
        // было (no-op Update), журналу нечего сообщать.
        List<string>? edited = null;
        void Touched(string path)
        {
            edited ??= [];
            if (!edited.Contains(path)) edited.Add(path);
        }
        // Тестовый шов (F-6a доработка 1): под замком id, до чтения файла — см. поле.
        TestProbeAfterFileExists?.Invoke();

        // If-Match сверяется с фактическим хешом содержимого до любой правки (в т.ч. до
        // переименования файла): расхождение — NoteConflictException, заметка на диске не тронута.
        // Принимается quoted/слабая форма ETag (RFC 9110); «*» — «если существует» (существование
        // файла только что проверено). Без If-Match — прежнее поведение.
        if (ifMatch is { Length: > 0 } raw)
        {
            var token = UnquoteETag(raw);
            var currentText = File.ReadAllText(full, Encoding.UTF8);
            var currentHash = ContentSha256(currentText);
            if (token != "*" && !string.Equals(token, currentHash, StringComparison.OrdinalIgnoreCase))
                throw new NoteConflictException(currentText, currentHash, SafeTime(() => File.GetLastWriteTimeUtc(full)));
        }

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
                var newFull = SafePath.Join(rootDir, newRel);
                if (!File.Exists(newFull))
                {
                    File.Move(full, newFull);
                    Touched(full);   // старый путь: его больше нет — правка удаления тоже факт
                    full = newFull;
                    Touched(full);   // новый путь: сюда же сольётся запись содержимого ниже
                    effectiveId = EncodeId(sourceKey, newRel);
                    // Комментарии на переименованную заметку-документ следуют за ней.
                    // S2c-12: Inner — без своего замка (вложенный сломал бы порядок), под
                    // замком переименовываемой заметки.
                    RewriteAnnotationTargetsInner(userId, sourceKey, DocPathOf(sourceKey, NormalizeRel(relPath)),
                        sourceKey, DocPathOf(sourceKey, newRel));
                }
            }
        }

        if (req.Content is not null)
        {
            // Merge-защита системных полей комментария: если текущий файл — комментарий,
            // а новый контент потерял annotates (агент/редактор переписал frontmatter),
            // поля привязки восстанавливаются — перезапись не сиротит комментарий молча.
            var newContent = req.Content;
            var curText = File.ReadAllText(full, Encoding.UTF8);
            var curFm = ParseFrontmatter(curText, "");
            if (curFm.Annotation is not null &&
                ParseFrontmatter(newContent, "").Annotation is null)
                newContent = ReinjectAnnotationFields(newContent, curFm.Annotation);
            // S3-3c3: тот же узор, что у no-op срока ниже: равное содержимое — не запись.
            // No-op правки (галочка уже в нужном состоянии, тот же срок) не должна плодить
            // запись нетронутого файла: git затем не находит изменений —
            // «Коммит правок человека не удался» (та же причина, что у no-op срока).
            if (!string.Equals(newContent, curText, StringComparison.Ordinal))
            {
                File.WriteAllText(full, newContent, new UTF8Encoding(false));
                Touched(full);
            }
        }

        // Время жизни: -1 (по умолчанию) — не менять; null — снять; N — установить
        if (req.ExpiresAfterMinutes != -1)
        {
            var current = File.ReadAllText(full, Encoding.UTF8);
            var updated = req.ExpiresAfterMinutes is null
                ? RemoveExpiresField(current)
                : req.ExpiresAfterMinutes > 0
                    ? InjectExpiresField(current, DateTime.UtcNow.AddMinutes(req.ExpiresAfterMinutes.Value).ToString("o"))
                    : current;
            // S3-3c1 доработка 1 (minor, вердикт ea994f7c): снятие несуществующего срока или
            // срок 0 оставляют контент ровно тем же — файла нет, и в журнал с ним не идёт
            // ничего: no-op не должен плодить запись нетронутого файла (git затем не находит
            // изменений и пишет «Коммит правок человека не удался»).
            if (!string.Equals(updated, current, StringComparison.Ordinal))
            {
                File.WriteAllText(full, updated, new UTF8Encoding(false));
                Touched(full);
            }
        }

        // Авто-обновление входящих ссылок при смене заголовка: во всех заметках,
        // ссылавшихся на старый заголовок, заменяем [[Старый]] → [[Новый]] (как Obsidian).
        if (!string.IsNullOrWhiteSpace(req.Title) && Norm(req.Title!) != Norm(oldTitle)
            && model.Backlinks.TryGetValue(id, out var inbound))
        {
            var sources = inbound.Select(b => b.SourceId).Distinct();
            foreach (var srcId in sources)
            {
                if (srcId == id || !model.ById.TryGetValue(srcId, out var srcNote)) continue;
                // S2c-5: авто-обновление ссылок при переименовании — тот же фильтр каскада:
                // только источники с WriteContent у вызывающего
                if (!CanWriteContentIn(userId, srcNote.SourceKey)) continue;
                try
                {
                    var updated = RewriteLinks(File.ReadAllText(srcNote.FullPath, Encoding.UTF8), oldTitle, req.Title!);
                    File.WriteAllText(srcNote.FullPath, updated, new UTF8Encoding(false));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Обновление ссылок в {File}", srcNote.FullPath); }
            }
        }

        // S3-3c1: запись состоялась — затронутые пути в журнал правок человека, ПОД замком id:
        // порядок вызовов Record обязан совпадать с порядком записей файла, иначе автором
        // последней правки журнала окажется проигравший гонку (краснеет на мутации «Record
        // после выхода из WithWriteGates»).
        if (byHuman && edited is not null)
            foreach (var p in edited) RecordHumanEdit(userId, sourceKey, p);

        InvalidateWrite(userId, sourceKey);
        if (sourceKey != PersonalKey)
            _events?.Append(sourceKey, userId, ProjectEventTypes.NoteChanged, "user",
                $"Изменена заметка «{req.Title ?? oldTitle}»", effectiveId);
        return GetDetail(userId, effectiveId);
    }

    // Заменяет [[Старый]] / [[Старый|подпись]] / [[Старый#якорь]] / [[Папка/Старый]]
    // на [[Новый…]] с сохранением подписи и якоря; не-совпадающие ссылки и ссылки
    // внутри кода (`…`/```-блока) не трогает.
    private static string RewriteLinks(string content, string oldTitle, string newTitle)
    {
        var oldNorm = Norm(oldTitle);
        var mask = CodeMask(content);
        return WikiLink.Replace(content, m =>
        {
            if (InCode(mask, m.Index)) return m.Value;
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

    // S2c-5 (Конкурентность): удаление — под тем же замком на id заметки, что и Update (S2c-2).
    // Без него фоновая уборка истёкших и запись той же заметки переплетаются: правка
    // возрождает уже удалённый файл (File.Delete прошёл, WriteAllText создал заново).
    public bool Delete(string userId, string id, bool byHuman = false) =>
        WithWriteGates(() => DeleteCore(userId, id, byHuman), id);

    private bool DeleteCore(string userId, string id, bool byHuman)
    {
        var (sourceKey, relPath) = DecodeId(id);
        // S2c-7: удаление заметки — дверь WriteContent
        var rootDir = ResolveRoot(userId, sourceKey, ProjectRight.WriteContent);
        var full = SafePath.Join(rootDir, relPath);
        if (!File.Exists(full)) return false;
        File.Delete(full);
        // S3-3c1: удаление — правка пути (журнал принимает несуществующий путь: удаление тоже
        // правка), под замком id — тот же порядок, что у записи.
        if (byHuman) RecordHumanEdit(userId, sourceKey, full);
        InvalidateWrite(userId, sourceKey);
        if (sourceKey != PersonalKey)
            _events?.Append(sourceKey, userId, ProjectEventTypes.NoteChanged, "user",
                $"Удалена заметка «{Path.GetFileNameWithoutExtension(relPath)}»", id);
        return true;
    }

    // Явные реализации Core-шва INoteAccessor (Create/Update): шов объявлен под прежними
    // сигнатурами, и его вызывающие — спина и фоновые службы (PersonaMemoryService,
    // SessionSummaryService, ChatDigestService, DailyBriefingService) — это агентский путь:
    // он никогда не бывает правкой человека, поэтому byHuman здесь всегда false и журнал
    // молчит. Расширять сам шов нечем: у byHuman нет потребителя за пределами вертикали
    // (человеческий путь — NotesController, он зовёт NotesService напрямую).
    NoteDetail INoteAccessor.Create(string ownerId, CreateNoteRequest request) => Create(ownerId, request);
    NoteDetail? INoteAccessor.Update(string ownerId, string noteId, UpdateNoteRequest request, string? ifMatch) =>
        Update(ownerId, noteId, request, ifMatch);

    // Возвращает истёкшие заметки пользователя (expiresAt в прошлом).
    // Не инвалидирует кэш — чистая проверка на скане.
    public IReadOnlyList<(string Id, string Title)> GetExpiredNotes(string userId, DateTime nowUtc)
    {
        // Уборка истёкших — это запись: только по Write-источникам. Иначе участник с
        // истёкшей заметкой общего проекта получил бы 403 посреди фоновой уборки и
        // остановил бы очистку своих собственных заметок (NoteExpiryService ловит
        // исключение сразу над всем списком пользователя).
        var result = new List<(string, string)>();
        foreach (var src in SourcesFor(userId, ProjectRight.Write))
        {
            if (!Directory.Exists(src.RootDir)) continue;
            try
            {
                foreach (var full in Directory.EnumerateFiles(src.RootDir, "*.md", SearchOption.AllDirectories))
                {
                    string text;
                    try { text = File.ReadAllText(full, Encoding.UTF8); }
                    catch { continue; }

                    var rel = NormalizeRel(Path.GetRelativePath(src.RootDir, full));
                    if (rel.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)) continue;
                    var fm = ParseFrontmatter(text, Path.GetFileNameWithoutExtension(full));
                    var (title, expires) = (fm.Title, fm.ExpiresAt);
                    if (expires is null) continue;
                    if (DateTime.TryParse(expires, null, System.Globalization.DateTimeStyles.RoundtripKind, out var exp)
                        && exp <= nowUtc)
                    {
                        var id = EncodeId(src.Key, rel);
                        result.Add((id, title));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Сканирование истёкших заметок в {Dir}", src.RootDir);
            }
        }
        return result;
    }

    // Убирает [[wikilinks]] из всех заметок пользователя, указывающие на заданный заголовок.
    // [[Заголовок]] → Заголовок, [[Заголовок|алиас]] → алиас.
    // S2c-12: каскад правит сразу много заметок — по замку на каждую правимую, ключи
    // WithWriteGates берёт по возрастанию (как в Move): две чистки или чистка с правкой той же
    // заметки не разойдутся по кругу и не переплетутся в полуреальном состоянии. Модель
    // снимается ДО замка (поиск целей — только чтение), под замком — запись файлов и
    // InvalidateWrite по затронутым источникам (сбрасывает и модель арендатора).
    public void RemoveWikilinksTo(string userId, string targetTitle, string targetId)
    {
        // Перестраиваем модель, чтобы получить входящие ссылки на цель
        var model = GetModel(userId);
        if (!model.Backlinks.TryGetValue(targetId, out var backlinks)) return;

        // S2c-5: каскад правит только те источники, где у вызывающего есть WriteContent;
        // read-only проект (участник-читатель) остаётся нетронутым
        var targets = new List<string>();
        foreach (var srcId in backlinks.Select(b => b.SourceId).Distinct())
        {
            if (model.ById.TryGetValue(srcId, out var n) && CanWriteContentIn(userId, n.SourceKey))
                targets.Add(srcId);
        }
        if (targets.Count == 0) return;

        var ids = targets.ToArray();
        WithWriteGates(() => RemoveWikilinksCore(model, userId, targetTitle, ids), ids);
    }

    private int RemoveWikilinksCore(Model model, string userId, string targetTitle, string[] targetIds)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var srcId in targetIds)
        {
            if (!model.ById.TryGetValue(srcId, out var srcNote)) continue;
            try
            {
                var content = File.ReadAllText(srcNote.FullPath, Encoding.UTF8);
                var mask = CodeMask(content);
                // Заменяем [[TargetTitle]] → TargetTitle, [[TargetTitle|alias]] → alias;
                // ссылки в коде (`…`/```-блок) не трогаем
                var updated = Regex.Replace(content,
                    @"\[\[" + Regex.Escape(targetTitle) + @"(\|[^\[\]]*?)?\]\]",
                    m => InCode(mask, m.Index)
                        ? m.Value
                        : m.Groups[1].Success
                            ? m.Groups[1].Value.TrimStart('|')
                            : targetTitle,
                    RegexOptions.IgnoreCase);
                File.WriteAllText(srcNote.FullPath, updated, new UTF8Encoding(false));
                touched.Add(srcNote.SourceKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Удаление ссылок на {Title} в {File}", targetTitle, srcNote.FullPath);
            }
        }
        foreach (var sourceKey in touched) InvalidateWrite(userId, sourceKey);
        return touched.Count;
    }

    // Вставляет поле expires в frontmatter (или создаёт новый frontmatter, если его нет).
    internal static string InjectExpiresField(string content, string expiresAt)
    {
        if (content.StartsWith("---"))
        {
            // Ищем конец первой секции --- и вставляем после открывающих ---
            var idx = content.IndexOf('\n', 3);
            if (idx > 0)
                return content[..(idx + 1)] + $"expires: {expiresAt}\n" + content[(idx + 1)..];
        }
        // Нет frontmatter — создаём
        return $"---\nexpires: {expiresAt}\n---\n{content}";
    }

    // Удаляет поле expires из frontmatter (если есть).
    internal static string RemoveExpiresField(string content)
    {
        if (!content.StartsWith("---")) return content;
        var endIdx = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIdx < 0) return content;
        var fm = content[..endIdx];
        // Убираем строки с expires:
        var cleaned = Regex.Replace(fm, @"\nexpires:\s*[^\n]*", "", RegexOptions.IgnoreCase);
        return cleaned + content[endIdx..];
    }

    // Вставляет поле source_session_id в frontmatter (или создаёт новый, если его нет).
    internal static string InjectSourceSessionId(string content, string sessionId)
    {
        if (content.StartsWith("---"))
        {
            var idx = content.IndexOf('\n', 3);
            if (idx > 0)
                return content[..(idx + 1)] + $"source_session_id: {sessionId}\n" + content[(idx + 1)..];
        }
        return $"---\nsource_session_id: {sessionId}\n---\n{content}";
    }

    // Вставляет поле file (привязка к файлу проекта) в frontmatter — мерж с уже
    // существующим frontmatter из content, как у source_session_id.
    internal static string InjectFileField(string content, string filePath)
    {
        if (content.StartsWith("---"))
        {
            var idx = content.IndexOf('\n', 3);
            if (idx > 0)
                return content[..(idx + 1)] + $"file: {filePath}\n" + content[(idx + 1)..];
        }
        return $"---\nfile: {filePath}\n---\n{content}";
    }

    // --- Проекции ---

    // FileRef у личных заметок отброшен ещё при скане (ProjectRoot null) — проекции
    // просто транслируют поле
    private NoteSummary ToSummary(RawNote n) =>
        new(n.Id, n.Title, n.SourceKey, n.SourceLabel, n.RelPath, n.Tags, n.CreatedAt, n.UpdatedAt,
            n.ExpiresAt, n.SourceSessionId, n.Annotation, n.FileRef, n.FileMissing);

    private NoteDetail ToDetail(Model model, RawNote n) =>
        new(n.Id, n.Title, n.SourceKey, n.SourceLabel, n.RelPath, n.Content, n.Tags,
            model.OutLinks.GetValueOrDefault(n.Id) ?? new(),
            model.Backlinks.GetValueOrDefault(n.Id) ?? new(),
            model.Unlinked.GetValueOrDefault(n.Id) ?? new(),
            n.CreatedAt, n.UpdatedAt, n.ExpiresAt, n.SourceSessionId, n.Annotation,
            n.FileRef, n.FileMissing);

    // --- Разрешение источника и права к нему ---

    /// <summary>
    /// Область вызывающего к проекту-источнику или null — доступа нет. Решает её
    /// <see cref="IProjectAccess"/>: роль по членству и гейт «рубильник и флаг» (S1-2) живут
    /// там, собственной таблицы прав здесь нет. Точка доступа резолвится при первом обращении
    /// (см. комментарий поля _services): к этому моменту синглтон NotesService уже создан и
    /// резолв не ре-ентрит в свой ключ. Без точки доступа (тестовое конструирование сервиса
    /// вне DI или непробужденный провайдер) остаётся собственное владение: для чужого проекта
    /// тот же отказ, что при выключенном гейте — fail-closed, а не «разрешили на всякий случай».
    /// </summary>
    private RequestScope? ProjectScope(string userId, string projectId, ProjectRight right)
    {
        // Гонка первой резолва безопасна: GetService отдаёт тот же синглтон, лишняя запись
        // идемпотентна. GetService, не GetRequiredService: непробужденный провайдер (тест вне
        // DI) — не ошибка, а путь к запасной ветке владения.
        var access = _access ??= _services?.GetService<IProjectAccess>();
        if (access is not null) return access.Resolve(userId, projectId, right);
        var own = _projects.GetById(projectId);
        if (own is null || own.OwnerId != userId) return null;
        return new RequestScope(userId, own.OwnerId!, ProjectRole.Owner);
    }

    /// <summary>
    /// S2c-5: боковые пути записи заметок (мост «чекбокс → задача», перенос между источниками)
    /// проверяются owner-Write ЯВНО, здесь, а не через право основной двери заметки: двери
    /// Create/Update/Delete/Move внутри источника переведены на WriteContent (S2c-7), а эти пути
    /// остались закрытыми для не-владельца. Отказ тот же, что у ResolveRoot: чужой проект без
    /// права записи — UnauthorizedAccessException → 403.
    /// </summary>
    public void EnsureSourceWrite(string userId, string sourceKey) => _ = ResolveRoot(userId, sourceKey, ProjectRight.Write);

    // S2c-5: каскад ссылок (удаление/переименование заметки) пишет только в источниках, где у
    // вызывающего есть WriteContent: его личный vault и проекты, где он владелец или редактор.
    // Read-only источник пропускается: каскад не правит чужой проект руками того, кто удаляет
    // или переименоывает свою заметку.
    private bool CanWriteContentIn(string userId, string sourceKey) =>
        sourceKey == PersonalKey || ProjectScope(userId, sourceKey, ProjectRight.WriteContent) is not null;

    /// <summary>
    /// Корень источника заметок. Личный vault — папка самого вызывающего (личное ключается
    /// личным id, карта S0-3 §3 «сверх перечня»). Проектный источник — notes/ самого проекта:
    /// папка одна на всех и лежит она у арендатора, поэтому корень берётся из проекта,
    /// к которому вызывающего допустила точка доступа (RequestScope.TenantOwnerId), а не
    /// выводится из id вызывающего: иначе участник и владелец читали бы разные файлы
    /// об одном проекте.
    /// Отказы: нет права чтения (посторонний, выключенный флаг или рубильник, проекта нет) —
    /// KeyNotFoundException → 404, «чужое» и «несуществующее» не различаем, как в IProjectAccess;
    /// право чтения есть, а нужного права нет (читатель не пишет; с S2c-7 редактор пишет
    /// содержимое через WriteContent, а owner-двери вроде переноса между источниками ему закрыты) —
    /// UnauthorizedAccessException → 403.
    /// </summary>
    private string ResolveRoot(string userId, string sourceKey, ProjectRight right = ProjectRight.Write)
    {
        if (sourceKey == PersonalKey)
            return Path.Combine(_dataDir, "notes", userId);

        if (ProjectScope(userId, sourceKey, ProjectRight.Read) is null)
            throw new KeyNotFoundException($"Источник {sourceKey} не найден");
        if (right != ProjectRight.Read && ProjectScope(userId, sourceKey, right) is null)
            throw new UnauthorizedAccessException("Нет права на это действие в проекте");

        var project = _projects.GetById(sourceKey)
            ?? throw new KeyNotFoundException($"Источник {sourceKey} не найден");
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("У проекта нет корневой папки");
        return Path.Combine(Composition.ProjectCapabilityGuard.ServerRoot(project), "notes");
    }

    /// <summary>
    /// S3-3c1 (по образцу S3-3b, FilesController.RecordHumanEdit): УСПЕШНАЯ правка заметки
    /// общего проекта человеком — в журнал правок (S3-3). Корень — RootPath проекта, а не его
    /// notes/: путь в журнале = «notes/<файл>.md», тот же, что рисует git и ключ file_uncommitted.
    /// Личный vault — вне журнала (у него нет общего проекта и git-репозитория). Отбирать общий
    /// проект, репозиторий и автора-участника — правило самого журнала (HumanEditCommitter.Record),
    /// здесь не повторяем его. Вызывается ВНУТРИ замка *Core: порядок записей в журнале обязан
    /// совпадать с порядком записей файла (иначе автором последней правки окажется проигравший).
    /// </summary>
    private void RecordHumanEdit(string userId, string sourceKey, string full)
    {
        if (_humanEdits is null || sourceKey == PersonalKey) return;
        var project = _projects.GetById(sourceKey);
        if (project is null || string.IsNullOrWhiteSpace(project.RootPath)) return;
        _humanEdits.Record(sourceKey, project.RootPath,
            Path.GetRelativePath(project.RootPath, full).Replace('\\', '/'), userId);
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

    // Список недопустимых символов неизменяем — он не пересоздаётся на каждый сегмент:
    // SanitizeFolder зовёт SanitizeFileName на каждый сегмент глубокого пути.
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static string SanitizeFileName(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title.Trim())
            sb.Append(Array.IndexOf(InvalidFileNameChars, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim().TrimEnd('.');
    }

    private static string SafeTime(Func<DateTime> f)
    {
        try { return f().ToString("o"); } catch { return DateTime.UtcNow.ToString("o"); }
    }

    // Хеш содержимого заметки (К8): SHA-256 по UTF-8-байтам текста так, как его видит сервис
    // (File.ReadAllText). Одно и то же правило служит ETag в GET/PUT и сравнению в If-Match —
    // второго определения хеша нет. Заметки пишутся без BOM (UTF8Encoding(false)), поэтому
    // хеш по строке совпадает с хешом байтов файла на диске.
    internal static string ContentSha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    // Токен If-Match: снять кавычки и маркер слабого ETag «W/» (RFC 9110).
    private static string UnquoteETag(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        return v.Trim('"').Trim();
    }
}

// Расхождение If-Match и фактического хеша содержимого (S2c-2, К8): заметка на диске не тронута,
// контроллер отвечает 409 note_changed с актуальным состоянием (хеш, содержимое, время правки).
public sealed class NoteConflictException : Exception
{
    public string Content { get; }     // фактическое содержимое на диске
    public string Sha256 { get; }      // его хеш
    public string ChangedAt { get; }   // время последней записи файла (ISO 8601)

    public NoteConflictException(string content, string sha256, string changedAt)
        : base("Заметка изменилась с момента чтения")
    {
        Content = content;
        Sha256 = sha256;
        ChangedAt = changedAt;
    }
}
