using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
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
/// TAIL-SEARCH-PRIV (хвост ревью 11256e5f, секция 6): поиск — последний обход без канонического
/// префикса, сведён к той же схеме; его ожидаемый набор — тот же, что у Tree/List.
/// Доработка по вердикту 13d3c2ab (секции 7–8): якорь префикса — по глубочайшей паре
/// .cc-attachments+private (предок канона с тем же именем его не уводит), а неразрешимый канон
/// якоря — отказ обхода, тот же fail-closed, что у читающих и разрушительных точек.
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
    private readonly string _otherForeignGuid = Guid.NewGuid().ToString("N");
    private readonly string _otherOwnGuid = Guid.NewGuid().ToString("N");
    private readonly string _otherCommonGuid = Guid.NewGuid().ToString("N");

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

    /// <summary>Корень чужого проекта (раскладка «S»): свой контейнер со своей раскладкой и
    /// обычная папка. Лежит под _tempDir — удаляется вместе с ним.</summary>
    private string OtherProject()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "otherProject")).FullName;
        Write(dir, $"{TreeExcludes.AttachmentsDir}/private/{Other}/{_otherForeignGuid}/other-foreign.txt", "чужое");
        Write(dir, $"{TreeExcludes.AttachmentsDir}/private/{Me}/{_otherOwnGuid}/other-own.txt", "своё");
        Write(dir, $"{TreeExcludes.AttachmentsDir}/{_otherCommonGuid}/other-common.txt", "общее");
        Write(dir, "docs/plain.txt", "обычное");
        return dir;
    }

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

    // --- 5. (доработка по ревью 6c2e1385) сам .cc-attachments — ссылка на каталог с другим
    //        именем: обход судит чужесть обеими линиями предиката, как читающие ручки ---

    /// <summary>
    /// Вердикт ревью 6c2e1385 (MAJOR 1) к шагу 212886b1: префикс closed-поддерева на переходе
    /// ссылки строился только первой линией — литеральным сегментом .cc-attachments в каноне
    /// цели. Если сам .cc-attachments — ссылка на каталог с другим именем (вложения унесены на
    /// другой диск: .cc-attachments → /mnt/bigdisk/attachments), пары сегментов в каноне нет и
    /// обход снова слепнет на L/private/{чужой} — хотя IsForeignResolved второй линией
    /// (IsUnderCanonicalPrivate, PrivateAttachments.cs:284) тот же путь отказывает
    /// (guard «СсылкаНаКаталогСДругимИменем…», PrivateAttachmentsResolvedTests). Здесь — та же
    /// раскладка на уровне обхода: чужое закрыто ровно тем же набором, что от прямого имени
    /// контейнера; своё и общая раскладка — открыты; легальная ссылка наружу на не-private путь
    /// (frontend/node_modules → primary checkout у worktree-setup.sh) под тем же корнем
    /// остаётся видимой; без зрителя обход не меняется.
    /// </summary>
    [Fact]
    public void СсылкаКонтейнераНаКаталогСДругимИменем_ЧужоеPrivateЗакрыто_КакПриПрямомИмени()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        var disk = Directory.CreateDirectory(Path.Combine(_tempDir, "attachments")).FullName;
        Write(disk, $"private/{Other}/{_foreignGuid}/foreign.txt", "чужое");
        Write(disk, $"private/{Me}/{_ownGuid}/own.txt", "своё");
        Write(disk, $"{_commonGuid}/common.txt", "общее");
        Directory.Delete(Path.Combine(_root, TreeExcludes.AttachmentsDir), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, TreeExcludes.AttachmentsDir), disk);
        Link("L", TreeExcludes.AttachmentsDir);
        // инвариант той же раскладки: легальная ссылка наружу на не-private путь — под тем же
        // корнем операции, её содержимое — обычные записи
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside")).FullName;
        Write(outside, "keep.txt", "внешнее");
        Link("ext", outside);

        var viaRoot = PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me));
        var viaName = PathsOf(_svc.Tree(_root, "L", showHidden: true, viewerUserId: Me));
        var expected = DirectOfContainer(TreeExcludes.AttachmentsDir);
        var foreignDir = $"L/private/{Other}";

        foreach (var (label, paths) in new[] { ("обход корня", viaRoot), ("названный L", viaName) })
        {
            paths.Where(p => p.StartsWith("L/")).Should().Equal(expected,
                $"{label}: closed-поддерево за ссылкой на каталог с другим именем — ровно тот набор, " +
                "что от прямого имени: своё private и общая раскладка открыты, чужого нет");
            paths.Should().NotContain(p => p.StartsWith(foreignDir),
                $"чужое private через обе ссылки закрыто: ни каталогов ({foreignDir}), ни файлов под ним");
        }

        viaRoot.Should().Contain("L", "сам контейнер через ссылку — обычная запись дерева, как при прямом имени");
        viaRoot.Should().Contain("docs/plain.txt", "обычное дерево не меняется");
        viaRoot.Should().Contain("ext/keep.txt",
            "легальная ссылка наружу на не-private путь под тем же корнем остаётся видимой в обходе");
        viaRoot.Should().NotContain(p => p.StartsWith(TreeExcludes.AttachmentsDir),
            "прямой путь как прежде: TreeExcludes режет обход по имени");

        // List по пути под ссылкой — тот же исход, что от названного контейнера
        PathsOf(_svc.List(_root, "L", showHidden: true, viewerUserId: Me))
            .Should().BeEquivalentTo(new[] { "L/private", $"L/{_commonGuid}" },
                "чужой каталог автора не перечисляется и через ссылку на каталог с другим именем");
        PathsOf(_svc.List(_root, "L/private", showHidden: true, viewerUserId: Me))
            .Should().Equal(new[] { $"L/private/{Me}" });

        // без зрителя обход не меняется (контракт вызывающих без актора — см. doc Tree)
        PathsOf(_svc.Tree(_root, showHidden: true))
            .Should().Contain($"L/private/{Other}/{_foreignGuid}/foreign.txt");
    }

    // --- 6. (TAIL-SEARCH-PRIV, хвост ревью 11256e5f) поиск — тот же обход: чужое по ссылке
    //        не находится, легальное — находится ---

    /// <summary>
    /// После db1d85fe4 поиск остался единственным обходом без канонического префикса:
    /// EnumerateFiles(AllDirectories) спускается через «L» в настоящий .cc-attachments, а
    /// текстовый IsForeign с первым сегментом «L» слепнет на L/private/{чужой} — чужое private
    /// уходило в выдачу ни именем, ни содержимым (Search ищет только по имени; «содержимое» —
    /// та запись, через которую оно читалось). Здесь та же expectation, что у Tree/List: чужого нет
    /// — ни с зрителем, ни без (без зрителя — машинный шов, всё private закрыто); своё и общая
    /// раскладка находятся по обоим именам (поиск — не дерево: он спускается в .cc-attachments);
    /// легальные ссылки обходятся как прежде.
    /// </summary>
    [Fact]
    public void Search_ЧерезСсылкуНаКонтейнер_ЧужоеНеНаходится_СвоёОбщееИОбычноеНаходятся()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        Link("L", TreeExcludes.AttachmentsDir);

        // чужого нет ни с зрителем, ни без него: и по прямому пути, и через L
        PathsOf(_svc.Search(_root, "foreign", Me)).Should().BeEmpty(
            "содержимое L → .cc-attachments судится по канону, а не по имени L: ни .cc-attachments/…, ни L/… в выдаче");
        PathsOf(_svc.Search(_root, "foreign")).Should().BeEmpty(
            "без зрителя: L/private/{чужой} закрыто, как прямой путь (fail-closed машинного шва)");

        // автор своё видит и через ссылку — по обоим именам, как прямым именем
        PathsOf(_svc.Search(_root, "foreign", Other))
            .Should().BeEquivalentTo(new[] { ForeignRel, $"L/private/{Other}/{_foreignGuid}/foreign.txt" });

        // своё и общая раскладка через ссылку находятся поиском — поиск не дерево:
        // он спускается в .cc-attachments, фильтр по чужести, не по каталогу
        PathsOf(_svc.Search(_root, "own", Me))
            .Should().BeEquivalentTo(new[] { OwnRel, $"L/private/{Me}/{_ownGuid}/own.txt" });
        PathsOf(_svc.Search(_root, "common", Me))
            .Should().BeEquivalentTo(new[] { CommonRel, $"L/{_commonGuid}/common.txt" });
        PathsOf(_svc.Search(_root, "plain", Me)).Should().Equal("docs/plain.txt");

        // без зрителя своё private тоже закрыто (машинный шов), обычный файл находится
        PathsOf(_svc.Search(_root, "own")).Should().BeEmpty();
        PathsOf(_svc.Search(_root, "plain")).Should().Equal("docs/plain.txt");
    }

    [Fact]
    public void Search_ЛегальныеСсылкиНаружуНаходятся_ЧужоеЧерезLЗакрыто()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("L", TreeExcludes.AttachmentsDir);
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside")).FullName;
        Write(outside, "keep.txt", "внешнее");
        Link("ext", outside);
        // ссылка внутри closed-поддерева наружу: её цель — обычная папка, не чужое private
        Link($"{TreeExcludes.AttachmentsDir}/outlink", "../docs");

        PathsOf(_svc.Search(_root, "keep", Me)).Should().Equal(new[] { "ext/keep.txt" },
            "легальная ссылка наружу на не-private путь остаётся доступной для поиска");
        PathsOf(_svc.Search(_root, "plain", Me)).Should().BeEquivalentTo(
            new[] { "docs/plain.txt", $"{TreeExcludes.AttachmentsDir}/outlink/plain.txt", "L/outlink/plain.txt" },
            "обычное дерево, прямой путь к контейнеру и ссылка из closed-поддерева наружу — как прежде");
        PathsOf(_svc.Search(_root, "foreign", Me)).Should().BeEmpty(
            "при этом чужое private через L остаётся закрытым");
    }

    /// <summary>Тот же класс, что у теста 5, на уровне поиска: .cc-attachments — ссылка на
    /// каталог со своим private/.</summary>
    [Fact]
    public void Search_СсылкаКонтейнераНаКаталогСДругимИменем_ЧужоеНеНаходится_СвоёИОбщееНаходятся()
    {
        if (OperatingSystem.IsWindows()) return;
        var disk = Directory.CreateDirectory(Path.Combine(_tempDir, "attachments")).FullName;
        Write(disk, $"private/{Other}/{_foreignGuid}/foreign.txt", "чужое");
        Write(disk, $"private/{Me}/{_ownGuid}/own.txt", "своё");
        Write(disk, $"{_commonGuid}/common.txt", "общее");
        Directory.Delete(Path.Combine(_root, TreeExcludes.AttachmentsDir), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, TreeExcludes.AttachmentsDir), disk);
        Link("L", TreeExcludes.AttachmentsDir);

        PathsOf(_svc.Search(_root, "foreign", Me)).Should().BeEmpty(
            "closed-поддерево за ссылкой на каталог с другим именем: чужого нет ни по прямому имени, ни через L");
        PathsOf(_svc.Search(_root, "foreign")).Should().BeEmpty("без зрителя — тот же fail-closed");
        PathsOf(_svc.Search(_root, "own", Me))
            .Should().BeEquivalentTo(new[]
            {
                $"{TreeExcludes.AttachmentsDir}/private/{Me}/{_ownGuid}/own.txt",
                $"L/private/{Me}/{_ownGuid}/own.txt",
            });
        PathsOf(_svc.Search(_root, "common", Me))
            .Should().BeEquivalentTo(new[]
            {
                $"{TreeExcludes.AttachmentsDir}/{_commonGuid}/common.txt",
                $"L/{_commonGuid}/common.txt",
            });
    }

    /// <summary>
    /// Вердикт 34cd6fac (п.4 «Новое в тронутых строках») к шагу 86d2a75f3: ссылка в корне проекта
    /// на каталог, СОДЕРЖАЩИЙ контейнер .cc-attachments (корень чужого проекта в общем проекте),
    /// префикса не давала — канон цели сегмента .cc-attachments не содержит и под closedRoot
    /// операции не лежит ⇒ текстовая линия видит первым сегментом имя ссылки («S») и чужее не
    /// видит: Search отдал S/.cc-attachments/private/{чужой}/… ни именем, ни содержимым. (Содержимое
    /// при этом не утекало: чтение закрыто IsForeignResolved; Tree/List не текли только потому,
    /// что TreeExcludes режет переход .cc-attachments по имени.) Теперь якорь ставится и по имени
    /// перехода: чужое закрыто ровно тем же набором, что от прямого имени контейнера; своё и
    /// общая раскладка через ссылку видны; обычный не-private содержимое чужого проекта — легальная
    /// ссылка наружу, она видна; без зрителя — тот же fail-closed.
    /// </summary>
    /// <Named мутации: «прежнее правило префикса» (в ClosedPrefixFor убрать fromName — якорь только
    /// по канону цели) краснит этот тест: foreign.txt через S возвращается в выдачу поиска.</Named>
    [Fact]
    public void Search_СсылкаНаКаталогСодержащийКонтейнер_ЧужоеНеНаходится_СвоёОбщееИОбычноеНаходятся()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        var other = OtherProject();
        Link("S", other);

        // чужого нет: ни по своему пути (текстовая линия), ни через S (имя ссылки больше не первый сегмент)
        PathsOf(_svc.Search(_root, "foreign", Me)).Should().BeEmpty(
            "чужое private закрыто и по своему пути, и через ссылку на каталог, содержащий контейнер");
        PathsOf(_svc.Search(_root, "foreign")).Should().BeEmpty(
            "без зрителя — тот же fail-closed: своё private закрыто, чужого нет");

        // автору — своё в обоих проектах: и своим именем, и через ссылку на каталог, содержащий контейнер
        var foreignThroughS = $"S/{TreeExcludes.AttachmentsDir}/private/{Other}/{_otherForeignGuid}/other-foreign.txt";
        PathsOf(_svc.Search(_root, "foreign", Other)).Should().BeEquivalentTo(new[] { ForeignRel, foreignThroughS },
            "автор своё видит везде, где до него дотянулся обход");

        // своё и общая раскладка чужого проекта — тот же набор, что от прямого имени его контейнера
        PathsOf(_svc.Search(_root, "other-foreign", Other)).Should().Equal(new[] { foreignThroughS },
            "автор своё видит и через ссылку на каталог, содержащий контейнер");
        PathsOf(_svc.Search(_root, "other-own", Me)).Should().Equal(
            new[] { $"S/{TreeExcludes.AttachmentsDir}/private/{Me}/{_otherOwnGuid}/other-own.txt" },
            "своё private чужого проекта через ссылку — не чужое");
        PathsOf(_svc.Search(_root, "other-common", Me)).Should().Equal(
            new[] { $"S/{TreeExcludes.AttachmentsDir}/{_otherCommonGuid}/other-common.txt" },
            "общая раскладка через ссылку — как прежде");

        // обычный не-private содержимый чужой проект — легальная ссылка наружу: видна и без скрытых
        PathsOf(_svc.Search(_root, "plain", Me)).Should().BeEquivalentTo(new[] { "docs/plain.txt", "S/docs/plain.txt" },
            "легальная ссылка наружу на не-private путь остаётся доступной для поиска");
        PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me))
            .Should().Contain("S/docs/plain.txt", "Tree через S: обычное содержимое видно");
        PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me))
            .Should().NotContain(p => p.StartsWith($"S/{TreeExcludes.AttachmentsDir}"),
                "Tree через S: переход .cc-attachments режется по имени, как и в своём дереве");
        PathsOf(_svc.List(_root, "S", showHidden: true, viewerUserId: Me))
            .Should().Equal(new[] { "S/docs" }, "List корня чужого проекта: тот же TreeExcludes по имени");

        // тот же исход в той же раскладке и для своей ссылки на контейнер (ссылка «Q» → его .cc-attachments):
        // закрыта первой линией — по канону цели, как была
        Link("Q", Path.Combine(other, TreeExcludes.AttachmentsDir));
        PathsOf(_svc.Search(_root, "other-foreign", Me)).Should().BeEmpty(
            "ссылка прямо на контейнер чужого проекта — закрыта и с новой линией, как была");
    }

    /// <summary>Явный обход вместо EnumerateFiles обязан держать и циклы ссылок: в уже
    /// открытый по текущему спуску каталог не идём — содержимое один раз, без зависания.</summary>
    [Fact]
    public void Search_ЦиклСсылок_НеВиснет_СодержимоеОдинРаз()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_root, "loopdir"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "loopdir", "loop"), ".."); // → корень
        Write(_root, "loopdir/keep.txt", "здесь");

        PathsOf(_svc.Search(_root, "keep", Me)).Should().Equal(new[] { "loopdir/keep.txt" },
            "цикл через ссылку не порождает повторных обходов и не зацикливает поиск");
    }

    [Fact]
    public void Search_ФайлСсылкаВЧужоеPrivate_ЗакрытЗрителю_АвторуОткрыт()
    {
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(_root, "evilfile"),
            $"{TreeExcludes.AttachmentsDir}/private/{Other}/{_foreignGuid}/foreign.txt");

        PathsOf(_svc.Search(_root, "evilfile", Me)).Should().BeEmpty(
            "файл-ссылка в чужое private: через него читалось чужое содержимое");
        PathsOf(_svc.Search(_root, "evilfile")).Should().BeEmpty("без зрителя — тот же fail-closed");
        PathsOf(_svc.Search(_root, "evilfile", Other)).Should().Equal(new[] { "evilfile" },
            "автору — своё и через файл-ссылку, как прямым именем");
    }

    // --- 7. (доработка по вердикту 13d3c2ab, минор 1) предок канона с тем же именем не уводит якорь ---

    /// <summary>
    /// Вердикт 13d3c2ab (минор 1) к доработке 81fb61c3: первая линия ClosedPrefixOf брала ПЕРВЫЙ
    /// сегмент .cc-attachments в каноне цели. Если путь к вложениям сам проходит через каталог с
    /// тем же именем (вложения вынесены на отдельный диск: .cc-attachments →
    /// /mnt/.cc-attachments/store/.cc-attachments) — в каноне цели их два, и якорь брался от
    /// предка: префикс .cc-attachments/store/.cc-attachments/… не начинается с
    /// .cc-attachments/private/, текстовый фильтр слепнет на L/private/{чужой}, а верная линия
    /// корня не выполнялась — первая вернула раньше. Здесь та же проверка, что тест 5: чужое
    /// закрыто ровно тем же набором, что от прямого имени контейнера; своё и общая раскладка
    /// открыты; ссылка прямо в чужое private под тем же корнем закрыта (якорь по паре
    /// .cc-attachments+private); легальная ссылка наружу на не-private путь — видна; без зрителя
    /// обход не меняется.
    /// </summary>
    /// <Named мутации: «первая линия ClosedPrefixOf возвращает ПЕРВЫЙ сегмент .cc-attachments»
    /// (как до этой доработки) краснит этот тест: якорь уходит в предка, L/private/{чужой} и evil
    /// возвращаются в дерево.</Named>
    [Fact]
    public void ПредокКанонаСТемЖеИменемНеУводитЯкорь_ЧужоеPrivateЗакрыто_КакПриПрямомИмени()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        // «диск» с вложениями: путь к ним сам содержит .cc-attachments (предок с тем же именем),
        // и сам каталог вложений назван .cc-attachments — в каноне цели их ДВА.
        var disk = Path.Combine(_tempDir, TreeExcludes.AttachmentsDir, "store", TreeExcludes.AttachmentsDir);
        Write(disk, $"private/{Other}/{_foreignGuid}/foreign.txt", "чужое");
        Write(disk, $"private/{Me}/{_ownGuid}/own.txt", "своё");
        Write(disk, $"{_commonGuid}/common.txt", "общее");
        Directory.Delete(Path.Combine(_root, TreeExcludes.AttachmentsDir), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, TreeExcludes.AttachmentsDir), disk);
        Link("L", TreeExcludes.AttachmentsDir);
        // ссылка прямо в чужое private под тем же корнем: здесь якорь — по паре .cc-attachments+private
        Link("evil", $"{TreeExcludes.AttachmentsDir}/private/{Other}");
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside")).FullName;
        Write(outside, "keep.txt", "внешнее");
        Link("ext", outside);

        var viaRoot = PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me));
        var viaName = PathsOf(_svc.Tree(_root, "L", showHidden: true, viewerUserId: Me));
        var expected = DirectOfContainer(TreeExcludes.AttachmentsDir);
        var foreignDir = $"L/private/{Other}";

        foreach (var (label, paths) in new[] { ("обход корня", viaRoot), ("названный L", viaName) })
        {
            paths.Where(p => p.StartsWith("L/")).Should().Equal(expected,
                $"{label}: якорь — по глубочайшей паре/сегменту, не по первому: closed-поддерево за " +
                "предком с тем же именем — ровно тот набор, что от прямого имени");
            paths.Should().NotContain(p => p.StartsWith(foreignDir),
                $"чужое private через обе ссылки закрыто: ни каталогов ({foreignDir}), ни файлов под ним");
        }

        viaRoot.Should().NotContain("evil", "ссылка прямо в чужое private под корнем с тем же именем — не запись дерева");
        viaRoot.Should().NotContain(p => p.StartsWith("evil/"));
        viaRoot.Should().Contain("ext/keep.txt", "легальная ссылка наружу на не-private путь — как прежде");
        viaRoot.Should().Contain("docs/plain.txt", "обычное дерево не меняется");

        // List по пути под ссылкой — тот же исход, что от названного контейнера
        PathsOf(_svc.List(_root, "L", showHidden: true, viewerUserId: Me))
            .Should().BeEquivalentTo(new[] { "L/private", $"L/{_commonGuid}" });
        PathsOf(_svc.List(_root, "L/private", showHidden: true, viewerUserId: Me))
            .Should().Equal(new[] { $"L/private/{Me}" });

        // без зрителя обход не меняется (контракт вызывающих без актора — см. doc Tree)
        PathsOf(_svc.Tree(_root, showHidden: true))
            .Should().Contain($"L/private/{Other}/{_foreignGuid}/foreign.txt");
    }

    // --- 8. (доработка по вердикту 13d3c2ab, минор 2) неразрешимый канон якоря: обход fail-closed ---

    /// <summary>
    /// Вердикт 13d3c2ab (минор 2): при неразрешимом каноне якоря (цикл ссылок в самом
    /// .cc-attachments) обход возвращал пустой префикс и судил запись текстовым IsForeign по
    /// relDir, тогда как читающие и разрушительные точки в той же раскладке отказывают
    /// (IsForeignResolved → PrivateAttachments.cs:235/:287, IsContainerAt → :317/:322):
    /// «поддерево, которого нельзя разобрать, не может быть и «своим»». Расхождение наблюдаемо:
    /// то же содержимое достижимо отдельной легальной ссылкой «L». Здесь обход отказывает так же:
    /// с зрителем — ни содержимого под ссылкой, ни обычного дерева (тот же отказ, что даёт
    /// IsForeignResolved по любому пути этой операции); без зрителя обход не меняется (контракт
    /// вызывающих без актора — см. doc Tree).
    /// </summary>
    /// <Named мутации: «пустой префикс вместо fail-closed» (ClosedPrefixOf на неразрешимом
    /// closedRoot возвращает "") краснит этот тест: L/… и docs/plain.txt возвращаются в обход.</Named>
    [Fact]
    public void НеразрешимыйКанонЯкоря_ОбходНеОтдаётСодержимое_FailClosedКакЧитающиеТочки()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = Directory.CreateDirectory(Path.Combine(_tempDir, "store")).FullName;
        Write(store, $"private/{Other}/{_foreignGuid}/foreign.txt", "чужое");
        Write(store, $"private/{Me}/{_ownGuid}/own.txt", "своё");
        Write(store, $"{_commonGuid}/common.txt", "общее");
        Directory.Delete(Path.Combine(_root, TreeExcludes.AttachmentsDir), recursive: true);
        // сам контейнер — цикл ссылок: его канон неразрешим (тот же приём, что
        // «НеразрешимыйПуть_ЦиклСсылок_FailClosedОбаПредиката» в PrivateAttachmentsResolvedTests)
        Directory.CreateSymbolicLink(Path.Combine(_root, TreeExcludes.AttachmentsDir), "loop");
        Directory.CreateSymbolicLink(Path.Combine(_root, "loop"), TreeExcludes.AttachmentsDir);
        // отдельная легальная ссылка на то же содержимое: обход доходит до него, не разбирая якорь
        Link("L", store);

        // гейт читающих точек в этой же раскладке — отказывает: это основание, а не проверка правки
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Other}/{_foreignGuid}/foreign.txt", Me)
            .Should().BeTrue();
        PrivateAttachments.IsForeignResolved(_root, "docs/plain.txt", Me).Should().BeTrue();

        // обход — тот же отказ: с зрителем closed-поддерево не опознано, содержимого нет
        PathsOf(_svc.Tree(_root, showHidden: true, viewerUserId: Me)).Should().BeEmpty(
            "якорь неразрешим → обход fail-closed: ни чужого, ни своего за ссылкой, ни обычного дерева");
        var listed = PathsOf(_svc.List(_root, showHidden: true, viewerUserId: Me));
        listed.Should().NotContain(p => p.StartsWith("L/"), "листинг корня — тот же отказ");
        listed.Should().NotContain("docs/plain.txt", "обычное дерево в этой раскладке закрыто и для List");
        PathsOf(_svc.Search(_root, "foreign", Me)).Should().BeEmpty();
        PathsOf(_svc.Search(_root, "own", Me)).Should().BeEmpty();
        PathsOf(_svc.Search(_root, "common", Me)).Should().BeEmpty();
        PathsOf(_svc.Search(_root, "plain", Me)).Should().BeEmpty();

        // без зрителя обход не меняется (контракт вызывающих без актора — см. doc Tree)
        PathsOf(_svc.Tree(_root, showHidden: true)).Should().Contain($"L/{_commonGuid}/common.txt");
    }
}
