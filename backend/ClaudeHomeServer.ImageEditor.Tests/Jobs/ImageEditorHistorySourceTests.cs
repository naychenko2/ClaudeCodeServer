using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.ImageEditor.Tests.Jobs;

// Источник результата для истории (ADR-030 §3): редактор картинок. Контракт считает варианты
// с 0 (n < VariantCount), а редактор хранит их с 1 (v1, v2, …) — сопоставление n → VariantNumbers()[n].
// Сценарии: один вариант (n=0 → файл), несколько (n по индексу), n за пределами (404),
// удалённый файл (410). Мутация «подставлять n прямо в имя файла» красит все тесты.
public class ImageEditorHistorySourceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string JobId = "job-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imghist_" + Guid.NewGuid().ToString("N"));
    private readonly ImageEditWorkspace _workspace;

    public ImageEditorHistorySourceTests()
    {
        _workspace = new ImageEditWorkspace(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* уборка temp — не предмет теста */ }
    }

    private ImageEditorHistorySource NewSource() => new(_workspace);

    private static MediaHistoryRef Ref() => new("p1", Owner, "proj-1", JobId, "chat-1");

    // Кладёт count вариантов (v1..vN) в рабочую папку
    private void SaveVariants(int count)
    {
        for (var v = 1; v <= count; v++)
            _workspace.SaveVariant(Owner, JobId, v, new EditedImage(TestImages.Png(4, 4, (byte)v), "image/png"));
    }

    // Один вариант, n=0 → файл v1, VariantCount=1
    [Fact]
    public async Task ОдинВариант_N0_Файл()
    {
        SaveVariants(1);
        var res = await NewSource().ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().BeNull("файл есть");
        res.VariantCount.Should().Be(1);
        res.HostPath.Should().NotBeNull();
        File.Exists(res.HostPath!).Should().BeTrue();
        res.ContentType.Should().Be("image/png");
    }

    // Несколько вариантов: n=0,1,2 → v1, v2, v3 по реальному списку (а не «n+1» вслепую)
    [Fact]
    public async Task НесколькоВариантов_ПоИндексу()
    {
        SaveVariants(3);
        var src = NewSource();
        for (var n = 0; n < 3; n++)
        {
            var res = await src.ResolveAsync(Ref(), n, CancellationToken.None);
            res.Should().NotBeNull();
            res!.Reason.Should().BeNull($"вариант {n} есть");
            res.VariantCount.Should().Be(3);
            res.HostPath.Should().NotBeNull();
            Path.GetFileName(res.HostPath!).Should().Be($"v{n + 1}.png", $"n={n} → v{n + 1}");
        }
    }

    // n за пределами (3 варианта, n=3) → Reason==null, VariantCount=3 → 404 в контроллере
    [Fact]
    public async Task NЗаПределами_404()
    {
        SaveVariants(3);
        var res = await NewSource().ResolveAsync(Ref(), 3, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().BeNull("вне диапазона — 404 (контроллер проверяет n < VariantCount), а не 410");
        res.HostPath.Should().BeNull();
        res.VariantCount.Should().Be(3);
    }

    // Удалённый файл (вариант был, файла нет) → 410 missing
    [Fact]
    public async Task УдалённыйФайл_410()
    {
        SaveVariants(1);
        File.Delete(_workspace.VariantPath(Owner, JobId, 1)!);
        var res = await NewSource().ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }
}
