using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Subsystems;

/// <summary>
/// Сторож ссылок вида «Файл.cs:строка» (и «Файл.cs:строка-строка») в комментариях исходников.
/// Мерится ровно это: файл адреса есть в дереве, обе границы диапазона — в [1..число строк]
/// этого файла и в порядке (первая не больше второй); число длиннее int — запись в списке
/// мёртвых, а не OverflowException. Разбор границ — общий с SubsystemDisableMatrixTests через
/// TestSourceAddresses.BadRangeReason: сам сторож своей копии разбора не содержит. Ничего
/// сверх этого не мерится: сторож не смотрит, что записано под указанным номером —
/// семантическая сверка («строка содержит то, о чём говорит комментарий») отдельная работа.
///
/// Скоуп — исходники продуктов backend/: каталоги проектов, в имени которых нет «.Tests».
/// Порождение сборки (obj/, bin/) мимо. Тестовые исходники здесь не мерятся: новых мёртвых
/// ссылок в тестовых не мерит никто. Мерится только названная форма «Файл.cs:NN». Короткая
/// ссылка «Имя (:NN)» — стиль словаря KnownViolations, где владелец задан предыдущим полным
/// адресом той же строки; в комментарии исходника за таким именем стоит то другой файл, то
/// сам файл, где стоит комментарий, и мерить её нечем — она оставлена вне замера.
///
/// Известные ограничения сторожа (зафиксированы, чтобы следующий автор не принял сторож за
/// полную защиту; ни одно из них не требует правки продуктов):
/// * на то, что записано под номером, не смотрит — ссылка на существующую, но сдвинувшуюся
///   строку проходит: файл сократился, комментарий перенаведён по номеру, а код под этим
///   номером уже другой; сторож мерит только файл и номер;
/// * короткую форму «Имя (:NN)» не мерит (абзац выше);
/// * исходники фронта не мерит: regex адреса только .cs, скоуп — backend/;
/// * комментарий ищет по префиксам строк: «//» внутри строкового литерала тоже берётся
///   началом комментария (ссылка в нём мерится), а «/* … */» в середине строки кода не
///   сканируется;
/// * короткое имя резолвится с бейасом Main-проекта: вторая форма в TestSourceAddresses.Resolve
///   проверяется до проверки неоднозначности.
///
/// Два словаря, оба самоочищающиеся:
/// * <see cref="NotLineReferences"/> — числа в комментариях, которые не ссылка на строку
///   (образец формата значения, мета-пример правила). Запись, которая в дереве ничего не
///   исключает, красная: словарь не должен обрастать мёртвыми строками.
/// * <see cref="KnownStaleLinks"/> — мёртвые ссылки дерева сегодня. Ровность двусторонняя:
///   новая мёртвая ссылка красная, и запись базлайна, которая перестала быть мёртвой,
///   красная — базлайн чинится тем же коммитом, что и ссылка.
/// </summary>
public class SourceLinkGuardTests
{
    /// Числа в комментариях, которые не адрес строки. Ключ — дословный текст совпадения.
    private static readonly string[] NotLineReferences =
    [
        // CodeGraphQueryDto.cs:14 — в <summary> поля Location записан образец формата его
        // значения («относительный/путь.cs:42»), а не ссылка на файл дерева.
        "относительный/путь.cs:42",
    ];

    /// Мёртвые ссылки дерева сегодня: файл (путь от корня) и дословный адрес. Позиция строки
    /// в ключ не входит — правка комментария не должна ронять сторожа. Базлайн пуст; починка
    /// ссылки и снятие её записи — один коммит.
    private static readonly (string File, string Address)[] KnownStaleLinks = [];

