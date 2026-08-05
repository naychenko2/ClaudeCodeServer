using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Docs;

// Индекс документации проекта для панели «Доки»: README.md в корне + docs/**/*.md.
//
// Зачем отдельный сервис, а не files/tree + files/content: панели нужна документация как
// СВЯЗНЫЙ КОРПУС — заголовки, слаги якорей, ссылки между документами и обратные ссылки.
// Собрать это с фронта означало бы скачать все документы и разобрать их в браузере на
// каждое открытие панели.
//
// Кеш живёт на КОРЕНЬ ПАПКИ, а не на проект: у соседей по папке (один RootPath, разные
// владельцы) документация одна и та же, разбирать её дважды незачем. Доступ проверяет
// контроллер — сюда попадает уже разрешённый корень.
// files необязателен (DI подставляет): нужен только созданию документов — писать в рабочее
// дерево продукт обязан через файловый сервис, там SafeJoin и уведомление OnMutated, на
// котором висит синк базы знаний. Юнит-тестам чтения он ни к чему.
public sealed partial class DocsIndexService(FileService? files = null)
{
    // Область по умолчанию, пока проект не настроил свою: docs/ + README.md + markdown.
    // Имена точные: на Linux файловая система регистрозависима, и «Docs/» — другая папка.
    public static readonly DocsScope DefaultScope = new(["docs"], ["README.md"], ["markdown"]);

