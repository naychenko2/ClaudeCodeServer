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
    // которые действуют на диске (дефект a6f5cb15, ревью R-QA-W1 03.11; формула чужести —
    // DEC-ROOTS 5c8a4705 + DEC-TAIL п.1 c83ff66d, 04.11).
    // IsForeign/IsPrivateContainerPath/IsAttachmentsContainer выше судят по СТРОКОВЫМ
    // сегментам: они держат обходы («..», «./», %2F, слеши, регистр) без обращения к диску,
    // но символьные ссылки не разыменовывают. Операция же идёт по разыменованному пути
    // (ядро разыменовывает компонент-ссылку на любом syscall): симлинк «L» → .cc-attachments
    // в корне проекта делает «L/private/{other}/…» тем же чужым private, и текстовый гейт о
    // нём молчит — чужое читается, пишется, переносится и сносится через ссылку.
    // ЧУЖЕСТЬ (DEC-ROOTS 5c8a4705 + хвост ревью 44e1219c): ДВУМЯ линиями канона —
    // 1) сегмент .cc-attachments/private/{X} на ЛЮБОЙ глубине канонического АБСОЛЮТНОГО
    // пути цели, X ≠ актор; 2) канон цели под КАОНИЧЕСКИМ <opRoot>/.cc-attachments/private
    // (формула до 8b56ba3db, оставленная как вторая линия, а не заменённая первой). Вторая
    // линия нужна там, где первая слепнет: если сам .cc-attachments (или его private) —
    // ссылка на каталог с ДРУГИМ именем (.cc-attachments → /mnt/bigdisk/attachments), пары
    // сегментов в каноне нет, но closed-поддерево — то же поддерево: оно за ссылкой.
    // Ни одна из линий не требует «канон под opRoot»: префикс второй строится от канона
    // private корня — сам он вправе уйти за opRoot, как и цель через ссылку. Канон цели при
    // этом вправе выйти за opRoot: closed-поддерево живёт не только в корне проекта, а в
    // рабочем дереве чата (EffectiveRoot = WorktreePath ?? RootPath, SessionManager.GetChatRoot),
    // и ссылка из корня в worktree (W → wt), ссылка внутри worktree и вложенный worktree
    // (защитный кейс: worktree, созданный ВНУТРИ корня — штатные деревья чатов и слотов
    // ревью лежат вне главного дерева, {home}/.worktrees/<проект>/<ветка>,
    // SessionManager.WorktreesBase) — то же чужое private. opRoot нужен только для SafeJoin
    // и канонизации, а не для того, чтобы назначить «правильный» private. Легальные ссылки
    // наружу на не-private путь (frontend/node_modules → primary checkout у worktree-setup.sh)
    // ни под одну линию не попадают и остаются разрешены: вариант «канон обязан остаться под
    // opRoot» отклонён — он отказал бы им всем. Ложное срабатывание возможно только на
    // пользовательском каталоге с буквальным .cc-attachments/private внутри дерева или с
    // ссылкой .cc-attachments на каталог, где есть свой private/ — риск принят: это
    // служебное имя продукта.
    // Путь сначала проходит SafePath.Join — ровно так же, как его пропустит сама операция
    // (FileService открывает уже свёрнутый по «..» путь). Но это равенство предиката и
    // операции, а не «свёртка ничего не меняет»: «..» сворачивается текстово ДО
    // разыменования, и для ссылки, чья цель внутри корня, текстово свёрнутый путь и то,
    // что дал бы ОС по несвёрнутой строке, — разные места. Пример: inner → .cc-attachments/
    // private/{other}, путь inner/../{other}/x свернётся в <root>/{other}/x (вне closed-
    // поддерева), а ОС по несвёрнутой строке развернул бы «..» физически — в
    // .cc-attachments/private/{other}/x. И предикат, и операция судят свёрнутый путь:
    // closed-поддерево таким обходом не открывается (направление безопасное), но сравниваются
    // здесь разные пути. За пределами корня — false: там вызывающий отдаёт свой отказ раньше
    // (SafeJoin), как и в текстовой ветке. Canonical == null (цикл ссылок, превышен бюджет)
    // — true: предикат fail-closed, как IsWikiPath — отказ возможной операции безопаснее
    // действия по неразрешённому пути. Регистр автора — по правилам ФС хоста
    // (SafePath.ComparisonFor), как у канонических проверок MOVE.
    // Вызывающим, которые открывают файл (WebDAV, FilesController, MCP, Knowledge), нужен
    // этот вариант; текстовый остаётся там, где пути не идут на диск (перенос раскладки,
    // фильтр обхода FileService по относительным путям).
    // Границы, принятые намеренно и этим предикатом НЕ закрытые (DEC-ROOTS «D», зафиксировано
    // в docs/research/shared-projects-cut-2026-10.md §И.2; guard-тесты —
    // PrivateAttachmentsResolvedTests):
    // D.1 — hardlink файла из private под другим именем не ловит ни один предикат: суд идёт
    //     по пути, а не по inode; закрыть можно только на уровне ФС (права или отдельный
    //     mount), в эту волну не входит.
    // D.2 — записи и чтения агента (Bash/Write/Read) идут в его cwd = EffectiveRoot и
    //     предикат продукта не проходят: private — граница API и UI продукта, а не ФС для
    //     агента. Настоящее закрытие требует изоляции ФС — отдельная фича (карточка
    //     бэклога 66407b5f, с ADR-заметкой).
    // D.3′ — вторая линия судит по private-каталогу КОРНЯ ОПЕРАЦИИ: если каталог private
    //     проекта не разрешается из корня операции (операция идёт по дереву фичи/локатору,
    //     а каталог — в корне проекта или в дереве чата) И при этом .cc-attachments ТОГО ЖЕ
    //     КОРНЯ, ГДЕ ЛЕЖИТ private-КАТАЛОГ (или его private) — ссылка на каталог с другим
    //     именем, чужесть не определяется ни одной линией: префикс opRoot не совпадает с
    //     каноном чужого private, а пары .cc-attachments+private в каноне нет — чужое
    //     private читается. Не случай ссылки ПОД opRoot: её ловит сама вторая линия —
    //     её префикс строится от канона private корня операции. Не регресс:
    //     сегментная формула 8b56ba3db этот кейс тоже не ловила (хвост ревью R-ROOTS-PAIR
    //     88ee96aa п.1). Закрытие — отдельное решение (вторая линия с явным корнем
    //     проекта), в эту волну не входит. Инвариант остаётся: ни одна из линий не требует
    //     «канон под opRoot» — легальная ссылка наружу на не-private разрешена; когда
    //     корневой private вне opRoot, но разрешается из него (.cc-attachments — ссылка на
    //     чужой каталог со своим private/), чужое через имя всё равно чужое
    //     (guard «СсылкаНаКаталогСДругимИменем…»).

    /// <summary>
    /// Чужое ли это приватное вложение по разыменованному пути: ДВУМЯ линиями канона —
    /// канонический абсолютный путь цели содержит сегмент <c>.cc-attachments/private/{X}</c>
    /// на любой глубине и X ≠ userId (DEC-ROOTS 5c8a4705) ИЛИ лежит под каноническим
    /// <c>&lt;opRoot&gt;/.cc-attachments/private</c> (хвост ревью 44e1219c: ловит случай,
    /// когда сам .cc-attachments или его private — ссылка на каталог с другим именем, и
    /// пары сегментов в каноне нет; автор — следующий сегмент под ним). opRoot —
    /// фактический корень операции, нужен для SafeJoin и канонизации, а не для выбора
    /// «правильного» private: ни одна из линий не требует «канон под opRoot» — канон вправе
    /// уйти за opRoot (ссылка в worktree, ссылка из корня в worktree, вложенный worktree —
    /// защитный кейс). Сам контейнер private/ — не «чужой автор»: его решает
    /// <see cref="IsContainerResolved"/>.
    /// Пустой userId — «своего» нет: любое приватное чужое (fail-closed, как <see cref="IsForeign"/>).
    /// Неразрешимый путь (цикл ссылок) — true: тот же fail-closed, что у IsWikiPath.
    /// Чужой по СТРОКЕ путь (<see cref="IsForeign"/>) — true и когда он разрешается наружу:
    /// имя, ведущее в closed-поддерево, отказано независимо от того, куда оно разыменовалось
    /// (инвариант «closed-поддерево отказано по любому имени», ревью 6db670ff; ручная ссылка
    /// .cc-attachments/private/{other} на каталог вне поддерева не обходит гейт). На путях без
    /// ссылок и с обходами («..») канонические линии ничего не добавляют к текстовой:
    /// Normalize сворачивает «..» так же, как SafeJoin (тест «ОбходыЧерезСсылку» — те же
    /// исходы). За пределами opRoot (SafeJoin бросает) — false: тот же порядок, что у
    /// текстовой ветки, свой отказ отдаёт вызывающий. Легальная ссылка наружу на не-private
    /// путь остаётся не-чужой (guard-тесты «СсылкаНаружу…» и «СсылкаИзКорняВWorktree…»:
    /// чужесть судится по сегменту и по канону private корня, а не по месту канона).
    /// Остаток D.3′ (см. блок границ выше): если private-каталог проекта не разрешается из
    /// opRoot и при этом .cc-attachments корня, где лежит private-каталог (или его private),
    /// — ссылка на каталог с другим именем, обе канонические линии молчат и чужое
    /// читается; это принятый остаток, не регресс.
    /// </summary>
    public static bool IsForeignResolved(string opRoot, string? relPath, string? userId)
    {
        // Текстовый гейт — первой линией, ДО канонического (ревью 6db670ff, п.2): чужесть
        // судится и по имени, не только по разыменованному пути
        if (IsForeign(relPath, userId)) return true;
        string abs;
        try { abs = SafePath.Join(opRoot, relPath ?? ""); }
        catch (UnauthorizedAccessException) { return false; } // за пределами корня — свой отказ вызывающего
        var target = CanonicalOf(abs);
        if (target is null) return true; // неразрешимый путь: fail-closed
        if (HasForeignPrivateSegment(target, userId)) return true;
        return IsUnderCanonicalPrivate(opRoot, target, userId);
    }

    // Первая линия канона — чужесть по СЕГМЕНТУ (DEC-ROOTS): .cc-attachments/private/{X} на любой глубине
    // абсолютного канонического пути, независимо от того, под каким корнем цель оказалась
    // после разыменования. Имена служебных каталогов — регистронезависимо (тот же приём, что
    // у текстовой линии и у TreeExcludes); автор — по правилам ФС хоста (SafePath.ComparisonFor),
    // как у канонических проверок MOVE. Сам контейнер (.cc-attachments, .cc-attachments/private)
    // автора не несёт — не чужой: его решает IsContainerResolved.
    private static bool HasForeignPrivateSegment(string canonical, string? userId)
    {
        var comparison = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        var segments = canonical.Split('/');
        for (var i = 0; i + 2 < segments.Length; i++)
        {
            if (!string.Equals(segments[i], TreeExcludes.AttachmentsDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(segments[i + 1], PrivateRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(userId)) return true; // «своего» нет: любое приватное чужое
            if (!segments[i + 2].Equals(userId, comparison)) return true;
        }
        return false;
    }

    // Вторая линия канона (хвост ревью 44e1219c к 8b56ba3db): канон цели под КАОНИЧЕСКИМ
    // <opRoot>/.cc-attachments/private — формула, которая была до DEC-ROOTS и осталась здесь
    // как дополнение к сегментной, а не заменённая ею. Она ловит то, чего первая не видит:
    // если сам .cc-attachments (или его private) — ссылка на каталог с другим именем
    // (.cc-attachments → /mnt/bigdisk/attachments), пары сегментов в каноне цели нет, но
    // closed-поддерево — то же поддерево: оно за ссылкой. Канон контейнера берётся тем же
    // обходом SafePath.Canonical, что и канон цели: «L/private» и настоящий private описывают
    // одну цель (тот же приём, что у IsContainerAt). Префикс строится от канона private корня,
    // а не от opRoot: «канон обязан остаться под opRoot» не требуется ни одной из линий —
    // легальная ссылка наружу на не-private (node_modules → checkout) под этим префиксом не
    // оказывается. Автор — ровно следующий сегмент после private (глубже судит первая линия);
    // сам контейнер — не чужой автор, его решает IsContainerResolved (проверка префикса с «/»
    // сама по себе отсекает и private/, и .cc-attachments: канон приходит без завершающего «/»,
    // первый сегмент под префиксом всегда непустой). Неразрешимый канон
    // контейнера (цикл ссылок в самом .cc-attachments) — тот же fail-closed, что у IsContainerAt:
    // поддерево, которого нельзя разобрать, не может быть и «своим».
    // Граница этой линии (D.3′, принятый остаток — см. блок границ выше): она судит по
    // private-каталогу КОРНЯ ОПЕРАЦИИ. Если каталог private проекта из opRoot не разрешается
    // (операция по дереву фичи/локатору, каталог — в корне проекта или дерева чата) и при этом
    // .cc-attachments корня, где лежит private-каталог (или его private), — ссылка на каталог
    // с другим именем, префикс opRoot не совпадает с каноном чужого private и первая линия
    // молчит из-за выпавшей пары — чужое читается. Ссылка под opRoot — не этот случай: её
    // ловит сама эта линия (префикс строится от канона private корня операции, см. выше).
    // Закрытие — отдельное решение (явный корень проекта второму параметру), не эта волна.
    private static bool IsUnderCanonicalPrivate(string opRoot, string target, string? userId)
    {
        var privateDir = CanonicalOf(Path.Combine(opRoot, TreeExcludes.AttachmentsDir, PrivateRoot));
        if (privateDir is null) return true; // сам контейнер неразрешим — тот же fail-closed
        var comparison = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        if (!target.StartsWith(privateDir + "/", comparison)) return false; // не под closed-поддеревом корня
        var author = target[(privateDir.Length + 1)..].Split('/')[0];
        if (string.IsNullOrEmpty(userId)) return true; // «своего» нет: любое приватное чужое
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

    // Путь запроса → каноническая форма внутри корня проекта (только для контейнерных
    // предикатов ниже; чужесть судится по сегменту канона, см. IsForeignResolved). Отметка
    // inRoot=false — путь за пределами корня: его отклоняет SafeJoin у вызывающего, здесь
    // он не контейнер (тот же порядок, что у текстовой ветки). Canonical=null при inRoot=true — путь неразрешим
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
