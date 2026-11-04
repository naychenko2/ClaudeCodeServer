using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Files;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Дефект d07d7054 (последний известный остаток приватности файлов, хвост a6f5cb15 и шага
/// 0859cdb5): обход FileService остался текстовым — исключение по ИМЕНИ каталога (TreeExcludes)
/// и фильтр по относительному пути (PrivateAttachments.IsForeign). Симлинк «L» → .cc-attachments
/// в корне проекта ни не исключён по имени (имя «L»), ни не отфильтрован (первый сегмент «L») →
/// обход спускается в настоящий .cc-attachments и отдаёт L/private/{чужой}/…, а IndexFolder
/// индексирует их содержимое в общую базу знаний.
///
/// Здесь — обход по каноническому пути: чужое private-поддерево через ссылку закрыто так же,
/// как прямым именем; своё и общая раскладка — ровно как при прямом пути (тот же набор, что от
/// названного контейнера); легальные ссылки (на обычную папку, наружу, из closed-поддерева
/// наружу) и обычное дерево — без изменений; без зрителя обход не меняется (контракт
/// WebDAV/MCP/ProjectFiles).
///
/// Разыменование — одно на переход ссылки, не на запись (замечание ревью 6db670ff): это держат
/// тесты «…КакПриПрямомИмени» — через ссылку тот же набор записей, что прямым именем, поэтому
/// лишнего обхода нет ни в одну сторону.
/// Ссылка на каталог требует привилегий на Windows (образец PrivateAttachmentsResolvedTests),
/// тесты ссылки идут только на Linux.
/// </summary>
public sealed class FileServicePrivateAttachmentsLinkTests : IDisposable
{
    // id той же формы, что в сторе (Guid «N»)
    private const string Me = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Other = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private readonly string _tempDir;
    private readonly string _root;
    private readonly FileService _svc = new();
    private readonly string _foreignGuid = Guid.NewGuid().ToString("N");
    private readonly string _ownGuid = Guid.NewGuid().ToString("N");
    private readonly string _commonGuid = Guid.NewGuid().ToString("N");

    private string ForeignRel => $".cc-attachments/private/{Other}/{_foreignGuid}/foreign.txt";
    private string OwnRel => $".cc-attachments/private/{Me}/{_ownGuid}/own.txt";
    private string CommonRel => $".cc-attachments/{_commonGuid}/common.txt";

    public FileServicePrivateAttachmentsLinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fs_priv_link_" + Guid.NewGuid().ToString("N"));
        _root = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        Write(_root, ForeignRel, "чужое");
        Write(_root, OwnRel, "своё");
        Write(_root, CommonRel, "общее");
        Write(_root, "docs/plain.txt", "обычное");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
    }

    private static void Write(string dir, string rel, string content)
    {
        var abs = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private void Link(string relName, string target) =>
        Directory.CreateSymbolicLink(Path.Combine(_root, relName), target);

    private static List<string> PathsOf(IEnumerable<FileEntry> entries) => entries.Select(e => e.Path).ToList();

    /// <summary>Содержимое closed-поддерева из обхода корня, приведённое к имени через ссылку.</summary>
    private List<string> DirectOfContainer(string start) =>
        PathsOf(_svc.Tree(_root, start, showHidden: true, Me))
            .Select(p => "L/" + p[(TreeExcludes.AttachmentsDir.Length + 1)..])
            .ToList();

    // --- 1. дерево: через ссылку — тот же набор, что от названного контейнера; чужого нет ---

    [Fact]
    public void Tree_ЧерезСсылкуНаКонтейнер_ЧужоеPrivateЗакрыто_СвоёИОбщееКакПриПрямомИмени()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        Link("L", ".cc-attachments");

        // обычный обход корня: спуск через L
        var viaRoot = PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me));
        // тот же контейнер, названный через ссылку
        var viaName = PathsOf(_svc.Tree(_root, "L", showHidden: true, viewerUserId: Me));

        var expected = DirectOfContainer(TreeExcludes.AttachmentsDir);
        var ownDir = $"L/private/{Me}";
        var foreignDir = $"L/private/{Other}";

        foreach (var (label, paths) in new[] { ("обход корня", viaRoot), ("названный L", viaName) })
        {
            paths.Where(p => p.StartsWith("L/")).Should().Equal(expected,
                $"{label}: через ссылку — ровно тот набор, что от прямого имени .cc-attachments: " +
                "своё private и общая раскладка открыты, чужого нет");

            paths.Should().Contain(ownDir);
            paths.Should().Contain($"{ownDir}/{_ownGuid}/own.txt");
            paths.Should().Contain($"L/{_commonGuid}/common.txt", "общая раскладка .cc-attachments/{{guid}}/… не тронута");
            paths.Should().NotContain(p => p.StartsWith(foreignDir),
                $"чужое private-поддерево через ссылку закрыто: ни каталогов ({foreignDir}), ни файлов под ним");
            paths.Should().NotContain(p => p.StartsWith(".cc-attachments"),
                "прямой путь как прежде: TreeExcludes режет обход по имени");
        }

        // Сам контейнер — запись обхода корня (его имя не исключено): через ссылку он виден.
        // Названный старт своей записью не является — тот же контракт, что у прямого имени
        // контейнера (FilesControllerPrivateAttachmentsReadTests: от названного контейнера
        // перечисляется его содержимое, не он сам).
        viaRoot.Should().Contain("L", "ссылка на контейнер — обычная запись дерева: её имя не исключён");
        viaRoot.Should().Contain("docs/plain.txt", "обычное дерево не меняется");
        viaName.Should().NotContain("L", "названный старт не перечисляется сам — как прямой путь .cc-attachments");
    }

    // --- 2. листинг папки через ссылку: чужого каталога и его файлов нет, свой и общий есть ---

    [Fact]
    public void List_ЧерезСсылку_ЧужогоКаталогаНет_СвойИОбщийЕсть()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("L", ".cc-attachments");

        var top = PathsOf(_svc.List(_root, "L", showHidden: true, viewerUserId: Me));
        top.Should().BeEquivalentTo(new[] { "L/private", $"L/{_commonGuid}" },
            "свой каталог и общая раскладка — как от названного контейнера; чужой каталог автора — нет");

        var inner = PathsOf(_svc.List(_root, "L/private", showHidden: true, viewerUserId: Me));
        inner.Should().Equal(new[] { $"L/private/{Me}" },
            "List по пути под ссылкой: чужой автор не перечисляется, свой — как раньше");

        // без зрителя (WebDAV/MCP/ProjectFiles решают сами) обход не меняется: тот же контракт,
        // что у прямого имени
        PathsOf(_svc.List(_root, "L/private", showHidden: true))
            // порядок — по полному пути, как в прямом имени: id чужого раньше id зрителя
            .Should().Equal(new[] { $"L/private/{Other}", $"L/private/{Me}" });
    }

    // --- 3. ссылка прямо в чужое private: всё её поддерево закрыто зрителю, автору — своё ---

    [Fact]
    public void СсылкаПрямоВЧужоеPrivate_ВсёПоддеревоЗакрыто_АвторуСвоёОткрыто()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("evil", $".cc-attachments/private/{Other}");
        File.CreateSymbolicLink(Path.Combine(_root, "evilfile"), $".cc-attachments/private/{Other}/{_foreignGuid}/foreign.txt");

        var asMe = PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me));
        asMe.Should().NotContain("evil", "ссылка на чужой каталог автора — не запись дерева: ни имени, ни содержимого");
        asMe.Should().NotContain(p => p.StartsWith("evil/"));
        asMe.Should().NotContain("evilfile", "файл-ссылка в чужое private: его содержимое читалось бы как чужое");
        asMe.Should().Contain("docs/plain.txt");

        // автору — своё, и через ссылку: та же запись, что прямым именем
        var asOther = PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Other));
        asOther.Should().Contain($"evil/{_foreignGuid}/foreign.txt");
        asOther.Should().Contain("evilfile");
    }

    // --- 4. легальные ссылки не ломают обход: обычное дерево и ссылка наружу — как прежде ---

    [Fact]
    public void ЛегальныеСсылкиНеЛомаютОбход_ОбычноеДеревоКакПрежде()
    {
        if (OperatingSystem.IsWindows()) return;
        // ссылка на обычную папку проекта
        Link("docslink", "docs");
        // ссылка наружу на не-private путь (образец worktree-setup.sh: frontend/node_modules →
        // primary checkout): её содержимое — обычные записи, канон за пределами корня не мешает)
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside")).FullName;
        Write(outside, "keep.txt", "внешнее");
        Link("ext", outside);
        // ссылка внутри closed-поддерева наружу: её цель — обычная папка, не чужое private
        // (относительная цель резолвится от каталога самой ссылки — от .cc-attachments)
        Link("L", ".cc-attachments");
        Link(".cc-attachments/outlink", "../docs");

        var paths = PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me));

        paths.Should().Contain("docs/plain.txt");
        paths.Should().Contain("docslink/plain.txt", "ссылка на обычную папку обходится как прежде");
        paths.Should().Contain("ext/keep.txt", "ссылка наружу — обычные записи, канон за корнем их не закрывает");
        paths.Should().Contain("L/outlink/plain.txt", "ссылка из closed-поддерева наружу — обычное содержимое");
        paths.Should().Contain($"L/private/{Me}", "своё private через ссылку — как прямым именем");
        paths.Should().NotContain(p => p.StartsWith($"L/private/{Other}"),
            "при этом чужое private через L остаётся закрытым");
        paths.Should().NotContain(p => p.StartsWith(".cc-attachments/outlink"),
            "прямой путь к закрытому поддереву не обходится по имени (TreeExcludes) — как прежде");

        // без зрителя обход не меняется: чужое private через ссылку видно (контракт вызывающих
        // без актора — см. doc Tree), как и прямым именем
        PathsOf(_svc.Tree(_root, showHidden: true))
            .Should().Contain($"L/private/{Other}/{_foreignGuid}/foreign.txt");
    }
}