    // Что можно включить в документацию — ровно то, что продукт умеет открыть
    // (FileService.ViewableDocuments / IsImageFile / IsAudioFile / IsVideoFile и drawio
    // в FileViewer). Группами, а не списком расширений: их три десятка, и в настройке
    // они не читаются.
    //
    // Text=false — файл без текста: он числится в списке и открывается в центральной
    // области, но заголовков, ссылок и поиска по телу у него нет и быть не может.
    public static readonly IReadOnlyList<DocTypeGroup> TypeGroups =
    [
        new("markdown", "Markdown", [".md"], true),
        new("text", "Текст", [".txt"], true),
        new("pdf", "PDF", [".pdf"], false),
        new("office", "Office", [".docx", ".xlsx", ".pptx"], false),
        new("visio", "Visio", [".vsdx", ".vsdm", ".vssx", ".vssm", ".vstx", ".vstm"], false),
        new("diagram", "Диаграммы", [".drawio", ".dio"], false),
        new("image", "Картинки", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp"], false),
        new("audio", "Аудио", [".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".opus", ".weba"], false),
        new("video", "Видео", [".mp4", ".webm", ".mov", ".avi", ".mkv"], false),
    ];

    public static readonly IReadOnlyList<string> SupportedExtensions =
        [.. TypeGroups.SelectMany(g => g.Extensions)];

    // Расширения, содержимое которых разбирается в корпус
    private static readonly HashSet<string> TextExtensions =
        new(TypeGroups.Where(g => g.Text).SelectMany(g => g.Extensions), StringComparer.OrdinalIgnoreCase);

    private static bool IsTextDoc(string path) => TextExtensions.Contains(Path.GetExtension(path));

    // Расширения выбранных групп — область работает с ними, а хранится в группах
    public static IReadOnlyList<string> ExtensionsOf(IReadOnlyList<string> types) =>
        [.. TypeGroups.Where(g => types.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
            .SelectMany(g => g.Extensions)];

    // Предохранители: область не должна превращаться в обход всего репозитория
    private const int MaxDocs = 2000;
    private const long MaxDocBytes = 2 * 1024 * 1024;
    // Больше папок в области — это уже «весь репозиторий», а список в диалоге перестаёт читаться
    private const int MaxFolders = 30;
    private const int MaxRootFiles = 50;

    // Глубина и объём поиска кандидатов в папки: диалогу нужны обозримые варианты,
    // а не полный обход репозитория с node_modules
    private const int SuggestMaxDepth = 3;
    private const int SuggestMaxFolders = 200;

    // Папки, которые не документация ни в одном проекте: обходить их дорого, а .md внутри
    // (README пакетов, шаблоны генераторов) только зашумили бы и область, и список кандидатов
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor",
        "packages", "venv", "__pycache__", "coverage", "TestResults",
    };

    // Сколько символов текста показываем вокруг совпадения в поиске
    private const int SnippetRadius = 60;

    // Файл порядка страниц — из Azure DevOps code wiki: по одному на папку, построчный список
    // имён без расширения. Читаем его, чтобы панель показывала документацию в том же порядке,
    // в каком её увидит читатель опубликованной wiki: алфавит по заголовку разрушает
    // выстроенный автором маршрут чтения.
    private const string OrderFileName = ".order";

    // Описание области в корне репозитория. Настройка проекта живёт в хранилище продукта и
    // у каждого владельца своя; файл версионируется вместе с документами, поэтому у всех,
    // кто открыл репозиторий, документация одна и та же.
    public const string ScopeFileName = ".docs";

    // Имена полей — camelCase, как в API. Регистр игнорируем, неизвестные поля отбрасываем:
    // файл переживёт и старый бэкенд, и новое поле в формате.
    private static readonly JsonSerializerOptions ScopeFileJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Пустое поле не пишем: «home: null» в файле читается как выбранный пустой путь,
        // хотя означает ровно противоположное — «начальный документ выбирается сам»
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Сырой файл: все оси необязательны. Отсутствующая — «как по умолчанию», пустой массив —
    // осознанное «ничего отсюда» (это разные вещи, и нормализаторы их различают).
    private sealed record ScopeFileShape(
        List<string>? Folders, List<string>? RootFiles, List<string>? Types, string? Home);

    // Результат чтения .docs: область либо причина, по которой файл не применился.
    // Ошибку показываем в диалоге — молча игнорировать битый файл нельзя, иначе «почему
    // не применилось» выясняется только по логам сервера.
    public record ScopeFileResult(DocsScope? Scope, string? Error);

    private readonly ConcurrentDictionary<string, CachedIndex> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Разобранный корпус + отпечаток файлов, по которому решаем, не устарел ли он
    private sealed record CachedIndex(string Fingerprint, DocsCorpus Corpus);

    internal sealed class DocsCorpus
    {
        public required IReadOnlyList<DocEntry> Docs { get; init; }
        // Ключи всех словарей — относительный путь документа с прямыми слэшами.
        // Сравнение без учёта регистра: ссылки в доках пишут вольно, а на Windows
        // (среда разработки) регистр и так не различается. Коллизии на регистро-
        // зависимой ФС не роняют разбор — вторая запись просто не добавляется.
        public required Dictionary<string, DocEntry> ByPath { get; init; }
        public required Dictionary<string, string> Texts { get; init; }
        public required Dictionary<string, List<DocLink>> OutLinks { get; init; }
        public required Dictionary<string, List<DocBacklink>> Backlinks { get; init; }
        // Строки .order по папкам («» — корень проекта). Нужны и после сортировки:
        // «Начало» по умолчанию берётся из первой строки файла.
        public required Dictionary<string, IReadOnlyList<string>> Orders { get; init; }
    }

    // ---------- публичное API ----------

    // scope во всех методах: null — область по умолчанию. Настройка приходит из полей
    // Project.Docs*, разбирать её здесь незачем — сервис не знает про проекты и работает
    // от корня папки.
    public IReadOnlyList<DocEntry> GetIndex(string rootPath, DocsScope? scope = null) =>
        GetCorpus(rootPath, scope).Docs;

    // Документ с содержимым и связями. null — путь вне области документации: это и есть
    // гейт эндпоинта. Проверяем ВХОЖДЕНИЕМ В ИНДЕКС, а не сравнением строки с «docs/»:
    // индекс построен обходом реальной файловой системы, поэтому вопрос регистра и
    // разделителей решается один раз здесь, одинаково для Windows и Linux.
    public DocDetail? GetDoc(string rootPath, string relativePath, DocsScope? scope = null)
    {
        var corpus = GetCorpus(rootPath, scope);
        var key = NormalizePath(relativePath);
        if (key is null || !corpus.ByPath.TryGetValue(key, out var entry)) return null;
        // Бинарный отдаётся без содержимого: панель предложит открыть его в центре,
        // где живут просмотрщики pdf/office/visio/картинок/звука
        if (entry.Binary)
            return new DocDetail(entry.Path, entry.Title, "", [],
                corpus.Backlinks.TryGetValue(key, out var refs) ? refs : [], Binary: true);
        if (!corpus.Texts.TryGetValue(key, out var text)) return null;
        return new DocDetail(
            entry.Path, entry.Title, text,
            corpus.OutLinks.TryGetValue(key, out var outs) ? outs : [],
            corpus.Backlinks.TryGetValue(key, out var backs) ? backs : []);
    }

    public IReadOnlyList<DocSearchHit> Search(string rootPath, string query,
        DocsScope? scope = null, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var corpus = GetCorpus(rootPath, scope);
        var q = query.Trim();
        var hits = new List<DocSearchHit>();

        foreach (var doc in corpus.Docs)
        {
            if (hits.Count >= limit) break;

            // Заголовок документа и путь — совпадение без фрагмента текста
            if (doc.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                doc.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new DocSearchHit(doc.Path, doc.Title, null, doc.Title));
                continue;
            }

            // Подзаголовок — ведём сразу к разделу
            var heading = doc.Headings.FirstOrDefault(h => h.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (heading is not null)
            {
                hits.Add(new DocSearchHit(doc.Path, doc.Title, heading.Slug, heading.Text));
                continue;
            }

            // Тело документа — фрагмент вокруг совпадения + якорь ближайшего заголовка выше
            if (!corpus.Texts.TryGetValue(doc.Path, out var text)) continue;
            var idx = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            hits.Add(new DocSearchHit(doc.Path, doc.Title, HeadingAbove(text, idx, doc.Headings), Snippet(text, idx, q.Length)));
        }

        return hits;
    }

    // ---------- настройка области ----------

    // Область к каноничному виду. Мусор молча отбрасывается — настройка приходит с фронта,
    // и ронять из-за неё индекс всего проекта незачем. Пустой список НЕ подменяется
    // дефолтом: «снял все галки» — осознанный выбор, а не отсутствие настройки (это null).
    public static DocsScope NormalizeScope(DocsScope? scope) => scope is null
        ? DefaultScope
        : new DocsScope(
            NormalizeFolders(scope.Folders),
            NormalizeRootFiles(scope.RootFiles),
            NormalizeTypes(scope.Types),
            NormalizeHome(scope.Home));

    // Домашний документ — путь от корня проекта (в отличие от файлов корня, он может
    // лежать в папке). Значение вне корня отбрасывается: гейт области дальше всё равно
    // не отдаст такой документ, и молчаливо пустое «Начало» было бы непонятным
    public static string? NormalizeHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home)) return null;
        var s = home.Trim().Replace('\\', '/').TrimStart('/');
        if (s.Contains(':')) return null;
        var segments = new List<string>();
        foreach (var seg in s.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") return null;
            segments.Add(seg);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    // Что панель показывает «Началом»: явно выбранный документ, если он есть в корпусе,
    // иначе README из корня. Решает бэкенд, а не фронт: правило одно и то же для индекса,
    // и панели незачем знать про «readme.*» и порядок предпочтений
    public string? ResolveHome(string rootPath, DocsScope? rawScope = null)
    {
        var scope = NormalizeScope(rawScope);
        var corpus = GetCorpus(rootPath, scope);
        if (scope.Home is not null && corpus.ByPath.TryGetValue(scope.Home, out var chosen))
            return chosen.Path;
        // Первая строка .order первой папки области: автор выстроил порядок сам, и первая
        // страница списка — вход в документацию (так же её трактует code wiki)
        if (scope.Folders.Count > 0 && FirstOfOrder(corpus, scope.Folders[0]) is { } first)
            return first;
        // README корня. Дальше не спускаемся: null здесь означает «начального документа нет»,
        // и панель по нему предлагает вернуть README в область — подмена первым попавшимся
        // документом эту подсказку бы отняла
        return corpus.Docs.FirstOrDefault(d => IsReadme(d.Path))?.Path;
    }

    // Документ, названный первой строкой .order указанной папки
    private static string? FirstOfOrder(DocsCorpus corpus, string folder)
    {
        if (!corpus.Orders.TryGetValue(folder, out var names) || names.Count == 0) return null;
        foreach (var doc in corpus.Docs)
        {
            if (!string.Equals(Folder(doc.Path), folder, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(Path.GetFileNameWithoutExtension(doc.Path), names[0], StringComparison.OrdinalIgnoreCase))
                return doc.Path;
        }
        return null;
    }

    // Собрать область из полей проекта: у каждой оси свой null со своим дефолтом
    public static DocsScope ScopeOf(Project project) => NormalizeScope(new DocsScope(
        project.DocsFolders ?? DefaultScope.Folders,
        project.DocsRootFiles ?? DefaultScope.RootFiles,
        project.DocsTypes ?? DefaultScope.Types,
        project.DocsHome));

    // Единственная точка резолва области: файл репозитория сильнее настройки проекта.
    // Иначе двое владельцев одной папки видели бы разную документацию — ровно то, от чего
    // уходили, вынося настройку в репозиторий. Файл читается на каждый вызов (он крошечный):
    // правку из git панель обязана заметить без перезапуска.
    public DocsScope ResolveScope(Project project) =>
        ReadScopeFile(project.RootPath).Scope ?? ScopeOf(project);

    // Область из файла .docs. Scope = null — файла нет либо он не разобран; тогда действует
    // настройка проекта, а причина уезжает во фронт вместе с описанием области.
    public ScopeFileResult ReadScopeFile(string rootPath)
    {
        var file = Path.Combine(Path.GetFullPath(rootPath), ScopeFileName);
        string json;
        try
        {
            if (!File.Exists(file)) return new ScopeFileResult(null, null);
            json = File.ReadAllText(file);
        }
        catch (IOException) { return new ScopeFileResult(null, null); }          // пишут прямо сейчас
        catch (UnauthorizedAccessException) { return new ScopeFileResult(null, null); }

        ScopeFileShape? parsed;
        try { parsed = JsonSerializer.Deserialize<ScopeFileShape>(json, ScopeFileJson); }
        catch (JsonException e) { return new ScopeFileResult(null, e.Message); }

        // Пустой файл или «null» — то же, что отсутствие: описания области в нём нет
        if (parsed is null) return new ScopeFileResult(null, null);

        // Нормализаторы вызываются поштучно, а не через NormalizeScope: у каждой оси свой
        // дефолт на null, и общий конструктор DocsScope такой формы не принимает
        return new ScopeFileResult(new DocsScope(
            NormalizeFolders(parsed.Folders),
            NormalizeRootFiles(parsed.RootFiles),
            NormalizeTypes(parsed.Types),
            NormalizeHome(parsed.Home)), null);
    }

    // Записать область в файл репозитория. Пишем все оси явно: файл читают и правят руками,
    // и «чего нет — то по умолчанию» в сохранённом виде сбивало бы с толку. Home опускается,
    // когда его нет: пустая строка в файле выглядела бы как выбранный пустой путь.
    public void WriteScopeFile(string rootPath, DocsScope scope)
    {
        var normalized = NormalizeScope(scope);
        var shape = new ScopeFileShape(
            [.. normalized.Folders], [.. normalized.RootFiles], [.. normalized.Types], normalized.Home);
        var file = Path.Combine(Path.GetFullPath(rootPath), ScopeFileName);
        // Без BOM и с \n: файл лежит в репозитории, и лишние байты дают шум в диффе
        File.WriteAllText(file, JsonSerializer.Serialize(shape, ScopeFileJson).ReplaceLineEndings("\n") + "\n",
            new UTF8Encoding(false));
    }

    // Папки: прямые слэши, без краёв-разделителей, без дублей и без выходов за корень
    public static IReadOnlyList<string> NormalizeFolders(IReadOnlyList<string>? folders)
    {
        if (folders is null) return DefaultScope.Folders;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in folders)
        {
            var folder = NormalizeFolder(raw);
            if (folder is null || !seen.Add(folder)) continue;
            result.Add(folder);
            if (result.Count >= MaxFolders) break;
        }
        return result;
    }

    // Корневые файлы — именами, без путей: подпапки задаются папками, и «docs/x.md»
    // здесь означал бы вторую дорогу к тому же файлу мимо настройки папок
    public static IReadOnlyList<string> NormalizeRootFiles(IReadOnlyList<string>? files)
    {
        if (files is null) return DefaultScope.RootFiles;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in files)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim().Replace('\\', '/');
            if (name.Contains('/') || name.Contains(':') || name is "." or "..") continue;
            if (!seen.Add(name)) continue;
            result.Add(name);
            if (result.Count >= MaxRootFiles) break;
        }
        return result;
    }

