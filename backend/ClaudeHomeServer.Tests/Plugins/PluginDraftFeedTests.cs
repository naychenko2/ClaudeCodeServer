using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// Запись черновика в ленте чата (ADR-019 §2, ADR-031 шаг 4.5a): успешный draft_create
// дописывает в ленту чата-вызывателя якорь module_record (module "plugins", recordType
// "plugin_draft", data { slug }) — карточка черновика. Идемпотентно: не больше одной записи
// на (сессию, slug) — повторный create (после reject) дубль не дописывает; draft_validate/
// draft_write_file записей не пишут (дальнейшие изменения карточка получает по
// plugin_draft_changed). Нет сессии-вызывателя — записи нет и ошибки нет; сбой ленты
// не роняет создание (черновик уже на диске).
public class PluginDraftFeedTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string Stranger = "owner-b";
    private const string ChatId = "chat-1";
    private const string ProjectId = "proj-1";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginDraftService _drafts;
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly InMemoryFeed _feed = new();

    public PluginDraftFeedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_feed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _drafts = new PluginDraftService(_store, new PluginPackageValidator());
        AddChat(ChatId, Owner, ProjectId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    private void AddChat(string id, string owner, string? projectId) =>
        _sessions[id] = new Session { Id = id, OwnerId = owner, ProjectId = projectId };

    private PluginsToolset Toolset(IFeatureFlagGate? flags = null, IConfiguration? config = null)
    {
        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) =>
                _sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(It.IsAny<string>()))
            .Returns((string id) => id == ProjectId
                ? new Project { Id = id, OwnerId = Owner, DeviceId = null }
                : null);
        return new PluginsToolset(accessor.Object, projects.Object, _drafts, flags, config, _feed);
    }

    private static McpToolCallContext Ctx(string owner = Owner, string? tail = ChatId) =>
        new(owner, tail, tail);

    private static Task<McpToolCallResult> Call(PluginsToolset toolset, string tool,
        JsonObject? args = null, McpToolCallContext? ctx = null) =>
        toolset.CallAsync(tool, args ?? new JsonObject(), ctx ?? Ctx(), default);

    // ── draft_create: ровно одна запись с якорем и данными { slug } ─────────────

    [Fact]
    public async Task DraftCreate_ПишетОднуЗапись_СЯкоремИДаннымиSlug()
    {
        var toolset = Toolset();

        var result = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        result.IsError.Should().BeFalse();
        _feed.Records.Should().HaveCount(1, "draft_create дописывает ровно один якорь");
        var (sessionId, record) = _feed.Records[0];
        sessionId.Should().Be(ChatId, "запись — в чат-вызыватель (хвост маршрута)");
        record.Module.Should().Be("plugins", "module — ключ подсистемы");
        record.RecordType.Should().Be("plugin_draft", "recordType — якорь черновика");
        record.Data.Should().NotBeNull("data обязано быть задано");
        record.Data!.Value.GetRawText().Should().Be("""{"slug":"my-plugin"}""",
            "в данных только slug: содержимого файлов, прав и хостовых путей нет");
        record.Fallback.Should().NotBeNullOrWhiteSpace("fallback — тихая строка, если модуль выключен");
        record.Timestamp.Should().NotBeNull();
    }

    // ── Идемпотентность: повторные операции дубль не дописывают ─────────────────

    [Fact]
    public async Task ПовторныйValidate_ДубльНеПишет()
    {
        var toolset = Toolset();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" })).IsError.Should().BeFalse();

        var validate = await Call(toolset, PluginsToolset.ToolValidate, new JsonObject { ["slug"] = "my-plugin" });

        validate.IsError.Should().BeFalse();
        _feed.Records.Should().HaveCount(1, "draft_validate записей не пишет — карточка уже есть");
    }

    [Fact]
    public async Task ПовторныйCreate_ПослеReject_ДубльНеПишет()
    {
        var toolset = Toolset();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" })).IsError.Should().BeFalse();

        // Человек отклонил черновик (перенос в _rejected) — каталог _drafts пуст,
        // агент создаёт черновик заново: якорь в ленте один, не два.
        _drafts.Reject(Owner, "my-plugin").Ok.Should().BeTrue();
        var again = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        again.IsError.Should().BeFalse("после reject черновик можно создать заново");
        _feed.Records.Should().HaveCount(1, "идемпотентность: не больше одной записи на (сессию, slug)");
    }

    [Fact]
    public async Task ВторойCreate_ДругойSlug_ВтораяЗапись()
    {
        var toolset = Toolset();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "plugin-a" })).IsError.Should().BeFalse();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "plugin-b" })).IsError.Should().BeFalse();

        _feed.Records.Should().HaveCount(2, "каждый новый черновик — свой якорь");
        _feed.Records.Select(r => r.Record.Data!.Value.GetRawText()).Should()
            .ContainEquivalentOf("""{"slug":"plugin-a"}""")
            .And.ContainEquivalentOf("""{"slug":"plugin-b"}""");
    }

    // ── Нет сессии-вызывателя: записи нет и ошибки нет ───────────────────────────

    [Fact]
    public async Task НетСессии_ЗаписиНет_ОшибкиНет()
    {
        var toolset = Toolset();

        // Хвост маршрута null — вызов без контекста чата: создание не падает, записи нет.
        var result = await Call(toolset, PluginsToolset.ToolCreate,
            new JsonObject { ["slug"] = "my-plugin" }, Ctx(tail: null));

        result.Should().NotBeNull("вызов завершён без исключения");
        _feed.Records.Should().BeEmpty("без сессии-вызывателя запись некуда писать");
    }

    // ── Изоляция: чужой владелец записи не получает ──────────────────────────────

    [Fact]
    public async Task ЧужойВладелец_ЗаписейНеПолучает()
    {
        AddChat("chat-b", Stranger, ProjectId);
        var toolset = Toolset();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" })).IsError.Should().BeFalse();

        var stranger = await Call(toolset, PluginsToolset.ToolCreate,
            new JsonObject { ["slug"] = "my-plugin" }, Ctx(owner: Stranger, tail: "chat-b"));

        stranger.IsError.Should().BeFalse("у владельца B свой каталог — свой черновик");
        _feed.Records.Should().HaveCount(2);
        _feed.Records.Select(r => r.SessionId).Should().ContainEquivalentOf(ChatId)
            .And.ContainEquivalentOf("chat-b", "каждый владелец — запись в СВОЁМ чате, не в чужом");
    }

    // ── Гейты: флаг/подсистема выключены — записей нет ───────────────────────────

    [Fact]
    public async Task ФлагВыключен_ЗаписейНет()
    {
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, PluginsToolset.BuildYourselfFlag)).Returns(false);
        var toolset = Toolset(flags: flags.Object);

        var result = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        result.IsError.Should().BeTrue("флаг выключен — честный отказ");
        _feed.Records.Should().BeEmpty("черновик не создан — записи нет");
    }

    [Fact]
    public async Task ПодсистемаВыключена_ЗаписейНет()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "false",
            })
            .Build();
        var toolset = Toolset(config: config);

        var result = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        result.IsError.Should().BeTrue("подсистема выключена — честный отказ");
        _feed.Records.Should().BeEmpty("черновик не создан — записи нет");
    }

    // ── Шов: IChatFeed как в Main (адаптер ChatFeed) ─────────────────────────────

    // Памятный фид: AppendRecordAsync копит, HasRecordAsync сканирует накопленное —
    // тот же контракт, что у адаптера ChatFeed поверх SessionManager.
    private sealed class InMemoryFeed : IChatFeed
    {
        public List<(string SessionId, StoredModuleRecord Record)> Records { get; } = [];

        public Task<bool> AppendRecordAsync(string sessionId, StoredModuleRecord record, CancellationToken ct = default)
        {
            lock (Records) Records.Add((sessionId, record));
            return Task.FromResult(true);
        }

        public Task<bool> HasRecordAsync(string sessionId, string module, string recordType,
            Func<JsonElement, bool>? match = null, CancellationToken ct = default)
        {
            lock (Records)
            {
                return Task.FromResult(Records.Any(r => r.SessionId == sessionId
                    && r.Record.Module == module && r.Record.RecordType == recordType
                    && (match is null || (r.Record.Data is { } d && match(d)))));
            }
        }
    }
}
