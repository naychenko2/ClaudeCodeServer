namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Кто, что и где может в папке предмета (И8, ADR-041 ред. 6 §12 «Папки и права», решение 65).
/// Одна точка правды: её зовут решение разрешения CLI (Б10b), рельса «Файлы» (Б11a),
/// загрузка (Б11b), фильтр вложений хаба (Б11c) и заметки (Б20b) — второй копии правил
/// ни у одного потребителя нет. Чистая функция без IO, кроме канонизации
/// (SafePath.Canonical разворачивает симлинки по состоянию диска).
/// Fail-closed: путь или корень не разобрать — Deny; отказ лишнего действия безопаснее
/// действия по неверному пути (тот же приём, что у IsWikiPath).
/// Имена защищённых папок сравниваются по имени (OrdinalIgnoreCase + NFC): «материалы/»
/// или NFD-форма «Для репетитора» — та же папка для политики, перемена регистра или
/// состава символа не обходит границу.
/// Текстовая копия судится по своему оригиналу (решение 91): форма копии — одна функция
/// TextCopyOf (решение 90), OriginalOf — её обратная; «.ccs-text» как папка значима только
/// на верхнем уровне, на глубине это обычный скрытый сегмент.
/// </summary>
public static class ChildPathPolicy
{
    public const string MaterialsDir = "Материалы";
    public const string TutorDir = "Для репетитора";
    public const string TextDir = ".ccs-text";

    internal const StringComparison NameComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Путь текстовой копии файла (решение 90): «.ccs-text/&lt;путь оригинала&gt;.txt».
    /// Разделители выравниваются в «/», ведущий «/» срезается; расширение оригинала
    /// сохраняется: «лист.pdf» → «.ccs-text/лист.pdf.txt», «Задачи/лист.pdf» →
    /// «.ccs-text/Задачи/лист.pdf.txt» — копия ключа сохраняет его полный путь. Путь, уже
    /// лежащий под «.ccs-text/», возвращается как есть: копия сама. Чистая функция без IO:
    /// её зовут и генератор копии, и все судящие, чтобы форма копии задавалась одним местом.</summary>
    public static string TextCopyOf(string rel)
    {
        var norm = (rel ?? "").Replace('\\', '/').Trim().TrimStart('/');
        return norm.StartsWith(TextDir + "/", NameComparison) ? norm : TextDir + "/" + norm + ".txt";
    }