    // Типы: только известные группы каталога. Порядок — как в каталоге, чтобы настройка
    // не зависела от того, в каком порядке юзер щёлкал чипы
    public static IReadOnlyList<string> NormalizeTypes(IReadOnlyList<string>? types)
    {
        if (types is null) return DefaultScope.Types;
        return [.. TypeGroups.Select(g => g.Key)
            .Where(key => types.Contains(key, StringComparer.OrdinalIgnoreCase))];
    }

    // Одна папка настройки. null — значение непригодно: пустое, абсолютное («C:\…», «/etc»)
    // или уводящее выше корня. Корень проекта («.», «/») тоже отбрасываем: выбор корня
    // означал бы обход всего репозитория, а README и так в области всегда.
    private static string? NormalizeFolder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace('\\', '/');
        if (s.Contains(':')) return null;               // «C:/…» и alternate data stream
        var segments = new List<string>();
        foreach (var seg in s.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") return null;               // выход за корень проекта
            segments.Add(seg);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    // ---------- сборка корпуса ----------

    private DocsCorpus GetCorpus(string rootPath, DocsScope? rawScope)
    {
        var root = Path.GetFullPath(rootPath);
        var scope = NormalizeScope(rawScope);
        // Ключ кеша — корень ВМЕСТЕ с областью: у соседей по папке (один RootPath, разные
        // владельцы) настройки свои, и без области в ключе они вытесняли бы корпус друг друга
        var key = $"{root}\n{string.Join('|', scope.Folders)}\n{string.Join('|', scope.RootFiles)}\n{string.Join('|', scope.Types)}";
        var files = CollectFiles(root, scope);
        // Файлы порядка обязаны попасть в отпечаток: правка .order не меняет ни один документ,
        // и без них кеш не инвалидируется — панель показывала бы прежний порядок до перезапуска
        var orderFiles = CollectOrderFiles(root, files);
        var fingerprint = Fingerprint(root, [.. files, .. orderFiles]);

        if (_cache.TryGetValue(key, out var cached) && cached.Fingerprint == fingerprint)
            return cached.Corpus;

        var corpus = BuildCorpus(root, files, ReadOrders(root, orderFiles));
        _cache[key] = new CachedIndex(fingerprint, corpus);
        return corpus;
    }

    // Файлы области: выбранные файлы корня поимённо + выбранные папки целиком.
    // Корневые файлы берём как названы, не проверяя расширение: раз пользователь выбрал
    // файл явно — он важнее общего фильтра типов.
    private static List<string> CollectFiles(string root, DocsScope scope)
    {
        var files = new List<string>();
        // Вложенные друг в друга папки настройки («docs» и «docs/adr») дают один файл дважды
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in scope.RootFiles)
            {
                var file = Path.Combine(root, name);
                if (File.Exists(file) && seen.Add(file)) files.Add(file);
            }

            foreach (var folder in scope.Folders)
            {
                if (files.Count >= MaxDocs) break;
                var dir = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) continue;
                foreach (var file in EnumerateDocs(dir, ExtensionsOf(scope.Types)))
                {
                    if (files.Count >= MaxDocs) break;
                    if (seen.Add(file)) files.Add(file);
                }
            }
        }
        catch (DirectoryNotFoundException) { /* папку удалили между проверкой и обходом */ }
        catch (UnauthorizedAccessException) { /* нет прав на часть дерева — отдаём что смогли */ }
        return files;
    }

    // Файлы .order по всем папкам дерева документов, включая промежуточные: порядок папки
    // нужен, даже когда её собственные документы лежат уровнем ниже (docs/ и docs/adr/).
    // Корень проекта тоже участвует — в области бывают файлы корня (README.md).
    private static List<string> CollectOrderFiles(string root, List<string> files)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var file in files)
        {
            var folder = Folder(Relative(root, file));
            // Поднимаемся до корня; встретили уже добавленную папку — выше тоже добавляли
            while (folder.Length > 0 && folders.Add(folder))
                folder = Folder(folder);
        }

        var result = new List<string>();
        foreach (var folder in folders)
        {
            var dir = folder.Length == 0
                ? root
                : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            var path = Path.Combine(dir, OrderFileName);
            if (File.Exists(path)) result.Add(path);
        }
        return result;
    }

    // Строки .order по папкам («» — корень проекта). Пустые строки и края-пробелы
    // отброшены, BOM срезан: файл правят руками, в том числе редакторами Windows.
    private static Dictionary<string, IReadOnlyList<string>> ReadOrders(string root, List<string> orderFiles)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in orderFiles)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }             // файл переписывают прямо сейчас
            catch (UnauthorizedAccessException) { continue; }

            var names = new List<string>();
            foreach (var raw in lines)
            {   // ﻿ — BOM: ReadAllLines снимает его сам, но не когда файл записан
                // как UTF-8 с BOM внутри уже открытого потока
                var name = raw.Trim('﻿', ' ', '\t', '\r');
                if (name.Length > 0) names.Add(name);
            }
            result[Folder(Relative(root, file))] = names;
        }
        return result;
    }

    private static bool HasExtension(string path, IReadOnlyList<string> extensions)
    {
        var ext = Path.GetExtension(path);
        foreach (var allowed in extensions)
            if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Обход вручную, а не EnumerateFiles(AllDirectories): нужен пропуск служебных
    // подпапок. Выбранной может оказаться папка с node_modules внутри, и рекурсия туда
    // затянула бы тысячи чужих README.
    private static IEnumerable<string> EnumerateDocs(string dir, IReadOnlyList<string> extensions)
    {
        var queue = new Queue<string>();
        queue.Enqueue(dir);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(current);
                subdirs = Directory.GetDirectories(current);
            }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            // Фильтр по расширениям в коде, а не маской GetFiles: расширений в области
            // несколько, и один проход дешевле нескольких обходов той же папки
            foreach (var f in files)
                if (HasExtension(f, extensions)) yield return f;
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                queue.Enqueue(sub);
            }
        }
    }

    // Настройка области проекта вместе с источником: файл репозитория или настройка продукта.
    // Отдельный метод от Describe(rootPath, scope), потому что источник знает только резолв —
    // а диалогу без него не понять, что именно он правит
    public DocsScopeInfo Describe(Project project)
    {
        var file = ReadScopeFile(project.RootPath);
        var info = Describe(project.RootPath, file.Scope ?? ScopeOf(project));
        return info with
        {
            ScopeSource = file.Scope is not null ? "file" : "project",
            ScopeFileError = file.Error,
        };
    }

    // Настройка области целиком: что выбрано, что можно выбрать, что было бы по умолчанию
    public DocsScopeInfo Describe(string rootPath, DocsScope? rawScope = null)
    {
        var scope = NormalizeScope(rawScope);
        return new DocsScopeInfo(
            scope,
            SuggestFolders(rootPath, scope),
            SuggestRootFiles(rootPath, scope),
            TypeGroups,
            DefaultScope,
            // Документы области — из них выбирают «Начало»; заодно панель узнаёт,
            // какой документ им сейчас работает
            GetIndex(rootPath, scope).Select(d => new DocOption(d.Path, d.Title)).ToList(),
            ResolveHome(rootPath, scope));
    }

    // Кандидаты в корневые файлы: всё подходящее по расширению, что лежит в корне.
    // Считаем по ВСЕМ поддерживаемым расширениям, а не по выбранным: иначе, сузив типы
    // до .md, пользователь терял бы из виду свой же выбранный CHANGELOG.txt.
    public IReadOnlyList<DocRootFileCandidate> SuggestRootFiles(string rootPath, DocsScope? rawScope = null)
    {
        var root = Path.GetFullPath(rootPath);
        var scope = NormalizeScope(rawScope);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.GetFiles(root))
            {
                if (!HasExtension(file, SupportedExtensions)) continue;
                names.Add(Path.GetFileName(file));
                if (names.Count >= MaxRootFiles) break;
            }
        }
        catch (DirectoryNotFoundException) { /* корень проекта исчез — отдаём выбранные */ }
        catch (UnauthorizedAccessException) { }

        foreach (var name in scope.RootFiles) names.Add(name);

        return names
            .Select(n => new DocRootFileCandidate(n, File.Exists(Path.Combine(root, n))))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Кандидаты в папки документации: папки с документами внутри, неглубоко и без служебных.
    // Выбранные добавляются всегда — в том числе несуществующие, иначе галка на удалённой
    // папке пропала бы из диалога, и пустой список документов выглядел бы поломкой.
    public IReadOnlyList<DocFolderCandidate> SuggestFolders(string rootPath, DocsScope? rawScope = null)
    {
        var root = Path.GetFullPath(rootPath);
        var scope = NormalizeScope(rawScope);
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Walk(root, "", 0);

        foreach (var folder in scope.Folders)
            found.TryAdd(folder, 0);

        return found
            .Select(kv => new DocFolderCandidate(kv.Key, kv.Value,
                Directory.Exists(Path.Combine(root, kv.Key.Replace('/', Path.DirectorySeparatorChar)))))
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Возвращает число документов в поддереве: родительская папка показывает суммарный
        // счётчик, даже когда её собственные документы лежат уровнем ниже (docs/ и docs/adr/).
        // Считаем по ВСЕМ поддерживаемым расширениям, а не по выбранным типам: иначе цифра
        // в диалоге врала бы, пока выбор типов там ещё редактируется и не сохранён
        int Walk(string dir, string rel, int depth)
        {
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (DirectoryNotFoundException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }

            var count = files.Count(f => HasExtension(f, SupportedExtensions));
            if (depth < SuggestMaxDepth)
            {
                foreach (var sub in subdirs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                    count += Walk(sub, rel.Length == 0 ? name : $"{rel}/{name}", depth + 1);
                }
            }
            // Корень проекта папкой-кандидатом не бывает: его выбор = обход всего репозитория
            if (rel.Length > 0 && count > 0 && found.Count < SuggestMaxFolders) found[rel] = count;
            return count;
        }
    }

    // Отпечаток области: путь + время правки + размер каждого файла. Не «максимальный
    // mtime + количество»: удаление одного файла с добавлением другого в ту же секунду
    // такой ключ не заметил бы, и панель показывала бы устаревший корпус.
    private static string Fingerprint(string root, List<string> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            var info = new FileInfo(f);
            var rel = Relative(root, f);
            sb.Append(rel).Append('|')
              .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('|')
              .Append(info.Exists ? info.Length : -1);
            // Есть ли рядом одноимённая папка — то есть документ ли это или страница
            // раздела. Ни один файл при появлении пустой папки не меняется, и без этого
            // признака новый раздел не показался бы до следующей правки документов
            if (IsMarkdown(rel))
                sb.Append(SectionDirExists(root, rel[..^3]) ? "|s" : "|_");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static DocsCorpus BuildCorpus(string root, List<string> files,
        Dictionary<string, IReadOnlyList<string>> orders)
    {
        var docs = new List<DocEntry>();
        var byPath = new Dictionary<string, DocEntry>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Ссылки собираем сырыми: класс Doc/Repo можно определить только когда известен
        // ВЕСЬ состав области, поэтому классификация — вторым проходом ниже.
        var rawLinks = new Dictionary<string, List<ParsedLink>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var rel = Relative(root, file);
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists) continue;
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            // Файл без текста (pdf, visio, картинка, звук) числится в списке, но не читается:
            // держать в кеше мегабайты байтов незачем, а разбирать в них нечего
            if (!IsTextDoc(file))
            {
                var binary = new DocEntry(rel, Path.GetFileName(file), info.LastWriteTimeUtc, info.Length, [], Binary: true);
                if (!byPath.TryAdd(rel, binary)) continue;
                docs.Add(binary);
                continue;
            }

            string text;
            try
            {
                if (info.Length > MaxDocBytes) continue;
                text = File.ReadAllText(file);
            }
            catch (IOException) { continue; }             // файл переписывают прямо сейчас
            catch (UnauthorizedAccessException) { continue; }

            var parsed = ParseDocument(text);
            var title = parsed.Title ?? Path.GetFileNameWithoutExtension(file);
            var entry = new DocEntry(rel, title, info.LastWriteTimeUtc, info.Length, parsed.Headings);

            // TryAdd, а не индексатор: на регистрозависимой ФС рядом могут лежать
            // docs/api.md и docs/API.md — дубль по ключу не должен ронять разбор
            if (!byPath.TryAdd(rel, entry)) continue;
            docs.Add(entry);
            texts[rel] = text;
            rawLinks[rel] = parsed.Links;
        }

        var outLinks = new Dictionary<string, List<DocLink>>(StringComparer.OrdinalIgnoreCase);
        var backlinks = new Dictionary<string, List<DocBacklink>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (from, links) in rawLinks)
        {
            var resolved = new List<DocLink>();
            foreach (var link in links)
            {
                if (IsExternal(link.Target))
                {
                    resolved.Add(new DocLink(link.Target, null, DocLinkKind.External, link.Text));
                    continue;
                }

                // Ссылка-якорь внутри того же документа
                if (link.Target.Length == 0 && link.Anchor is not null)
                {
                    resolved.Add(new DocLink(from, link.Anchor, DocLinkKind.Doc, link.Text));
                    continue;
                }

                var target = ResolveRelative(from, link.Target);
                if (target is null) continue;   // ведёт за пределы проекта — не наша забота

                var kind = byPath.ContainsKey(target) ? DocLinkKind.Doc : DocLinkKind.Repo;
                resolved.Add(new DocLink(target, link.Anchor, kind, link.Text));

                if (kind != DocLinkKind.Doc || string.Equals(target, from, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Обратные ссылки — разворот исходящих, отдельного хранилища нет (как в NotesService)
                var sourceTitle = byPath.TryGetValue(from, out var src) ? src.Title : from;
                if (!backlinks.TryGetValue(target, out var list))
                    backlinks[target] = list = [];
                if (!list.Any(b => string.Equals(b.Path, from, StringComparison.OrdinalIgnoreCase) && b.Anchor == link.Anchor))
                    list.Add(new DocBacklink(from, sourceTitle, link.Anchor));
            }
            outLinks[from] = resolved;
        }

        // Пары «страница + папка» проставляем до сортировки: порядок опирается на эту связь —
        // страница раздела и его дочерние документы идут в дереве одной строкой .order
        MarkSections(root, docs, byPath);

        return new DocsCorpus
        {
            Docs = OrderDocs(docs, orders), ByPath = byPath, Texts = texts,
            OutLinks = outLinks, Backlinks = backlinks, Orders = orders,
        };
    }

    // ---------- порядок документов ----------

    // Документ, рядом с которым лежит одноимённая папка, — страница её раздела
    // («docs/decisions.md» + «docs/decisions/»). Записи в списке и в словаре подменяются
    // вместе: DocEntry неизменяем, а расходиться этим двум представлениям нельзя.
    private static void MarkSections(string root, List<DocEntry> docs, Dictionary<string, DocEntry> byPath)
    {
        // Канонический путь папки (в том регистре, в каком он пришёл из ФС) — фронту нужно
        // ровно то же значение, что стоит у дочерних документов в Folder(Path)
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs)
        {
            var folder = Folder(doc.Path);
            while (folder.Length > 0 && folders.TryAdd(folder, folder))
                folder = Folder(folder);
        }

        for (var i = 0; i < docs.Count; i++)
        {
            var doc = docs[i];
            var parent = Folder(doc.Path);
            var name = Path.GetFileNameWithoutExtension(doc.Path);
            var candidate = parent.Length == 0 ? name : $"{parent}/{name}";
            // Папка ЕЩЁ ПУСТА (только что созданный раздел) — документов в ней нет, и по
            // ним её не найти. Спрашиваем файловую систему: без этого новый раздел
            // выглядел бы обычной строкой, и войти в него, чтобы наполнить, было нечем
            if (!folders.TryGetValue(candidate, out var canonical))
            {
                if (!IsMarkdown(doc.Path) || !SectionDirExists(root, candidate)) continue;
                canonical = candidate;
            }
            var updated = doc with { SectionFolder = canonical };
            docs[i] = updated;
            byPath[doc.Path] = updated;
        }
    }

    // Лежит ли рядом с документом одноимённая папка. Отдельным методом, потому что ту же
    // проверку делает отпечаток: без неё появление папки в git не инвалидировало бы кеш —
    // ни один файл при этом не менялся, и раздел не появлялся бы до следующей правки
    private static bool SectionDirExists(string root, string relativeFolder)
    {
        try
        {
            return Directory.Exists(Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Узел уровня: имя без расширения, документы с этим именем и одноимённая папка.
    // Документ и папка объединены в один узел намеренно — в .order это ОДНА строка
    // («decisions» задаёт место и страницы раздела, и его содержимого).
    private sealed class OrderNode(string key)
    {
        public string Key { get; } = key;
        public List<DocEntry> Docs { get; } = [];
        public string? Folder { get; set; }
    }

    // Порядок документов = дерево папок, на каждом уровне упорядоченное своим .order.
    // Уплощается обходом в глубину, поэтому страница раздела идёт непосредственно перед
    // дочерними документами — как в дереве wiki. Неперечисленное встаёт после перечисленного
    // по прежнему правилу (README первым, затем заголовок): .order сортирует, а не фильтрует.
    private static List<DocEntry> OrderDocs(List<DocEntry> docs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> orders)
    {
        var byFolder = new Dictionary<string, List<DocEntry>>(StringComparer.OrdinalIgnoreCase);
        var subFolders = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in docs)
        {
            var folder = Folder(doc.Path);
            if (!byFolder.TryGetValue(folder, out var list)) byFolder[folder] = list = [];
            list.Add(doc);

            // Регистрируем цепочку папок до корня: промежуточная папка без собственных
            // документов всё равно должна попасть в дерево
            var current = folder;
            while (current.Length > 0)
            {
                var parent = Folder(current);
                if (!subFolders.TryGetValue(parent, out var set))
                    subFolders[parent] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!set.Add(current)) break;   // выше по цепочке уже регистрировали
                current = parent;
            }
        }

        var result = new List<DocEntry>(docs.Count);
        Walk("");
        return result;

        void Walk(string folder)
        {
            var nodes = new Dictionary<string, OrderNode>(StringComparer.OrdinalIgnoreCase);
            var level = new List<OrderNode>();

            OrderNode NodeFor(string key)
            {
                if (nodes.TryGetValue(key, out var existing)) return existing;
                var created = new OrderNode(key);
                nodes[key] = created;
                level.Add(created);
                return created;
            }

            if (byFolder.TryGetValue(folder, out var here))
                foreach (var doc in here)
                    NodeFor(Path.GetFileNameWithoutExtension(doc.Path)).Docs.Add(doc);

            if (subFolders.TryGetValue(folder, out var subs))
                foreach (var sub in subs)
                    NodeFor(NameOf(sub)).Folder = sub;

            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (orders.TryGetValue(folder, out var lines))
                for (var i = 0; i < lines.Count; i++) index.TryAdd(lines[i], i);

            level.Sort((a, b) => CompareNodes(a, b, index));

            foreach (var node in level)
            {
                // Один ключ — обычно один документ; несколько бывает при api.md рядом с api.pdf
                if (node.Docs.Count > 1)
                    node.Docs.Sort((x, y) => string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase));
                result.AddRange(node.Docs);
                if (node.Folder is not null) Walk(node.Folder);
            }
        }
    }

    private static int CompareNodes(OrderNode a, OrderNode b, Dictionary<string, int> index)
    {
        var ia = index.TryGetValue(a.Key, out var x) ? x : int.MaxValue;
        var ib = index.TryGetValue(b.Key, out var y) ? y : int.MaxValue;
        if (ia != ib) return ia.CompareTo(ib);

        // README — первый среди корневых: он вход в документацию, а по алфавиту заголовков
        // мог оказаться где угодно среди прочих файлов корня
        var readmeA = a.Docs.Count > 0 && IsReadme(a.Docs[0].Path);
        var readmeB = b.Docs.Count > 0 && IsReadme(b.Docs[0].Path);
        if (readmeA != readmeB) return readmeA ? -1 : 1;

        // Узел с подпапкой — ниже обычных документов уровня: без .order сохраняется прежнее
        // правило «папка старше заголовка», иначе вложенная группа вклинивалась бы между
        // документами своей же папки по алфавиту заголовков
        var nestedA = a.Folder is not null;
        var nestedB = b.Folder is not null;
        if (nestedA != nestedB) return nestedA ? 1 : -1;

        // Сравнение с учётом языка: заголовки русские, и ordinal ставил бы кириллицу после
        // латиницы, а внутри кириллицы — по кодам, а не по алфавиту
        var byTitle = string.Compare(TitleOf(a), TitleOf(b), StringComparison.CurrentCultureIgnoreCase);
        // Ключ добивает сравнение до детерминированного: List.Sort нестабилен
        return byTitle != 0 ? byTitle : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

        // У узла без документов (папка без страницы раздела) заголовка нет — сортируем по имени
        static string TitleOf(OrderNode node) => node.Docs.Count > 0 ? node.Docs[0].Title : node.Key;
    }

    // Последний сегмент пути папки: «docs/decisions» → «decisions»
    private static string NameOf(string folder)
    {
        var i = folder.LastIndexOf('/');
        return i < 0 ? folder : folder[(i + 1)..];
    }

    // ---------- запись порядка ----------

    public enum OrderWriteStatus { Ok, FolderNotInScope, BadItems }

    // Результат записи .order: причина отказа нужна контроллеру, чтобы развести 404 и 400
    public sealed record OrderWriteResult(OrderWriteStatus Status, string? Error = null);

    // Переставить строки .order указанной папки. items — имена БЕЗ расширения (как в файле)
    // в новом порядке; это подмножество уровня, а не весь его состав: панель показывает
    // документы папки и её разделы разными группами, и присылает то, что пользователь
    // реально видел.
    //
    // Перестановка идёт ПО ЗАНЯТЫМ ПОЗИЦИЯМ: строки, которых в items нет (раздел между
    // документами, документ с временно снятым типом, строка от чужой ветки), остаются на
    // своих местах. Иначе жест мышью выбрасывал бы из порядка то, чего пользователь не видел.
    public OrderWriteResult WriteOrder(string rootPath, string? folder,
        IReadOnlyList<string> items, DocsScope? scope = null)
    {
        var root = Path.GetFullPath(rootPath);

        string target;
        if (string.IsNullOrWhiteSpace(folder)) target = "";     // корень репозитория тоже уровень
        else
        {
            var normalized = NormalizeFolder(folder);
            if (normalized is null)
                return new OrderWriteResult(OrderWriteStatus.FolderNotInScope, $"Недопустимая папка: {folder}");
            target = normalized;
        }

        var corpus = GetCorpus(root, scope);
        var level = LevelNames(corpus, target);
        if (level is null)
            return new OrderWriteResult(OrderWriteStatus.FolderNotInScope,
                $"Папка вне области документации: {(target.Length == 0 ? "корень проекта" : target)}");

        // Имена сверяем с фактическим составом уровня: .order — не место для произвольных
        // строк от клиента, чужую строку туда может дописать только сам пользователь в git
        var wanted = new List<string>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in items)
        {
            var name = raw?.Trim() ?? "";
            if (name.Length == 0)
                return new OrderWriteResult(OrderWriteStatus.BadItems, "Пустое имя в списке порядка");
            if (!level.Contains(name, StringComparer.OrdinalIgnoreCase))
                return new OrderWriteResult(OrderWriteStatus.BadItems,
                    $"В папке нет документа или раздела «{name}»");
            if (!seen.Add(name))
                return new OrderWriteResult(OrderWriteStatus.BadItems, $"Имя повторяется: «{name}»");
            wanted.Add(name);
        }

        var dir = target.Length == 0 ? root : Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar));
        var file = Path.Combine(dir, OrderFileName);
        var existing = File.Exists(file) ? File.ReadAllText(file) : null;

        // Файла не было — фиксируем ВЕСЬ текущий порядок уровня, а не одну перетащенную
        // строку: иначе остальные документы стали бы неперечисленными и уехали в хвост,
        // то есть жест сломал бы порядок вместо того, чтобы его задать
        List<string> lines = existing is null ? [.. level] : SplitOrderLines(existing);

        // Появившееся мимо панели (git pull параллельно) дописываем в хвост: без этого
        // перетаскивание молча выкинуло бы чужой документ из порядка
        foreach (var name in level)
            if (!lines.Contains(name, StringComparer.OrdinalIgnoreCase))
                lines.Add(name);

        var slots = new List<int>(wanted.Count);
        for (var i = 0; i < lines.Count; i++)
            if (wanted.Contains(lines[i], StringComparer.OrdinalIgnoreCase)) slots.Add(i);
        for (var k = 0; k < slots.Count && k < wanted.Count; k++) lines[slots[k]] = wanted[k];

        // Стиль концов строк сохраняем: файл лежит в репозитории, и смена CRLF на LF
        // показала бы в диффе весь файл вместо одной перестановки. Новый пишем с \n —
        // git применит autocrlf сам. BOM не пишем никогда
        var eol = existing is not null && existing.Contains("\r\n") ? "\r\n" : "\n";
        File.WriteAllText(file, string.Join(eol, lines) + eol, new UTF8Encoding(false));
        return new OrderWriteResult(OrderWriteStatus.Ok);
    }

    // Дописать имя в конец .order, ЕСЛИ файл в папке уже есть. Нет файла — не создаём:
    // порядок в такой папке задан правилом индекса (README первым, дальше по заголовку),
    // и одно нажатие «Создать» не должно рожать в чужом репозитории файл на весь состав
    // папки, которого никто не просил. Появится он от перетаскивания — жеста, который
    // и означает «я задаю порядок сам».
    private static void AppendToOrder(string root, string folder, string name)
    {
        var dir = folder.Length == 0 ? root : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
        var file = Path.Combine(dir, OrderFileName);
        if (!File.Exists(file)) return;

        var existing = File.ReadAllText(file);
        var lines = SplitOrderLines(existing);
        if (lines.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        lines.Add(name);
        var eol = existing.Contains("\r\n") ? "\r\n" : "\n";
        File.WriteAllText(file, string.Join(eol, lines) + eol, new UTF8Encoding(false));
    }

    // Строки существующего .order как есть (без пустых и краёв-пробелов) — их порядок
    // и состав переживают запись, включая имена, которым сейчас не соответствует файл
    private static List<string> SplitOrderLines(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var name = raw.Trim('﻿', ' ', '\t', '\r');
            if (name.Length > 0) result.Add(name);
        }
        return result;
    }

    // Имена узлов уровня в нынешнем порядке: markdown-документы самой папки и её подпапки.
    // Порядок берём из индекса — он уже уплощён деревом, поэтому первое появление имени и
    // есть его место среди соседей. Не-markdown в состав не входит: .order обязан оставаться
    // wiki-совместимым, а «cover» без «cover.md» там просто мусор.
    // null — папки в области нет вовсе (гейт эндпоинта).
    private static List<string>? LevelNames(DocsCorpus corpus, string folder)
    {
        var prefix = folder.Length == 0 ? "" : $"{folder}/";
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = folder.Length == 0;     // корень существует, пока в области есть хоть что-то

        foreach (var doc in corpus.Docs)
        {
            if (prefix.Length > 0 && !doc.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            known = true;
            var rest = doc.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                if (!IsMarkdown(rest)) continue;
                var name = Path.GetFileNameWithoutExtension(rest);
                if (seen.Add(name)) names.Add(name);
            }
            // Документ глубже уровнем: его место в .order занимает первый сегмент — раздел
            else if (seen.Add(rest[..slash])) names.Add(rest[..slash]);
        }

        return known && corpus.Docs.Count > 0 ? names : null;
    }

    private static bool IsMarkdown(string path) =>
        Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    // ---------- создание документов и разделов ----------

    public enum DocCreateStatus { Ok, FolderNotInScope, BadName, Conflict }

    public sealed record DocCreateResult(DocCreateStatus Status, string? Path = null, string? Error = null);

    // Azure DevOps wiki не открывает страницу, полный путь которой длиннее этого. Ограничение
    // чужое, но проверяем его мы: узнать о нём при публикации, когда документов уже сотня,
    // дороже, чем отказать в момент создания
    private const int MaxDocPathLength = 235;

    // Имена, которыми на Windows нельзя назвать файл ни с каким расширением: «CON.md» не
    // создастся, а на Linux создастся и сломается при первом клоне на Windows
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Имя файла из названия по правилам wiki: пробелы становятся дефисами (в wiki обратное
    // преобразование делает заголовок страницы). null — название непригодно, причина в error.
    public static string? DocFileName(string? title, out string? error)
    {
        error = null;
        var name = (title ?? "").Trim().Replace(' ', '-');
        if (name.Length == 0) { error = "Название пустое"; return null; }

        // Точка по краям: «.order» стал бы скрытым файлом, «имя.» Windows молча срежет
        if (name.StartsWith('.') || name.EndsWith('.'))
        {
            error = "Название не может начинаться или заканчиваться точкой";
            return null;
        }
        // Набор запрещённых — явный и платформонезависимый: Path.GetInvalidFileNameChars()
        // на Linux (среда CI) НЕ содержит ':' '*' '?' '"' '<' '>' '|', и «a:b» там молча
        // создалось бы, а при первом клоне на Windows сломалось. Перечисляем весь Windows-набор
        // сами, а Linux-набор добавляем сверху надмножеством (control-символы 0–31 уже в нём).
        // '#' формально допустим в имени файла, но в markdown-ссылке он открывает якорь,
        // и ссылка на такой документ не соберётся ни в панели, ни в wiki.
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars())
            { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '#' };
        foreach (var ch in name)
            if (invalid.Contains(ch))
            {
                error = $"Название содержит недопустимый символ «{ch}»";
                return null;
            }
        if (ReservedNames.Contains(name))
        {
            error = $"«{name}» — зарезервированное имя Windows";
            return null;
        }
        return name;
    }

    // Создать документ или раздел в папке области.
    //
    // Раздел — это ПАРА «<имя>.md + <имя>/»: в code wiki раздел существует только так, и
    // папка без парного файла открывается пустой страницей. Ради этого дефекта всё и
    // затевалось, поэтому продукт создаёт обе половины сразу — и достраивает недостающую,
    // если половина уже лежит на диске.
    public DocCreateResult CreateDoc(string rootPath, string? folder, string? title,
        bool section, DocsScope? rawScope = null)
    {
        if (files is null)
            return new DocCreateResult(DocCreateStatus.BadName, Error: "Файловый сервис недоступен");

        var root = Path.GetFullPath(rootPath);
        var scope = NormalizeScope(rawScope);
        // Корень репозитория — законная цель: в области рядом с docs/ живут и файлы корня
        // (README.md, docs.md). Документ там попадёт в панель, только если его имя стоит
        // в «файлах корня», — дописывает его контроллер по флагу InRoot ниже
        var target = string.IsNullOrWhiteSpace(folder) ? "" : NormalizeFolder(folder);
        if (target is null || (target.Length > 0 && !InScope(scope, target)))
            return new DocCreateResult(DocCreateStatus.FolderNotInScope,
                Error: $"Папка вне области документации: {folder}");

        // Раздел в корне — это новая папка документации, то есть правка области, а не
        // создание страницы: продукт не расширяет область молча, за спиной у остальных
        // владельцев репозитория
        if (section && target.Length == 0)
            return new DocCreateResult(DocCreateStatus.BadName,
                Error: "Раздел в корне репозитория не создаётся — выберите папку документации");

        var name = DocFileName(title, out var nameError);
        if (name is null) return new DocCreateResult(DocCreateStatus.BadName, Error: nameError);

        var docPath = target.Length == 0 ? $"{name}.md" : $"{target}/{name}.md";
        if (docPath.Length > MaxDocPathLength)
            return new DocCreateResult(DocCreateStatus.BadName,
                Error: $"Путь длиннее {MaxDocPathLength} символов — wiki такую страницу не откроет");

        // Сравнение без учёта регистра: на Windows «API.md» и «api.md» — один файл, и
        // создание второго молча затёрло бы первый
        var dir = target.Length == 0 ? root : Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar));
        var pageExists = EntryExists(dir, $"{name}.md", directory: false);
        var folderExists = EntryExists(dir, name, directory: true);
        if (section ? pageExists && folderExists : pageExists)
            return new DocCreateResult(DocCreateStatus.Conflict,
                Error: $"«{name}» в этой папке уже есть");

        // Через FileService, а не File.WriteAllText: там SafeJoin и уведомление OnMutated,
        // на котором висит синк базы знаний
        if (!pageExists)
        {
            files.CreateFile(root, docPath);
            files.WriteFile(root, docPath, $"# {(title ?? "").Trim()}\n");
        }
        if (section && !folderExists) files.CreateDirectory(root, $"{target}/{name}");

        // Строка в .order РОДИТЕЛЬСКОЙ папки — одна и та же для документа и для раздела:
        // «decisions» задаёт место и страницы раздела, и всего его содержимого
        AppendToOrder(root, target, name);
        return new DocCreateResult(DocCreateStatus.Ok, docPath);
    }

    // ---------- переименование ----------

    public enum DocRenameStatus { Ok, NotFound, BadName, Conflict, Failed }

    // Moved — старый путь → новый по КАЖДОМУ переехавшему документу: контроллеру он нужен
    // для побочных привязок (комментарии заметок, «Начало»), а панели — чтобы поправить
    // закреплённые и открытый документ
    public sealed record DocRenameResult(
        DocRenameStatus Status, string? Path = null, int UpdatedDocs = 0, int BrokenLinks = 0,
        string? Error = null, IReadOnlyDictionary<string, string>? Moved = null);

    // Переименовать документ или раздел.
    //
    // Раздел переименовывается ПАРОЙ: файл и одноимённая папка. Расщеплённая пара хуже
    // отказа — в wiki она даёт сразу и пустой раздел, и осиротевшую страницу, — поэтому
    // сбой второго шага откатывает первый.
    //
    // Ссылки чинятся по РАЗОБРАННЫМ целям, а не текстовым поиском старого имени: для
    // каждой ссылки пересчитывается относительный путь от источника к новому расположению.
    // Подписи ссылок («Журнал решений») не трогаем — это авторский текст, а не путь.
    // Предел механизма: видно только то, что входит в корпус. Ссылка из кода или из .md
    // вне области останется битой при любом updateLinks — их число возвращается наружу.
    public DocRenameResult RenameDoc(string rootPath, string path, string? newTitle,
        bool updateLinks, DocsScope? rawScope = null)
    {
        if (files is null)
            return new DocRenameResult(DocRenameStatus.Failed, Error: "Файловый сервис недоступен");

        var root = Path.GetFullPath(rootPath);
        var scope = NormalizeScope(rawScope);
        var corpus = GetCorpus(root, scope);
        var key = NormalizePath(path);
        if (key is null || !corpus.ByPath.TryGetValue(key, out var entry))
            return new DocRenameResult(DocRenameStatus.NotFound, Error: $"Документ вне области документации: {path}");
        if (!IsMarkdown(entry.Path))
            return new DocRenameResult(DocRenameStatus.BadName,
                Error: "Переименование поддержано только для markdown-документов");

        var name = DocFileName(newTitle, out var nameError);
        if (name is null) return new DocRenameResult(DocRenameStatus.BadName, Error: nameError);

        var parent = Folder(entry.Path);
        var oldName = Path.GetFileNameWithoutExtension(entry.Path);
        var newDocPath = parent.Length == 0 ? $"{name}.md" : $"{parent}/{name}.md";
        if (newDocPath.Length > MaxDocPathLength)
            return new DocRenameResult(DocRenameStatus.BadName,
                Error: $"Путь длиннее {MaxDocPathLength} символов — wiki такую страницу не откроет");
        if (string.Equals(name, oldName, StringComparison.Ordinal))
            return new DocRenameResult(DocRenameStatus.Ok, entry.Path);

        // Смена ТОЛЬКО регистра — не коллизия: это переименование того же файла
        var sameFile = string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase);
        var dir = parent.Length == 0 ? root : Path.Combine(root, parent.Replace('/', Path.DirectorySeparatorChar));
        if (!sameFile && (EntryExists(dir, $"{name}.md", directory: false)
            || (entry.SectionFolder is not null && EntryExists(dir, name, directory: true))))
            return new DocRenameResult(DocRenameStatus.Conflict, Error: $"«{name}» в этой папке уже есть");

        // Куда что переезжает: сама страница плюс всё поддерево раздела. Ключи — старые
        // пути, значения — новые; по этой карте потом чинятся ссылки
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [entry.Path] = newDocPath };
        var oldFolder = entry.SectionFolder;
        var newFolder = oldFolder is null ? null : (parent.Length == 0 ? name : $"{parent}/{name}");
        if (oldFolder is not null)
            foreach (var doc in corpus.Docs)
                if (doc.Path.StartsWith($"{oldFolder}/", StringComparison.OrdinalIgnoreCase))
                    moved[doc.Path] = $"{newFolder}{doc.Path[oldFolder.Length..]}";

        // Ссылки на переезжающее ИЗ документов, которые сами никуда не едут: только их и
        // придётся чинить. Считаем до переименования — после карта путей уже другая
        var broken = 0;
        foreach (var (target, _) in moved)
            if (corpus.Backlinks.TryGetValue(target, out var backs))
                broken += backs.Count(b => !moved.ContainsKey(b.Path));

        try
        {
            RenameEntry(root, entry.Path, newDocPath, sameFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DocRenameResult(DocRenameStatus.Failed, Error: $"Не удалось переименовать файл: {e.Message}");
        }

        if (oldFolder is not null)
        {
            try
            {
                RenameEntry(root, oldFolder, newFolder!, sameFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Откат: половина пары хуже отказа — в wiki это сразу и пустой раздел,
                // и осиротевшая страница
                try { RenameEntry(root, newDocPath, entry.Path, sameFile); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    return new DocRenameResult(DocRenameStatus.Failed,
                        Error: $"Папка не переименована ({e.Message}), и вернуть файл не удалось: {rollback.Message}");
                }
                return new DocRenameResult(DocRenameStatus.Failed,
                    Error: $"Не удалось переименовать папку раздела: {e.Message}");
            }
        }

        // Строка .order меняется НА МЕСТЕ: позиция страницы в порядке чтения к имени
        // отношения не имеет. Не было строки — не добавляем: документ и раньше был
        // неперечисленным, и переименование не повод менять это
        ReplaceInOrder(root, parent, oldName, name);

        var updated = updateLinks ? UpdateLinks(root, corpus, moved) : 0;
        return new DocRenameResult(DocRenameStatus.Ok, newDocPath, updated,
            updateLinks ? 0 : broken, Moved: moved);
    }

    // Переименование через файловый сервис (там SafeJoin и уведомление OnMutated, на
    // котором висит синк базы знаний). sameFile — смена только регистра: на Windows это
    // один и тот же файл, git с core.ignorecase его не замечает, поэтому идём в два шага
    // через временное имя — иначе правка не попала бы ни в панель, ни в коммит
    private void RenameEntry(string root, string oldRel, string newRel, bool sameFile)
    {
        if (!sameFile) { files!.Rename(root, oldRel, newRel); return; }
        var temp = $"{oldRel}~ccs-rename";
        files!.Rename(root, oldRel, temp);
        files.Rename(root, temp, newRel);
    }

    // Строка порядка на прежней позиции. Файла нет — ничего не создаём: порядок этой
    // папки задан правилом индекса, и переименование не повод фиксировать его в git
    private static void ReplaceInOrder(string root, string folder, string oldName, string newName)
    {
        var dir = folder.Length == 0 ? root : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
        var file = Path.Combine(dir, OrderFileName);
        if (!File.Exists(file)) return;

        var existing = File.ReadAllText(file);
        var lines = SplitOrderLines(existing);
        var hit = false;
        for (var i = 0; i < lines.Count; i++)
            if (string.Equals(lines[i], oldName, StringComparison.OrdinalIgnoreCase)) { lines[i] = newName; hit = true; }
        if (!hit) return;

        var eol = existing.Contains("\r\n") ? "\r\n" : "\n";
        File.WriteAllText(file, string.Join(eol, lines) + eol, new UTF8Encoding(false));
    }

    // Починка ссылок на переехавшее. Возвращает число изменённых документов.
    //
    // Каждый документ разбирается заново по своему НОВОМУ расположению: цель ссылки
    // резолвится от СТАРОГО пути источника (в файле она записана относительно него),
    // а записывается уже относительно нового. Поэтому ссылки внутри переехавшего
    // поддерева на такие же переехавшие цели остаются нетронутыми — их относительный
    // путь не изменился.
    private int UpdateLinks(string root, DocsCorpus corpus, Dictionary<string, string> moved)
    {
        var changed = 0;
        foreach (var doc in corpus.Docs)
        {
            if (!IsMarkdown(doc.Path)) continue;
            var from = doc.Path;
            var fromNew = moved.GetValueOrDefault(from, from);

            string text;
            var file = Path.Combine(root, fromNew.Replace('/', Path.DirectorySeparatorChar));
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var updated = LinkRegex().Replace(text, m =>
            {
                var raw = m.Groups[2].Value.Trim();
                if (raw.Length == 0 || IsExternal(raw)) return m.Value;
                var (targetPart, _) = SplitRawAnchor(raw);
                if (targetPart.Length == 0) return m.Value;      // якорь внутри документа
                var target = ResolveRelative(from, targetPart);
                if (target is null || !moved.TryGetValue(target, out var newTarget)) return m.Value;
                var anchor = raw[targetPart.Length..];           // «#…» как был, вместе с регистром
                return $"[{m.Groups[1].Value}]({RelativeLink(fromNew, newTarget)}{anchor})";
            });

            if (updated == text) continue;
            try { File.WriteAllText(file, updated); changed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return changed;
    }

    // Путь ссылки БЕЗ разбора якоря: для замены нужен исходный хвост «#…» как он записан,
    // а SplitAnchor нормализует его в слаг
    private static (string Target, string Anchor) SplitRawAnchor(string raw)
    {
        var i = raw.IndexOf('#');
        return i < 0 ? (raw, "") : (raw[..i], raw[i..]);
    }

    // Относительная ссылка от одного документа к другому — в том же виде, в каком её
    // пишут руками: соседний файл именем, глубже — через папки, выше — через «../».
    // Пробел кодируем: markdown обрывает цель ссылки на первом же пробеле
    internal static string RelativeLink(string fromDoc, string toDoc)
    {
        var fromParts = Folder(fromDoc).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var toParts = toDoc.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < fromParts.Length && common < toParts.Length - 1 &&
               string.Equals(fromParts[common], toParts[common], StringComparison.OrdinalIgnoreCase))
            common++;

        var up = string.Concat(Enumerable.Repeat("../", fromParts.Length - common));
        var down = string.Join('/', toParts.Skip(common));
        var link = up + down;
        return link.Replace(" ", "%20");
    }

    // Папка входит в область: сама выбрана в настройке либо лежит внутри выбранной.
    // Проверяем по НАСТРОЙКЕ, а не по индексу (как гейт .order): в пустой папке области
    // документов ещё нет, а создать в ней первый документ — законное действие
    private static bool InScope(DocsScope scope, string folder)
    {
        foreach (var f in scope.Folders)
            if (string.Equals(folder, f, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith($"{f}/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Существует ли в папке файл или каталог с таким именем — с точностью до регистра.
    // File.Exists на Linux регистрозависим, а имя, отличающееся регистром, при клоне на
    // Windows схлопнется с существующим
    private static bool EntryExists(string dir, string name, bool directory)
    {
        try
        {
            var entries = directory ? Directory.GetDirectories(dir) : Directory.GetFiles(dir);
            foreach (var entry in entries)
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (DirectoryNotFoundException) { /* папки ещё нет — конфликтовать не с чем */ }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    // README любого поддерживаемого расширения: в корне лежит и README.md, и README.txt
    private static bool IsReadme(string relativePath) =>
        !relativePath.Contains('/') &&
        Path.GetFileNameWithoutExtension(relativePath).Equals("README", StringComparison.OrdinalIgnoreCase);

    // Папка документа («docs/adr/x.md» → «docs/adr»); корневые документы — пустая строка
    private static string Folder(string relativePath)
    {
        var i = relativePath.LastIndexOf('/');
        return i < 0 ? "" : relativePath[..i];
    }

    // ---------- разбор markdown ----------

    // Сырая ссылка до классификации: путь и якорь уже разделены
    internal readonly record struct ParsedLink(string Target, string? Anchor, string Text);

    internal sealed record ParsedDocument(string? Title, IReadOnlyList<DocHeading> Headings, List<ParsedLink> Links);

    // Заголовок вне блока кода: «## Текст» (до трёх пробелов отступа, как в CommonMark)
    [GeneratedRegex(@"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    // Ссылка [текст](цель) — но не картинка ![alt](src): у картинок навигации нет.
    // Хвостовой title в кавычках отбрасывается вместе с пробелом перед ним.
    [GeneratedRegex(@"(?<!\!)\[([^\]]*)\]\(\s*([^)\s]*)(?:\s+""[^""]*"")?\s*\)")]
    private static partial Regex LinkRegex();

    // Ограда блока кода: ``` или ~~~ (с отступом до трёх пробелов)
    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    internal static ParsedDocument ParseDocument(string markdown)
    {
        string? title = null;
        var headings = new List<DocHeading>();
        var links = new List<ParsedLink>();
        var inFence = false;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Блоки кода пропускаем целиком: «# комментарий» в bash-примере — не заголовок,
            // а markdown-ссылка в примере кода — не связь между документами
            if (FenceRegex().IsMatch(line)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var h = HeadingRegex().Match(line);
            if (h.Success)
            {
                var level = h.Groups[1].Value.Length;
                var text = StripMarkdown(h.Groups[2].Value);
                if (level == 1 && title is null) title = text;
                else if (level is >= 2 and <= 3) headings.Add(new DocHeading(level, text, Slugify(text)));
            }

            foreach (Match m in LinkRegex().Matches(line))
            {
                var target = m.Groups[2].Value.Trim();
                if (target.Length == 0) continue;
                var (path, anchor) = SplitAnchor(target);
                links.Add(new ParsedLink(path, anchor, StripMarkdown(m.Groups[1].Value)));
            }
        }

        return new ParsedDocument(title, headings, links);
    }

    // Текст без markdown-разметки: код, выделение, ссылки и картинки схлопываются в текст.
    // От ЭТОГО текста считается слаг — фронт получает уже очищенный textContent DOM-узла,
    // и при разных входах одинаковая функция слагификации дала бы разные якоря.
    internal static string StripMarkdown(string text)
    {
        var s = text;
        s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1");   // картинка → alt
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");    // ссылка → подпись
        s = s.Replace("`", "");
        s = Regex.Replace(s, @"\*\*|__|\*|_|~~", "");
        return s.Trim();
    }

    // Слаг якоря: нижний регистр, разделители в дефис, прочая пунктуация отброшена.
    // Буквы любых алфавитов сохраняются — заголовки в проекте русские.
    internal static string Slugify(string headingText)
    {
        var sb = new StringBuilder();
        foreach (var ch in StripMarkdown(headingText).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '\t' or '-' or '_' or '.' or '/') sb.Append('-');
        }
        // Схлопываем повторы дефисов и обрезаем края
        var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug;
    }

    // «foo.md#раздел» → ("foo.md", "раздел"); якорь нормализуется тем же слагом,
    // потому что в доках его пишут и словами, и уже готовым слагом.
    // Декодирование обязательно: в markdown кириллический якорь часто записан процент-
    // энкодингом («#%D1%81%D1%80%D0%BE%D0%BA»), и без decode слаг получался мусорный —
    // переход по такой ссылке открывал документ с начала вместо нужного раздела.
    internal static (string Path, string? Anchor) SplitAnchor(string target)
    {
        var i = target.IndexOf('#');
        if (i < 0) return (target, null);
        var raw = target[(i + 1)..];
        string decoded;
        try { decoded = Uri.UnescapeDataString(raw); }
        catch (UriFormatException) { decoded = raw; }   // битая %-последовательность
        var anchor = Slugify(decoded);
        return (target[..i], anchor.Length == 0 ? null : anchor);
    }

    internal static bool IsExternal(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("//", StringComparison.Ordinal);

    // Путь ссылки относительно документа-источника → путь от корня проекта.
    // null — ссылка уводит выше корня: такие в корпус не берём.
    internal static string? ResolveRelative(string fromDoc, string target)
    {
        var decoded = Uri.UnescapeDataString(target.Replace('\\', '/'));
        var baseDir = fromDoc.Contains('/') ? fromDoc[..fromDoc.LastIndexOf('/')] : "";
        var combined = decoded.StartsWith('/')
            ? decoded.TrimStart('/')
            : baseDir.Length > 0 ? $"{baseDir}/{decoded}" : decoded;

        var segments = new List<string>();
        foreach (var seg in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (segments.Count == 0) return null;   // выше корня проекта
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    // ---------- вспомогательное ----------

    // Путь от корня с прямыми слэшами: один формат для API, ссылок и ключей словарей
    private static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    // Нормализация пути из запроса к формату ключей корпуса
    private static string? NormalizePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.Length == 0 ? null : normalized;
    }

    // Якорь ближайшего заголовка выше совпадения — чтобы поиск вёл сразу в раздел
    private static string? HeadingAbove(string text, int matchIndex, IReadOnlyList<DocHeading> headings)
    {
        string? slug = null;
        foreach (var h in headings)
        {
            var pos = text.IndexOf(h.Text, StringComparison.Ordinal);
            if (pos < 0 || pos > matchIndex) continue;
            slug = h.Slug;
        }
        return slug;
    }

    private static string Snippet(string text, int matchIndex, int matchLength)
    {
        var start = Math.Max(0, matchIndex - SnippetRadius);
        var end = Math.Min(text.Length, matchIndex + matchLength + SnippetRadius);
        var body = text[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
        return (start > 0 ? "…" : "") + body + (end < text.Length ? "…" : "");
    }
}