    [Fact]
    public void Ссылки_в_комментариях_исходников_ведут_в_существующие_строки()
    {
        var dead = new List<(string File, string Address, int Line, string Why)>();
        var excepted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rel in SourceFiles())
        {
            var path = Path.Combine(TestRepoFiles.RepoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            foreach (var (line, text) in CommentTexts(path))
            {
                foreach (Match m in TestSourceAddresses.FullAddress.Matches(text))
                {
                    var address = m.Value;
                    if (NotLineReferences.Contains(address))
                    {
                        excepted.Add(address);
                        continue;
                    }
                    var file = TestSourceAddresses.Resolve(m.Groups["path"].Value);
                    if (file is null)
                    {
                        dead.Add((rel, address, line, "файла нет в дереве"));
                        continue;
                    }
                    var bad = TestSourceAddresses.BadRangeReason(
                        m, "line", "lineTo", TestSourceAddresses.LineCount(file));
                    if (bad is not null) dead.Add((rel, address, line, bad));
                }
            }
        }

        // Ровность двусторонняя: базлайн — список сегодняшних мёртвых ссылок, а не память о
        // том, что было при заведении сторожа.
        var known = new HashSet<(string, string)>(KnownStaleLinks);
        var found = dead.Select(d => (d.File, d.Address)).ToHashSet();
        var fresh = dead.Where(d => !known.Contains((d.File, d.Address))).ToList();
        var stale = KnownStaleLinks.Where(k => !found.Contains(k)).ToList();

        fresh.Should().BeEmpty(
            $"новых протухших ссылок в комментариях исходников: {fresh.Count}. Ссылка на строку "
            + "обязана вести в существующий файл и в номер в пределах его строк, границы "
            + "диапазона — в порядке; на то, что "
            + "записано под этим номером, сторож не смотрит — семантическая сверка — "
            + "отдельная работа. Все новые:\n"
            + string.Join("\n", fresh.Select(d => $"{d.File}:{d.Line} — {d.Address} — {d.Why}")));

        stale.Should().BeEmpty(
            $"записей базлайна, которые больше не мёртвые: {stale.Count}. Ссылку чинят и снимают "
            + "из KnownStaleLinks одним коммитом. Все такие записи:\n"
            + string.Join("\n", stale.Select(k => $"{k.File} — {k.Address}")));

        NotLineReferences.Except(excepted).Should().BeEmpty(
            "исключение, которое в дереве ничего не исключает, — мёртвая запись словаря "
            + "NotLineReferences");
    }

    // Исходники продуктов backend/: каталог проекта без «.Tests» в имени, порождение сборки мимо.
    private static IEnumerable<string> SourceFiles()
    {
        var root = TestRepoFiles.RepoRoot;
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(root, "backend")))
        {
            if (Path.GetFileName(project).Contains(".Tests", StringComparison.Ordinal)) continue;
            foreach (var f in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
                    continue;
                yield return rel;
            }
        }
    }

    // Текст комментариев строки: строка, начинающаяся с «//» или «/*», строки внутри «/* … */»
    // и хвост после первого «//» в строке. Код строки не меряется ровно: «//» внутри строкового
    // литерала тоже берётся началом комментария (ссылка в нём мерится), а «/* … */» в середине
    // строки кода не сканируется — известное ограничение, оно записано в докстринге.
    private static IEnumerable<(int Line, string Text)> CommentTexts(string path)
    {
        var inBlock = false;
        var n = 0;
        foreach (var raw in File.ReadLines(path))
        {
            n++;
            var s = raw.TrimStart();
            if (inBlock)
            {
                yield return (n, raw);
                if (s.Contains("*/", StringComparison.Ordinal)) inBlock = false;
                continue;
            }
            if (s.StartsWith("//", StringComparison.Ordinal))
            {
                yield return (n, s);
                continue;
            }
            if (s.StartsWith("/*", StringComparison.Ordinal))
            {
                yield return (n, s);
                inBlock = !s.Contains("*/", StringComparison.Ordinal);
                continue;
            }
            var i = s.IndexOf("//", StringComparison.Ordinal);
            if (i >= 0) yield return (n, s[i..]);
        }
    }
}
