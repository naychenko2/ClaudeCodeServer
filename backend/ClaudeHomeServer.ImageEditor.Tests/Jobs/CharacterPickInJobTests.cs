using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Подбор фото персонажа под сюжет в задаче генерации (ADR-027 §7–8): что уходит драйверу и что
// запоминает запуск. Флаг выключен — запрос драйверу тот же, что и без подбора
public class CharacterPickInJobTests : IDisposable
{
    private const string Owner = "user-p";
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-pick-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string[] _files;
    private readonly List<ImageEditRequest> _requests = [];
    private readonly List<ImageThreadCharacter> _picks = [];
    private readonly ImageEditJobService _service;
    private readonly ImageEditScope _scope;

    public CharacterPickInJobTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_root);
        var created = CharacterStore.Create(_root,
            new CharacterDraft("Аня", null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        created.Error.Should().BeNull();
        var manifest = created.Value!.Manifest;
        _files = [.. manifest.Photos.Select(p => p.File)];
        // Третье фото — в профиль, второе — улыбка
        CharacterStore.Update(_root, Owner, manifest.Slug, new CharacterPatch(null, null, [], [], null, null,
            new Dictionary<string, CharacterPhotoLabels>
            {
                [_files[2]] = new("profile", "level", "neutral"),
                [_files[1]] = new("front", "level", "smile"),
            }))!.Error.Should().BeNull();
        _scope = ImageEditScope.Of(new Project { Id = Project, OwnerId = Owner, RootPath = _root });

        var model = new ImageEditModelInfo("fake-model", "Фейк",
            new ImageEditCaps([ImageEditOp.Generate], MaskSupport.None, 5, 4, true),
            new ImageEditPriceHint(0.01, ImageEditPriceUnits.Usd, "image"));
        var editor = new Mock<IImageEditor>();
        editor.SetupGet(e => e.Key).Returns("fake");
        editor.SetupGet(e => e.Label).Returns("Фейк");
        editor.SetupGet(e => e.PriceUnit).Returns(ImageEditPriceUnits.Usd);
        editor.SetupGet(e => e.Enabled).Returns(true);
        editor.SetupGet(e => e.Registered).Returns(true);
        editor.SetupGet(e => e.Models).Returns([model]);
        editor.Setup(e => e.PickModel(It.IsAny<ImageEditOp>(), It.IsAny<EditMode>(), It.IsAny<EditTraits>())).Returns(model);
        editor.Setup(e => e.RunAsync(It.IsAny<ImageEditRequest>(), It.IsAny<IProgress<EditProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((ImageEditRequest r, IProgress<EditProgress> _, CancellationToken _) =>
            {
                lock (_requests) _requests.Add(r);
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                    [new EditedImage(TestImages.Png(8, 6), "image/png")], new EditCost(0.01, ImageEditPriceUnits.Usd), true, null, null));
            });
        _service = new ImageEditJobService([editor.Object], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(), new RecordingBroadcaster());
        _service.CharacterPicked += (_, _, launch) =>
        {
            lock (_picks) _picks.Add(launch);
            return Task.CompletedTask;
        };
        _editor = editor.Object;
    }

    private readonly IImageEditor _editor;

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class NoRunner : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) => throw new NotSupportedException();
        // Модель молчит — разбор идёт по словарю правил
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private async Task<ImageEditRequest> Run(bool flagOn, string prompt, IReadOnlyList<string>? photos = null)
    {
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.CharacterSmartPhotos)).Returns(flagOn);
        var assembler = new ImageEditLaunchAssembler([_editor], _service, new SkiaImageRaster(),
            flags: flags.Object, sceneParser: new CharacterSceneParser(new NoRunner()));

        var quote = await _service.QuoteAsync(Owner, Project,
            new ImageEditQuoteRequest("fake", "fake-model", EditMode.Auto, ImageEditOp.Generate, 1, false,
                References: 3, HasCharacter: true), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var assembled = await assembler.AssembleAsync(Owner, _scope,
            new ImageEditLaunchRequest(quote.Value!.QuoteId, prompt, null, null, null, null, null, [], [], "anya",
                CharacterPhotos: photos), default);
        assembled.Value.Should().NotBeNull(assembled.Error);
        var started = await _service.StartAsync(Owner, Project, assembled.Value!, default);
        started.Value.Should().NotBeNull(started.Error);
        for (var i = 0; i < 500 && _service.Get(Owner, Project, started.Value!.JobId)!.IsActive(); i++) await Task.Delay(10);
        lock (_requests) return _requests.Last();
    }

    // Всё, что видит драйвер о персонаже: роли, подписи и байты образцов по порядку, эталон лица, промпт
    private static string Snapshot(ImageEditRequest r) => JsonSerializer.Serialize(new
    {
        r.Prompt, r.Count, r.AspectRatio, r.Model,
        References = r.References.Select(x => new { x.Role, x.Label, x.ContentType, Bytes = Convert.ToHexString(x.Bytes) }),
        Character = r.Character is null ? null : new
        {
            r.Character.Slug, r.Character.Name,
            Faces = r.Character.FacePhotos.Select(Convert.ToHexString),
        },
    });

    private static string Hex(byte tag) => Convert.ToHexString(Characters.CharacterStoreTests.Jpeg(tag));

    private static IEnumerable<string> Order(ImageEditRequest r) =>
        r.References.Where(x => x.Role == ReferenceRole.Character).Select(x => Convert.ToHexString(x.Bytes));

    [Fact]
    public async Task Флаг_выключен_запрос_драйверу_как_без_подбора()
    {
        var request = await Run(false, "портрет в профиль у окна");

        Order(request).Should().Equal(Hex(1), Hex(2), Hex(3));
        request.Character!.FacePhotos.Select(Convert.ToHexString).Should().Equal(Hex(1), Hex(2), Hex(3));
        lock (_picks) _picks.Should().BeEmpty();
    }

    [Fact]
    public async Task Флаг_включён_сюжет_в_профиль_берёт_фото_с_меткой_профиля()
    {
        var request = await Run(true, "портрет в профиль у окна");

        // Главное первым, подобранное профильное — вторым, остальное следом
        Order(request).Should().Equal(Hex(1), Hex(3), Hex(2));
        // Эталон сходства от подбора не зависит
        request.Character!.FacePhotos.Select(Convert.ToHexString).Should().Equal(Hex(1), Hex(2), Hex(3));
        lock (_picks)
        {
            var pick = _picks.Should().ContainSingle().Subject;
            pick.Pick.Should().Be("auto");
            pick.Slug.Should().Be("anya");
            pick.Photos.Should().Equal(_files[0], _files[2], _files[1]);
            pick.Scene!.Angle.Should().Be("profile");
        }
    }

    [Fact]
    public async Task Флаг_включён_сюжет_без_признаков_порядок_прежний_и_fallback()
    {
        var request = await Run(true, "кот на подоконнике");
        var baseline = Snapshot(await Run(false, "кот на подоконнике"));

        Snapshot(request).Should().Be(baseline);
        Order(request).Should().Equal(Hex(1), Hex(2), Hex(3));
        lock (_picks) _picks.Should().ContainSingle().Which.Pick.Should().Be("fallback");
    }

    [Fact]
    public async Task Явный_список_идёт_как_есть_и_помечен_manual()
    {
        var request = await Run(true, "портрет в профиль у окна", [_files[2], _files[0]]);

        Order(request).Should().Equal(Hex(3), Hex(1));
        lock (_picks)
        {
            var pick = _picks.Should().ContainSingle().Subject;
            pick.Pick.Should().Be("manual");
            pick.Photos.Should().Equal(_files[2], _files[0]);
        }
    }
}

