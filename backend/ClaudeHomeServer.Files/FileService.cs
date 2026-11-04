using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
// WikiPaths — тип сборки Core.dll в namespace Services.Notes, а не вертикали Notes:
// граница Files проверяется по сборке (IsCoreAssembly в SubsystemBoundaryTests), ADR-014
// (Files не зовёт Notes) не затронут.
using WikiPaths = ClaudeHomeServer.Services.Notes.WikiPaths;

namespace ClaudeHomeServer.Services.Files;

public class FileService(
    IGitWorkingTree? git = null,
    IProjectManager? projects = null,
    ILogger<FileService>? logger = null,
    IOpenedPathGuard? openedPathGuard = null,
    // data-корень для гейта «в вики пишет только Хранитель» (ADR-035, решение 2):
    // тот же резолв от DataPath, что у NotesService/DeviceRegistry. Без конфига — см. GuardNotWiki.
    IConfiguration? config = null)
{
    private readonly ILogger<FileService>? _logger = logger;
    private readonly IConfiguration? _config = config;
    private readonly string? _dataDir = config is null ? null : SubsystemHostingExtensions.ResolveDataDir(config);

    /// <summary>
    /// Тот же сервис, но каждый открытый файл и каталог-родитель сверяются с корнем по
    /// дескриптору (агент устройства): проверка пути до открытия подмену ссылки не ловит.
    /// </summary>
    public FileService WithOpenedPathGuard(IOpenedPathGuard guard) =>
        new(git, projects, _logger, guard, _config) { GitStatusComputer = GitStatusComputer };

    // git/projects/logger опциональны (DI подставляет): git-операции идут через слой Execution
    // с резолвом владельца по корню — статусы/дифф/револт честны и для container-юзеров.
    // Без них (юнит-тесты, выключенная подсистема Git) — прежний прямой запуск git на хосте
    // и тихое логирование.

    // Владелец по корню проекта: у соседей по папке владелец один по построению
    private string? OwnerOf(string rootPath) =>
        projects?.GetByRootPath(rootPath).FirstOrDefault()?.OwnerId;

    // Мутации через файловый API (UI, OnlyOffice, upload; правки Claude идут мимо — их ловят
    // ватчеры). Подписчик — ProjectKnowledgeSyncService (синк базы знаний).
    // Аргументы: root, относительный путь, вид, новый путь (только для Rename).
    public event Action<string, string, FileMutationKind, string?>? OnMutated;

    // Уведомление подписчиков; сбой подписчика не должен ронять файловую операцию.
    // Публичный — дёргают и точки записи мимо FileService (Upload/SaveFromUrl в FilesController).
    public void NotifyMutated(string root, string rel, FileMutationKind kind, string? newRel = null)
    {
        try { OnMutated?.Invoke(root, rel, kind, newRel); }
        catch { /* синк знаний best-effort */ }
    }

    // Папка вложений чата в рабочей папке (файлы, загруженные в сообщение с компьютера).
    // Служебная: исключена из дерева, ватчеров, дефолтного .gitignore и синка базы знаний.
    // Константа живёт в Core (TreeExcludes) — значение нужно вертикалям мимо Main;
    // форвардер оставлен ради существующих вызывающих внутри Main.
    public const string AttachmentsDir = TreeExcludes.AttachmentsDir;

    // Список исключений дерева переехал в Core (TreeExcludes) — по соглашению проекта
    // из вертикалей нельзя звать FileService ради статики. Здесь только потребитель.

    // Предохранитель от патологически больших деревьев
    private const int TreeMaxEntries = 20000;

    // .claude/worktrees/<имя> — полные копии репозитория (git worktree). При showHidden=true
    // рекурсия в них раздувает дерево и вытесняет настоящие файлы проекта из TreeMaxEntries.
    // Проверка по относительному пути, а не по имени "worktrees" — обычная папка с таким
    // именем в другом месте проекта исключаться не должна.
    private static bool IsWorktreesPath(string relativePath) =>
        relativePath.Equals(".claude/worktrees", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(".claude/worktrees/", StringComparison.OrdinalIgnoreCase);

    // ─── обход closed-поддерева через символьную ссылку (дефект d07d7054) ────────
    // Текстовый фильтр ниже судит относительный путь и не знает о ссылках: «L» → .cc-attachments
    // в корне проекта ни не исключён по имени (имя «L»), ни не отфильтрован (первый сегмент «L»),
    // обход спускается в настоящий .cc-attachments и отдаёт L/private/{чужой}/… — их содержимое
    // доходит и до IndexFolder. Канонический гейт (шаг 0859cdb5) закрыл читающие и разрушительные
    // точки; обход оставал текстовым.
    //
    // Цена (замечание ревью 6db670ff): канонический предикат на каждую запись дерева — лишние
    // разыменования на всём обходе (замер ~42 мс на 1000 вызовов; при пороке TreeMaxEntries
    // заметно). Обход знает место, где перешёл по ссылке: каталог-ссылка (reparse-точка) или
    // названный старт. Поэтому разыменование — ОДНО на переход, дальше только строковая сборка:
    // closedPrefix — путь текущего каталога относительно корня closed-поддерева
    // (.cc-attachments); пустой — вне его. На переходе выводится из канона цели ссылки, ниже
    // продлевается именами обхода: канон записи под ссылкой = канон цели + имена обхода. Новая
    // ссылка внутри закрытого поддерева — свой переход и свой префикс (она может вести и наружу:
    // обход снова обычный). Ссылка наружу на не-private путь (frontend/node_modules → primary
    // checkout у worktree-setup.sh) даёт пустой префикс: обход и его цена — как прежде.
    // Неразрешимый канон (цикл ссылок) — пустой префикс: на запись он не влияет, а сама
    // операция по такому пути даст свой отказ ниже (Directory.Exists/404).
    //
    // ДВЕ ЛИНИИ, как у канонического предиката (доработка по ревью 6c2e1385, MAJOR 1): раньше
    // префикс выводился только из первой — литерального сегмента .cc-attachments в каноне цели.
    // Если сам .cc-attachments — ссылка на каталог с другим именем (.cc-attachments →
    // /mnt/bigdisk/attachments), пары сегментов в каноне нет и обход снова слепнет на
    // L/private/{чужой}. Вторая линия — та же, что у IsUnderCanonicalPrivate
    // (PrivateAttachments.cs:284): канон цели под каноном <opRoot>/.cc-attachments. Его корень
    // (closedRoot) берётся ОДИН на операцию, а не на переход: один разыменовывающий обход на
    // List/Tree, не разыменование на запись.
    //
    // Якорь префикса — КОРЕНЬ поддерева (контейнер .cc-attachments), а не его private/, и
    // равенство якорю тоже даёт префикс («.cc-attachments»): он обязан появиться на переходе
    // в контейнер («L» → .cc-attachments), иначе его потомки наследуют пустой префикс и
    // текстовый фильтр снова слепнет на L/private/{чужой}. Чужести это не меняет: её решает
    // IsForeign по собранному пути — автор это сегмент после private/ (тот же разбор, что у
    // предиката); сам контейнер и его private — не чужие. Инвариант сохранён: легальная ссылка
    // наружу на не-private путь (frontend/node_modules → primary checkout у worktree-setup.sh)
    // под якорем не оказывается — префикс пуст, обход и его цена как прежде. Неразрешимый канон
    // якоря (цикл ссылок в самом .cc-attachments) — пустой префикс: второй линии нечем судить;
    // читающие и разрушительные точки остаются на fail-closed предикате (IsForeignResolved),
    // обход такой записи — тот же принятый порядок, что у неразрешимой цели.
    private static string ClosedPrefixOf(string? canonical, string? closedRoot)
    {
        if (canonical is null) return "";
        var segments = canonical.Split('/'); // Canonical отдаёт «/» независимо от ОС
        for (var i = 0; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], TreeExcludes.AttachmentsDir, StringComparison.OrdinalIgnoreCase))
                return string.Join('/', segments[i..]);
        }
        if (closedRoot is null) return "";
        // Форма сравнения — та же, что у второй линии предиката: канон без завершающего «/»,
        // регистр по правилам ФС хоста (SafePath.ComparisonFor).
        var target = canonical.TrimEnd('/');
        var root = closedRoot.TrimEnd('/');
        var comparison = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        // Цель — сам контейнер через ссылку («L» → .cc-attachments → каталог с другим именем):
        // префикс корня, содержимое судится ниже по именам обхода.
        if (target.Equals(root, comparison)) return TreeExcludes.AttachmentsDir;
        if (target.StartsWith(root + "/", comparison))
            return TreeExcludes.AttachmentsDir + "/" + target[(root.Length + 1)..];
        return "";
    }

    // Канон корня closed-поддерева операции: <opRoot>/.cc-attachments через ссылки (тот же
    // контейнер, что у ContainersOf предиката). Несуществующий путь остаётся путём
    // (WikiPaths.Canonical) — реальной записи под ним нет, вторая линия молчит, как прежде.
    private static string? ClosedRootOf(string opRoot) =>
        SafePath.Canonical(Path.Combine(opRoot, TreeExcludes.AttachmentsDir));

    // closedPrefix записи: продолжение префикса родителя или новый префикс на переходе.
    private static string ClosedPrefixFor(string parentPrefix, string name, string absolutePath, bool isLink,
        string? closedRoot)
    {
        var inherited = parentPrefix.Length == 0 ? "" : parentPrefix + "/" + name;
        if (!isLink) return inherited;
        var fromTarget = ClosedPrefixOf(SafePath.Canonical(absolutePath), closedRoot);
        // Ссылка наружу из closed-поддерева — обход снова обычный (её цель вне его);
        // внутрь — тот же префикс, что дал бы прямой путь.
        return fromTarget.Length != 0 ? fromTarget : inherited;
    }

    // Защита от path traversal — форвардер к Core-примитиву (Этап 3, уборка Git).
    // Реализация и обоснование граничных случаев (`..`, абсолютный путь, символы диска)
    // живут в `ClaudeHomeServer.Core/Services/SafePath.cs`. Внутренний форвардер — для кода
    // самой вертикали; снаружи зовут SafePath.Join или SafeJoinPublic.
    internal static string SafeJoin(string root, string relativePath) =>
        SafePath.Join(root, relativePath);

    // Публичная обёртка SafeJoin для использования вне сборки (WebDav и др.)
    public static string SafeJoinPublic(string root, string relativePath) =>
        SafePath.Join(root, relativePath);

    // ─── гейт «в вики пишет только Хранитель» (ADR-035, решение 2) ─────────────
    // data/ в норме вне корня любого проекта, но корень проекта может оказаться предком
    // data/ (проект на весь домашний каталог) — тогда относительный путь «wiki/{owner}/…»
    // лежит под data/wiki/ и файловый API дотянулся бы до поддерева владельца. Проверка
    // по каноническому пути (WikiPaths): дешёвая страховка, не основной гейт. Безусловная:
    // не зависит от фич-флага и от того, заведена ли вика (признак на диске — не здесь).
    // _dataDir null (DeviceAgent, юнит-тесты без конфига) — data-корня нет и проверить
    // нечем: отказа нет, тот же контракт, что у ClaudeSession.
    private void GuardNotWiki(string path)
    {
        if (_dataDir is null || !WikiPaths.IsWikiPath(path, _dataDir)) return;
        throw new UnauthorizedAccessException("В вики пишет только Хранитель");
    }

    // Обратное направление — для операций, действующих рекурсивно над поддеревом (Delete,
    // Rename каталога): путь сам может быть ПРЕДКОМ вики — корень проекта на весь домашний
    // каталог («», «data»), и такое удаление/переименование снесло бы data/wiki/ целиком,
    // обойдя гейт (ревью S1b, MAJOR 2). Путь сюда приходит абсолютным (SafeJoin), и предок
    // сравнивается с ним тем же каноническим обходом, что у IsWikiPath.
    private void GuardNotWikiSubtree(string path)
    {
        if (_dataDir is null) return;
        if (!WikiPaths.IsWikiPath(path, _dataDir) && !WikiPaths.IsWikiAncestor(path, _dataDir)) return;
        throw new UnauthorizedAccessException("В вики пишет только Хранитель");
    }

    /// <summary>
    /// Листинг названной папки. viewerUserId — ЗРИТЕЛЬ (S2d-6b-2 доработка 1, ревью f24f7533): чужие
    /// приватные вложения (.cc-attachments/private/{другой}/…, S2d-6b-1) из ответа выпадают — ни имён,
    /// ни id автора, ни количества. null — вызов не про зрителя: поведение существующих вызывающих
    /// не меняется (WebDAV, MCP files_*, ProjectFiles решают сами: у них свой актор или актора нет
    /// в контракте, и их тесты держат «своё видно»); пустая строка — явный вызов без зрителя
    /// (Files-ручка без JWT): любое private закрыто, fail-closed, как сам IsForeign.
    /// TreeExcludes режет ОБХОД, а не названный путь: прямая ссылка на .cc-attachments или private/
    /// отдаёт их содержимое — поэтому фильтр стоит по записям, а не по стартовому каталогу.
    /// Названный путь идёт на диск: «L» → .cc-attachments — тот же контейнер, и чужесть его
    /// содержимого решается по каноническому относительному пути (дефект d07d7054, см. ClosedPrefixOf).
    /// </summary>
    public IEnumerable<FileEntry> List(string rootPath, string relativePath = "", bool showHidden = false,
        string? viewerUserId = null)
    {
        var dir = SafeJoin(rootPath, relativePath);
        if (!Directory.Exists(dir))
        {
            // Виртуальная папка заметок: показываем notes/ в дереве всегда, физически
            // она появляется при первой заметке (NotesService.Create). Раскрытие
            // несозданной папки — пустой список, не 404.
            if (IsNotesPath(relativePath)) return [];
            throw new DirectoryNotFoundException();
        }

        var (modified, newFiles) = GetGitStatus(rootPath);
        var entries = new List<FileEntry>();

        // Канонический префикс названного старта: «L» → .cc-attachments и «L/private» — те же
        // контейнеры на диске (шаг 0859cdb5), их содержимое судится по каноническому пути.
        // Обычный путь даёт пустой префикс — фильтр ниже остаётся текстовым, как прежде.
        // closedRoot — корень closed-поддерева этой операции (вторая линия, см. ClosedPrefixOf).
        var closedRoot = ClosedRootOf(rootPath);
        var startPrefix = ClosedPrefixOf(SafePath.Canonical(dir), closedRoot);

        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            // Чтение метаданных одной записи может бросить IOException/UnauthorizedAccessException
            // (резервные DOS-имена вроде «nul», гонка «удалили между GetFiles и FileInfo», битые
            // reparse-точки). Запись пропускается — иначе упадёт весь листинг папки.
            string name;
            DateTime modifiedAt;
            string relDir;
            bool isLink;
            try
            {
                var info = new DirectoryInfo(d);
                name = info.Name;
                modifiedAt = info.LastWriteTimeUtc;
                relDir = Path.GetRelativePath(rootPath, d).Replace('\\', '/');
                // Attributes — тот же снимок, что у Name/LastWriteTimeUtc: нового syscall нет
                isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Пропуск подкаталога: не удалось прочитать метаданные ({Path})", d);
                continue;
            }
            if (TreeExcludes.Contains(name)) continue;
            if (!showHidden && name.StartsWith('.')) continue;
            // S2d-6b-2 доработка 1: каталог чужого автора (и чужой private выше по дереву) — не в ответе, см. doc метода
            var prefix = ClosedPrefixFor(startPrefix, name, d, isLink, closedRoot);
            if (viewerUserId is not null &&
                PrivateAttachments.IsForeign(prefix.Length == 0 ? relDir : prefix, viewerUserId)) continue;
            entries.Add(new FileEntry(name, relDir, true, null, modifiedAt, false));
        }

        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
        {
            // см. комментарий в цикле директорий выше
            string name;
            DateTime modifiedAt;
            long size;
            string rel;
            bool isLink;
            try
            {
                var info = new FileInfo(f);
                name = info.Name;
                size = info.Length;
                modifiedAt = info.LastWriteTimeUtc;
                rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Пропуск файла: не удалось прочитать метаданные ({Path})", f);
                continue;
            }
            if (!showHidden && name.StartsWith('.')) continue;
            var prefix = ClosedPrefixFor(startPrefix, name, f, isLink, closedRoot);
            if (viewerUserId is not null &&
                PrivateAttachments.IsForeign(prefix.Length == 0 ? rel : prefix, viewerUserId)) continue;
            entries.Add(new FileEntry(name, rel, false, size, modifiedAt,
                modified.Contains(rel), IsNew: newFiles.Contains(rel)));
        }

        // Папка заметок в корне проекта присутствует всегда (даже если ещё не создана
        // физически) — vault проекта виден и до первой заметки.
        if (string.IsNullOrEmpty(relativePath) &&
            !entries.Any(e => e.IsDirectory && e.Name.Equals("notes", StringComparison.OrdinalIgnoreCase)))
        {
            entries.Insert(0, new FileEntry("notes", "notes", true, null, DateTime.UtcNow, false));
        }

        return entries;
    }

    // Путь указывает на папку заметок проекта (сам notes/ или внутри неё)
    private static bool IsNotesPath(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/').Trim('/');
        return norm.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
               norm.StartsWith("notes/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Поиск по имени файла по всему дереву. viewerUserId — ЗРИТЕЛЬ (S2d-6b-2, разрез 4bc3d04f
    /// §1 путь 19): чужие приватные вложения (.cc-attachments/private/{другой}/…, S2d-6b-1) из
    /// выдачи выпадают — ни именем, ни количеством; фильтр стоит ДО Take(100), поэтому чужой
    /// материал не съедает слоты своих результатов. Свои private, общие вложения и любые прочие
    /// пути — без изменений. Без id зрителя (машинный шов ProjectFiles: актора в контракте
    /// IProjectFiles нет) — fail-closed, всё private закрыто, как IsForeign по пустому userId;
    /// чтение вложений агентом — шаг 6b-3.
    /// TAIL-SEARCH-PRIV (хвост ревью 11256e5f): обход — тот же, что у List/Tree: closedPrefix
    /// на переходе по ссылке (ClosedPrefixOf/ClosedPrefixFor, дефект d07d7054, две линии по
    /// 180efbfef), текстовый фильтр по относительному пути остаётся первой линией. Чужое
    /// private, достигнутое ссылкой внутри корня («L» → .cc-attachments), судится по канону,
    /// а не по имени «L»: текстовый фильтр один его не видел. Явный обход вместо
    /// EnumerateFiles(AllDirectories): тот не знает, где перешёл по ссылке. В отличие от дерева,
    /// поиск спускается в скрытые каталоги и в .cc-attachments: общие вложения и своё private
    /// поиск находит по имени — фильтр по записям, не по стартовому каталогу. Циклы ссылок —
    /// как у прежнего обхода: в уже открытый по текущему спуску каталог не идём (засечка по
    /// канону цели — тому же, что ClosedPrefixFor использует для префикса, без второго
    /// разыменования).
    /// </summary>
    public IEnumerable<FileEntry> Search(string rootPath, string query, string? viewerUserId = null)
    {
        var (modified, newFiles) = GetGitStatus(rootPath);

        // Тот же канонический старт и closedRoot, что у List/Tree (по одному на операцию):
        // обычный корень даёт пустой префикс — фильтр по относительным путям, как прежде.
        var closedRoot = ClosedRootOf(rootPath);
        var startCanonical = SafePath.Canonical(rootPath);
        var startPrefix = ClosedPrefixOf(startCanonical, closedRoot);

        // Fuse циклов ссылок: явная рекурсия, в отличие от EnumerateFiles, сама не знает об
        // уже открытом по текущему спуску каталоге. В засечке только цели ссылок: цикл замыкается
        // ссылкой, и первый проход по нему оставляет свою цель в засечке; на выходе цель
        // снимается — две легальные ссылки на одну папку обходятся по каждой, как прежде.
        var openChain = new HashSet<string>();
        if (startCanonical is not null) openChain.Add(startCanonical);

        // Найденные файлы — до метаданных: обход ленивый, как прежний, FileInfo только для
        // совпавших по имени.
        IEnumerable<(string Abs, string Rel, string Name, FileInfo Info)> Matches(string dir, string parentPrefix)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Пропуск каталога при поиске: не удалось прочитать содержимое ({Path})", dir);
                yield break;
            }
            foreach (var f in files)
            {
                // см. комментарий в List: битая запись пропускается, иначе упадёт весь поиск
                var name = Path.GetFileName(f);
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                FileInfo info;
                bool isLink;
                try
                {
                    info = new FileInfo(f);
                    isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск файла при поиске: не удалось прочитать метаданные ({Path})", f);
                    continue;
                }
                var prefix = ClosedPrefixFor(parentPrefix, name, f, isLink, closedRoot);
                var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                if (PrivateAttachments.IsForeign(prefix.Length == 0 ? rel : prefix, viewerUserId)) continue;
                yield return (f, rel, name, info);
            }

            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Пропуск каталога при поиске: не удалось прочитать содержимое ({Path})", dir);
                yield break;
            }
            foreach (var d in dirs)
            {
                string name;
                bool isLink;
                try
                {
                    var info = new DirectoryInfo(d);
                    name = info.Name;
                    isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск подкаталога при поиске: не удалось прочитать метаданные ({Path})", d);
                    continue;
                }
                var prefix = ClosedPrefixFor(parentPrefix, name, d, isLink, closedRoot);
                if (!isLink)
                {
                    foreach (var m in Matches(d, prefix)) yield return m;
                    continue;
                }
                var target = SafePath.Canonical(d);
                if (target is null || !openChain.Add(target)) continue; // неразрешимый или цикл: не спускаемся
                try
                {
                    foreach (var m in Matches(d, prefix)) yield return m;
                }
                finally { openChain.Remove(target); }
            }
        }

        return Matches(rootPath, startPrefix)
            .Take(100)
            .Select(m =>
            {
                // см. комментарий в List: одна битая запись не должна ронять весь поиск
                try
                {
                    return new FileEntry(m.Name, m.Rel, false, m.Info.Length, m.Info.LastWriteTimeUtc,
                        modified.Contains(m.Rel), IsNew: newFiles.Contains(m.Rel));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск файла при поиске: не удалось прочитать метаданные ({Path})", m.Abs);
                    return null;
                }
            })
            .Where(e => e is not null)
            .Select(e => e!);
    }

    // Рекурсивный листинг всего поддерева — для prefetch офлайн-снапшота и синхронизации папок.
    // Исключает тяжёлые папки (TreeExcludes), ограничен TreeMaxEntries.
    // viewerUserId — ЗРИТЕЛЬ (S2d-6b-2 доработка 1, ревью f24f7533): чужое private-поддерево из
    // результата выпадает — ни имён, ни id автора, ни количества; в чужой каталог автора не идём
    // (его содержимое не съедает слоты своих по TreeMaxEntries — тот же порядок, что фильтр до
    // Take(100) в Search). null — вызов не про зрителя: поведение существующих вызывающих не
    // меняется (WebDAV/MCP/ProjectFiles, см. doc List); пустая строка — без зрителя: любое private
    // закрыто (fail-closed). TreeExcludes режет ОБХОД, а не названный старт: путь внутрь
    // .cc-attachments вызывающий назвал явно — фильтр по записям, не по стартовому каталогу.
    // Через символьную ссылку в обход попадает и то, что режет имя: «L» → .cc-attachments из
    // обычного старта даёт L/private/{чужой}/… — их чужесть решается по каноническому
    // относительному пути (дефект d07d7054, см. ClosedPrefixOf), и там же передача зрителя
    // становится наблюдаемой поведением (тест IndexFolder через ссылку).
    // virtual — тестовый шов (S2d-6b-8, хвост ревью R-S2d-6b-6): из стартов без ссылки чужое
    // private фильтром не достигается (обход ниже .cc-attachments не спускается), и мутация
    // «снять аргумент UserId» на них не краснит ни один поведенческий тест (проба А ревьюера:
    // 6/6 зелёные) — белый тест на шов остаётся: подменный FileService, пишущий переданного
    // зрителя, тот же приём, что virtual-шов у EgressConnector/MockupCatalog.
    public virtual IEnumerable<FileEntry> Tree(string rootPath, string relativePath = "", bool showHidden = false,
        string? viewerUserId = null)
    {
        var start = SafeJoin(rootPath, relativePath);
        if (!Directory.Exists(start)) throw new DirectoryNotFoundException();

        var (modified, newFiles) = GetGitStatus(rootPath);
        var result = new List<FileEntry>();

        // Тот же канонический старт, что у List: «L» → .cc-attachments и «L/private» как названный
        // старт дают префикс closed-поддерева, обычный путь — пустой (обход и цена как прежде).
        // closedRoot — один на операцию, Walk наследует его вместе с префиксом.
        var closedRoot = ClosedRootOf(rootPath);
        var startPrefix = ClosedPrefixOf(SafePath.Canonical(start), closedRoot);

        void Walk(string dir, string parentPrefix)
        {
            if (result.Count >= TreeMaxEntries) return;

            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
            {
                if (result.Count >= TreeMaxEntries) return;
                // см. комментарий в List: битая запись пропускается, иначе упадёт всё дерево
                string name;
                DateTime modifiedAt;
                string relDir;
                bool isLink;
                try
                {
                    var info = new DirectoryInfo(d);
                    name = info.Name;
                    modifiedAt = info.LastWriteTimeUtc;
                    relDir = Path.GetRelativePath(rootPath, d).Replace('\\', '/');
                    isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск подкаталога в Tree: не удалось прочитать метаданные ({Path})", d);
                    continue;
                }
                if (TreeExcludes.Contains(name)) continue;
                if (!showHidden && name.StartsWith('.')) continue;
                if (IsWorktreesPath(relDir)) continue;
                // S2d-6b-2 доработка 1: чужой каталог автора — ни в список, ни в обход (см. doc метода).
                // Через ссылку чужесть решается по каноническому относительному пути (см. блок
                // ClosedPrefixOf выше), её поддерево не обходится: префикс наследуется вниз.
                var prefix = ClosedPrefixFor(parentPrefix, name, d, isLink, closedRoot);
                if (viewerUserId is not null &&
                    PrivateAttachments.IsForeign(prefix.Length == 0 ? relDir : prefix, viewerUserId)) continue;
                result.Add(new FileEntry(name, relDir, true, null, modifiedAt, false));
                Walk(d, prefix);
            }

            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
            {
                if (result.Count >= TreeMaxEntries) return;
                // см. комментарий выше
                string name;
                DateTime modifiedAt;
                long size;
                string rel;
                bool isLink;
                try
                {
                    var info = new FileInfo(f);
                    name = info.Name;
                    size = info.Length;
                    modifiedAt = info.LastWriteTimeUtc;
                    rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                    isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск файла в Tree: не удалось прочитать метаданные ({Path})", f);
                    continue;
                }
                if (!showHidden && name.StartsWith('.')) continue;
                var prefix = ClosedPrefixFor(parentPrefix, name, f, isLink, closedRoot);
                if (viewerUserId is not null &&
                    PrivateAttachments.IsForeign(prefix.Length == 0 ? rel : prefix, viewerUserId)) continue;
                result.Add(new FileEntry(name, rel, false, size, modifiedAt,
                    modified.Contains(rel), IsNew: newFiles.Contains(rel)));
            }
        }

        Walk(start, startPrefix);
        return result;
    }

    public string ReadFile(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        using var reader = new StreamReader(OpenVerified(rootPath, path), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // ---------- сверка открытого (IOpenedPathGuard) ----------
    // Без сверщика (сервер) — обычное открытие. Со сверщиком решает то, что открылось, а не
    // путь до открытия: ни байта не читается и не пишется, пока дескриптор не сверен.

    private FileStream OpenVerified(string rootPath, string path, int bufferSize = 4096, bool useAsync = false)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, useAsync);
        return Verified(rootPath, stream);
    }

    // Запись без усечения при открытии: FileMode.Create обнулил бы файл за подменённой
    // ссылкой ещё до сверки. Новый файл — CreateNew: он не идёт по ссылке в последнем
    // сегменте (висячая ссылка наружу даёт «уже существует», а не файл снаружи).
    private FileStream OpenForWrite(string rootPath, string path, bool createNew)
    {
        VerifyParent(rootPath, path);
        FileStream stream;
        if (createNew) stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        else
        {
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); }
            catch (FileNotFoundException) { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        }
        Verified(rootPath, stream).SetLength(0);
        return stream;
    }

    private FileStream Verified(string rootPath, FileStream stream)
    {
        if (openedPathGuard is null) return stream;
        try
        {
            openedPathGuard.Verify(rootPath, OpenedPaths.Of(stream.SafeFileHandle));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // Каталог, в котором создаём, переименовываем или удаляем, — по его дескриптору
    private void VerifyParent(string rootPath, string path) =>
        VerifyDirectory(rootPath, Path.GetDirectoryName(path)!);

    private void VerifyDirectory(string rootPath, string directory)
    {
        if (openedPathGuard is null) return;
        using var handle = OpenedPaths.OpenDirectory(directory);
        openedPathGuard.Verify(rootPath, OpenedPaths.Of(handle));
    }

    // Создание каталога со всеми недостающими предками: сверяется ближайший существующий
    // предок (в нём появится новое) и сам созданный каталог
    private void CreateDirectoryVerified(string rootPath, string directory)
    {
        if (openedPathGuard is not null)
        {
            var existing = directory;
            while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing)!;
            VerifyDirectory(rootPath, existing);
        }
        Directory.CreateDirectory(directory);
        VerifyDirectory(rootPath, directory);
    }

    private static byte[] ReadAll(FileStream stream)
    {
        using (stream)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    public bool IsBinaryFile(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        if (!File.Exists(path)) return false;
        // Медиа — по тем же спискам, что и выбор плеера, иначе wav/webm/webp читались бы текстом
        if (IsImageFile(rootPath, relativePath) || IsVideoFile(relativePath) || IsAudioFile(relativePath))
            return true;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var binaryExts = new[] { ".zip", ".tar", ".gz", ".exe", ".dll", ".bin", ".pdf",
            ".ico", ".wasm", ".so", ".dylib", ".ppt" };
        return binaryExts.Contains(ext);
    }

    public bool IsImageFile(string rootPath, string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp" }.Contains(ext);
    }

    public static bool IsVideoFile(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return new[] { ".mp4", ".webm", ".mov", ".avi", ".mkv" }.Contains(ext);
    }

    public static bool IsAudioFile(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return new[] { ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".opus", ".weba" }.Contains(ext);
    }

    public byte[] ReadFileBytes(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        return ReadAll(OpenVerified(rootPath, path));
    }

    // Поток на чтение для отдачи без загрузки в память. Запись и удаление не блокируются:
    // харнес может править файл, пока его читают.
    public FileStream OpenRead(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        return OpenVerified(rootPath, path, bufferSize: 81920, useAsync: true);
    }

    // Документы: PDF рендерится на клиенте (pdf.js), Office-форматы — через OnlyOffice DS.
    private static readonly Dictionary<string, (string Kind, string Mime)> ViewableDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ("pdf", "application/pdf"),
        [".docx"] = ("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xlsx"] = ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".pptx"] = ("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        // Visio-диаграммы: OnlyOffice DS открывает их только на просмотр (Diagram Viewer, DS 9.0+)
        [".vsdx"] = ("visio", "application/vnd.ms-visio.drawing"),
        [".vsdm"] = ("visio", "application/vnd.ms-visio.drawing.macroenabled.12"),
        [".vssx"] = ("visio", "application/vnd.ms-visio.stencil"),
        [".vssm"] = ("visio", "application/vnd.ms-visio.stencil.macroenabled.12"),
        [".vstx"] = ("visio", "application/vnd.ms-visio.template"),
        [".vstm"] = ("visio", "application/vnd.ms-visio.template.macroenabled.12"),
    };

    // Предельный размер документа для отдачи base64; больше — только скачивание.
    public const long MaxDocumentBytes = 25 * 1024 * 1024;

    public (string Kind, string Mime)? GetDocumentInfo(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ViewableDocuments.TryGetValue(ext, out var info) ? info : null;
    }

    public long GetFileSize(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        if (openedPathGuard is null) return new FileInfo(path).Length;
        using var stream = OpenVerified(rootPath, path);
        return stream.Length;
    }

    public string GetFileBase64(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        return Convert.ToBase64String(ReadAll(OpenVerified(rootPath, path)));
    }

    public void WriteFile(string rootPath, string relativePath, string content)
    {
        var path = SafeJoin(rootPath, relativePath);
        GuardNotWiki(path);
        using (var writer = new StreamWriter(OpenForWrite(rootPath, path, createNew: false), new System.Text.UTF8Encoding(false)))
            writer.Write(content);
        NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
    }

    public void WriteFileBytes(string rootPath, string relativePath, byte[] content)
    {
        var path = SafeJoin(rootPath, relativePath);
        GuardNotWiki(path);
        CreateDirectoryVerified(rootPath, Path.GetDirectoryName(path)!);
        using (var stream = OpenForWrite(rootPath, path, createNew: false))
            stream.Write(content);
        NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
    }

    public void CreateFile(string rootPath, string relativePath) => CreateFile(rootPath, relativePath, "");

    // Создание с начальным содержимым (диаграммы из меню «+»: шаблон пишется одним
    // запросом, чтобы сбой не оставлял пустой файл). Коллизия — ошибка, а не тихая
    // перезапись: File.WriteAllText молча стёр бы существующий файл.
    // Guard best-effort: Exists+WriteAllText не атомарны, при двух одновременных
    // create на один путь победит один из них — 409 это подсказка UI, не гарантия.
    public void CreateFile(string rootPath, string relativePath, string content)
    {
        var path = SafeJoin(rootPath, relativePath);
        GuardNotWiki(path);
        if (File.Exists(path)) throw new InvalidOperationException("файл уже существует");
        CreateDirectoryVerified(rootPath, Path.GetDirectoryName(path)!);
        using (var writer = new StreamWriter(OpenForWrite(rootPath, path, createNew: true), new System.Text.UTF8Encoding(false)))
            writer.Write(content);
        NotifyMutated(rootPath, relativePath, FileMutationKind.Create);
    }

    public void CreateDirectory(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        GuardNotWiki(path);
        CreateDirectoryVerified(rootPath, path);
    }

    public void Delete(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        // Удаление — тоже запись в вику её минусом: Хранитель не терпит, что страницу
        // снесет файловый API (ADR-035, решение 2). Directory.Delete(recursive) действует
        // над поддеревом, поэтому ловится и обратный случай — путь-предок вики.
        GuardNotWikiSubtree(path);
        VerifyParent(rootPath, path);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
        else throw new FileNotFoundException();
        NotifyMutated(rootPath, relativePath, FileMutationKind.Delete);
    }

    public void Rename(string rootPath, string oldRelative, string newRelative)
    {
        var src = SafeJoin(rootPath, oldRelative);
        var dst = SafeJoin(rootPath, newRelative);
        // Обе стороны: вынести из вики нельзя так же, как и внести в неё (как Move в NotesService).
        // Directory.Move действует над поддеревом — поэтому на каждой стороне проверяются
        // оба направления: путь под вики и путь-предок вики (ревью S1b, MAJOR 2).
        GuardNotWikiSubtree(src);
        GuardNotWikiSubtree(dst);
        VerifyParent(rootPath, src);
        VerifyParent(rootPath, dst);
        if (Directory.Exists(src)) Directory.Move(src, dst);
        else File.Move(src, dst);
        NotifyMutated(rootPath, oldRelative, FileMutationKind.Rename, newRelative);
    }

    public async Task<string?> GetDiffAsync(string rootPath, string relativePath, CancellationToken ct = default)
    {
        if (!IsGitRepo(rootPath)) return null;
        try
        {
            // Валидация до передачи в git; корень — отказ: с «:(literal)» это всё дерево
            SafePath.JoinBelowRoot(rootPath, relativePath);
            return await DiffFileVsHeadAsync(rootPath, relativePath, ct);
        }
        catch
        {
            return null;
        }
    }

    // Единая точка входа: для владельца-через-Execution — типизированный метод GitService
    // (внутри сам GitService валидирует путь, идёт через Execute/локальный git и собирает
    // --cached как фолбэк), без DI — прежний хостовый путь через Process.Start.
    private Task<string?> DiffFileVsHeadAsync(string rootPath, string relativePath, CancellationToken ct) =>
        git is not null
            ? git.DiffFileVsHeadAsync(OwnerOf(rootPath), rootPath, relativePath, ct)
            : Task.FromResult<string?>(TryLocalDiff(rootPath, relativePath));

    private static string? TryLocalDiff(string rootPath, string relativePath)
    {
        // «:(literal)» — путь, а не маска: «[ab]», «*» в имени не подтянут соседей
        var spec = ":(literal)" + relativePath;
        var head = GitRun(rootPath, "diff", "HEAD", "--", spec);
        if (!string.IsNullOrWhiteSpace(head)) return head;
        var cached = GitRun(rootPath, "diff", "--cached", "--", spec);
        return string.IsNullOrWhiteSpace(cached) ? null : cached;
    }

    /// <summary>
    /// Последние коммиты репозитория (сырье для продуктовой сводки). Алиасы авторов:
    /// map email → отображаемое имя; нет совпадения — остается git user.name.
    /// projectName проставляется в каждый коммит — для агрегации по всем проектам.
    /// </summary>
    public List<Models.GitCommitRaw> GetCommitsRaw(string rootPath, string projectName = "", int limit = 200, IReadOnlyDictionary<string, string>? authorAliases = null)
    {
        if (!IsGitRepo(rootPath)) return [];
        try
        {
            // Аргументы передаём раздельно (защита от инъекции); %x1f/%x1e —
            // unit/record separators: subject и body могут содержать переводы строк
            var output = GitRun(rootPath, "log", "-n", limit.ToString(),
                "--pretty=format:%H%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b%x1e");
            var commits = new List<Models.GitCommitRaw>();
            foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = record.Trim('\n', '\r').Split('\x1f');
                if (f.Length < 6) continue;
                if (!DateTimeOffset.TryParse(f[3], out var date)) continue;
                var name = f[1];
                var email = f[2];
                if (authorAliases != null && authorAliases.TryGetValue(email, out var alias))
                    name = alias;
                commits.Add(new Models.GitCommitRaw(f[0], name, email, date, f[4], f[5].Trim(), projectName));
            }
            return commits;
        }
        catch { return []; }
    }

    private static string GitRun(string rootPath, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // git выводит UTF-8; без явной кодировки .NET читает в системной (OEM/ANSI)
            // и кириллица в сообщениях коммитов превращается в кракозябры (особенно на проде)
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        // stderr читаем асинхронно, чтобы многословный git не забил буфер и не подвесил ReadToEnd
        proc.BeginErrorReadLine();
        var output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(3000))
            try { proc.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
        return output;
    }

    public bool RevertFile(string rootPath, string relativePath)
    {
        // Откат HEAD — запись в файл (для подписчиков это Write), значит мутация. Вне try:
        // catch ниже возвращает false на сбой git, а отказ вики — не сбой git и не false.
        GuardNotWiki(SafeJoin(rootPath, relativePath));
        if (!IsGitRepo(rootPath)) return false;
        // git checkout HEAD -- file
        try
        {
            // Корень — отказ: с «:(literal)» откат «одного файла» снёс бы всё дерево
            SafePath.JoinBelowRoot(rootPath, relativePath);
            if (git is not null)
            {
                // Через слой Execution (container-юзеры) — DiscardAsync бросает при ошибке
                git.DiscardAsync(OwnerOf(rootPath), rootPath, relativePath).GetAwaiter().GetResult();
                // Откат меняет содержимое файла — подписчики (синк знаний) должны узнать
                NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
                return true;
            }
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = rootPath,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            // «:(literal)» — путь, а не маска: «[ab]», «*» в имени не откатят соседей
            foreach (var a in new[] { "checkout", "HEAD", "--", ":(literal)" + relativePath }) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            // Откат меняет содержимое файла — подписчики (синк знаний) должны узнать
            if (proc.ExitCode == 0) NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private record GitStatusCache(HashSet<string> Modified, HashSet<string> New, long ExpiresAt);
    private static readonly Dictionary<string, GitStatusCache> _statusCache = new();
    // Идущие прямо сейчас вычисления статуса по rootPath — single-flight: при монтировании
    // панели фронт шлёт пачку параллельных листингов (корень + восстановленные раскрытые
    // папки + полное дерево), и все они обязаны ждать ОДИН запуск git status, а не делать
    // одинаковую работу независимо.
    private static readonly Dictionary<string, Lazy<(HashSet<string> modified, HashSet<string> @new)>>
        _statusInFlight = new();
    private static readonly Lock _cacheLock = new();

    // Подмена вычислителя git-статуса в тестах (счётчик вызовов для single-flight);
    // null — штатный путь через GitService / прямой запуск git.
    internal Func<string, (HashSet<string> modified, HashSet<string> @new)>? GitStatusComputer { get; set; }

    // Кеш признака «папка — git-репо». Меняется редко (git init), TTL длиннее статуса.
    private record GitRepoCache(bool IsRepo, long ExpiresAt);
    private static readonly Dictionary<string, GitRepoCache> _repoCache = new();

    private static bool IsGitRepo(string rootPath)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_cacheLock)
        {
            if (_repoCache.TryGetValue(rootPath, out var cached) && cached.ExpiresAt > now)
                return cached.IsRepo;
        }
        // .git — папка (обычный репо) или файл-указатель (worktree/submodule)
        var isRepo = Path.Exists(Path.Combine(rootPath, ".git"));
        var ttl = System.Diagnostics.Stopwatch.Frequency * 60; // 60 секунд
        lock (_cacheLock)
        {
            _repoCache[rootPath] = new GitRepoCache(isRepo, now + ttl);
        }
        return isRepo;
    }

    private (HashSet<string> modified, HashSet<string> @new) GetGitStatus(string rootPath)
    {
        // Не git-репо — не спавним git (иначе на каждый листинг летит
        // `fatal: not a git repository` в stderr и плодятся процессы)
        if (!IsGitRepo(rootPath))
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        Lazy<(HashSet<string> modified, HashSet<string> @new)> flight;
        lock (_cacheLock)
        {
            if (_statusCache.TryGetValue(rootPath, out var cached) && cached.ExpiresAt > now)
                return (cached.Modified, cached.New);
            if (!_statusInFlight.TryGetValue(rootPath, out flight!))
            {
                // ExecutionAndPublication: фабрику исполняет ровно один поток,
                // остальные блокируются на flight.Value до её результата
                flight = new Lazy<(HashSet<string>, HashSet<string>)>(
                    () => ComputeGitStatus(rootPath),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
                _statusInFlight[rootPath] = flight;
            }
        }

        try
        {
            var result = flight.Value;
            var ttl = System.Diagnostics.Stopwatch.Frequency * 5; // 5 секунд
            lock (_cacheLock)
            {
                _statusCache[rootPath] = new GitStatusCache(result.modified, result.@new,
                    System.Diagnostics.Stopwatch.GetTimestamp() + ttl);
            }
            return result;
        }
        finally
        {
            // Снимаем flight по ссылке: запись мог уже заменить следующий промах кэша
            lock (_cacheLock)
            {
                if (_statusInFlight.TryGetValue(rootPath, out var cur) && ReferenceEquals(cur, flight))
                    _statusInFlight.Remove(rootPath);
            }
        }
    }

    private (HashSet<string> modified, HashSet<string> @new) ComputeGitStatus(string rootPath)
    {
        if (GitStatusComputer is not null)
            return GitStatusComputer(rootPath);

        var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var @new = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (git is not null)
            {
                // Через слой Execution: для container-юзеров git выполняется в песочнице
                // по правильному дереву (владелец резолвится по корню проекта).
                // Лёгкий StatusPathsAsync — только пути, один запуск git status: тяжёлый
                // StatusAsync с построчной статистикой здесь не нужен, числа выбрасывались.
                var st = git.StatusPathsAsync(OwnerOf(rootPath), rootPath).GetAwaiter().GetResult();
                foreach (var f in st.Staged) modified.Add(f.Path.Replace('\\', '/'));
                foreach (var f in st.Unstaged) modified.Add(f.Path.Replace('\\', '/'));
                foreach (var f in st.Untracked) @new.Add(f.Path.Replace('\\', '/'));
            }
            else
            {
                // Фолбэк без DI (юнит-тесты): прежний прямой запуск git на хосте
                // -uall — как в GitService.StatusPathsAsync: иначе новая папка приходит одной записью
                // «dir/», и файлы внутри неё не помечаются новыми в дереве
                var psi = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain -uall")
                {
                    WorkingDirectory = rootPath,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length < 4) continue;
                    if (line[0] == '!' && line[1] == '!') continue; // ignored
                    var path = line[3..];
                    // для переименований: "R  old -> new" берём новый путь
                    var arrowIdx = path.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrowIdx >= 0) path = path[(arrowIdx + 4)..];
                    var normalizedPath = path.Trim().Replace('\\', '/');
                    if (line[0] == '?' && line[1] == '?')
                        @new.Add(normalizedPath);
                    else
                        modified.Add(normalizedPath);
                }
                proc.WaitForExit(3000);
            }
        }
        catch { }

        return (modified, @new);
    }
}
