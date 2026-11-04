using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Закрытое поддерево вложений приватного чата (S2d-6b-1, разрез 4bc3d04f §1 путь 19).
/// Вложения ПРИВАТНОГО проектного чата пишутся в <c>.cc-attachments/private/{author}/…</c> —
/// принадлежность определяется по ПУТИ, без обращения к каталогу сессий: путь несёт id автора.
/// Общие чаты и чаты вне проекта — прежняя раскладка <c>.cc-attachments/{guid}/{имя}</c>
/// (<see cref="AttachmentsGitExclude.AttachmentPath"/>), этот префикс их не касается.
/// Чтение закрывает <see cref="IsForeign"/> в 6b-2 (FilesController) и 6b-3 (WorkspaceToolset, WebDAV).
/// </summary>
public static class PrivateAttachments
{
    // Корень поддерева внутри .cc-attachments (TreeExcludes режет всё под .cc-attachments
    // из дерева и git-игнора — поддерево закрыто ими же, отдельного правила не нужно)
    public const string PrivateRoot = "private";

    // Санация id автора в пути: тот же характер, что у id пользователей (Guid «N»),
    // без разделителей пути и мусора. Непрошедшая — отказ записи (Build → null).
    private static readonly Regex UserIdPattern = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

    /// <summary>
    /// Путь нового вложения приватного чата относительно рабочей папки:
    /// <c>.cc-attachments/private/{authorUserId}/{guid}/{имя}</c>. null — id автора не проходит
    /// санацию или имя не имя файла: <c>Path.GetFileName</c> срезает path-сегменты (../evil),
    /// ровно как у <see cref="AttachmentsGitExclude.AttachmentPath"/> — форма ответа Upload
    /// для потребителя (фронт, CLI) не меняется, добавляется только средний сегмент.
    /// </summary>
    public static string? Build(string? authorUserId, Guid guid, string? fileName)
    {
        var safeName = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrEmpty(safeName) || safeName is "." or "..") return null;
        if (string.IsNullOrEmpty(authorUserId) || !UserIdPattern.IsMatch(authorUserId)) return null;
        return $"{TreeExcludes.AttachmentsDir}/{PrivateRoot}/{authorUserId}/{guid:N}/{safeName}";
    }

    /// <summary>
    /// Общий путь вложения <c>.cc-attachments/{guid}/{имя}</c> → путь в закрытом поддереве автора
    /// <c>.cc-attachments/private/{author}/{guid}/{имя}</c> (S2d-6b-4, перенос при включении приватности).
    /// null — путь не в общей раскладке (уже private/ или чужой формы) либо автор не прошёл санацию.
    /// </summary>
    public static string? ToPrivate(string? authorUserId, string? sharedRel)
    {
        var seg = Normalize(sharedRel);
        if (seg.Count != 3 || !string.Equals(seg[0], TreeExcludes.AttachmentsDir, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(seg[1], PrivateRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(authorUserId) || !UserIdPattern.IsMatch(authorUserId)) return null;
        return $"{TreeExcludes.AttachmentsDir}/{PrivateRoot}/{authorUserId}/{seg[1]}/{seg[2]}";
    }

    /// <summary>
    /// Путь в поддереве автора → общий путь <c>.cc-attachments/{guid}/{имя}</c> (обратный перенос при
    /// снятии приватности). null — путь не вида <c>.cc-attachments/private/{author}/{guid}/{имя}</c>.
    /// </summary>
    public static string? ToShared(string? privateRel)
    {
        var seg = Normalize(privateRel);
        if (seg.Count != 5 || !string.Equals(seg[0], TreeExcludes.AttachmentsDir, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(seg[1], PrivateRoot, StringComparison.OrdinalIgnoreCase)) return null;
        return $"{TreeExcludes.AttachmentsDir}/{seg[3]}/{seg[4]}";
    }

    /// <summary>
    /// Чужое ли это приватное вложение. Путь после канонизации (проценты, обратные слеши,
    /// «.» и «..», пустые сегменты) лежит под <c>.cc-attachments/private/{X}/…</c> и X ≠ userId →
    /// true. Всё остальное — общие вложения, свои private, пути вне .cc-attachments — false:
    /// поведение вызывающих не меняется. Пустой userId — «своего» нет: любое приватное чужое
    /// (fail-closed, как <see cref="SessionAccess.CanSee"/>).
    /// Обходы («..», «./», регистр, смешанные слеши, %2F/%2E) для чужого private дают true,
    /// а не false: сравнение после нормализации, имена каталогов и id — регистронезависимо
    /// (на ОС, чувствительной к регистру, несуществующий каталог всё равно даст 404 ниже).
    /// </summary>
    public static bool IsForeign(string? relPath, string? userId)
    {
        var segments = Normalize(relPath);
        if (segments is null || segments.Count < 3) return false;
        if (!string.Equals(segments[0], TreeExcludes.AttachmentsDir, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(segments[1], PrivateRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(userId)) return true;
        return !string.Equals(segments[2], userId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Контейнер ли это, под которым лежит closed-поддерево private/: корень проекта (пустой путь,
    /// «.», «docs/..»), сам .cc-attachments, .cc-attachments/private. IsForeign их не видит: у них нет
    /// сегмента-автора (тот требует ≥3), а операция над контейнером относится ко всему поддереву
    /// (S2d-6b-3 доработка 2, дельта-ревью 24c92aef). Формула одна для WebDAV (COPY/MOVE) и MCP
    /// (files_rename/files_delete) — здесь. Каталоги авторов — дети private/, НЕ контейнеры: свой
    /// private-каталог автор переносит и удаляет, как раньше; общие {guid}-каталоги — тоже.
    /// Нормализация та же, что у IsForeign: «..», «./», %2F/%2E, слеши, двойные разделители, регистр.
    /// Путь за пределами корня решает вызывающий (SafeJoin даёт ему свой отказ раньше этого
    /// предиката), поэтому сюда он не доходит. Вариант без корня — <see cref="IsAttachmentsContainer"/>
    /// (там, где корень — штатная цель операции, напр. IndexFolder, S2d-6b-6).
    /// </summary>
    public static bool IsPrivateContainerPath(string? relPath)
    {
        var segments = Normalize(relPath);
        // корень — под ним лежит весь .cc-attachments: GetRelativePath корня от себя — «.»
        if (segments.Count == 0) return true;
        return IsAttachmentsContainer(relPath);
    }

    /// <summary>
    /// Контейнеры закрытого поддерева без корня проекта: сам .cc-attachments и .cc-attachments/private.
    /// Отличен от <see cref="IsPrivateContainerPath"/> ровно на корне: для разрушительных операций
    /// корень — родитель поддерева (Rename/Delete, S2d-6b-3 доработка 2), а индексация корня — штатная
    /// цель IndexFolder: обход TreeExcludes в .cc-attachments не спускается, и индексация её содержимого
    /// через корень невозможна в принципе (S2d-6b-6, хвост S2d-6b-5). Нормализация та же, что у IsForeign.
    /// </summary>
    public static bool IsAttachmentsContainer(string? relPath)
    {
        var segments = Normalize(relPath);
        if (segments.Count == 0) return false; // корень здесь не контейнер — см. doc метода
        if (!string.Equals(segments[0], TreeExcludes.AttachmentsDir, StringComparison.OrdinalIgnoreCase))
            return false;
        if (segments.Count == 1) return true; // .cc-attachments
        return segments.Count == 2
            && string.Equals(segments[1], PrivateRoot, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Канонический (по РАЗЫМЕНОВАННОМУ пути) гейт того же поддерева — для точек,
    // которые действуют на диске (дефект a6f5cb15, ревью R-QA-W1 03.11).
    // IsForeign/IsPrivateContainerPath/IsAttachmentsContainer выше судят по СТРОКОВЫМ
    // сегментам: они держат обходы («..», «./», %2F, слеши, регистр) без обращения к диску,
    // но символьные ссылки не разыменовывают. Операция же идёт по разыменованному пути
    // (ядро разыменовывает компонент-ссылку на любом syscall): симлинк «L» → .cc-attachments
    // в корне проекта делает «L/private/{other}/…» тем же чужым private, и текстовый гейт о
    // нём молчит — чужое читается, пишется, переносится и сносится через ссылку.
    // Здесь — сравнение канонических форм по образцу WikiPaths.IsWikiPath: путь и сами
    // контейнеры канонизируются одним обходом SafePath.Canonical — иначе «L/private» и
    // настоящий .cc-attachments/private описывают разные деревья (MAJOR 2 ревью S1a).
    // Путь сначала проходит SafePath.Join — ровно так же, как его пропустит сама операция
    // (FileService открывает уже свёрнутый по «..» путь), поэтому «..» в строке не меняет
    // смысл сравнения. За пределами корня — false: там вызывающий отдаёт свой отказ раньше
    // (SafeJoin), как и в текстовой ветке. Canonical == null (цикл ссылок, превышен бюджет)
    // — true: предикат fail-closed, как IsWikiPath — отказ возможной операции безопаснее
    // действия по неразрешённому пути. Регистр — по правилам ФС хоста (SafePath.ComparisonFor),
    // как у канонических проверок MOVE.
    // Вызывающим, которые открывают файл (WebDAV, FilesController, MCP, Knowledge), нужен
    // этот вариант; текстовый остаётся там, где пути не идут на диск (перенос раскладки,
    // фильтр обхода FileService по относительным путям).

    /// <summary>
    /// Чужое ли это приватное вложение по разыменованному пути: каноническая форма пути лежит
    /// под каноническим .cc-attachments/private/ и первый сегмент под ним — не userId. Сам
    /// контейнер private/ — не «чужой автор»: его решает <see cref="IsContainerResolved"/>.
    /// Пустой userId — «своего» нет: любое приватное чужое (fail-closed, как <see cref="IsForeign"/>).
    /// Неразрешимый путь (цикл ссылок) — true: тот же fail-closed, что у IsWikiPath.
    /// </summary>
    public static bool IsForeignResolved(string root, string? relPath, string? userId)
    {
        var (inRoot, target) = CanonicalWithinRoot(root, relPath);
        if (!inRoot) return false;
        if (target is null) return true; // неразрешимый путь: fail-closed
        var privateDir = CanonicalOf(Path.Combine(root, TreeExcludes.AttachmentsDir, PrivateRoot));
        if (privateDir is null) return true; // сам контейнер неразрешим — тот же fail-closed
        var comparison = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        if (!target.StartsWith(privateDir + "/", comparison)) return false; // не под closed-поддеревом
        var author = target[(privateDir.Length + 1)..].Split('/')[0];
        if (author.Length == 0) return false; // сам private/ — контейнер, не автор
        if (string.IsNullOrEmpty(userId)) return true;
        return !author.Equals(userId, comparison);
    }

    /// <summary>
    /// Контейнер ли это (корень проекта, .cc-attachments, .cc-attachments/private) по
    /// разыменованному пути: каноническая форма пути равна канонической форме одного из
    /// контейнеров — «L» → .cc-attachments и «L/private» дают те же контейнеры на диске.
    /// Неразрешимый путь — true (fail-closed). Каталоги авторов и {guid}-каталоги — не
    /// контейнеры и через ссылку: их канонический путь не равен ни одному контейнеру.
    /// </summary>
    public static bool IsContainerResolved(string root, string? relPath) =>
        IsContainerAt(root, relPath, includeRoot: true);

    /// <summary>
    /// То же без корня проекта — там, где корень не разрушается, а является штатной целью
    /// операции (индексация папки, IndexFolder, S2d-6b-6). Нормализация та же, что у
    /// <see cref="IsContainerResolved"/>.
    /// </summary>
    public static bool IsAttachmentsContainerResolved(string root, string? relPath) =>
        IsContainerAt(root, relPath, includeRoot: false);

    private static bool IsContainerAt(string root, string? relPath, bool includeRoot)
    {
        var (inRoot, target) = CanonicalWithinRoot(root, relPath);
        if (!inRoot) return false;
        if (target is null) return true; // неразрешимый путь: fail-closed
        var comparison = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        foreach (var container in ContainersOf(root, includeRoot))
        {
            var canonical = CanonicalOf(container);
            if (canonical is null) return true; // контейнер неразрешим — тот же fail-closed
            if (target.Equals(canonical, comparison)) return true;
        }
        return false;
    }

    private static IEnumerable<string> ContainersOf(string root, bool includeRoot)
    {
        if (includeRoot) yield return root;
        yield return Path.Combine(root, TreeExcludes.AttachmentsDir);
        yield return Path.Combine(root, TreeExcludes.AttachmentsDir, PrivateRoot);
    }

    // Путь запроса → каноническая форма внутри корня проекта. Отметка inRoot=false — путь за
    // пределами корня: его отклоняет SafeJoin у вызывающего, здесь он ни чужой, ни контейнер
    // (тот же порядок, что у текстовой ветки). Canonical=null при inRoot=true — путь неразрешим
    // (цикл ссылок, превышен бюджет): вызывающие трактают это fail-closed. Форма — с «/», без
    // завершающего разделителя: «L» и «L/» — одна цель (сравнение идёт по равенству и префиксу).
    private static (bool InRoot, string? Canonical) CanonicalWithinRoot(string root, string? relPath)
    {
        string abs;
        try { abs = SafePath.Join(root, relPath ?? ""); }
        catch (UnauthorizedAccessException) { return (false, null); }
        return (true, CanonicalOf(abs));
    }

    private static string? CanonicalOf(string path) => SafePath.Canonical(path)?.TrimEnd('/');

    // Путь к сегментам в каноническом виде. Одно снятие %-кодирования: %2F/%2E — тот же путь,
    // записанный экранированно (двойное кодирование вне контракта: %252e остаётся сегментом
    // «%2e» — он не «.» и не «..», чужое private под ним остаётся чужим). «..» при пустом
    // стеке — выход за корень: его отклоняет SafeJoin у вызывающего, здесь он просто попоп.
    private static List<string> Normalize(string? relPath)
    {
        var stack = new List<string>();
        if (string.IsNullOrEmpty(relPath)) return stack;
        foreach (var raw in Uri.UnescapeDataString(relPath).Replace('\\', '/').Split('/'))
        {
            if (raw.Length == 0 || raw == ".") continue;
            if (raw == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(raw);
        }
        return stack;
    }
}
