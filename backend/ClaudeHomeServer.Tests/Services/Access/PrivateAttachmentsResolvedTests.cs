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

    private void Write(string relPath, string content)
    {
        var abs = Path.Combine(_root, relPath);
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
