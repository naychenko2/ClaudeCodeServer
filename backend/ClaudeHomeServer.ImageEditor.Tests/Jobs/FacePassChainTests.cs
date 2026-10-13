using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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
            FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, default));
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
            FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, cts.Token));
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
            FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, cts.Token));
        progress.ReadyCount.Should().Be(0, "отмена во время прохода 1 — версии нет");
    }

    // (4) Падение прохода 1: удержанного Ready ещё нет — версий нет
    [Fact]
    public async Task Падение_прохода_1_версий_нет()
    {
        var progress = new RecordingProgress();
        var editor = new FakeEditor((req, prog, ct) => throw new InvalidOperationException("сбой прохода 1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, default));
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

        await FacePassChain.RunAsync(editor, TwoPasses(), new ImageBytes(Png(8, 8, 0), "image/png"), "m", progress, default);

        progress.ReadyCount.Should().Be(1, "ровно одна версия — выход последнего прохода");
    }

    // ── Шаг 10: финализация задачи (ImageEditJobService) ─────────────────────────
    // Цепочка + финал: упал проход 2 → задача Completed (Outcome=Ok) с версией; отмена после
    // прохода 1 → Cancelled с версией. Гоняем через настоящий исполнитель с подменным редактором

    private sealed class PartialResultEditor : IImageEditor
    {
        public const string ProviderKey = "partial";
        private static readonly ImageEditModelInfo Model = new("m", "M",
            new ImageEditCaps([ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, false));

        // passOutcome: "ok" — оба прохода прошли; "fail2" — второй упал; "cancel2" — отмена во втором
        public string PassOutcome { get; init; } = "ok";
        // Сигнал: первый проход завершён (тест отменяет задачу после него)
        public TaskCompletionSource Pass1Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Key => ProviderKey;
        public string Label => "Partial";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            var pass = req.Character!.Slug; // anya = проход 1, borya = проход 2
            if (pass == "anya")
            {
                var img = new EditedImage(Png(8, 8, 1), "image/png");
                progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 2, Ready: [img]));
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

    private (ImageEditJobService Jobs, string JobId, PartialResultEditor Editor) StartPartial(string passOutcome, string dir)
    {
        var editor = new PartialResultEditor { PassOutcome = passOutcome };
        var jobs = new ImageEditJobService([editor], new ImageEditWorkspace(dir),
            NullLogger<ImageEditJobService>.Instance);
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
}
