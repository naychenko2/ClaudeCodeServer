using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Wiki;

// Разделы единой вики владельца (ADR-036, решение 1): раздел — папка по id плюс правило дома.
// Ключ раздела — строка "projects/{id}" | "spheres/{id}" | "common" | "personal".
// Разбор пути живёт здесь и больше нигде: второй разбор ad hoc в другом месте — второй
// рубеж, и одна ошибка в нём откроет чужой раздел (риск «широкого префикса» из ADR-036).
// Чистые функции без I/O; членство проекта в сфере считается живьём через ISphereDirectory —
// смена сферы меняет цепочку со следующего вызова, файлы не двигаются.
public static class WikiSections
{
    public const string Common = "common";
    public const string Personal = "personal";
    public const string ProjectsPrefix = "projects/";
    public const string SpheresPrefix = "spheres/";

    // Имена папок под pages/ по таблице ADR-036 §1. Регистр точный: папки создаёт Folder(),
    // путь или ссылка другого регистра — не путь раздела (Parse → null, fail-closed).
    private const string ProjectsFolder = "Проекты";
    private const string SpheresFolder = "Сферы";
    private const string CommonFolder = "Общее";
    private const string PersonalFolder = "Личное";

    // Причина отказа записи в раздел сферы (ADR-036, решение 7): в Ф0–Ф1 ключ разбирается и
    // входит в видимость, запись — за следующей фичей.
    public const string SphereWriteRefusal = "раздел сферы — следующая фича";

    /// <summary>Ключ раздела по пути относительно pages/. null — путь не принадлежит
    /// ни одному разделу: неразобранный файл не видим нигде (ADR-036 §2, fail-closed).</summary>
    public static string? Parse(string relPathUnderPages)
    {
        if (string.IsNullOrWhiteSpace(relPathUnderPages))
            return null;
        var parts = relPathUnderPages
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".")
            .ToArray();
        if (parts.Length == 0)
            return null;
        return parts[0] switch
        {
            ProjectsFolder => TrySegmentId(parts, out var projectId) ? ProjectsPrefix + projectId : null,
            SpheresFolder => TrySegmentId(parts, out var sphereId) ? SpheresPrefix + sphereId : null,
            CommonFolder => Common,
            PersonalFolder => Personal,
            _ => null,
        };
    }

    /// <summary>Папка раздела под pages/ со слэшем на конце — обратное к ключу.
    /// null — не ключ раздела.</summary>
    public static string? Folder(string key) =>
        TryProjectId(key, out var projectId) ? $"{ProjectsFolder}/{projectId}/"
        : TrySphereId(key, out var sphereId) ? $"{SpheresFolder}/{sphereId}/"
        : key switch
        {
            Common => $"{CommonFolder}/",
            Personal => $"{PersonalFolder}/",
            _ => null,
        };

    /// <summary>Цепочка «вверх» от раздела, без самого раздела (ADR-036, решение 4):
    /// projects/P → spheres/S(P) → common; personal → common; spheres/S → common; common — пусто.
    /// Сфера — живьём: флаг выключен или сфера удалена → SphereOf null, цепочка только common.</summary>
    public static IReadOnlyList<string> UpChain(string key, string ownerId, ISphereDirectory dir)
    {
        var chain = new List<string>();
        if (TryProjectId(key, out var projectId))
        {
            if (dir.SphereOf(ownerId, projectId) is { } sphereId)
                chain.Add(SpheresPrefix + sphereId);
            chain.Add(Common);
        }
        else if (TrySphereId(key, out _) || key == Personal)
        {
            chain.Add(Common);
        }
        // common и неизвестный ключ — вверх идти некуда.
        return chain;
    }

    /// <summary>Сирота — раздел удалённого проекта или удалённой сферы (ADR-036, решение 1):
    /// скрыт из всех наборов видимости сразу, удаляется только кнопкой человека.
    /// Сфера оценивается справочником без флага: существующая сфера при выключенном флаге
    /// не сирота — она есть, просто не видна.</summary>
    public static bool IsOrphan(string key, string ownerId, ISphereDirectory dir, IProjectManager projects)
    {
        if (TryProjectId(key, out var projectId))
            return projects.GetById(projectId) is not { } project || project.OwnerId != ownerId;
        if (TrySphereId(key, out var sphereId))
            return dir.SphereName(ownerId, sphereId) is null;
        // common и personal — не сироты никогда; неизвестный ключ разделом вики не является.
        return key != Common && key != Personal;
    }

    /// <summary>Проверка «можно ли писать»: Reason — текст отказа для пользователя и гейта.</summary>
    public readonly record struct WriteCheck(bool Writable, string? Reason);

    /// <summary>Запись в раздел. Для spheres/* — false с причиной из решения 7;
    /// неизвестный ключ не записываем тоже.</summary>
    public static WriteCheck IsWritable(string key)
    {
        if (TrySphereId(key, out _))
            return new WriteCheck(false, SphereWriteRefusal);
        if (TryProjectId(key, out _) || key == Common || key == Personal)
            return new WriteCheck(true, null);
        return new WriteCheck(false, "неразобранный раздел");
    }

    /// <summary>Ключ projects/{id} → id проекта.</summary>
    public static bool TryProjectId(string key, out string projectId) =>
        TryKeyWithId(key, ProjectsPrefix, out projectId);

    /// <summary>Ключ spheres/{id} → id сферы.</summary>
    public static bool TrySphereId(string key, out string sphereId) =>
        TryKeyWithId(key, SpheresPrefix, out sphereId);

    private static bool TryKeyWithId(string key, string prefix, out string id)
    {
        id = "";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var candidate = key[prefix.Length..];
        if (!IsValidId(candidate))
            return false;
        id = candidate;
        return true;
    }

    private static bool TrySegmentId(string[] parts, out string id)
    {
        id = "";
        if (parts.Length < 2 || !IsValidId(parts[1]))
            return false;
        id = parts[1];
        return true;
    }

    // id раздела — id проекта/сферы: ASCII буквы, цифры, «-» и «_» (id проектов — hex GUID "N").
    // Всё остальное («..», пробел, слэш) id не является: раздел не разбирается.
    private static bool IsValidId(string id) =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
