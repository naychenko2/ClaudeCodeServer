using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Цепочка проходов подстановки (ADR-028, этап 3): исходник прохода k — выход прохода k−1,
// обёртка прогресса гасит Ready у непоследних (промежуточные выходы версиями не становятся),
// стадии «проход k из N». Тесты гоняют цепочку напрямую с подменным редактором: в боевом пути
// цепочка недостижима до шага 12 (временный отказ N > 1 в сборщике)
public class FacePassChainTests
{
    private static byte[] Png(int w, int h, byte tag) => TestImages.Png(w, h, tag);

    // Подменный редактор: запоминает каждый полученный ImageEditRequest, отдаёт выход по метке
    private sealed class FakeEditor(
        Func<ImageEditRequest, IProgress<EditProgress>, CancellationToken, Task<ImageEditResult>> run) : IImageEditor
    {
        public List<ImageEditRequest> Requests { get; } = [];
        public string Key => "local";
        public string Label => "local";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => null;

        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            lock (Requests) Requests.Add(req);
            return run(req, progress, ct);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    // Обёртка прогресса: считает, сколько раз пришёл Ready (версия) и какие стадии
    private sealed class RecordingProgress : IProgress<EditProgress>
    {
        public List<EditProgress> Reports { get; } = [];
        public int ReadyCount => Reports.Count(r => r.Ready is not null);
        public void Report(EditProgress value) { lock (Reports) Reports.Add(value); }
    }

    // Два прохода: лицо 1 → персонаж A, лицо 2 → персонаж B
    private static IReadOnlyList<FacePass> TwoPasses()
    {
        var charA = new CharacterRef("anya", "Аня", null, null, null);
        var charB = new CharacterRef("borya", "Боря", null, null, null);
        var refA = new ReferenceImage(Png(4, 4, 1), "image/jpeg", ReferenceRole.Character, "a1.jpg");
        var refB = new ReferenceImage(Png(4, 4, 2), "image/jpeg", ReferenceRole.Character, "b1.jpg");
        var maskA = new ImageBytes(Png(8, 8, 10), "image/png");
        var maskB = new ImageBytes(Png(8, 8, 11), "image/png");
        var cropA = new LocalImageCrop(0, 0, 8, 8, 16, 16);
        var cropB = new LocalImageCrop(8, 0, 8, 8, 16, 16);
        return
        [
            new FacePass(1, maskA, cropA, maskA, charA, [refA],
                new ImageThreadFaceTarget(1, [0, 0, 8, 8], "anya"), "Заменить на Аня"),
            new FacePass(2, maskB, cropB, maskB, charB, [refB],
                new ImageThreadFaceTarget(2, [8, 0, 16, 8], "borya"), "Заменить на Боря"),
        ];
    }

    // (1) Два прохода дают РОВНО ОДНУ версию: Ready гасится у первого, только последний — версия
    [Fact]
    public async Task Два_прохода_дают_ровно_одну_версию()
    {
        var progress = new RecordingProgress();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            var img = new EditedImage(Png(8, 8, (byte)(req.Model.Length % 255 + 1)), "image/png");
            prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
        });

        await FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, default);

        progress.ReadyCount.Should().Be(1, "только последний проход стал версией");
    }

    // (2) Вход прохода 2 побайтно равен выходу прохода 1
    [Fact]
    public async Task Вход_второго_прохода_равен_выходу_первого()
    {
        var editor = new FakeEditor((req, prog, ct) =>
        {
            // Выход прохода = картинка с меткой, зависящей от входа (доказывает передачу)
            var tag = req.Source!.Bytes[^1];
            var img = new EditedImage(Png(8, 8, (byte)(tag + 1)), "image/png");
            prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
        });

        await FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", new RecordingProgress(), default);

        // Вход прохода 2 (req[1].Source) = выход прохода 1 (req[0] отда img с tag=1)
        editor.Requests.Should().HaveCount(2);
        var pass1Output = Png(8, 8, 1); // выход прохода 1: вход 0 + 1
        editor.Requests[1].Source!.Bytes.Should().Equal(pass1Output, "вход прохода 2 = выход прохода 1");
    }

    // (3) Запрос прохода 2 несёт персонажа 2 и его маску/кроп
    [Fact]
    public async Task Запрос_второго_прохода_несёт_персонажа_2_и_его_маску_кроп()
    {
        var editor = new FakeEditor((req, prog, ct) =>
            Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                [new EditedImage(Png(8, 8, 1), "image/png")], null, true, null, null)));

        await FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", new RecordingProgress(), default);

        var req2 = editor.Requests[1];
        req2.Character!.Slug.Should().Be("borya", "проход 2 — персонаж 2");
        req2.KeepMask!.Bytes.Should().Equal(TwoPasses()[1].Mask.Bytes, "маска прохода 2");
        req2.FaceCrop!.X.Should().Be(8, "кроп прохода 2");
        req2.References.Should().OnlyContain(r => r.Label == "b1.jpg", "образцы персонажа 2");
    }

    // (4) Стадии идут по порядку: «проход 1 из 2», «проход 2 из 2»
    [Fact]
    public async Task Стадии_идут_по_порядку_проход_k_из_N()
    {
        var progress = new RecordingProgress();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            prog.Report(new EditProgress(EditStage.Running, Run: 1, Runs: 1));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                [new EditedImage(Png(8, 8, 1), "image/png")], null, true, null, null));
        });

        await FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, default);

        // Обёртка выставила Run/Runs: первый отчёт — Run=1, Runs=2; второй — Run=2, Runs=2
        var reports = progress.Reports;
        reports.Should().NotBeEmpty();
        var runs = reports.Select(r => r.Run).ToList();
        runs.Should().Contain(1, "стадия «проход 1 из 2»");
        runs.Should().Contain(2, "стадия «проход 2 из 2»");
        reports.Should().OnlyContain(r => r.Runs == 2, "всё время «из 2»");
    }

    // (5) Один проход (N = 1) — прежний путь: Ready не гасится, стадия «1 из 1»
    [Fact]
    public async Task Один_проход_идёт_прежним_путём()
    {
        var progress = new RecordingProgress();
        var passes = TwoPasses().Take(1).ToList();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            var img = new EditedImage(Png(8, 8, 1), "image/png");
            prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
        });

        await FacePassChain.RunAsync(editor, passes, new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, default);

        progress.ReadyCount.Should().Be(1, "единственный проход — версия");
        progress.Reports.Should().OnlyContain(r => r.Runs == 1, "стадия «1 из 1»");
    }
}