    /// <summary>Оригинал текстовой копии (обратная к TextCopyOf): путь оригинала или null.
    /// Не null только для правильной формы копии: первый сегмент — «.ccs-text», за ним есть
    /// хотя бы один сегмент, путь кончается на «.txt» и после среза суффикса имя не пустое.
    /// Всё остальное — null: судящий обязан решить по факту пути, а не додумывать оригинал
    /// (fail-closed, тот же приём, что у всей функции).</summary>
    public static string? OriginalOf(string copyRel)
    {
        var norm = (copyRel ?? "").Replace('\\', '/').Trim().TrimStart('/');
        var segments = norm.Split('/');
        if (segments.Length < 2 || !segments[0].Equals(TextDir, NameComparison))
            return null;
        if (!norm.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return null;
        // Срез с двух сторон: «.ccs-text/» спереди и «.txt» сзади (суффикс ровно 4 символа,
        // EndsWith проверял регистронезависимо).
        var original = norm[(TextDir.Length + 1)..^4];
        // Имя после среза не пустое: «.ccs-text/.txt» и «.ccs-text/папка/.txt» — не копия файла.
        return Path.GetFileName(original).Length > 0 ? original : null;
    }

    public static ChildPathDecision Decide(ChildActor actor, ChildPathOp op, string hostPath, string subjectRoot)
    {
        // Fail-closed по входу (миноры ревью 111e3b4f): NUL в пути, пустой или null корень,
        // корень «/» — Deny без исключения наружу: Path.* бросает ArgumentException на NUL и на
        // пустом имени, GetFullPath — на пустом hostPath; ни одно из них не выходит за функцию.
        try
        {
            return DecideInner(actor, op, hostPath, subjectRoot);
        }
        catch (ArgumentException)
        {
            return ChildPathDecision.Deny("Не разобрать путь");
        }
    }

    private static ChildPathDecision DecideInner(ChildActor actor, ChildPathOp op, string hostPath, string subjectRoot)
    {
        // Пустой/null корень — не папка предмета (явный Deny: Path.GetFullPath("") бросает
        // ArgumentException, и равный ему Deny не должен зависеть от того, где именно он
        // бросается — в Canonical или в её GetPathRoot).
        if (string.IsNullOrEmpty(subjectRoot))
            return ChildPathDecision.Deny("Не задана папка предмета");

        var root = SafePath.Canonical(subjectRoot);
        if (root is null)
            return ChildPathDecision.Deny("Не разобрать папку предмета");
        root = root.TrimEnd('/'); // Canonical отдаёт сам корень с завершающим разделителем
        if (root.Length == 0)
            return ChildPathDecision.Deny("Не задана папка предмета"); // корень «/» после TrimEnd

        // Относительный путь разрешается от папки предмета: Canonical разрешает относительный
        // путь от cwd процесса — рабочая папка сервера не папка ребёнка.
        if (string.IsNullOrEmpty(hostPath))
            return ChildPathDecision.Deny("Путь не задан");
        var full = SafePath.Canonical(Path.IsPathRooted(hostPath) ? hostPath : root + "/" + hostPath);
        if (full is null)
            return ChildPathDecision.Deny("Не разобрать путь");

        var boundary = SafePath.ComparisonFor(OperatingSystem.IsWindows()); // та же граница, что у SafePath.Join
        if (full.Equals(root, boundary))
            // Сама папка предмета читается и перечисляется; записывать и удалять её как файл
            // не может никто, включая куратора (доработка ревью 111e3b4f, п. 1).
            return op is ChildPathOp.Read or ChildPathOp.List
                ? ChildPathDecision.Allow()
                : ChildPathDecision.Deny("Сама папка предмета не пишется и не удаляется");
        if (!full.StartsWith(root + "/", boundary))
            return ChildPathDecision.Deny("Вне папки предмета");

        var segments = full[(root.Length + 1)..].Split('/');

        // «.ccs-text/» (решение 91, дефект 64996f49): текстовая копия судится по своему
        // оригиналу — иначе копия ключа репетитора «.ccs-text/Для репетитора/k.pdf.txt»
        // была бы видна ребёнку (прежняя ветка открывала сегмент всем на любой глубине).
        // Копии пишет только сервер: любая операция кроме Read/List отказана всем, включая
        // куратора. Сам каталог — Allow всем (листать его можно). Остальное судится как
        // X = OriginalOf(rel) ?? остаток после «.ccs-text/» — теми же правилами. Разворот
        // ровно на один уровень: если X снова начинается с «.ccs-text», это обычный
        // скрытый сегмент (защита на глубине), а не повтор этого правила.
        if (segments[0].Equals(TextDir, NameComparison))
        {
            if (op is not (ChildPathOp.Read or ChildPathOp.List))
                return ChildPathDecision.Deny("Текстовые копии пишет только сервер");
            if (segments.Length == 1)
                return ChildPathDecision.Allow();
            var rel = full[(root.Length + 1)..];
            var original = OriginalOf(rel) ?? rel[(TextDir.Length + 1)..];
            return DecideByInnerRules(actor, op, original.Split('/'));
        }

        return DecideByInnerRules(actor, op, segments);
    }

    // Судение пути внутри папки предмета без правила «.ccs-text» — те же правила, по которым
    // судится оригинал, развёрнутый из копии.
    private static ChildPathDecision DecideByInnerRules(ChildActor actor, ChildPathOp op, string[] segments)
    {
        // «Материалы/» и «Для репетитора/» — только верхний уровень: вложенная папка с тем же
        // именем (Заметки/Материалы) — обычная папка ребёнка, не защищённая (п. 2).
        if (segments[0].Equals(MaterialsDir, NameComparison))
        {
            // «Материалы/» — ребёнку и репетитору только чтение, куратору всё (решение 65).
            return actor == ChildActor.Curator
                ? ChildPathDecision.Allow()
                : op is ChildPathOp.Read or ChildPathOp.List
                    ? ChildPathDecision.Allow()
                    : ChildPathDecision.Deny("«Материалы» — только чтение");
        }
        if (segments[0].Equals(TutorDir, NameComparison))
        {
            // «Для репетитора/» — ребёнку не видна ни в списке, ни в чтении; репетитор читает,
            // пишет куратор. Запись в невидимом — тоже отказ: Hidden описывает видимость.
            return actor switch
            {
                ChildActor.Child => op is ChildPathOp.Read or ChildPathOp.List
                    ? ChildPathDecision.Hidden()
                    : ChildPathDecision.Deny("Папка «Для репетитора» не видна ребёнку"),
                ChildActor.Tutor => op is ChildPathOp.Read or ChildPathOp.List
                    ? ChildPathDecision.Allow()
                    : ChildPathDecision.Deny("Репетитор не пишет в своей папке"),
                _ => ChildPathDecision.Allow(),
            };
        }

        // Защита на ЛЮБОЙ глубине (решение Софьи, п. 2): любой сегмент пути проверяется на
        // «CLAUDE*.md», «.claude», «.mcp.json» и на скрытый («.»-префикс); «Заметки/CLAUDE.md»
        // или «a/b/.mcp.json» — тот же запрет, что на верхнем уровне. Запись закрыта всем,
        // включая куратора: настройки CLI и служебное не правятся рельсой ни на каком уровне.
        var protectedAt = FirstProtectedSegment(segments);
        if (protectedAt >= 0)
        {
            var name = segments[protectedAt];
            if (op is ChildPathOp.Read or ChildPathOp.List)
                // Видимость чтения на глубине — та же, что у прочих скрытых: куратору видна
                // точка, остальным Hidden. «.ccs-text» здесь — обычный скрытый сегмент:
                // правило верхней копии к нему не применяется (решение 91).
                return name[0] == '.' && actor == ChildActor.Curator
                    ? ChildPathDecision.Allow()
                    : ChildPathDecision.Hidden();
            return ChildPathDecision.Deny("Настройки и служебное не пишутся рельсой");
        }

        // Всё остальное внутри папки (правило 6): читают и пишут все трое —
        // мягкая граница: что ребёнок или репетитор записал, куратор прочтёт.
        return ChildPathDecision.Allow();
    }

    // Индекс первого защищённого сегмента на любой глубине, −1 — их нет. «Материалы» и
    // «Для репетитора» защищают только на верхнем уровне: их правила живут на segments[0],
    // и те ветки возвращаются раньше; вложенная папка с тем же именем — обычная папка
    // ребёнка (п. 2 доработки). internal: мутация «регистрозависимое сравнение имён папок»
    // должна красить таблицу путей (критерий c2д).
    internal static int FirstProtectedSegment(string[] segments)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            if (!IsProtectedSegment(segments[i]))
                continue;
            if (i > 0 && (segments[i].Equals(MaterialsDir, NameComparison) || segments[i].Equals(TutorDir, NameComparison)))
                continue;
            return i;
        }
        return -1;
    }

    // защищённый сегмент — имена правил 2–5 и все скрытые («.»-префикс, включая .ccs-*).
    internal static bool IsProtectedSegment(string segment) =>
        segment.Length > 0 &&
        (segment.Equals(MaterialsDir, NameComparison)
            || segment.Equals(TutorDir, NameComparison)
            || segment.Equals(".claude", NameComparison)
            || segment.Equals(".mcp.json", NameComparison)
            || segment.Equals("CLAUDE.md", NameComparison)
            || segment.Equals("CLAUDE.local.md", NameComparison)
            || segment.StartsWith(".ccs-", NameComparison)
            || segment[0] == '.');
}