// Запись выбора в запуск нити: порядок «выбор раньше запуска» и «запуск раньше выбора»
public class CharacterPickLaunchRecordTests : IDisposable
{
    private const string Owner = "user-p";
    private const string Chat = "chat-1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ie-pick-launch-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ImageThreadStore _store;
    private readonly ImageThreadService _service;
    private readonly string _threadId;

    public CharacterPickLaunchRecordTests()
    {
        _store = new ImageThreadStore(_root);
        _service = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance);
        _threadId = _store.Create(Owner, Chat, null, "", focus: true).Thread.Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private static readonly ImageThreadCharacter Choice = new("anya", ["a.jpg", "b.jpg"], "auto", new CharacterScene("profile", null, null));

    private static ImageEditJobDto Job(string id) =>
        new(id, "p1", ImageEditJobStatus.Running, "fake", "fake-model", [], null, null, null, null, null, DateTime.UtcNow,
            ChatSessionId: Chat, ThreadId: null);

    private ImageThreadLaunch? LaunchOf(string jobId) =>
        _store.Get(Owner, Chat).Threads.Single().Launches.SingleOrDefault(l => l.JobId == jobId);

    private Task Launched(string jobId) =>
        _service.OnLaunchedAsync(Owner, "p1", Chat, _threadId, Job(jobId), jobId, "кадр", ImageEditInitiator.Human, null, null, default);

    [Fact]
    public async Task Запуск_хранит_Character_если_выбор_пришёл_после_запуска()
    {
        await Launched("job-1");
        LaunchOf("job-1")!.Character.Should().BeNull();

        await _service.OnCharacterPickedAsync(Owner, Job("job-1") with { ThreadId = _threadId }, Choice);

        LaunchOf("job-1")!.Character.Should().BeEquivalentTo(Choice);
    }

    [Fact]
    public async Task Запуск_хранит_Character_если_выбор_пришёл_до_запуска()
    {
        await _service.OnCharacterPickedAsync(Owner, Job("job-2") with { ThreadId = _threadId }, Choice);

        await Launched("job-2");

        LaunchOf("job-2")!.Character.Should().BeEquivalentTo(Choice);
    }

    [Fact]
    public void Старая_запись_запуска_без_Character_читается()
    {
        const string old = """{"jobId":"j","baseVersionId":null,"baseStepId":null,"at":"2026-10-01T00:00:00Z","status":"done","initiator":"human","prompt":"p"}""";

        var launch = JsonSerializer.Deserialize<ImageThreadLaunch>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        launch!.JobId.Should().Be("j");
        launch.Character.Should().BeNull();
    }

    [Fact]
    public void Character_переживает_запись_и_чтение_стора()
    {
        _store.AddLaunch(Owner, Chat, _threadId, new ImageThreadLaunch("job-3", null, null, _store.Now(),
            ImageThreadLaunchStatus.Running, "human", "p", Choice));

        var again = new ImageThreadStore(_root);

        again.Get(Owner, Chat).Threads.Single().Launches.Single().Character.Should().BeEquivalentTo(Choice);
    }
}
