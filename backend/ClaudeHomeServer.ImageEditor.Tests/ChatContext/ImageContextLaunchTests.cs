using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.ImageEditor.Tests.ChatContext;

// Запуск и котировка картинки по ревизии контекста (ADR-023 §Д2.1, 2б-3): раскладка элементов по входам,
// 409 при чужой ревизии, прежнее тело без ревизии, строки исполнителей и загрузка образца
public sealed class ImageContextLaunchTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";
    private const string ProjectId = "p-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "image-ctx-launch-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly ImageThreadStore _threads;
    private readonly ImageEditWorkspace _workspace;
    private readonly ChatContextStore _store;
    private readonly ImageContextLaunch _context;
    private readonly ImageEditLaunchAssembler _assembler;
    private readonly QuoteJobs _jobs = new();
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner, ProjectId = ProjectId };
    private readonly ImageEditScope _scope;
    private readonly Session _stranger = new() { Id = "chat-stranger", OwnerId = "owner-2", ProjectId = ProjectId };
    private readonly Session _otherProject = new() { Id = "chat-other-project", OwnerId = Owner, ProjectId = "p-2" };

    // Котировки: по id отдаёт заранее заданные операцию и ревизию; всё остальное тут не нужно
    private sealed class QuoteJobs : IImageEditJobs
    {
        public Dictionary<string, ImageEditQuoteInfo> Quotes { get; } = [];

        public ImageEditQuoteInfo? QuoteInfo(string ownerId, string projectId, string quoteId) => Quotes.GetValueOrDefault(quoteId);

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(string o, string p, ImageEditQuoteRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(string o, string p, ImageEditJobInput i, CancellationToken ct) => throw new NotSupportedException();
        public ImageEditJobDto? Get(string o, string p, string j) => null;
        public Task<ImageEditJobDto?> CancelAsync(string o, string p, string j, CancellationToken ct) => Task.FromResult<ImageEditJobDto?>(null);
        public EditedImage? OpenVariant(string o, string p, string j, int v) => null;
    }

    public ImageContextLaunchTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, "art"));
        foreach (var name in new[] { "base", "style", "face", "body" })
            File.WriteAllBytes(Path.Combine(_project, "art", name + ".png"), TestImages.Png(8, 6));
        _scope = ImageEditScope.Of(new Project { Id = ProjectId, RootPath = _project, OwnerId = Owner });

        _threads = new ImageThreadStore(Path.Combine(_root, "threads"));
        _workspace = new ImageEditWorkspace(Path.Combine(_root, "workspace"));
        var registry = new ContextKindRegistry([new ImageContextKind(_threads, null, null, _workspace), new ProjectFileContextKind()]);
        _store = new ChatContextStore(Path.Combine(_root, "context"), registry);

        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(_session);
        directory.Setup(d => d.GetById("chat-stranger")).Returns(_stranger);
        directory.Setup(d => d.GetById("chat-other-project")).Returns(_otherProject);
        directory.Setup(d => d.ResolveOwnerId(It.IsAny<Session>())).Returns((Session s) => s.OwnerId ?? Owner);
        _context = new ImageContextLaunch(_store, registry, directory.Object, _threads, workspace: _workspace);
        _assembler = new ImageEditLaunchAssembler([HiggsfieldImageEditorTests.Create().Editor], _jobs, new SkiaImageRaster(),
            threads: new ImageThreadService(_threads, NullLogger<ImageThreadService>.Instance, directory.Object),
            steps: null, context: _context);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string NewThread() => _threads.Open(Owner, Chat, "art/base.png", null, _threads.Get(Owner, Chat).Revision).Thread!.Id;

    private static JsonObject Obj(string name, string value) => new() { [name] = value };

    private ContextItem Item(string kind, JsonObject reference, string? role) =>
        new("i" + Guid.NewGuid().ToString("N")[..8], kind, reference, role, ContextActor.Human, DateTime.UtcNow);

    // Контекст: основная картинка и по референсу на каждую роль; возвращает слаг персонажа и номер ревизии
    private (string ThreadId, string Slug, long Revision) Fill()
    {
        var thread = NewThread();
        var slug = CharacterStore.Create(_project, new CharacterDraft("Аня", null, CharacterStoreTests.Photos(3)), DateTime.UtcNow)
            .Value!.Manifest.Slug;
        var upload = _workspace.SaveUpload(Owner, TestImages.Png(5, 5));
        _store.SetPrimary(Owner, Chat, Item("image", Obj("threadId", thread), null), null);
        _store.AddRef(Owner, Chat, Item("project-file", Obj("path", "art/style.png"), "style"), null);
        _store.AddRef(Owner, Chat, Item("image", Obj("upload", upload), "object"), null);
        _store.AddRef(Owner, Chat, Item("project-file", Obj("path", "art/face.png"), "face"), null);
        var state = _store.AddRef(Owner, Chat, Item("image-character", Obj("slug", slug), "character"), null);
        return (thread, slug, state.Revision);
    }

    private ImageEditLaunchRequest Request(string quoteId, long? revision, IReadOnlyList<(string, ReferenceRole)>? paths = null,
        string? character = null, string? threadId = null) =>
        new(quoteId, "кот", null, null, null, null, null, [], paths ?? [], character,
            ThreadSessionId: Chat, ThreadId: threadId, ContextRevision: revision);

    // ── Раскладка по входам ──

    [Fact]
    public void Resolve_раскладывает_основной_и_референсы_по_входам_и_ролям()
    {
        var (thread, slug, revision) = Fill();

        var resolved = _context.Resolve(Owner, _scope, Chat, revision, ImageEditOp.Edit);

        resolved.ErrorCode.Should().BeNull(resolved.Error);
        var inputs = resolved.Value.Inputs;
        inputs.ThreadId.Should().Be(thread);
        inputs.ReferencePaths.Should().Equal(("art/style.png", ReferenceRole.Style), ("art/face.png", ReferenceRole.Object));
        inputs.Samples.Should().ContainSingle().Which.Role.Should().Be(ReferenceRole.Object, "образец с диска идёт образцом объекта");
        inputs.CharacterSlug.Should().Be(slug);
        inputs.ReferenceCount.Should().Be(3);
    }

    [Fact]
    public void Resolve_пропускает_референсы_операции_которая_их_не_берёт()
    {
        var (_, _, revision) = Fill();

        var inputs = _context.Resolve(Owner, _scope, Chat, revision, ImageEditOp.Outpaint).Value.Inputs;

        inputs.ReferenceCount.Should().Be(0);
        inputs.CharacterSlug.Should().BeNull();
    }

    [Fact]
    public void Resolve_чужая_ревизия_это_context_changed_со_свежим_контекстом()
    {
        var (_, _, revision) = Fill();

        var stale = _context.Resolve(Owner, _scope, Chat, revision - 1, ImageEditOp.Edit);

        stale.ErrorCode.Should().Be(ImageEditErrorCodes.ContextChanged);
        stale.Payload.Should().BeOfType<ChatContextConflictDto>().Which.Context.Revision.Should().Be(revision);
    }

    [Fact]
    public void Resolve_чужой_чат_и_без_основной_картинки_отказ()
    {
        _context.Resolve(Owner, _scope, "чужой", 0, ImageEditOp.Edit).ErrorCode.Should().Be(ImageEditErrorCodes.ChatNotFound);
        _context.Resolve(Owner, _scope, Chat, _store.Get(Owner, Chat).Revision, ImageEditOp.Edit).ErrorCode
            .Should().Be(ImageEditErrorCodes.InvalidRequest, "основного объекта-картинки нет");
    }

    [Fact]
    public void Resolve_существующий_чат_чужого_владельца_и_чат_другого_проекта_отказ_как_несуществующий()
    {
        _context.Resolve(Owner, _scope, "chat-stranger", 0, ImageEditOp.Edit).ErrorCode
            .Should().Be(ImageEditErrorCodes.ChatNotFound, "сессия есть, но владелец другой (ResolveOwnerId != ownerId)");
        _context.Resolve(Owner, _scope, "chat-other-project", 0, ImageEditOp.Edit).ErrorCode
            .Should().Be(ImageEditErrorCodes.ChatNotFound, "сессия своя, но из другой области (scope.Key)");
    }

    // ── Запуск по ревизии ──

    [Fact]
    public async Task Запуск_по_ревизии_берёт_входы_из_стора_а_поля_тела_игнорирует()
    {
        var (thread, slug, revision) = Fill();
        _jobs.Quotes["q1"] = new ImageEditQuoteInfo(ImageEditOp.Edit, revision);
        var request = Request("q1", revision, [("art/body.png", ReferenceRole.Style)], character: "чужой-слаг", threadId: "чужая-нить");

        var result = await _assembler.AssembleAsync(Owner, _scope, request, default);

        result.ErrorCode.Should().BeNull(result.Error);
        var input = result.Value!;
        input.ThreadId.Should().Be(thread, "нить — основной объект контекста, а не поле тела");
        input.Character!.Slug.Should().Be(slug);
        input.References.Select(r => r.Label).Should().NotContain("body.png");
        input.References.Should().HaveCount(6, "два файла и образец контекста плюс три фото персонажа");
        input.References.Count(r => r.Role == ReferenceRole.Character).Should().Be(3);
        input.Source.Should().NotBeNull("картинка основной версии прочитана с диска проекта");
    }

    [Fact]
    public async Task Запуск_по_ревизии_с_чужой_ревизией_отказ_context_changed_с_телом()
    {
        var (_, _, revision) = Fill();
        _jobs.Quotes["q1"] = new ImageEditQuoteInfo(ImageEditOp.Edit, revision);

        var result = await _assembler.AssembleAsync(Owner, _scope, Request("q1", revision - 1), default);

        result.ErrorCode.Should().Be(ImageEditErrorCodes.ContextChanged);
        result.Payload.Should().BeOfType<ChatContextConflictDto>();
    }

    [Fact]
    public async Task Отказ_context_changed_доезжает_до_ответа_запуска_с_телом()
    {
        var (_, _, revision) = Fill();
        _jobs.Quotes["q1"] = new ImageEditQuoteInfo(ImageEditOp.Edit, revision);

        var result = await _assembler.LaunchAsync(Owner, _scope, Request("q1", revision - 1), default);

        result.ErrorCode.Should().Be(ImageEditErrorCodes.ContextChanged);
        result.Payload.Should().BeOfType<ChatContextConflictDto>("409 несёт свежий контекст, а не пустое {error, code}");
    }

    [Fact]
    public async Task Запуск_с_котировкой_с_другой_ревизии_отказ_context_changed()
    {
        var (_, _, revision) = Fill();
        _jobs.Quotes["q1"] = new ImageEditQuoteInfo(ImageEditOp.Edit, revision - 1);

        var result = await _assembler.AssembleAsync(Owner, _scope, Request("q1", revision), default);

        result.ErrorCode.Should().Be(ImageEditErrorCodes.ContextChanged);
    }

    [Fact]
    public async Task Запуск_без_ревизии_берёт_тело_как_прежде_и_контекст_не_читает()
    {
        Fill();
        _jobs.Quotes["q1"] = new ImageEditQuoteInfo(ImageEditOp.Edit, null);
        var request = Request("q1", null, [("art/body.png", ReferenceRole.Style)]);

        var result = await _assembler.AssembleAsync(Owner, _scope, request, default);

        result.ErrorCode.Should().BeNull(result.Error);
        result.Value!.References.Should().ContainSingle().Which.Label.Should().Be("body.png");
        result.Value.Character.Should().BeNull("персонаж контекста без ревизии не подмешивается");
    }

    // ── Образец с диска ──

    [Fact]
    public void Загруженный_образец_читается_по_uploadId_и_чужой_не_открывается()
    {
        var id = _workspace.SaveUpload(Owner, TestImages.Png(3, 3));

        ImageEditWorkspace.IsUploadId(id).Should().BeTrue();
        _workspace.OpenUpload(Owner, id)!.Bytes.Should().NotBeEmpty();
        _workspace.OpenUpload("чужой", id).Should().BeNull();
        _workspace.OpenUpload(Owner, "up_../../etc").Should().BeNull("id только по нашему формату");
    }

    [Fact]
    public void Вид_image_принимает_upload_и_отказывает_пропавшему()
    {
        var kind = new ImageContextKind(_threads, null, null, _workspace);
        var scope = new ContextScope(Owner, _session, null);
        var id = _workspace.SaveUpload(Owner, TestImages.Png(3, 3));

        kind.Validate(scope, "image", Obj("upload", id)).Should().BeNull();
        kind.Validate(scope, "image", Obj("upload", "up_" + new string('a', 32))).Should().NotBeNull();
        kind.Describe(scope, Item("image", Obj("upload", id), "style")).Missing.Should().BeFalse();
    }

    // ── Строки исполнителей ──

    [Fact]
    public void Строки_исполнителей_Авто_первым_локальные_раньше_облака_и_серая_с_причиной()
    {
        var media = new LocalImageEditorTests.FakeMedia();
        IImageEditor[] editors = [HiggsfieldImageEditorTests.Create().Editor, new LocalImageEditor(media)];
        var catalog = ImageEditCatalog.Build(editors, "local", null);

        var rows = ImageExecutorRows.Build(catalog, editors, ImageEditOp.Edit, hasImage: true, hasMask: false);

        rows[0].Id.Should().Be("auto");
        rows[0].Sub.Should().StartWith("сейчас: ");
        var groups = rows.Select(r => r.Group).ToList();
        groups.LastIndexOf("local").Should().BeLessThan(groups.IndexOf("cloud"), "сначала своя видеокарта, потом облако");
        var local = rows.First(r => r.Group == "local" && r.Id != "local|auto");
        local.Free.Should().BeTrue();
        local.Unit.Should().Be("free");
        local.Amount.Should().BeNull();
        var cloud = rows.First(r => r.Group == "cloud" && r.Amount is not null);
        cloud.Free.Should().BeFalse();
        cloud.Unit.Should().BeOneOf("usd", "credits");
        cloud.Price.Should().NotBeEmpty();

        var upscale = ImageExecutorRows.Build(catalog, editors, ImageEditOp.Upscale, hasImage: true, hasMask: false);
        upscale.Where(r => r.Disabled).Should().NotBeEmpty().And.OnlyContain(r => !string.IsNullOrEmpty(r.Reason));
    }
}
