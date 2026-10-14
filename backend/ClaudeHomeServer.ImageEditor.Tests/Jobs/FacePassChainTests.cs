using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Цепочка проходов подстановки (ADR-028, этап 3): исходник прохода k — выход прохода k−1,
// обёртка прогресса гасит Ready у непоследних (промежуточные выходы версиями не становятся),
// стадии «проход k из N». Тесты гоняют цепочку напрямую с подменным редактором;
// сквозной боевой путь по HTTP — FaceTargetLaunchHttpTwoFacesTests
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

    // Готовые запросы проходов (в задаче их собирает PrepareAsync: ужатие + Compose); исходник у
    // всех — базовый, цепочка сама подставляет выход прохода k−1 во вход прохода k
    private static IReadOnlyList<ImageEditRequest> Requests(IReadOnlyList<FacePass> passes) =>
        passes.Select(p => new ImageEditRequest(ImageEditOp.Inpaint, p.Prompt, new ImageBytes(Png(8, 8, 0), "image/png"),
            null, p.References, 1, null, null, "m", p.Character, null,
            Instruction: p.Prompt, FaceCrop: p.Crop, KeepMask: p.KeepMask)).ToList();

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

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default);

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

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), new RecordingProgress(), default);

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

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), new RecordingProgress(), default);

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

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default);

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

        await FacePassChain.RunAsync(editor, passes, Requests(passes), progress, default);

        progress.ReadyCount.Should().Be(1, "единственный проход — версия");
        progress.Reports.Should().OnlyContain(r => r.Runs == 1, "стадия «1 из 1»");
    }

    // ── Шаг 10: частичный результат (В1) ────────────────────────────────────────
    // Обёртка держит погашенный Ready прохода k: если цепочка оборвалась (сбой или отмена) до
    // последнего прохода, удержанный выход отпускается как итоговая версия. Отмена/падение во
    // время прохода 1 — версий нет (удержанного Ready ещё нет)

    // (1) Падение прохода 2: удержанный выход прохода 1 отпущен как версия, исключение проброшено
    [Fact]
    public async Task Падение_прохода_2_отпускает_удержанный_выход_прохода_1()
    {
        var progress = new RecordingProgress();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            if (req.Character!.Slug == "anya")
            {
                var img = new EditedImage(Png(8, 8, 1), "image/png");
                prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 2, Ready: [img]));
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
            }
            throw new InvalidOperationException("сбой прохода 2");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default));
        ex.Message.Should().Be("сбой прохода 2");
        progress.ReadyCount.Should().Be(1, "выход прохода 1 отпущен как итоговая версия");
    }

    // (2) Отмена между проходами: удержанный выход прохода 1 отпущен, OperationCanceledException проброшен
    [Fact]
    public async Task Отмена_между_проходами_отпускает_удержанный_выход()
    {
        var progress = new RecordingProgress();
        var cts = new CancellationTokenSource();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            if (req.Character!.Slug == "anya")
            {
                var img = new EditedImage(Png(8, 8, 1), "image/png");
                prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 2, Ready: [img]));
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
            }
            cts.Cancel();
            throw new OperationCanceledException(ct);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, cts.Token));
        progress.ReadyCount.Should().Be(1, "отмена после прохода 1 — версия с выходом прохода 1");
    }

    // (3) Отмена во время прохода 1: удержанного Ready ещё нет — версий нет
    [Fact]
    public async Task Отмена_во_время_прохода_1_версий_нет()
    {
        var progress = new RecordingProgress();
        var cts = new CancellationTokenSource();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, cts.Token));
        progress.ReadyCount.Should().Be(0, "отмена во время прохода 1 — версии нет");
    }

    // (4) Падение прохода 1: удержанного Ready ещё нет — версий нет
    [Fact]
    public async Task Падение_прохода_1_версий_нет()
    {
        var progress = new RecordingProgress();
        var editor = new FakeEditor((req, prog, ct) => throw new InvalidOperationException("сбой прохода 1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default));
        progress.ReadyCount.Should().Be(0, "падение прохода 1 — версии нет");
    }

    // (5) Успех двух проходов: ровно одна версия (выход последнего), удержанный выход прохода 1 не дублируется
    [Fact]
    public async Task Успех_двух_проходов_ровно_одна_версия_без_дубля()
    {
        var progress = new RecordingProgress();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            var img = new EditedImage(Png(8, 8, 1), "image/png");
            prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 2, Ready: [img]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
        });

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default);

        progress.ReadyCount.Should().Be(1, "ровно одна версия — выход последнего прохода");
    }

    // ── Поздний отчёт без Ready не затирает удержанный выход (ревью этапа 3) ─────
    // Проход 1 шлёт Downloading(Ready=[v1]), затем Running без Ready; удержанный v1 обязан
    // пережить этот отчёт, иначе падение/отмена прохода 2 оставляет задачу без версии (В1)

    // Подменный редактор: проход 1 — Ready, потом поздний Running без Ready; проход 2 — pass2
    private static FakeEditor LateReportEditor(
        Func<IProgress<EditProgress>, CancellationToken, Task<ImageEditResult>> pass2) =>
        new((req, prog, ct) =>
        {
            if (req.Character!.Slug == "anya")
            {
                var img = new EditedImage(Png(8, 8, 1), "image/png");
                prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
                prog.Report(new EditProgress(EditStage.Running, Run: 1, Runs: 1));
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
            }
            return pass2(prog, ct);
        });

    // (1) Падение прохода 2 после позднего Running: версия — выход прохода 1, лица done/failed
    [Fact]
    public async Task Поздний_отчёт_без_Ready_падение_прохода_2_версия_прохода_1()
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var editor = LateReportEditor((_, _) => throw new InvalidOperationException("сбой прохода 2"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()),
                progress, default, results.Add));

        progress.ReadyCount.Should().Be(1, "удержанный выход прохода 1 пережил поздний Running");
        progress.Reports.Single(r => r.Ready is not null).Ready![0].Bytes.Should().Equal(Png(8, 8, 1));
        results.Select(r => r.Status).Should().Equal("done", "failed");
    }

    // (2) Отмена между проходами после позднего Running: версия есть, лица done/cancelled
    [Fact]
    public async Task Поздний_отчёт_без_Ready_отмена_между_проходами_версия_есть()
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var cts = new CancellationTokenSource();
        var editor = LateReportEditor((_, ct) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()),
                progress, cts.Token, results.Add));

        progress.ReadyCount.Should().Be(1, "отмена после прохода 1 — версия с его выходом");
        results.Select(r => r.Status).Should().Equal("done", "cancelled");
    }

    // (3) Успех после позднего Running: ровно одна версия — Running не выпускает удержанное
    // (мутация «авто-выпуск на любой стадии» даёт две версии)
    [Fact]
    public async Task Поздний_отчёт_без_Ready_успех_ровно_одна_версия()
    {
        var progress = new RecordingProgress();
        var editor = LateReportEditor((prog, _) =>
        {
            // Проход 2 отдаёт свою версию через Ready, как боевой драйвер
            var img2 = new EditedImage(Png(8, 8, 2), "image/png");
            prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img2]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img2], null, true, null, null));
        });

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default);

        progress.ReadyCount.Should().Be(1, "только выход последнего прохода");
        progress.Reports.Single(r => r.Ready is not null).Ready![0].Bytes.Should().Equal(Png(8, 8, 2));
    }

    // (4) Проход 1 завершился без картинок (Downloading без Ready): это сбой прохода 1 (ADR-028, §8) —
    // версий нет, проход 2 не вызывается, лица failed/cancelled
    [Fact]
    public async Task Завершение_прохода_1_без_картинок_версий_нет()
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            if (req.Character!.Slug == "anya")
            {
                prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1));
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [], null, true, null, null));
            }
            throw new InvalidOperationException("сбой прохода 2");
        });

        var result = await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default, results.Add);

        result.Outcome.Should().Be(EditOutcome.Failed, "Ok без картинок — сбой прохода");
        editor.Requests.Should().HaveCount(1, "проход 2 не вызывается");
        results.Select(r => r.Status).Should().Equal("failed", "cancelled");
        progress.ReadyCount.Should().Be(0, "проход 1 картинок не дал — версии нет");
        progress.Reports.Should().Contain(r => r.Stage == EditStage.Downloading && r.Run == 1 && r.Runs == 2,
            "стадия завершения прохода 1 дошла наружу");
    }

    // ── Д2: сбой прохода результатом — та же ветка «проход упал», что исключение (ADR-028, §8) ──
    // Драйверы (Local, fal, Higgsfield) сообщают сбой ImageEditResult(Failed|Unavailable, []), а не
    // исключением. Цепочка не бросает, а возвращает результат прохода; Cancelled без отмены задачи — сбой

    // Подменный редактор: проход 1 — успех с Ready, проход 2 — заданный результат без исключения
    private static FakeEditor Pass2ResultEditor(ImageEditResult pass2) =>
        new((req, prog, ct) =>
        {
            if (req.Character!.Slug == "anya")
            {
                var img = new EditedImage(Png(8, 8, 1), "image/png");
                prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
            }
            return Task.FromResult(pass2);
        });

    public static TheoryData<EditOutcome, EditOutcome> Pass2Failures => new()
    {
        { EditOutcome.Failed, EditOutcome.Failed },
        { EditOutcome.Unavailable, EditOutcome.Unavailable },
        // Ok без картинок — сбой, в задачу уходит Failed
        { EditOutcome.Ok, EditOutcome.Failed },
        // Cancelled при неотменённом ct — прервали снаружи: сбой, а не отмена
        { EditOutcome.Cancelled, EditOutcome.Failed },
    };

    // (1) Проход 2 сообщил сбой результатом: done/failed, удержанный выход прохода 1 — ровно одна версия
    [Theory]
    [MemberData(nameof(Pass2Failures))]
    public async Task Сбой_прохода_2_результатом_отпускает_выход_прохода_1(EditOutcome pass2, EditOutcome expected)
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var editor = Pass2ResultEditor(new ImageEditResult(pass2, [], null, null, null, "сбой прохода 2"));

        var result = await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default, results.Add);

        result.Outcome.Should().Be(expected);
        result.Error.Should().Be("сбой прохода 2", "текст сбоя прохода — как есть");
        results.Select(r => r.Status).Should().Equal("done", "failed");
        progress.ReadyCount.Should().Be(1, "удержанный выход прохода 1 отпущен как итоговая версия");
        progress.Reports.Single(r => r.Ready is not null).Ready![0].Bytes.Should().Equal(Png(8, 8, 1));
    }

    // (2) Проход 1 сообщил сбой результатом: failed/cancelled, проход 2 не вызван, версий нет
    [Theory]
    [MemberData(nameof(Pass2Failures))]
    public async Task Сбой_прохода_1_результатом_проход_2_не_вызван(EditOutcome pass1, EditOutcome expected)
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var editor = new FakeEditor((_, _, _) =>
            Task.FromResult(new ImageEditResult(pass1, [], null, null, null, "сбой прохода 1")));

        var result = await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default, results.Add);

        result.Outcome.Should().Be(expected);
        editor.Requests.Should().HaveCount(1, "проход 2 не вызывается");
        results.Select(r => r.Status).Should().Equal("failed", "cancelled");
        progress.ReadyCount.Should().Be(0, "сбой прохода 1 — версии нет");
    }

    // (2а) Ok со списком картинок null (тип non-nullable, рантайм не защищён): сбой прохода, как Ok с [] —
    // проход 1: failed/cancelled, проход 2 не вызван; проход 2: done/failed, версия — выход прохода 1
    [Fact]
    public async Task Ok_без_списка_картинок_на_проходе_1_сбой_прохода()
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var editor = new FakeEditor((_, _, _) =>
            Task.FromResult(new ImageEditResult(EditOutcome.Ok, null!, null, null, null, null)));

        var result = await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default, results.Add);

        result.Outcome.Should().Be(EditOutcome.Failed);
        result.Error.Should().Be("Проход не вернул картинку");
        editor.Requests.Should().HaveCount(1, "проход 2 не вызывается");
        results.Select(r => r.Status).Should().Equal("failed", "cancelled");
        progress.ReadyCount.Should().Be(0);
    }

    [Fact]
    public async Task Ok_без_списка_картинок_на_проходе_2_сбой_прохода()
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var editor = Pass2ResultEditor(new ImageEditResult(EditOutcome.Ok, null!, null, null, null, null));

        var result = await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, default, results.Add);

        result.Outcome.Should().Be(EditOutcome.Failed);
        result.Error.Should().Be("Проход не вернул картинку");
        results.Select(r => r.Status).Should().Equal("done", "failed");
        progress.ReadyCount.Should().Be(1, "удержанный выход прохода 1 отпущен как итоговая версия");
    }

    // (3) Cancelled результатом при ОТМЕНЁННОМ ct — настоящая отмена: cancelled, исключение отмены
    [Fact]
    public async Task Cancelled_результатом_при_отмене_задачи_остаётся_отменой()
    {
        var progress = new RecordingProgress();
        var results = new List<FacePassChain.FacePassResult>();
        var cts = new CancellationTokenSource();
        var editor = new FakeEditor((req, prog, ct) =>
        {
            if (req.Character!.Slug == "anya")
            {
                var img = new EditedImage(Png(8, 8, 1), "image/png");
                prog.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
            }
            cts.Cancel();
            return Task.FromResult(new ImageEditResult(EditOutcome.Cancelled, [], null, null, null, "Остановлено"));
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), progress, cts.Token, results.Add));

        results.Select(r => r.Status).Should().Equal("done", "cancelled");
        progress.ReadyCount.Should().Be(1, "отмена после прохода 1 — версия с его выходом");
    }

    // ── Шаг 10: финализация задачи (ImageEditJobService) ─────────────────────────
    // Цепочка + финал: упал проход 2 → задача Completed (Outcome=Ok) с версией; отмена после
    // прохода 1 → Cancelled с версией. Гоняем через настоящий исполнитель с подменным редактором

    private sealed class PartialResultEditor : IImageEditor
    {
        public const string ProviderKey = "partial";
        private static readonly ImageEditModelInfo Model = new("m", "M",
            // Потолок веса — для теста подготовки исходника прохода 2; без растра в задаче он не действует
        new ImageEditCaps([ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, false, MaxInputMb: WeightLimitMb));

        // passOutcome: "ok" — оба прохода прошли; "fail2" — второй упал; "cancel2" — отмена во втором
        public string PassOutcome { get; init; } = "ok";
        // Проход 1 после Ready шлёт ещё Running без Ready (поздний отчёт не должен затереть удержанное)
        public bool LateRunning { get; init; }
        // Сигнал: первый проход завершён (тест отменяет задачу после него)
        public TaskCompletionSource Pass1Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Сбой прохода результатом без исключения (Д2): задан — проход отдаёт его как есть
        public ImageEditResult? Pass1Result { get; init; }
        public ImageEditResult? Pass2Result { get; init; }
        // Выход прохода 1 (по умолчанию — заглушка-заголовок, растр её не распознаёт)
        public byte[]? Pass1Image { get; init; }
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public string Key => ProviderKey;
        public string Label => "Partial";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var pass = req.Character!.Slug; // anya = проход 1, borya = проход 2
            if (pass == "anya" && Pass1Result is { } r1) return r1;
            if (pass == "borya" && Pass2Result is { } r2) return r2;
            if (pass == "anya")
            {
                var img = new EditedImage(Pass1Image ?? Png(8, 8, 1), "image/png");
                progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 2, Ready: [img]));
                if (LateRunning) progress.Report(new EditProgress(EditStage.Running, Run: 1, Runs: 2));
                Pass1Done.TrySetResult();
                return new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null);
            }
            // Проход 2
            if (PassOutcome == "fail2") throw new InvalidOperationException("сбой прохода 2");
            if (PassOutcome == "cancel2")
            {
                // Ждём отмены задачи: тест отменяет job.Cts, токен пробуждает и бросает OCE
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var reg = ct.Register(() => tcs.TrySetResult());
                await tcs.Task;
                throw new OperationCanceledException(ct);
            }
            var img2 = new EditedImage(Png(8, 8, 2), "image/png");
            progress.Report(new EditProgress(EditStage.Downloading, Run: 2, Runs: 2, Ready: [img2]));
            return new ImageEditResult(EditOutcome.Ok, [img2], null, true, null, null);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private (ImageEditJobService Jobs, string JobId, PartialResultEditor Editor) StartPartial(string passOutcome, string dir,
        bool lateRunning = false, ImageEditResult? pass1Result = null, ImageEditResult? pass2Result = null,
        IImageRaster? raster = null, byte[]? pass1Image = null)
    {
        var editor = new PartialResultEditor
        {
            PassOutcome = passOutcome, LateRunning = lateRunning, Pass1Result = pass1Result, Pass2Result = pass2Result,
            Pass1Image = pass1Image,
        };
        var jobs = new ImageEditJobService([editor], new ImageEditWorkspace(dir),
            NullLogger<ImageEditJobService>.Instance, raster: raster);
        var quote = new ImageEditQuoteRequest(
            PartialResultEditor.ProviderKey, "m", EditMode.Auto, ImageEditOp.Inpaint, 1,
            true, 0, false, null, null,
            FaceTarget: new FaceTarget("origin", "key",
                [new FaceAssignment(1, "anya"), new FaceAssignment(2, "borya")]));
        var quoteResult = jobs.QuoteAsync("owner", "p1", quote, default).GetAwaiter().GetResult();
        quoteResult.Value.Should().NotBeNull(quoteResult.Error);
        var passes = TwoPasses();
        var input = new ImageEditJobInput(
            quoteResult.Value!.QuoteId, "подстановка", null,
            new ImageBytes(Png(8, 8, 0), "image/png"), passes[0].Mask, null, passes[0].References,
            "images/hero.png", passes[0].Character,
            FaceTarget: passes[0].ThreadFaceTarget,
            FaceCrop: passes[0].Crop, KeepMask: passes[0].KeepMask,
            FacePasses: passes);
        var started = jobs.StartAsync("owner", "p1", input, default).GetAwaiter().GetResult();
        started.Value.Should().NotBeNull(started.Error);
        return (jobs, started.Value!.JobId, editor);
    }

    private static async Task<ImageEditJobDto> WaitDone(ImageEditJobService jobs, string jobId)
    {
        for (var i = 0; i < 500; i++)
        {
            var job = jobs.Get("owner", "p1", jobId)!;
            if (job.Status is ImageEditJobStatus.Completed or ImageEditJobStatus.Failed or ImageEditJobStatus.Cancelled)
                return job;
            await Task.Delay(10);
        }
        throw new TimeoutException("задача не завершилась");
    }

    // (6) Падение прохода 2: задача Completed (Outcome=Ok), одна версия, текст ошибки в Error
    [Fact]
    public async Task Падение_прохода_2_задача_Completed_с_версией()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, _) = StartPartial("fail2", dir);
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Completed, "упал проход 2 — задача не падает");
            job.Outcome.Should().Be(EditOutcome.Ok);
            job.Variants.Should().ContainSingle("версия — выход прохода 1");
            job.Error.Should().Contain("сбой прохода 2", "текст ошибки прохода 2 в Error");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // (7) Отмена после прохода 1: задача Cancelled, одна версия
    [Fact]
    public async Task Отмена_после_прохода_1_задача_Cancelled_с_версией()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, editor) = StartPartial("cancel2", dir);
        try
        {
            await editor.Pass1Done.Task; // первый проход завершён, второй ждёт отмены
            await jobs.CancelAsync("owner", "p1", jobId, default);
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Cancelled, "отмена после прохода 1 — Cancelled");
            job.Variants.Should().ContainSingle("версия — выход прохода 1");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // (8) Поздний Running после Ready прохода 1, проход 2 упал: задача Completed с одной версией
    [Fact]
    public async Task Поздний_отчёт_падение_прохода_2_задача_Completed_с_версией()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, _) = StartPartial("fail2", dir, lateRunning: true);
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Completed, "упал проход 2 — задача не падает");
            job.Variants.Should().ContainSingle("версия — выход прохода 1 пережил поздний Running");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // (9) Поздний Running после Ready прохода 1, отмена во втором: Cancelled с одной версией
    [Fact]
    public async Task Поздний_отчёт_отмена_после_прохода_1_задача_Cancelled_с_версией()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, editor) = StartPartial("cancel2", dir, lateRunning: true);
        try
        {
            await editor.Pass1Done.Task;
            await jobs.CancelAsync("owner", "p1", jobId, default);
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Cancelled, "отмена после прохода 1 — Cancelled");
            job.Variants.Should().ContainSingle("версия — выход прохода 1 пережил поздний Running");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── Д2 на уровне задачи: сбой прохода результатом финализирует, как исключение ─────────
    // Запуск нити выводится из задачи (ImageThreadService: Completed + версия → done)

    // (10) Проход 2 сообщил сбой результатом: Completed, Outcome=Ok, одна версия, Error прохода 2, done/failed
    [Theory]
    [InlineData(EditOutcome.Failed)]
    [InlineData(EditOutcome.Unavailable)]
    [InlineData(EditOutcome.Ok)]
    [InlineData(EditOutcome.Cancelled)]
    public async Task Сбой_прохода_2_результатом_задача_Completed_с_версией(EditOutcome pass2)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, editor) = StartPartial("ok", dir,
            pass2Result: new ImageEditResult(pass2, [], null, null, null, "сбой прохода 2"));
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Completed, "упал проход 2 — задача не падает");
            job.Outcome.Should().Be(EditOutcome.Ok);
            job.Variants.Should().ContainSingle("версия — выход прохода 1");
            job.Error.Should().Be("сбой прохода 2", "текст сбоя прохода 2 в Error");
            job.FaceResults!.Select(r => r.Status).Should().Equal("done", "failed");
            editor.Calls.Should().Be(2);
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // (11) Проход 1 сообщил сбой результатом: версии нет → FailAsync с Outcome прохода, failed/cancelled,
    // проход 2 не вызван. Cancelled без отмены задачи — Failed, а не Cancelled задачи
    [Theory]
    [InlineData(EditOutcome.Failed, EditOutcome.Failed)]
    [InlineData(EditOutcome.Unavailable, EditOutcome.Unavailable)]
    [InlineData(EditOutcome.Cancelled, EditOutcome.Failed)]
    public async Task Сбой_прохода_1_результатом_задача_Failed_без_версий(EditOutcome pass1, EditOutcome expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, editor) = StartPartial("ok", dir,
            pass1Result: new ImageEditResult(pass1, [], null, null, null, "сбой прохода 1"));
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Failed);
            job.Outcome.Should().Be(expected);
            job.Variants.Should().BeEmpty("сбой прохода 1 — версии нет");
            job.Error.Should().Be("сбой прохода 1");
            job.FaceResults!.Select(r => r.Status).Should().Equal("failed", "cancelled");
            editor.Calls.Should().Be(1, "проход 2 не вызывается");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // (12) Внешний Cancelled результатом (задачу НЕ отменяли — прервали снаружи, например на узле):
    // это сбой прохода, а не отмена. Проход 1: задача Failed (не Cancelled), лица failed/cancelled,
    // версий нет, проход 2 не вызван
    [Fact]
    public async Task Внешний_Cancelled_прохода_1_задача_Failed_а_не_Cancelled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, editor) = StartPartial("ok", dir,
            pass1Result: new ImageEditResult(EditOutcome.Cancelled, [], null, null, null, "прервано на узле"));
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Failed, "задачу не отменяли — внешний Cancelled это сбой");
            job.Outcome.Should().Be(EditOutcome.Failed);
            job.Variants.Should().BeEmpty("сбой прохода 1 — версии нет");
            job.Error.Should().Be("прервано на узле");
            job.FaceResults!.Select(r => r.Status).Should().Equal("failed", "cancelled");
            editor.Calls.Should().Be(1, "проход 2 не вызывается");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // (13) Внешний Cancelled результатом на проходе 2: задача Completed с версией прохода 1, Error заполнен,
    // лицо 2 — failed (не cancelled: человек ничего не отменял)
    [Fact]
    public async Task Внешний_Cancelled_прохода_2_задача_Completed_лицо_2_failed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId, editor) = StartPartial("ok", dir,
            pass2Result: new ImageEditResult(EditOutcome.Cancelled, [], null, null, null, "прервано на узле"));
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Completed, "задачу не отменяли — упал проход 2");
            job.Outcome.Should().Be(EditOutcome.Ok);
            job.Variants.Should().ContainSingle("версия — выход прохода 1");
            job.Error.Should().Be("прервано на узле");
            job.FaceResults!.Select(r => r.Status).Should().Equal("done", "failed");
            editor.Calls.Should().Be(2);
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── Major-1: исходник прохода 2 ужимается по весу, размер тот же (ADR-028) ─────────────
    // Выход прохода k — полноразмерный PNG модели, он минует PrepareAsync. Без ужатия фото ≳ 15–20 Мп
    // после прохода 1 тяжелее потолка входа local-media (30 МБ), и проход 2 падает всегда

    private const double WeightLimitMb = 0.15;
    private static long WeightLimitBytes => (long)(WeightLimitMb * 1024 * 1024);

    // Лимиты стороны и площади заведомо меньше картинки: ужатие по весу обязано их игнорировать
    private static readonly ImageEditCaps WeightCaps = new([ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, false,
        MaxInputSide: 64, MaxInputMegapixels: 0.004, MaxInputMb: WeightLimitMb);

    // Настоящий PNG из шума: не сжимается, вес ≈ 3 байта на пиксель
    private static byte[] NoisePng(int w, int h)
    {
        var rnd = new Random(42);
        using var bitmap = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Проход 1 отдаёт pass1Output, проход 2 — заглушку
    private static FakeEditor Pass1OutputEditor(byte[] pass1Output) =>
        new((req, prog, ct) =>
        {
            var img = new EditedImage(req.Character!.Slug == "anya" ? pass1Output : Png(8, 8, 2), "image/png");
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
        });

    // (1) Выход прохода 1 тяжелее MaxInputMb: исходник прохода 2 не тяжелее лимита, Width/Height те же
    [Fact]
    public async Task Тяжёлый_выход_прохода_1_ужимается_по_весу_размер_тот_же()
    {
        var raster = new SkiaImageRaster();
        var fitter = new InputFitter(raster);
        var heavy = NoisePng(256, 192);
        heavy.LongLength.Should().BeGreaterThan(WeightLimitBytes, "предусловие: выход прохода 1 тяжелее лимита");
        var editor = Pass1OutputEditor(heavy);

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), new RecordingProgress(), default,
            prepareSource: (src, ct) => fitter.FitWeightAsync(src, WeightCaps, ct));

        var source = editor.Requests[1].Source!;
        source.Bytes.LongLength.Should().BeLessThanOrEqualTo(WeightLimitBytes, "исходник прохода 2 — под потолком веса");
        var probe = raster.Probe(source.Bytes)!;
        (probe.DisplayWidth, probe.DisplayHeight).Should().Be((256, 192), "кроп и маска прохода 2 — в пикселях базовой версии");
    }

    // (2) Выход прохода 1 легче лимита: исходник прохода 2 — те же байты
    [Fact]
    public async Task Лёгкий_выход_прохода_1_едет_как_есть()
    {
        var fitter = new InputFitter(new SkiaImageRaster());
        var light = NoisePng(32, 32);
        light.LongLength.Should().BeLessThan(WeightLimitBytes, "предусловие: выход прохода 1 легче лимита");
        var editor = Pass1OutputEditor(light);

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), new RecordingProgress(), default,
            prepareSource: (src, ct) => fitter.FitWeightAsync(src, WeightCaps, ct));

        editor.Requests[1].Source!.Bytes.Should().Equal(light, "легче лимита — без перекодирования");
    }

    // Ш3 (ADR-028): выход прохода 1 (40 МБ) легче потолка холста (MaxCanvasMb = 90) — проход 2
    // получает его как есть, без JPEG. Мутация «MaxInputMb вместо MaxCanvasMb ?? MaxInputMb» → красный
    // (40 > 30 → JPEG, байты разойдутся)
    [Fact]
    public async Task Выход_Прохода_1_40МБ_Легче_Потолка_Холста_Проход_2_Получает_Как_Есть()
    {
        var raster = new SkiaImageRaster();
        var fitter = new InputFitter(raster);
        var canvas = PngOf(40 * 1024 * 1024);
        var caps = QwenCanvasCaps;
        var editor = Pass1OutputEditor(canvas);

        await FacePassChain.RunAsync(editor, TwoPasses(), Requests(TwoPasses()), new RecordingProgress(), default,
            prepareSource: (src, ct) => fitter.FitWeightAsync(src, caps, ct));

        editor.Requests[1].Source!.Bytes.Should().Equal(canvas, "40 МБ ≤ 90 МБ — без перекодирования");
    }

    // Лимиты Qwen-Image из каталога: MaxCanvasMb = 90, MaxInputMb = 30
    private static readonly ImageEditCaps QwenCanvasCaps = ImageEditCatalog.WithInputLimits(
        new LocalImageEditor(null).Models.Single(m => m.Id == LocalImageEditor.QwenImage)).Caps;

    // Холст нужного веса: настоящий PNG (SKBitmap, распознаётся растром) + нули до длины
    private static byte[] PngOf(int length)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.SteelBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        var result = new byte[length];
        Array.Copy(png, result, Math.Min(png.Length, length));
        return result;
    }

    // Растр, который распознаёт картинку, но отказывает в перекодировании
    private sealed class RefusingRaster : IImageRaster
    {
        private readonly SkiaImageRaster _inner = new();
        public RasterProbe? Probe(byte[] data) => _inner.Probe(data);
        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null) =>
            RasterOutcome.Fail(RasterError.Unsupported, "растр отказал");
        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => RasterOutcome.Fail(RasterError.Unsupported, "растр отказал");
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => _inner.ResizeMask(mask, width, height);
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => _inner.EraseMasked(image, mask);
    }

    // (3) Отказ подготовки исходника прохода 2 — сбой прохода 2: лица done/failed, проход 2 не вызван,
    // задача Completed с версией прохода 1, текст отказа в Error
    [Fact]
    public async Task Отказ_подготовки_исходника_прохода_2_задача_Completed_с_версией_прохода_1()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_partial_" + Guid.NewGuid().ToString("N"));
        var heavy = NoisePng(256, 192);
        var (jobs, jobId, editor) = StartPartial("ok", dir, raster: new RefusingRaster(), pass1Image: heavy);
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Status.Should().Be(ImageEditJobStatus.Completed, "упал проход 2 — задача не падает");
            job.Outcome.Should().Be(EditOutcome.Ok);
            job.Variants.Should().ContainSingle("версия — выход прохода 1");
            job.Error.Should().Be("растр отказал");
            job.FaceResults!.Select(r => r.Status).Should().Equal("done", "failed");
            editor.Calls.Should().Be(1, "проход 2 не вызывается: исходник не подготовлен");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── Поле сходства задачи (ADR-028 §11, Minor 7) ──────────────────────────────
    // При N > 1 общего сходства нет: сходство каждого лица — в FaceResults, «сходство варианта»
    // (Similarities) не отдаём. N = 1 — как раньше: Similarities заполнен

    // Драйвер: каждый проход отдаёт картинку с Similarity = 0.9
    private sealed class SimilarityEditor : IImageEditor
    {
        public const string ProviderKey = "sim";
        private static readonly ImageEditModelInfo Model = new("m", "M",
            new ImageEditCaps([ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, false));
        public string Key => ProviderKey;
        public string Label => "Sim";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            var img = new EditedImage(Png(8, 8, 1), "image/png", Similarity: 0.9);
            progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
        }
        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private (ImageEditJobService Jobs, string JobId) StartSimilarityJob(int faces, string dir)
    {
        var editor = new SimilarityEditor();
        var jobs = new ImageEditJobService([editor], new ImageEditWorkspace(dir),
            NullLogger<ImageEditJobService>.Instance);
        var quote = new ImageEditQuoteRequest(
            SimilarityEditor.ProviderKey, "m", EditMode.Auto, ImageEditOp.Inpaint, 1,
            true, 0, false, null, null,
            FaceTarget: new FaceTarget("origin", "key",
                Enumerable.Range(1, faces).Select(i => new FaceAssignment(i, "char" + i)).ToList()));
        var quoteResult = jobs.QuoteAsync("owner", "p1", quote, default).GetAwaiter().GetResult();
        quoteResult.Value.Should().NotBeNull(quoteResult.Error);
        var passes = TwoPasses().Take(faces).ToList();
        var input = new ImageEditJobInput(
            quoteResult.Value!.QuoteId, "подстановка", null,
            new ImageBytes(Png(8, 8, 0), "image/png"), passes[0].Mask, null, passes[0].References,
            "images/hero.png", passes[0].Character,
            FaceTarget: passes[0].ThreadFaceTarget,
            FaceCrop: passes[0].Crop, KeepMask: passes[0].KeepMask,
            FacePasses: passes);
        var started = jobs.StartAsync("owner", "p1", input, default).GetAwaiter().GetResult();
        started.Value.Should().NotBeNull(started.Error);
        return (jobs, started.Value!.JobId);
    }

    // N = 2: общего сходства нет — Similarities null (сходство каждого лица — в FaceResults)
    [Fact]
    public async Task Задача_на_два_лица_без_общего_сходства()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_sim_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId) = StartSimilarityJob(2, dir);
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.FaceResults!.Should().HaveCount(2, "два лица — два прохода");
            job.Similarities.Should().BeNull("при N > 1 общего сходства нет (ADR-028 §11)");
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // N = 1: как раньше — Similarities заполнен сходством единственного прохода
    [Fact]
    public async Task Задача_на_одно_лицо_с_сходством()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_sim_" + Guid.NewGuid().ToString("N"));
        var (jobs, jobId) = StartSimilarityJob(1, dir);
        try
        {
            var job = await WaitDone(jobs, jobId);
            job.Similarities.Should().NotBeNull("N = 1 — сходство варианта как раньше");
            job.Similarities!.Values.Should().Contain(0.9);
        }
        finally { jobs.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
