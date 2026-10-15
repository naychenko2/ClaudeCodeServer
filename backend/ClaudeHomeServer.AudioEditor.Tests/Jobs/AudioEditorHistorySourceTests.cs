using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// Источник результата для истории (ADR-030 §3): редактор звука. Контракт считает варианты с 0,
// а редактор хранит их с 1 (папки 1/, 2/, …) — сопоставление n → VariantNumbers()[n].
// Сценарии: один вариант (n=0 → файл), несколько (n по индексу), n за пределами (404),
// удалённый файл (410). Мутация «подставлять n прямо в имя папки» красит все тесты.
public class AudioEditorHistorySourceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string JobId = "job-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "audhist_" + Guid.NewGuid().ToString("N"));
    private readonly AudioEditWorkspace _workspace;

    // Валидная сигнатура WAV (RIFF....WAVE): MediaContentTypeSniffer.Detect узнаёт audio/wav
    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 1, 2, 3];

    public AudioEditorHistorySourceTests()
    {
        _workspace = new AudioEditWorkspace(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* уборка temp — не предмет теста */ }
    }

    private AudioEditorHistorySource NewSource() => new(_workspace);

    private static MediaHistoryRef Ref() => new("p1", Owner, "proj-1", JobId, "chat-1");

    // Кладёт count вариантов (папки 1/..N/ с main.wav) в рабочую папку
    private void SaveVariants(int count)
    {
        for (var v = 1; v <= count; v++)
        {
            var dir = Path.Combine(_workspace.JobDir(Owner, JobId), v.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "main.wav"), Wav);
        }
    }

    // Один вариант, n=0 → файл main.wav, VariantCount=1
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
    }

    // Несколько вариантов: n=0,1,2 → папки 1/, 2/, 3/ по реальному списку
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
            // main.wav лежит в папке n+1
            res.HostPath!.Should().Be(_workspace.MainFile(Owner, JobId, n + 1));
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

    // Удалённый файл (папка варианта есть, main.wav нет) → 410 missing
    [Fact]
    public async Task УдалённыйФайл_410()
    {
        SaveVariants(1);
        File.Delete(_workspace.MainFile(Owner, JobId, 1)!);
        var res = await NewSource().ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }
}
