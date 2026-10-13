using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Инвариант Ш1 (ADR-028, этап 3): проход цепочки по составу запроса неотличим от одиночного
// запуска на том же лице с тем же персонажем; отличаться может только Source прохода k > 1.
// Живой прогон №2: проходы собирали запрос мимо Compose — модель получала кроп и все сырые фото
// персонажа без перечня ролей и рисовала коллаж из голов
public class FacePassRequestParityTests : IDisposable
{
    private const string Owner = "owner";
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-pass-parity-" + Guid.NewGuid().ToString("N")[..8]);

    // Боевые возможности Qwen-Image 2.1: вуаль, одно фото персонажа на человека
    private static readonly ImageEditModelInfo Qwen =
        new LocalImageEditor(null).Models.Single(m => m.Id == LocalImageEditor.QwenImage);

    private static byte[] Png(int w, int h, byte tag) => TestImages.Png(w, h, tag);

    // Подменный редактор local: записывает все запросы, выход прохода — картинка с новой меткой
    private sealed class RecordingEditor : IImageEditor
    {
        public List<ImageEditRequest> Requests { get; } = [];
        public string Key => LocalImageEditor.ProviderKey;
        public string Label => "local";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Qwen];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Qwen;

        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            int n;
            lock (Requests) { Requests.Add(req); n = Requests.Count; }
            var img = new EditedImage(Png(8, 8, (byte)(100 + n)), "image/png");
            progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private static ReferenceImage Photo(byte tag, string label) =>
        new(Png(4, 4, tag), "image/jpeg", ReferenceRole.Character, label);

    // Лицо 1 — Аня (3 фото), лицо 2 — Боря (3 фото); маски и кропы свои
    private static FacePass PassAnya(ImageBytes? mask = null) => new(1,
        mask ?? new ImageBytes(Png(8, 8, 10), "image/png"), new LocalImageCrop(0, 0, 8, 8, 16, 16),
        mask ?? new ImageBytes(Png(8, 8, 10), "image/png"),
        new CharacterRef("anya", "Аня", null, null, null),
        [Photo(1, "a1.jpg"), Photo(2, "a2.jpg"), Photo(3, "a3.jpg")],
        new ImageThreadFaceTarget(1, [0, 0, 8, 8], "anya"), "Лицо заменить на лицо персонажа «Аня»");

    private static FacePass PassBorya(ImageBytes? mask = null) => new(2,
        mask ?? new ImageBytes(Png(8, 8, 11), "image/png"), new LocalImageCrop(8, 0, 8, 8, 16, 16),
        mask ?? new ImageBytes(Png(8, 8, 11), "image/png"),
        new CharacterRef("borya", "Боря", null, null, null),
        [Photo(4, "b1.jpg"), Photo(5, "b2.jpg"), Photo(6, "b3.jpg")],
        new ImageThreadFaceTarget(2, [8, 0, 16, 8], "borya"), "Лицо заменить на лицо персонажа «Бори»");

    // Вход — так же, как его отдаёт сборщик: базовый вход + ForPass(первый проход)
    private static ImageEditJobInput Input(string quoteId, IReadOnlyList<FacePass> passes) =>
        new ImageEditJobInput(quoteId, "", null, new ImageBytes(Png(8, 8, 0), "image/png"), null, null, [],
            "images/hero.png", FacePasses: passes).ForPass(passes[0]);

    private async Task<(ImageEditJobService Jobs, string QuoteId)> Quote(RecordingEditor editor, params int[] faces)
    {
        var jobs = new ImageEditJobService([editor], new ImageEditWorkspace(Path.Combine(_dir, Guid.NewGuid().ToString("N")[..6])),
            NullLogger<ImageEditJobService>.Instance);
        var quote = await jobs.QuoteAsync(Owner, Project, new ImageEditQuoteRequest(
            LocalImageEditor.ProviderKey, Qwen.Id, EditMode.Auto, ImageEditOp.Inpaint, 1, true, 3, true,
            FaceTarget: new FaceTarget("origin", "key",
                [.. faces.Select(f => new FaceAssignment(f, f == 1 ? "anya" : "borya"))])), default);
        quote.Value.Should().NotBeNull(quote.Error);
        return (jobs, quote.Value!.QuoteId);
    }

