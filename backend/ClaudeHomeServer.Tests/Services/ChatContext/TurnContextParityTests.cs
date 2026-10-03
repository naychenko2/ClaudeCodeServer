using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Хвост хода «Контекст хода» (ADR-023 §3.1, 2б-2): подписи «С чем» и «Плюс» в хвосте — ровно label из
// ChatContextDto, который отдают ручки (одна функция Describe провайдера владельца); «Чем» остаётся
// в хвосте (исключение Р2), «Где» — ветка без числа изменений
public sealed class TurnContextParityTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "turn-ctx-" + Guid.NewGuid().ToString("N"));
    private readonly ChatContextStore _store;
    private readonly ContextKindRegistry _registry;
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner };

    public TurnContextParityTests()
    {
        _registry = new ContextKindRegistry([new FakeKind(), new ProjectFileContextKind()]);
        _store = new ChatContextStore(Path.Combine(_root, "ctx"), _registry);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // Подписи с «неудобными» символами: форматирование в хвосте сломало бы совпадение
    private sealed class FakeKind : IContextKindProvider
    {
        public IReadOnlyList<string> Kinds { get; } = ["image", "image-character"];
        public bool CanBePrimary(string kind) => kind == "image";
        public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;

        public ContextItemSummary Describe(ContextScope scope, ContextItem item) => item.Kind switch
        {
            "image-character" => new ContextItemSummary("Аня Ковалёва", null, null, false),
            _ when item.Ref["threadId"]?.GetValue<string>() == "t9" => new ContextItemSummary("картинка недоступна", null, null, true),
            _ => new ContextItemSummary("hero_Final.PNG", "v2", null, false),
        };

        public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) =>
            primary.Kind != "image" ? [] :
            [
                new("style", "Образец стиля", ["image", "project-file"], ["edit"]),
                new("character", "Персонаж", ["image-character"], ["edit"]),
            ];

        public string? DescribeExecutor(ContextScope scope, ContextItem primary) =>
            primary.Kind == "image" ? "Авто · локально · Qwen-Image Edit · бесплатно" : null;
    }

    private static ContextItem Item(string kind, JsonObject reference, string? role, ContextActor by) =>
        new("ci_" + Guid.NewGuid().ToString("N")[..8], kind, reference, role, by, DateTime.UtcNow);

    private TurnContextContributor Contributor()
    {
        var projects = new Mock<IProjectManager>();
        var services = new ServiceCollection().AddSingleton<IChatContextStore>(_store).BuildServiceProvider();
        return new TurnContextContributor(services, _registry, projects.Object);
    }

    private PromptSessionContext Ctx(string? rootPath = null) => new(_session, Owner, null, rootPath ?? _root);

    private async Task<string> TailAsync(string? rootPath = null)
    {
        var contribution = await Contributor().BuildAsync(Ctx(rootPath), "привет");
        return contribution!.Sections.Single().Text;
    }

    private void Fill()
    {
        _store.SetPrimary(Owner, Chat, Item("image", new JsonObject { ["threadId"] = "t3" }, null, ContextActor.Human), null);
        _store.AddRef(Owner, Chat, Item("image-character", new JsonObject { ["slug"] = "anya" }, "character", ContextActor.Human), null);
        _store.AddRef(Owner, Chat, Item("image", new JsonObject { ["threadId"] = "t4" }, "style", ContextActor.Agent), null);
        _store.AddRef(Owner, Chat, Item("image", new JsonObject { ["threadId"] = "t9" }, "style", ContextActor.Human), null);
    }

    [Fact]
    public async Task Хвост_содержит_ровно_label_из_DTO_по_С_чем_и_Плюс()
    {
        Fill();
        var tail = await TailAsync();
        var dto = ChatContextDtoBuilder.Build(_registry, new ContextScope(Owner, _session, null), _store.Get(Owner, Chat));

        dto.Primary!.Label.Should().Be("hero_Final.PNG");
        var withWhat = tail.Split('\n').Single(l => l.StartsWith("С чем:", StringComparison.Ordinal));
        withWhat.Should().Contain(dto.Primary.Label);
        var plus = tail.Split('\n').Single(l => l.StartsWith("Плюс:", StringComparison.Ordinal));
        foreach (var r in dto.Refs) plus.Should().Contain(r.Label);
        dto.Refs.Should().HaveCount(3);
    }

    [Fact]
    public async Task Хвост_несёт_роли_пометки_актора_и_исполнителя()
    {
        Fill();
        var tail = await TailAsync();

        tail.Should().Contain("С чем: картинка t3 — hero_Final.PNG · версия 2 (поставил человек)");
        tail.Should().Contain("Чем: Авто · локально · Qwen-Image Edit · бесплатно", "«Чем» остаётся в хвосте (исключение Р2)");
        tail.Should().Contain("Аня Ковалёва [anya] — персонаж (character)");
        tail.Should().Contain("hero_Final.PNG [t4] · версия 2 — образец стиля (style) ✦ поставил Claude");
        tail.Should().Contain("картинка недоступна [t9] — образец стиля (style) (недоступен)");
        tail.Should().EndWith(TurnContextContributor.Footer);
    }

    [Fact]
    public async Task Референс_без_роли_помечен_как_для_Claude_и_не_вход_генератора()
    {
        var path = Path.Combine(_root, "notes.md");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "x");
        _store.SetPrimary(Owner, Chat, Item("image", new JsonObject { ["threadId"] = "t3" }, null, ContextActor.Human), null);
        _store.AddRef(Owner, Chat, Item("project-file", new JsonObject { ["path"] = "notes.md" }, null, ContextActor.Human), null);

        var tail = await TailAsync();

        tail.Should().Contain("notes.md [notes.md] — файл проекта, для тебя (не вход генератора)");
    }

    [Fact]
    public async Task Референсы_печатают_идентификатор_путь_файла_и_slug()
    {
        Directory.CreateDirectory(Path.Combine(_root, "art"));
        File.WriteAllText(Path.Combine(_root, "art", "style.png"), "x");
        _store.SetPrimary(Owner, Chat, Item("image", new JsonObject { ["threadId"] = "t3" }, null, ContextActor.Human), null);
        _store.AddRef(Owner, Chat, Item("project-file", new JsonObject { ["path"] = "art/style.png" }, "style", ContextActor.Human), null);
        _store.AddRef(Owner, Chat, Item("image-character", new JsonObject { ["slug"] = "anya" }, "character", ContextActor.Human), null);

        var tail = await TailAsync();

        tail.Should().Contain("style.png [art/style.png]").And.Contain("Аня Ковалёва [anya]");
    }

    // Отключаемость (ADR-023 §4): вид выключенной вертикали не зарегистрирован — в хвост не идёт, в DTO остаётся
    [Fact]
    public async Task Элементы_без_провайдера_в_хвост_не_идут_а_в_DTO_остаются_missing()
    {
        var withAudio = new ContextKindRegistry([new FakeKind(), new ProjectFileContextKind(), new AudioKind()]);
        var store = new ChatContextStore(Path.Combine(_root, "ctx2"), withAudio);
        store.SetPrimary(Owner, Chat, Item("image", new JsonObject { ["threadId"] = "t3" }, null, ContextActor.Human), null);
        store.AddRef(Owner, Chat, Item("audio", new JsonObject { ["threadId"] = "a1" }, null, ContextActor.Human), null);
        var projects = new Mock<IProjectManager>();
        var services = new ServiceCollection().AddSingleton<IChatContextStore>(store).BuildServiceProvider();
        var contributor = new TurnContextContributor(services, _registry, projects.Object);

        var tail = (await contributor.BuildAsync(Ctx(), "привет"))!.Sections.Single().Text;

        tail.Should().NotContain("звук").And.NotContain("аудио").And.NotContain("Плюс:");
        tail.Should().Contain("С чем: картинка t3");
        var dto = ChatContextDtoBuilder.Build(_registry, new ContextScope(Owner, _session, null), store.Get(Owner, Chat));
        dto.Refs.Should().ContainSingle().Which.Missing.Should().BeTrue();
    }

    private sealed class AudioKind : IContextKindProvider
    {
        public IReadOnlyList<string> Kinds { get; } = ["audio"];
        public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;
        public ContextItemSummary Describe(ContextScope scope, ContextItem item) => new("аудио", null, null, false);
        public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => [];
        public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;
    }

    [Fact]
    public async Task Где_берёт_ветку_worktree_без_числа_изменений()
    {
        Fill();
        _session.WorktreeBranch = "feat/video-editor";
        _session.WorktreePath = Path.Combine(_root, "wt");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var tail = await TailAsync();

        tail.Should().Contain("Где: ветка feat/video-editor (worktree чата)");
        tail.Should().NotContain("изменени");
    }

    [Fact]
    public async Task Чем_нет_без_основного_объекта_и_секции_нет_без_контекста_и_git()
    {
        _store.AddRef(Owner, Chat, Item("image-character", new JsonObject { ["slug"] = "anya" }, "character", ContextActor.Human), null);
        var tail = await TailAsync();
        tail.Should().Contain("С чем: ничего не выбрано").And.NotContain("Чем:");

        var empty = new Session { Id = "empty", OwnerId = Owner };
        var ctx = new PromptSessionContext(empty, Owner, null, _root);
        Contributor().IsEnabled(ctx).Should().BeFalse("контекст пуст и корень не git-репозиторий");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Contributor().IsEnabled(ctx).Should().BeTrue("проект с git получает хотя бы «Где»");
    }

    [Fact]
    public void Локальный_проект_не_читается_и_секции_нет()
    {
        Fill();
        var local = new PromptSessionContext(_session, Owner, null, _root, ServerContent: false);

        Contributor().IsEnabled(local).Should().BeFalse();
    }

    [Fact]
    public async Task Секция_едет_только_хвостом_с_ключом_заголовком_и_порядком()
    {
        Fill();
        var contributor = Contributor();
        var section = (await contributor.BuildAsync(Ctx(), "привет"))!.Sections.Single();

        section.Key.Should().Be("turn-context");
        section.Title.Should().Be("Контекст хода");
        section.InTurnTail.Should().BeTrue();
        contributor.Order.Should().Be(690);
        contributor.Group.Should().Be("misc");
    }
}
