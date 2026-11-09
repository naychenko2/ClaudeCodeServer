using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Subsystems;

/// <summary>
/// Сторож ссылок вида «Файл.cs:строка» (и «Файл.cs:строка-строка») в комментариях исходников
/// (S3-СТОРОЖ-ССЫЛКИ). Хвост ревью e013789e: правка вернула в комментарий FilesController.cs
/// протухшую ссылку Program.cs:621-622, и все тесты остались зелёными — на то, что адрес в
/// комментарии исходника ведёт в несуществующую строку, не было ни одного теста
/// (KnownViolations мерит только свой словарь). Здесь мерится: файл адреса есть в дереве,
/// обе границы диапазона — в [1..число строк] этого файла.
///
/// Скоуп — исходники продуктов backend/: каталоги проектов, в имени которых нет «.Tests».
/// Порождение сборки (obj/, bin/) мимо. Тестовые исходники здесь не мерятся: их ссылок в
/// дереве втрое больше, и их сплошная сверка — отдельный шаг S3-ССЫЛКИ-PROGRAM (6ccbde39);
/// заводить сторожа сразу на них значило бы начать его с вдвое большего базлайна.
///
/// Мерится только названная форма «Файл.cs:NN». Короткая ссылка «Имя (:NN)» — стиль словаря
/// KnownViolations, где владелец задан предыдущим полным адресом той же строки; в комментарии
/// исходника за таким именем стоит то другой файл, то сам файл, где стоит комментарий
/// (примеры в дереве: «TurnCostLedger (:59-68)» в LlmSubsystem.cs, «Build (:212)» в
/// FeatureStageIntervals.cs), и мерить её нечем — она оставлена вне замера.
///
/// Два словаря, оба самоочищающиеся:
/// * <see cref="NotLineReferences"/> — числа в комментариях, которые не ссылка на строку
///   (образец формата значения, мета-пример правила). Запись, которая в дереве ничего не
///   исключает, красная: словарь не должен обрастать мёртвыми строками.
/// * <see cref="KnownStaleLinks"/> — протухшие ссылки, найденные в день заведения сторожа.
///   Сторож заводится над кодом, который уже их содержит, а правки исходников в этот шаг не
///   входят (постановка): они — объём S3-ССЫЛКИ-PROGRAM. Ровность двусторонняя: новая мёртвая
///   ссылка красная, и запись базлайна, которая перестала быть мёртвой, красная — базлайн
///   чинится тем же коммитом, что и ссылка.
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

    /// Протухшие ссылки дерева на день заведения сторожа: файл (путь от корня) и дословный
    /// адрес. Позиция строки в ключ не входит — правка комментария не должна ронять сторожа.
    private static readonly (string File, string Address)[] KnownStaleLinks =
    [
        // SessionManager разбит на части, ссылки на его строки за ним не переведены.
        ("backend/ClaudeHomeServer/Controllers/SessionsController.cs", "SessionManager.cs:3660"),
        ("backend/ClaudeHomeServer/Controllers/ChatsController.cs", "SessionManager.cs:4311"),
        ("backend/ClaudeHomeServer/Controllers/PersonasController.cs", "SessionManager.cs:4378"),
        ("backend/ClaudeHomeServer/Services/GitAutoCommitService.cs", "SessionManager.cs:3785-3786"),
        ("backend/ClaudeHomeServer/Services/Sessions/ChatSettings.cs", "SessionManager.cs:7242"),
        // Свидетель назван файлом, которого в дереве нет.
        ("backend/ClaudeHomeServer/Services/GitAutoCommitService.cs", "SessionManagerTests.TurnActor.cs:34"),
        // Контракт переехал в Core под другим именем; ссылка на старый путь осталась.
        ("backend/ClaudeHomeServer.Core/Services/Turn/IPromptSectionContributor.cs",
            "Services/Turn/PromptSectionContributor.cs:61"),
        // TurnEventBus короче, чем ждёт ссылка.
        ("backend/ClaudeHomeServer.Core/Services/Turn/IPromptSectionContributor.cs", "TurnEventBus.cs:128"),
        ("backend/ClaudeHomeServer.Turn/PromptSectionContributorsRegistration.cs", "TurnEventBus.cs:128"),
        // Program.cs короче, чем ждёт ссылка тестового хоста.
        ("backend/ClaudeHomeServer.TestKit.Host/TestWebApplicationFactory.cs", "Program.cs:1766"),
        ("backend/ClaudeHomeServer.TestKit.Host/TestWebApplicationFactory.cs", "Program.cs:1802"),
    ];

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
                    var from = int.Parse(m.Groups["line"].Value);
                    var to = m.Groups["lineTo"].Success ? int.Parse(m.Groups["lineTo"].Value) : from;
                    var total = TestSourceAddresses.LineCount(file);
                    if (from < 1 || to < 1 || from > total || to > total)
                        dead.Add((rel, address, line, $"в файле {total} строк"));
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
            $"новых протухших ссылок в комментариях исходников: {fresh.Count}. Комментарий, "
            + "зовущий чужую строку, обязан вести в существующую строку существующего файла — "
            + "иначе через правку того файла он врёт неправду, как это было с Program.cs:621-622 "
            + "(хвост ревью e013789e). Все новые:\n"
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
    // и хвостовой комментарий после кода. Код строки не мерится: ссылка в строковом литерале
    // — не обещание о месте в дереве.
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