    private static async Task WaitDone(ImageEditJobService jobs, string jobId)
    {
        for (var i = 0; i < 500 && jobs.Get(Owner, Project, jobId)!.IsActive(); i++) await Task.Delay(10);
    }

    private async Task<List<ImageEditRequest>> Run(params FacePass[] passes)
    {
        var editor = new RecordingEditor();
        var (jobs, quoteId) = await Quote(editor, [.. passes.Select(p => p.Face)]);
        using (jobs)
        {
            var started = await jobs.StartAsync(Owner, Project, Input(quoteId, passes), default);
            started.Value.Should().NotBeNull(started.Error);
            await WaitDone(jobs, started.Value!.JobId);
        }
        lock (editor.Requests) return [.. editor.Requests];
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private static object Refs(ImageEditRequest r) =>
        r.References.Select(x => (x.Role, x.Label, x.Bytes.Length)).ToList();

    // Проход 1 цепочки на два лица = одиночный запуск на лице 1 по всему составу, кроме Source.
    // Мутация «цепочка собирает запрос из полей FacePass» → красный (3 сырых фото, промпт без ролей)
    [Fact]
    public async Task Проход_1_цепочки_равен_одиночному_запуску_на_том_же_лице()
    {
        var single = (await Run(PassAnya())).Should().ContainSingle().Subject;
        var chain = await Run(PassAnya(), PassBorya());
        chain.Should().HaveCount(2);
        var pass1 = chain[0];

        Refs(pass1).Should().BeEquivalentTo(Refs(single), o => o.WithStrictOrdering(), "образцы: число, роли, метки");
        pass1.References.Should().ContainSingle(r => r.Role == ReferenceRole.Character, "MaxCharacterPhotos = 1");
        pass1.Character.Should().Be(single.Character);
        pass1.Prompt.Should().Be(single.Prompt);
        pass1.Mask.Should().Be(single.Mask, "канал маски");
        pass1.KeepMask!.Bytes.Should().Equal(single.KeepMask!.Bytes);
        pass1.FaceCrop.Should().Be(single.FaceCrop);
        pass1.Op.Should().Be(single.Op);
        pass1.Count.Should().Be(single.Count);
    }

    // Проход 2: своё одно фото, перечень ролей, без вуали, ни одного фото персонажа 1; вход — выход прохода 1
    [Fact]
    public async Task Проход_2_собран_через_Compose_из_своего_персонажа()
    {
        var chain = await Run(PassAnya(), PassBorya());
        var pass2 = chain[1];

        pass2.References.Should().ContainSingle(r => r.Role == ReferenceRole.Character)
            .Which.Label.Should().Be("b1.jpg", "одно фото Бори — первое (главное)");
        pass2.References.Should().NotContain(r => r.Label!.StartsWith("a"), "фото Ани в проход Бори не попадают");
        pass2.Character!.Slug.Should().Be("borya");
        pass2.Prompt.Should().Contain("Картинки в запросе", "перечень ролей картинок от Compose");
        pass2.Prompt.Should().NotStartWith(EditRequestComposer.VeilHead, "при кропе лица вуали нет");
        pass2.Prompt.Should().NotContain(EditRequestComposer.VeilNote);
        pass2.FaceCrop!.X.Should().Be(8, "кроп прохода 2");
        pass2.Source!.Bytes.Should().Equal(Png(8, 8, 101), "вход прохода 2 = выход прохода 1");
    }

    // Отказ Compose прохода 2 (маска не того размера) — до траты: котировка жива, редактор не звали
    [Fact]
    public async Task Отказ_сборки_прохода_2_до_траты_котировки()
    {
        var editor = new RecordingEditor();
        var (jobs, quoteId) = await Quote(editor, 1, 2);
        using (jobs)
        {
            var badMask = new ImageBytes(Png(8, 9, 11), "image/png");
            var started = await jobs.StartAsync(Owner, Project,
                Input(quoteId, [PassAnya(), PassBorya(badMask)]), default);

            started.Value.Should().BeNull("задача не создана");
            started.Error.Should().Contain("Лицо 2").And.Contain("Размер маски");
            jobs.QuoteInfo(Owner, Project, quoteId).Should().NotBeNull("котировка не потрачена");
        }
        editor.Requests.Should().BeEmpty("до редактора дело не дошло");
    }
}
