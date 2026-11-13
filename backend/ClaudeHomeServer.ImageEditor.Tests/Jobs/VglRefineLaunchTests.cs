using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Доводка версии Fibo 2.0 по JSON-описанию (Ш8): описание варианта ложится в версию нити, сборщик запуска
// берёт его с сидом и холстом версии и отказывает до запуска всему, что доводкой не является
public class VglRefineLaunchTests : IDisposable
{
    private const string Owner = "user-vgl";
    private const string Chat = "chat-vgl";
    private static readonly ImageVglScene Scene = new("{\"background_setting\":\"street\"}", 101, 1248, 832);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs_vgl_refine_" + Guid.NewGuid().ToString("N"));
    private readonly ImageEditScope _scope;
    private readonly ImageThreadStore _store;
    private readonly ImageThreadService _threads;
    private readonly string _threadId;
    private readonly string _fiboVersion;

    public VglRefineLaunchTests()
    {
        Directory.CreateDirectory(_root);
        _scope = ImageEditScope.Of(new Project { Id = "p1", OwnerId = Owner, RootPath = _root });
        File.WriteAllBytes(Path.Combine(_root, "test.png"), [0x89, 0x50, 0x4E, 0x47]);
        _store = new ImageThreadStore(Path.Combine(_root, "threads"));
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1" });
        _threads = new ImageThreadService(_store, new Mock<ILogger<ImageThreadService>>().Object, directory: directory.Object);
        _threads.OpenAsync(Owner, "p1", Chat, "test.png", null, 0, default).GetAwaiter().GetResult();
        _threadId = _store.Get(Owner, Chat).Threads.Single().Id;

        // Запуск Fibo 2.0: вариант 1 — с описанием сцены, вариант 2 — без него
        _store.AddLaunch(Owner, Chat, _threadId,
            new ImageThreadLaunch("job-1", ImageThreadVersion.OriginId, null, _store.Now(), ImageThreadLaunchStatus.Running, "human", "ёжик"),
            new ImageThreadEvent(_store.Now(), ImageThreadEventKinds.Launched, "запуск", _threadId, "job-1"));
        var finished = _store.FinishLaunch(Owner, Chat, _threadId, "job-1", ImageThreadLaunchStatus.Done,
            [(1, "step-1", null), (2, "step-2", null)], scenes: new Dictionary<int, ImageVglScene> { [1] = Scene });
        _fiboVersion = finished.NewVersions.Single(v => v.Variant == 1).Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ImageEditLaunchAssembler Assembler(ImageEditOp op = ImageEditOp.Generate, string model = LocalImageModels.Fibo2)
    {
        var jobs = new Mock<IImageEditJobs>();
        jobs.Setup(j => j.QuoteInfo(Owner, "p1", "q1")).Returns(new ImageEditQuoteInfo(op, null, LocalImageEditor.ProviderKey, Model: model));
        return new ImageEditLaunchAssembler([HiggsfieldImageEditorTests.Create().Editor], jobs.Object, new SkiaImageRaster(), threads: _threads);
    }

    private ImageEditLaunchRequest Request(string? version, string? field = "light", string? value = "ночь") =>
        new("q1", "Доводка: свет → ночь", null, null, null, null, null, [], [], null,
            ThreadSessionId: Chat, ThreadId: _threadId, VersionId: version, RefineField: field, RefineValue: value);

    [Fact]
    public void ОписаниеВарианта_ЛожитсяВВерсию_ДругомуВариантуНеДостаётся()
    {
        var versions = _store.Get(Owner, Chat).Threads.Single().Versions;

        versions.Single(v => v.Variant == 1).Vgl.Should().Be(Scene);
        versions.Single(v => v.Variant == 2).Vgl.Should().BeNull();
    }

    [Fact]
    public void ОписаниеВарианта_ПоХодуЗапуска_ТожеЛожитсяВВерсию()
    {
        _store.AddLaunch(Owner, Chat, _threadId,
            new ImageThreadLaunch("job-2", _fiboVersion, null, _store.Now(), ImageThreadLaunchStatus.Running, "human", "доводка"),
            new ImageThreadEvent(_store.Now(), ImageThreadEventKinds.Launched, "запуск", _threadId, "job-2"));

        var added = _store.AddLaunchVersions(Owner, Chat, _threadId, "job-2", [(1, "step-3", null)],
            new Dictionary<int, ImageVglScene> { [1] = Scene with { Seed = 7 } });

        added.NewVersions.Should().ContainSingle().Which.Vgl!.Seed.Should().Be(7);
    }

    [Fact]
    public async Task Доводка_БерётОписаниеСидИХолстВерсии()
    {
        var result = await Assembler().AssembleAsync(Owner, Owner, _scope, Request(_fiboVersion, "background", "  зимний лес "), default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.Refine.Should().Be(new ImageVglRefine(Scene, "background", "зимний лес"));
        result.Value.BaseVersionId.Should().Be(_fiboVersion);
    }

    [Fact]
    public async Task БезДоводки_ОбычныйЗапуск()
    {
        var result = await Assembler().AssembleAsync(Owner, Owner, _scope, Request(_fiboVersion, null, null), default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.Refine.Should().BeNull();
    }

    public static IEnumerable<object?[]> Refusals() =>
    [
        ["версия без описания", "origin", "light", "ночь", ImageEditOp.Generate, LocalImageModels.Fibo2, "Fibo 2.0 с JSON-описанием"],
        ["неизвестное поле", null, "mood", "мрачно", ImageEditOp.Generate, LocalImageModels.Fibo2, "неизвестно"],
        ["пустое значение", null, "light", "   ", ImageEditOp.Generate, LocalImageModels.Fibo2, "новое значение"],
        ["значение без поля", null, null, "ночь", ImageEditOp.Generate, LocalImageModels.Fibo2, "неизвестно"],
        ["котировка правки", null, "light", "ночь", ImageEditOp.Edit, LocalImageModels.Fibo2, "котировка"],
        ["котировка Qwen", null, "light", "ночь", ImageEditOp.Generate, LocalImageModels.Quality, "котировка"],
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Доводка_НеПоПравилам_ОтказДоЗапуска(string why, string? version, string? field, string? value,
        ImageEditOp op, string model, string error)
    {
        var result = await Assembler(op, model).AssembleAsync(Owner, Owner, _scope,
            Request(version ?? _fiboVersion, field, value), default);

        result.Value.Should().BeNull(why);
        result.Error.Should().Contain(error, why);
    }
}
