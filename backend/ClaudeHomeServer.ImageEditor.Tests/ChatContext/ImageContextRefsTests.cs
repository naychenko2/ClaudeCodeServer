using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.ImageEditor.Tests.ChatContext;

// Роли референсов, вид image-character и «Чем» картинки (ADR-023 §1, 2б-1)
public sealed class ImageContextRefsTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "image-refs-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly ImageThreadStore _threads;
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner };

    public ImageContextRefsTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_project);
        _threads = new ImageThreadStore(Path.Combine(_root, "image"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ImageContextKind Kind(params IImageEditor[] editors) => new(_threads, null, editors);

    private ContextScope Scope => new(Owner, _session, new Project { Id = "p-1", RootPath = _project, OwnerId = Owner });
    private ContextScope Personal => new(Owner, _session, null);

    private static JsonObject Slug(string slug) => new() { ["slug"] = slug };

    private ContextItem Primary(string threadId) =>
        new("p", "image", new JsonObject { ["threadId"] = threadId }, null, ContextActor.Human, DateTime.UtcNow);

    private string NewThread(ImageThreadSettings? settings = null)
    {
        var id = _threads.Create(Owner, Chat, null, "", focus: false).Thread.Id;
        if (settings is not null) _threads.SetSettings(Owner, Chat, id, settings, _threads.Get(Owner, Chat).Revision);
        return id;
    }

    private string NewCharacter(string name = "Аня") =>
        CharacterStore.Create(_project, new CharacterDraft(name, null, CharacterStoreTests.Photos(3)), DateTime.UtcNow)
            .Value!.Manifest.Slug;

    [Fact]
    public void Validate_персонажа_принимает_свой_и_отказывает_пропавшему_битому_и_личному_чату()
    {
        var slug = NewCharacter();
        var kind = Kind();

        kind.Validate(Scope, "image-character", Slug(slug)).Should().BeNull();
        kind.Validate(Scope, "image-character", Slug("нет-такого")).Should().NotBeNull();
        kind.Validate(Scope, "image-character", Slug("net-takogo")).Should().Contain("не найден");
        kind.Validate(Scope, "image-character", Slug("../x")).Should().NotBeNull("слаг только по белому списку");
        kind.Validate(Scope, "image-character", new JsonObject()).Should().NotBeNull();
        kind.Validate(Personal, "image-character", Slug(slug)).Should().Contain("личном чате");
    }

    [Fact]
    public void Describe_персонажа_называет_по_имени_и_помечает_пропавшего()
    {
        var slug = NewCharacter("Аня");
        var kind = Kind();
        ContextItem Item(string s) => new("c", "image-character", Slug(s), "character", ContextActor.Human, DateTime.UtcNow);

        kind.Describe(Scope, Item(slug)).Should().Be(new ContextItemSummary("Аня", null, null, false));
        kind.Describe(Scope, Item("net-takogo")).Missing.Should().BeTrue();
        kind.Describe(Personal, Item(slug)).Missing.Should().BeTrue();
    }

    [Fact]
    public void Validate_image_по_прежнему_отказывает_чужой_нити()
    {
        var id = NewThread();
        var kind = Kind();
        kind.Validate(Scope, "image", new JsonObject { ["threadId"] = id }).Should().BeNull();
        kind.Validate(Scope, "image", new JsonObject { ["threadId"] = "чужая" }).Should().NotBeNull();
        kind.Validate(Scope with { OwnerId = "чужой" }, "image", new JsonObject { ["threadId"] = id }).Should().NotBeNull();
    }

    [Fact]
    public void AcceptedRefs_без_операции_отдаёт_полную_таблицу_ролей_по_таблице_ADR()
    {
        var table = Kind().AcceptedRefs(Scope, Primary(NewThread()), null);

        table.Select(r => r.Role).Should().Equal("style", "object", "face", "character");
        table.Where(r => r.Role != "character").Should().OnlyContain(r => r.Kinds.SequenceEqual(new[] { "image", "project-file" }));
        table.Single(r => r.Role == "character").Kinds.Should().Equal("image-character");
        table.Should().OnlyContain(r => r.Ops.SequenceEqual(new[] { "generate", "edit", "inpaint" }));
        table.Select(r => r.Role).Should().NotContain(["frame-a", "frame-b"], "кадры принимает сцена видео, не картинка");
    }

    [Theory]
    [InlineData("edit", 4)]
    [InlineData("removeBackground", 0)]
    [InlineData("upscale", 0)]
    [InlineData("outpaint", 0)]
    public void AcceptedRefs_по_операции_оставляет_только_берущие_её_роли(string op, int roles)
    {
        var table = Kind().AcceptedRefs(Scope, Primary(NewThread()), op);
        table.Should().HaveCount(roles);
        table.All(r => r.Ops.SequenceEqual(new[] { op })).Should().BeTrue();
    }

    [Fact]
    public void AcceptedRefs_чужой_основной_вид_и_персонаж_как_основной_ничего_не_принимают()
    {
        var kind = Kind();
        kind.AcceptedRefs(Scope, new ContextItem("a", "audio", new JsonObject(), null, ContextActor.Human, DateTime.UtcNow), null)
            .Should().BeEmpty();
        kind.AcceptedRefs(Scope, new ContextItem("c", "image-character", Slug("x"), null, ContextActor.Human, DateTime.UtcNow), null)
            .Should().BeEmpty();
    }

    [Fact]
    public void DescribeExecutor_берёт_поставщика_и_модель_нити_и_цену_из_каталога()
    {
        var editor = new Mock<IImageEditor>();
        editor.SetupGet(e => e.Key).Returns("local");
        editor.SetupGet(e => e.Label).Returns("Локально");
        editor.SetupGet(e => e.Models).Returns(
            [new ImageEditModelInfo("qwen", "Qwen-Image Edit", new ImageEditCaps([ImageEditOp.Edit], MaskSupport.None, 3, 4, false),
                new ImageEditPriceHint(0, "usd", "image"))]);
        var id = NewThread(new ImageThreadSettings("local", "qwen", 2, true));

        Kind(editor.Object).DescribeExecutor(Scope, Primary(id)).Should().Be("Локально · Qwen-Image Edit · бесплатно");
    }

    [Fact]
    public void DescribeExecutor_без_настроек_и_у_чужого_вида_честно_скромен()
    {
        Kind().DescribeExecutor(Scope, Primary(NewThread())).Should().Be("Авто · Авто");
        Kind().DescribeExecutor(Scope, new ContextItem("a", "audio", new JsonObject(), null, ContextActor.Human, DateTime.UtcNow))
            .Should().BeNull();
    }

    [Fact]
    public void Usedby_берётся_из_таблицы_основного_и_серый_референс_даёт_пустой_список()
    {
        var registry = new ContextKindRegistry([Kind(), new ProjectFileContextKind()]);
        var primary = Primary(NewThread());
        var state = new ChatContextState(1, primary,
        [
            new ContextItem("r1", "project-file", new JsonObject { ["path"] = "a.png" }, "style", ContextActor.Human, DateTime.UtcNow),
            new ContextItem("r2", "project-file", new JsonObject { ["path"] = "a.png" }, null, ContextActor.Human, DateTime.UtcNow),
        ]);

        var dto = ChatContextDtoBuilder.Build(registry, Scope, state);

        dto.Refs.Single(r => r.Id == "r1").UsedBy.Should().Equal("generate", "edit", "inpaint");
        dto.Refs.Single(r => r.Id == "r2").UsedBy.Should().BeEmpty("роль null — «для Claude», генераторам не вход");
    }
}
