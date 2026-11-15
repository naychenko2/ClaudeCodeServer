using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Вид personal-file (Ф-9): путь от корня «Личного» вызывающего, принимается только личным чатом.
// Корень резолвится по OwnerId — чужой владелец чужой папки не получает ни чтением, ни описанием
public sealed class PersonalFileContextKindTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Other = "owner-2";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "ctx-personal-" + Guid.NewGuid().ToString("N"));
    private readonly string _root; // «Личное» Owner
    private readonly string _otherRoot; // «Личное» Other
    private readonly string _outside;

    public PersonalFileContextKindTests()
    {
        _root = Path.Combine(_base, "personal");
        _otherRoot = Path.Combine(_base, "personal-other");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        Directory.CreateDirectory(Path.Combine(_otherRoot, "audio"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "audio", "jingle.wav"), "x");
        File.WriteAllText(Path.Combine(_otherRoot, "audio", "other.wav"), "y");
        File.WriteAllText(Path.Combine(_outside, "secret.wav"), "s");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    // «Личное» есть только у Owner и Other; чужого владельца resolver не знает (тот же порядок, что в проде)
    private PersonalFolderResolver PersonalFolder()
    {
        var store = new Mock<IPersonalProjects>();
        store.Setup(s => s.GetPersonalOf(Owner)).Returns(
            new Project { Id = "personal-1", OwnerId = Owner, RootPath = _root, Kind = ProjectKind.Personal });
        store.Setup(s => s.GetPersonalOf(Other)).Returns(
            new Project { Id = "personal-2", OwnerId = Other, RootPath = _otherRoot, Kind = ProjectKind.Personal });
        return new PersonalFolderResolver(store.Object);
    }

    private PersonalFileContextKind Kind() => new(PersonalFolder());

    // ActorId по умолчанию — вызывающий, как в ChatContextScope личного чата; null передаётся явно
    private static ContextScope Scope(string owner = Owner, string? projectId = null, string? actor = Owner, Project? area = null) =>
        new(owner, new Session { Id = "chat-1", OwnerId = owner, ProjectId = projectId }, area, actor);

    private static JsonObject Ref(string path) => new() { ["path"] = path };

    private static ContextItem Item(string kind, string path) =>
        new("i1", kind, Ref(path), null, ContextActor.Human, DateTime.UtcNow);

    [Fact]
    public void Путь_внутри_Личного_годится() =>
        Kind().Validate(Scope(), "personal-file", Ref("audio/jingle.wav")).Should().BeNull();

    // Область личной медиы несёт «Личное» в scope.Project (AudioEditScopeGate) — вердикт вида от этого не меняется
    [Fact]
    public void Область_личной_медиы_с_папкой_в_scope_тот_же_вердикт()
    {
        var area = new Project { Id = "personal-1", OwnerId = Owner, RootPath = _root, Kind = ProjectKind.Personal };

        Kind().Validate(Scope(area: area), "personal-file", Ref("audio/jingle.wav")).Should().BeNull();
    }

    [Fact]
    public void Чат_проекта_не_принимает_файл_Личного()
    {
        var projectRoot = Path.Combine(_base, "project");
        Directory.CreateDirectory(projectRoot);
        var project = new Project { Id = "p-1", OwnerId = Owner, RootPath = projectRoot };

        Kind().Validate(Scope(projectId: project.Id, area: project), "personal-file", Ref("audio/jingle.wav"))
            .Should().Be("Файлы «Личного» доступны только в личном чате");
    }

    [Fact]
    public void Без_папки_Личного_отказ_до_проверки_пути() =>
        Kind().Validate(Scope(owner: "нет-такого"), "personal-file", Ref("audio/jingle.wav"))
            .Should().Be(PersonalFolderResolver.UnavailableText);

    [Theory]
    [InlineData("../outside/secret.wav")]
    [InlineData("/etc/passwd")]
    [InlineData("audio/../../outside/secret.wav")]
    public void Путь_вне_Личного_отклоняется(string path) =>
        Kind().Validate(Scope(), "personal-file", Ref(path)).Should().Be("Путь вне «Личного»");

    [Fact]
    public void Символическая_ссылка_наружу_отклоняется()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "out"), _outside);

        Kind().Validate(Scope(), "personal-file", Ref("out/secret.wav")).Should().Be("Путь идёт через символическую ссылку");
    }

    [Fact]
    public void Пустой_ref_отклоняется() =>
        Kind().Validate(Scope(), "personal-file", new JsonObject()).Should().NotBeNull();

    // Чужой владелец: своего файла он не видит даже по верному пути (корень — его собственный),
    // и чужой путь не выходит за его корень
    [Fact]
    public void Чужой_владелец_не_получает_чужих_файлов()
    {
        var kind = Kind();
        // Личный чат: актор — сам владелец
        var other = Scope(owner: Other, actor: Other);

        kind.Describe(other, Item(PersonalFileContextKind.Kind, "audio/jingle.wav")).Missing
            .Should().BeTrue("файл другого владельца");
        kind.Validate(other, "personal-file", Ref("../personal/audio/jingle.wav"))
            .Should().Be("Путь вне «Личного»");
        kind.Validate(other, "personal-file", Ref("audio/other.wav")).Should().BeNull("свой — годится");
    }

    // Тот же субъект приватности, что у проектных путей: чужое private не подтверждаем даже существованием
    [Theory]
    [InlineData("other-user", "owner-1", "Путь вне «Личного»")]
    [InlineData("owner-1", "other-user", "Путь вне «Личного»")]
    [InlineData("owner-1", "owner-1", null)]
    [InlineData("owner-1", null, "Путь вне «Личного»")]
    public void Private_внутри_Личного_судит_вызывающий(string fileOf, string? actor, string? expected)
    {
        var dir = Path.Combine(_root, ".cc-attachments", "private", fileOf, "g1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "p.wav"), "x");
        var path = $".cc-attachments/private/{fileOf}/g1/p.wav";

        Kind().Validate(Scope(actor: actor), "personal-file", Ref(path)).Should().Be(expected);
    }

    [Fact]
    public void Describe_даёт_имя_файла_и_не_missing()
    {
        var s = Kind().Describe(Scope(), Item(PersonalFileContextKind.Kind, "audio/jingle.wav"));

        s.Label.Should().Be("jingle.wav");
        s.Missing.Should().BeFalse();
    }

    [Fact]
    public void Describe_пропавшего_файла_помечает_missing() =>
        Kind().Describe(Scope(), Item(PersonalFileContextKind.Kind, "audio/gone.wav")).Missing.Should().BeTrue();

    [Fact]
    public void Describe_без_папки_не_падает_и_missing() =>
        Kind().Describe(Scope(owner: "нет-такого"), Item(PersonalFileContextKind.Kind, "audio/jingle.wav")).Missing
            .Should().BeTrue();

    // Пара видов: личный чат берёт personal-file и отказывает project-file, чат проекта — наоборот
    [Fact]
    public void Реестр_пары_видов_каждый_вид_живёт_своей_областью()
    {
        var projectRoot = Path.Combine(_base, "project");
        Directory.CreateDirectory(projectRoot);
        var project = new Project { Id = "p-1", OwnerId = Owner, RootPath = projectRoot };
        var registry = new ContextKindRegistry([new ProjectFileContextKind(), Kind()]);

        registry.Validate(Scope(), "personal-file", Ref("audio/jingle.wav")).Should().BeNull();
        registry.Validate(Scope(), "project-file", Ref("audio/jingle.wav")).Should().NotBeNull();
        registry.Validate(Scope(projectId: project.Id, area: project), "personal-file", Ref("audio/jingle.wav"))
            .Should().NotBeNull();
    }

    // Контекстный слой: вид отдаёт свой label в DTO, как проектный файл
    [Fact]
    public void DTO_личного_чата_несёт_файл_Личного()
    {
        var registry = new ContextKindRegistry([Kind()]);
        var state = new ChatContextState(1, null, [Item(PersonalFileContextKind.Kind, "audio/jingle.wav")]);

        var dto = ChatContextDtoBuilder.Build(registry, Scope(), state);

        var added = dto.Refs.Should().ContainSingle().Which;
        added.Kind.Should().Be(PersonalFileContextKind.Kind);
        added.Label.Should().Be("jingle.wav");
        added.Missing.Should().BeFalse();
    }

    [Fact]
    public void Вид_не_бывает_основным_и_не_описывает_исполнителя()
    {
        var kind = Kind();
        var item = Item(PersonalFileContextKind.Kind, "audio/jingle.wav");

        ((IContextKindProvider)kind).CanBePrimary(PersonalFileContextKind.Kind).Should().BeFalse();
        kind.AcceptedRefs(Scope(), item, null).Should().BeEmpty();
        kind.DescribeExecutor(Scope(), item).Should().BeNull();
        ((IContextKindProvider)kind).DescribeFormat(Scope(), item).Should().BeNull();
    }
}