/// <summary>Кто действует над путём (тот же тройник, что у видимости папки в решении 64).</summary>
public enum ChildActor
{
    /// <summary>Владелец-ребёнок: инструменты его хода и рельса «Файлы».</summary>
    Child,
    /// <summary>Репетитор: инструменты хода урока в папке предмета.</summary>
    Tutor,
    /// <summary>Куратор (опекун, роль Editor): обычная рельса проекта-предмета.</summary>
    Curator,
}

/// <summary>Что делается над путём. Read/List — видимость, Write/Delete — запись.</summary>
public enum ChildPathOp
{
    Read,
    List,
    Write,
    Delete,
}

public enum ChildPathVerdict
{
    Allow,
    Deny,
    /// <summary>Пути нет ни в списке, ни в чтении: не отказ, а невидимость (для Hidden
    /// потребитель не показывает ни строки, ни ошибки).</summary>
    Hidden,
}

/// <summary>Вердикт функции путей. У Deny причина всегда есть — её показывает рельса
/// и журнал разрешения; у Allow и Hidden её нет.</summary>
public sealed record ChildPathDecision(ChildPathVerdict Verdict, string? Reason = null)
{
    public bool Allowed => Verdict == ChildPathVerdict.Allow;

    public static ChildPathDecision Allow() => new(ChildPathVerdict.Allow);
    public static ChildPathDecision Deny(string reason) => new(ChildPathVerdict.Deny, reason);
    public static ChildPathDecision Hidden() => new(ChildPathVerdict.Hidden);
}
