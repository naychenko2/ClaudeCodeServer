using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Модель, которая отработала, у запуска нити (жалоба 13.11): при запуске — модель котировки и причина «Авто»,
// с отчётом драйвера — фактическая (запасная Qwen вместо Krea/MOSS); переживает перечитывание нити
public class ImageThreadLaunchModelTests : IDisposable
{
    private const string Owner = "owner-lm";
    private const string Chat = "chat-lm";
    private const string Project = "p1";
    private const string Reason = "Авто → MOSS: фото людей";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs_thread_lm_" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _store;
    private readonly List<ImageEditJobService> _services = [];

    public ImageThreadLaunchModelTests()
    {
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // «Авто» всегда выбирает «moss»; драйвер сообщает фактическую модель ActualModel (null — не подменяет)
    private sealed class RoutedEditor : IImageEditor, IImageEditRouter
    {
        public const string ProviderKey = "routed";
        private static readonly ImageEditCaps Caps = new([ImageEditOp.Edit], MaskSupport.AsReference, 3, 4, false);
        private static readonly ImageEditModelInfo Moss = new("moss", "MOSS", Caps);
        private static readonly ImageEditModelInfo Qwen = new("qwen", "Qwen", Caps);
        public string? ActualModel { get; init; }
        public TaskCompletionSource Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Key => ProviderKey;
        public string Label => "Routed";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Moss, Qwen];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Moss;
        public ImageRoute? Route(ImageEditOp op, EditMode mode, EditTraits traits, string? category = null) => new(Moss, Reason);
        public bool NeedsClassifier(ImageEditOp op, EditMode mode, EditTraits traits) => false;

        public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            await Go.Task.WaitAsync(ct);
            var img = new EditedImage(Png(), "image/png");
            progress.Report(new EditProgress(EditStage.Running, Run: 1, Runs: 1, Model: ActualModel));
            progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 1, Ready: [img], Model: ActualModel));
            return new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Запуск «Авто» в нить: возвращает запись запуска сразу после OnLaunchedAsync и после финала
    private async Task<(ImageThreadLaunch AtLaunch, ImageThreadLaunch AtFinish)> RunAsync(string? actualModel)
    {
        var editor = new RoutedEditor { ActualModel = actualModel };
        var workspace = new ImageEditWorkspace(Path.Combine(_dir, "image-editor"));
        var jobs = new ImageEditJobService([editor], workspace, NullLogger<ImageEditJobService>.Instance);
        _services.Add(jobs);
        var steps = new ImageEditSteps(new SkiaImageRaster(), workspace, jobs);
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = Project });
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, null,
            new RecordingBroadcaster(), steps);
        threads.Watch(jobs);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Finished += (_, _) => { finished.TrySetResult(); return Task.CompletedTask; };

        var id = _store.Open(Owner, Chat, "images/fisher.png", null, _store.Get(Owner, Chat).Revision).Thread!.Id;
        var quote = await jobs.QuoteAsync(Owner, Project, new ImageEditQuoteRequest(RoutedEditor.ProviderKey,
            ImageEditCatalog.AutoModelId, EditMode.Auto, ImageEditOp.Edit, 1, false), default);
        quote.Value.Should().NotBeNull(quote.Error);
        quote.Value!.RouteReason.Should().Be(Reason);
        var input = new ImageEditJobInput(quote.Value.QuoteId, "убери рыбу", null, new ImageBytes(Png(), "image/png"),
            null, null, [], null, ChatSessionId: Chat, ThreadId: id, BaseVersionId: ImageThreadVersion.OriginId);
        var started = await jobs.StartAsync(Owner, Project, input, default);
        started.Value.Should().NotBeNull(started.Error);
        var jobId = started.Value!.JobId;

        await threads.OnLaunchedAsync(Owner, Project, Chat, id, jobs.Get(Owner, Project, jobId),
            jobId, "убери рыбу", ImageEditInitiator.Agent, ImageThreadVersion.OriginId, null, default);
        var atLaunch = Launch(id);
        editor.Go.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (atLaunch, Launch(id));
    }

    private ImageThreadLaunch Launch(string threadId) =>
        _store.Get(Owner, Chat).Threads.Single(t => t.Id == threadId).Launches.Single();

    [Fact]
    public async Task Запуск_Авто_хранит_модель_котировки_и_причину()
    {
        var (atLaunch, atFinish) = await RunAsync(actualModel: null);

        atLaunch.Model.Should().Be("moss");
        atLaunch.RouteReason.Should().Be(Reason);
        atFinish.Status.Should().Be(ImageThreadLaunchStatus.Done);
        atFinish.Model.Should().Be("moss", "драйвер модель не подменял");
    }

    [Fact]
    public async Task Драйвер_ушёл_на_запасную_модель_запуск_хранит_фактическую()
    {
        var (atLaunch, atFinish) = await RunAsync(actualModel: "qwen");

        atLaunch.Model.Should().Be("moss", "до отчёта драйвера известна только модель котировки");
        atFinish.Model.Should().Be("qwen");
        atFinish.RouteReason.Should().Be(Reason, "причина выбора «Авто» остаётся");
        _store.Get(Owner, Chat).Threads.Single().Versions.Where(v => !v.IsOrigin).Should().ContainSingle();

        var reread = new ImageThreadStore(_store.Root).Get(Owner, Chat).Threads.Single().Launches.Single();
        reread.Model.Should().Be("qwen");
        reread.RouteReason.Should().Be(Reason);
    }

    // Старые нити без полей модели читаются: Model и RouteReason — null, лишних полей JSON не пишет
    [Fact]
    public void Запуск_без_модели_не_пишет_полей_модели()
    {
        var id = _store.Open(Owner, Chat, "images/a.png", null, _store.Get(Owner, Chat).Revision).Thread!.Id;
        _store.AddLaunch(Owner, Chat, id, new ImageThreadLaunch("job-old", ImageThreadVersion.OriginId, null, DateTime.UtcNow,
            ImageThreadLaunchStatus.Running, "human", "кот"));

        var json = File.ReadAllText(_store.StatePath(Owner, Chat));
        json.Should().NotContain("\"routeReason\"").And.NotContain("\"model\"");
        Launch(id).Model.Should().BeNull();
    }
}
