using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// Хвост a6f5cb15: канонический (по РАЗЫМЕНОВАННОМУ пути) гейт closed-поддерева private/.
/// Текстовые IsForeign/IsPrivateContainerPath/IsAttachmentsContainer сравнивают строковые
/// сегменты и не разыменовывают символьные ссылки, а вызывающие (WebDAV, FilesController,
/// MCP, Knowledge) работают по разыменованному пути: симлинк «L» → .cc-attachments в корне
/// проекта даёт то же чужое private и тот же контейнер, что прямой путь. Здесь — сам
/// предикат на живой ФС: чужое и своё через ссылку, контейнер через ссылку (и ссылка не в
/// корне), цикл ссылок (fail-closed), запределье корня и ссылка наружу как обычный путь.
/// Ссылка на каталог требует привилегий на Windows (образец WebDavPrivateAttachmentTests.cs),
/// поэтому тесты ссылки идут только на Linux (CI и дев-машины).
/// </summary>
public sealed class PrivateAttachmentsResolvedTests : IDisposable
{
    // id той же формы, что в сторе (Guid «N»)
    private const string Me = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Other = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private readonly string _tempDir;
    private readonly string _root;
    private readonly string _foreignGuid = Guid.NewGuid().ToString("N");
    private readonly string _ownGuid = Guid.NewGuid().ToString("N");
    private readonly string _commonGuid = Guid.NewGuid().ToString("N");

    private string ForeignRel => $".cc-attachments/private/{Other}/{_foreignGuid}/foreign.txt";
    private string OwnRel => $".cc-attachments/private/{Me}/{_ownGuid}/own.txt";
    private string CommonRel => $".cc-attachments/{_commonGuid}/common.txt";

    public PrivateAttachmentsResolvedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "priv_resolved_" + Guid.NewGuid().ToString("N"));
        _root = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        Write(ForeignRel, "чужое");
        Write(OwnRel, "своё");
        Write(CommonRel, "общее");
        Write("docs/plain.txt", "обычное");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
    }

    private void Write(string relPath, string content) => WriteAt(_root, relPath, content);

    private static void WriteAt(string dir, string relPath, string content)
    {
        var abs = Path.Combine(dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private void Link(string relName, string target) =>
        Directory.CreateSymbolicLink(Path.Combine(_root, relName), target);

    // --- чужое private через ссылку ---

    /// <Named мутации: «IsForeignResolved игнорирует ссылки (сравнение по текстовому пути)»
    /// краснит этот тест: без разыменования путь через «L» не чужой.</Named>
    [Fact]
    public void IsForeignResolved_ЧужоеЧерезСимлинк_КакПрямойПуть_СвоёИОбщееНеЗакрыты()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        Link("L", ".cc-attachments");

        PrivateAttachments.IsForeignResolved(_root, ForeignRel, Me).Should().BeTrue("прямой путь — чужое");
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Other}/{_foreignGuid}/foreign.txt", Me)
            .Should().BeTrue("тот же файл через ссылку — то же чужое private");
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Other}", Me)
            .Should().BeTrue("сам каталог чужого автора через ссылку — чужой, как и текстово");

        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Me}/{_ownGuid}/own.txt", Me).Should().BeFalse();
        PrivateAttachments.IsForeignResolved(_root, $"L/{_commonGuid}/common.txt", Me).Should().BeFalse();
        PrivateAttachments.IsForeignResolved(_root, "L/docs/plain.txt", Me).Should().BeFalse();
        PrivateAttachments.IsForeignResolved(_root, "docs/plain.txt", Me).Should().BeFalse();
    }

    [Fact]
    public void IsForeignResolved_СсылкаНеВКорне_ТотЖеГейт()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("docs/att", "../.cc-attachments"); // ссылка внутри проекта, цель — от её каталога

        PrivateAttachments.IsForeignResolved(_root, "docs/att/private/" + Other + "/" + _foreignGuid + "/foreign.txt", Me)
            .Should().BeTrue("ссылка может быть на любом уровне пути, не только в корне");
        PrivateAttachments.IsForeignResolved(_root, "docs/att/" + _commonGuid + "/common.txt", Me).Should().BeFalse();
    }

    [Fact]
    public void IsForeignResolved_АкторПустой_ЛюбоеЧужоеЧерезСсылку()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("L", ".cc-attachments");

        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Other}/x.txt", null).Should().BeTrue();
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Me}/x.txt", null).Should().BeTrue();
        PrivateAttachments.IsForeignResolved(_root, "L/" + _commonGuid + "/common.txt", null).Should().BeFalse();
    }

    [Fact]
    public void IsForeignResolved_ОбходыЧерезСсылку_Ровно_ТотПуть_ЧтоОткроетОперация()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("L", ".cc-attachments");

        // «.» и пустые сегменты: тот же путь, что прямым именем
        PrivateAttachments.IsForeignResolved(_root, $"./L//private//{Other}//{_foreignGuid}//foreign.txt", Me)
            .Should().BeTrue();

        // «..» сворачивается SafeJoin'ом ДО разыменования — ровно как его свернёт сама операция
        // (FileService открывает уже свёрнутый путь), поэтому гейт судит о том пути, который
        // будет открыт: L/private/{Me}/../{Other} → L/private/{Other} → .cc-attachments/private/
        // {Other}. Текстовый IsForeign здесь молчал (первый сегмент «L»), канонический видит,
        // что открытое — чужой автор.
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Me}/../{Other}/{_foreignGuid}/foreign.txt", Me)
            .Should().BeTrue();
        // тот же обход к своему автору — своё, не закрыто
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Other}/../{Me}/{_ownGuid}/own.txt", Me)
            .Should().BeFalse();
        // обход из private/ через ссылку — .cc-attachments/{Other}: вне closed-поддерева, не чужое
        PrivateAttachments.IsForeignResolved(_root, $"L/private/../{Other}/{_foreignGuid}/foreign.txt", Me)
            .Should().BeFalse();
    }

    /// <Named мутации: «IsForeignResolved судит только разыменованный путь (текстовая линия
    /// снята)» краснит этот тест: .cc-attachments/private/{чужой}, сведённый в ссылку наружу,
    /// перестал бы быть чужим — GET отдавал бы содержимое, PUT/MKCOL проходили бы.</Named>
    [Fact]
    public void IsForeignResolved_ИмяВЧужомPrivate_РазрешаетсяНаржу_ВсёРавноЧужое()
    {
        if (OperatingSystem.IsWindows()) return;
        // .cc-attachments/private/{чужой} — ручная правка ФС: каталог сведён в ссылку на
        // каталог вне поддерева (достижимо только так; сама ФС таких ссылок не создаёт)
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside-priv")).FullName;
        File.WriteAllText(Path.Combine(outside, "x.txt"), "содержимое внешнего каталога");
        Directory.Delete(Path.Combine(_root, ".cc-attachments", "private", Other), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, ".cc-attachments", "private", Other), outside);

        // Инвариант «closed-поддерево отказано по любому имени» (ревью 6db670ff, п.2):
        // текстово чужое private остаётся чужим независимо от того, куда оно разыменовалось
        PrivateAttachments.IsForeignResolved(_root, $".cc-attachments/private/{Other}/x.txt", Me)
            .Should().BeTrue("текстово чужое private не обходится ссылкой наружу");
        PrivateAttachments.IsForeignResolved(_root, $".cc-attachments/private/{Other}/{_foreignGuid}/foreign.txt", Me)
            .Should().BeTrue("тот же исход для прежнего пути: имя ведёт в closed-поддерево");

        // своё имя — своё: текстовая линия не закрывает и своё private
        PrivateAttachments.IsForeignResolved(_root, $".cc-attachments/private/{Me}/x.txt", Me).Should().BeFalse();
    }

    // --- TAIL-ROOTS-GATE (DEC-ROOTS 5c8a4705): чужесть по сегменту канона, не по префиксу
    // относительно корня. closed-поддерево живёт в рабочем дереве чата (EffectiveRoot),
    // поэтому три формы одного чужого: вложенный worktree под корнем проекта (без всякой
    // ссылки), ссылка из корня в worktree, ссылка внутри worktree (гейт по фактическому
    // корню операции). Мутация «предикат судит префиксом относительно корня (прежняя
    // формула)» краснит первые два теста: в обоих канон не под .cc-attachments/private
    // корня, и файл читался бы. ---

    /// <Named мутации: «IsForeignResolved судит чужость префиксом относительно корня
    /// (прежняя формула)» краснит этот тест: segments[0]="worktrees", а канон не под
    /// private/ корня — чужое private вложенного дерева читалось бы прямым именем.</Named>
    [Fact]
    public void ВложенныйWorktreeПодКорнем_ЧужоеPrivate_Чужое_СвоёСвоё()
    {
        // защитный кейс: worktree, созданный ВНУТРИ корня (штатные деревья чатов и слотов
        // ревью — вне главного дерева: {home}/.worktrees/<проект>/<ветка>,
        // SessionManager.WorktreesBase). private образуется там сам, без всякой символьной
        // ссылки (DEC-ROOTS: «новая дыра, которой нет у Дениса»)
        Write($"worktrees/x/.cc-attachments/private/{Other}/nested.txt", "чужое во вложенном дереве");
        PrivateAttachments.IsForeignResolved(_root, $"worktrees/x/.cc-attachments/private/{Other}/nested.txt", Me)
            .Should().BeTrue("сегмент .cc-attachments/private/{X} на любой глубине канона — то же чужое");

        // свой автор во вложенном дереве — своё: сегмент судит по автору, а не по глубине
        Write($"worktrees/x/.cc-attachments/private/{Me}/mine.txt", "своё во вложенном дереве");
        PrivateAttachments.IsForeignResolved(_root, $"worktrees/x/.cc-attachments/private/{Me}/mine.txt", Me)
            .Should().BeFalse();

        // обычный файл вложенного дерева — обычный путь
        Write("worktrees/x/docs/plain.txt", "обычное во вложенном дереве");
        PrivateAttachments.IsForeignResolved(_root, "worktrees/x/docs/plain.txt", Me).Should().BeFalse();
    }

    /// <Named мутации: «IsForeignResolved судит чужость префиксом относительно корня
    /// (прежняя формула)» краснит этот тест: канон цели уходит за корень и не под
    /// private/ корня — чужое через W читалось бы.</Named>
    [Fact]
    public void СсылкаИзКорняВWorktree_КанонЗаПределомКорня_ВсёРавноЧужое()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        // worktree вне корня проекта (штатное размещение: {home}/.worktrees/{slug}) +
        // ссылка W → wt, посаженная в main: для гейта по GetRoot она «не наше дело»
        var wt = Directory.CreateDirectory(Path.Combine(_tempDir, "wt")).FullName;
        WriteAt(wt, $".cc-attachments/private/{Other}/x.txt", "чужое в worktree");
        WriteAt(wt, $".cc-attachments/private/{Me}/own.txt", "своё в worktree");
        WriteAt(wt, "docs/plain.txt", "обычное в worktree");
        Directory.CreateSymbolicLink(Path.Combine(_root, "W"), wt);

        PrivateAttachments.IsForeignResolved(_root, $"W/.cc-attachments/private/{Other}/x.txt", Me)
            .Should().BeTrue("канон вышел за opRoot — сегмент чужого автора в нём остаётся");
        PrivateAttachments.IsForeignResolved(_root, $"W/.cc-attachments/private/{Me}/own.txt", Me)
            .Should().BeFalse("свой автор за пределами корня — своё: чужесть судится по X, не по месту канона");
        PrivateAttachments.IsForeignResolved(_root, "W/docs/plain.txt", Me)
            .Should().BeFalse("легальная ссылка наружу на не-private путь остаётся разрешённой " +
                "(frontend/node_modules → primary checkout у worktree-setup.sh — тот же класс)");
    }

    /// <Named: вариант A — opRoot фактический корень операции: multi-root ручки FilesController
    /// (?featureId=/?sessionId=) гейтятся по дереву, из которого читают (defect f1c60d82).
    /// На чужом корне этот кейс не проверяем: ссылка «L» в worktree в нём не существует, и
    /// предикат по нему молчит — именно это и есть дыра, закрытая переносом корня.</Named>
    [Fact]
    public void СсылкаВнутриWorktree_ГейтПоФактическомуКорню_Чужое()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        var wt = Directory.CreateDirectory(Path.Combine(_tempDir, "wt2")).FullName;
        WriteAt(wt, $".cc-attachments/private/{Other}/x.txt", "чужое в worktree");
        WriteAt(wt, $".cc-attachments/private/{Me}/own.txt", "своё в worktree");
        Directory.CreateSymbolicLink(Path.Combine(wt, "L"), ".cc-attachments");

        PrivateAttachments.IsForeignResolved(wt, $"L/private/{Other}/x.txt", Me)
            .Should().BeTrue("ссылка внутри worktree + корень операции = worktree — то же чужое");
        PrivateAttachments.IsForeignResolved(wt, $".cc-attachments/private/{Other}/x.txt", Me)
            .Should().BeTrue("прямой путь в worktree — тот же исход");
        PrivateAttachments.IsForeignResolved(wt, $"L/private/{Me}/own.txt", Me).Should().BeFalse();
    }

    /// <Named мутации: «снять вторую линию канона (IsUnderCanonicalPrivate)» краснит этот тест:
    /// .cc-attachments — ссылка на каталог с другим именем, пары сегментов в каноне нет, и
    /// чужое private под ним (через «L») читалось бы 200 — регрессия приватности 8b56ba3db.</Named>
    [Fact]
    public void СсылкаНаКаталогСДругимИменем_ЧужоеPrivateПодНим_ВсёРавноЧужое()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        // Пользователь уводит .cc-attachments на другой диск: ссылка на каталог, чьё имя
        // не .cc-attachments (хвост ревью 44e1219c: сужение чужести до одной сегментной линии)
        var disk = Directory.CreateDirectory(Path.Combine(_tempDir, "attachments")).FullName;
        WriteAt(disk, $"private/{Other}/x.txt", "чужое на другом диске");
        WriteAt(disk, $"private/{Me}/own.txt", "своё на другом диске");
        WriteAt(disk, $"{_commonGuid}/common.txt", "общее на другом диске");
        Directory.Delete(Path.Combine(_root, ".cc-attachments"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, ".cc-attachments"), disk);
        Link("L", ".cc-attachments");

        // текстовая линия молчит (в имени нет литерала .cc-attachments), первая каноническая —
        // тоже (пары .cc-attachments+private в каноне нет): судит канон под private корня
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Other}/x.txt", Me)
            .Should().BeTrue("closed-поддерево за ссылкой — то же чужое private");
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Other}", Me)
            .Should().BeTrue("каталог чужого автора через обе ссылки — чужой, как и прямой путь");
        PrivateAttachments.IsForeignResolved(_root, $".cc-attachments/private/{Other}/x.txt", Me)
            .Should().BeTrue("тот же файл прямым именем — тот же отказ");

        // своё и общее под той же ссылкой не закрыты: линия судит по автору, а не по месту канона
        PrivateAttachments.IsForeignResolved(_root, $"L/private/{Me}/own.txt", Me).Should().BeFalse();
        PrivateAttachments.IsForeignResolved(_root, $"L/{_commonGuid}/common.txt", Me).Should().BeFalse();
    }

    // --- контейнеры через ссылку ---

    /// <Named мутации: «IsContainerResolved сравнивает текстовый путь» краснит этот тест:
    /// «L» и «L/private» не равны ни одному текстовому контейнеру.</Named>
    [Fact]
    public void IsContainerResolved_ЧерезСимлинк_ТотЖеКонтейнер_КаталогАвтораНет()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("L", ".cc-attachments");

        PrivateAttachments.IsContainerResolved(_root, "L").Should().BeTrue("ссылка на контейнер — тот же контейнер");
        PrivateAttachments.IsContainerResolved(_root, "L/").Should().BeTrue("завершающий разделитель — та же цель");
        PrivateAttachments.IsContainerResolved(_root, "L/private").Should().BeTrue();
        PrivateAttachments.IsContainerResolved(_root, ".cc-attachments").Should().BeTrue();
        PrivateAttachments.IsContainerResolved(_root, ".cc-attachments/private").Should().BeTrue();
        PrivateAttachments.IsContainerResolved(_root, "").Should().BeTrue("корень — предок, под ним всё поддерево");
        PrivateAttachments.IsContainerResolved(_root, "docs/..").Should().BeTrue("обход к корню — тот же корень");

        // границы правила те же и через ссылку: каталог автора и {guid}-каталог — не контейнеры
        PrivateAttachments.IsContainerResolved(_root, $"L/private/{Me}").Should().BeFalse();
        PrivateAttachments.IsContainerResolved(_root, $"L/{_commonGuid}").Should().BeFalse();
        PrivateAttachments.IsContainerResolved(_root, "docs/plain.txt").Should().BeFalse();
    }

    [Fact]
    public void IsAttachmentsContainerResolved_БезКорня_ЧерезСимлинк_ТотЖеГейт()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("L", ".cc-attachments");

        PrivateAttachments.IsAttachmentsContainerResolved(_root, "L").Should().BeTrue();
        PrivateAttachments.IsAttachmentsContainerResolved(_root, "L/private").Should().BeTrue();
        // корень здесь НЕ контейнер: индексация корня — штатная цель IndexFolder (S2d-6b-6)
        PrivateAttachments.IsAttachmentsContainerResolved(_root, "").Should().BeFalse();
        PrivateAttachments.IsAttachmentsContainerResolved(_root, "docs").Should().BeFalse();
    }

    // --- неразрешимый путь и запределье ---

    /// <Named мутации: «IsForeignResolved/IsContainerResolved отдают false на неразрешимом
    /// пути» (fail-open) краснят этот тест: цикл ссылок обязан отказываться, а не проходить.</Named>
    [Fact]
    public void НеразрешимыйПуть_ЦиклСсылок_FailClosedОбаПредиката()
    {
        if (OperatingSystem.IsWindows()) return;
        Link("a", "b");
        Link("b", "a");

        PrivateAttachments.IsForeignResolved(_root, "a/x.txt", Me).Should().BeTrue();
        PrivateAttachments.IsContainerResolved(_root, "a/x.txt").Should().BeTrue();
        PrivateAttachments.IsAttachmentsContainerResolved(_root, "a").Should().BeTrue();
    }

    [Fact]
    public void ЗаПределамиКорня_НеЧужоеИНеКонтейнер_СвойОтказВызывающего()
    {
        PrivateAttachments.IsForeignResolved(_root, "../outside/x.txt", Me).Should().BeFalse();
        PrivateAttachments.IsContainerResolved(_root, "../outside").Should().BeFalse();
    }

    [Fact]
    public void СсылкаНаружу_ОбычныйПуть_НеКонтейнерИНеЧужое()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "x.txt"), "чужое дерево");
        Directory.CreateSymbolicLink(Path.Combine(_root, "out"), outside); // абсолютная цель вне проекта

        PrivateAttachments.IsContainerResolved(_root, "out").Should().BeFalse("ссылка сама по себе — не причина отказывать");
        PrivateAttachments.IsForeignResolved(_root, "out/x.txt", Me).Should().BeFalse();
    }
}
