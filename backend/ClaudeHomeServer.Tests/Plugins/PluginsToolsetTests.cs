using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// MCP-тулсет plugins (ADR-031, шаг 4.1, пачка D): агент собирает плагин build-it-yourself
// в черновик владельца. Тестируем контракт тулсета: состав (5 инструментов, без установки/
// выдачи прав), владелец из JWT (не из аргументов), изоляция чужого владельца, и отказы
// на вызове (флаг, подсистема, локальный проект) — честный отказ, не исключение.
public class PluginsToolsetTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string Stranger = "owner-b";
    private const string ChatId = "chat-1";
    private const string ProjectId = "proj-1";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginDraftService _drafts;
    private readonly Dictionary<string, Session> _sessions = new();

    public PluginsToolsetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_mcp_" + Guid.NewGuid().ToString("N"));
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

    // deviceId = null — серверный проект (плагины доступны); deviceId != null — локальный (MVP: выключены).
    private PluginsToolset Toolset(string? deviceId = null, IFeatureFlagGate? flags = null,
        IConfiguration? config = null)
    {
        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) =>
                _sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(It.IsAny<string>()))
            .Returns((string id) => id == ProjectId
                ? new Project { Id = id, OwnerId = Owner, DeviceId = deviceId }
                : null);
        return new PluginsToolset(accessor.Object, projects.Object, _drafts, flags, config);
    }

    private static McpToolCallContext Ctx(string owner = Owner, string tail = ChatId) =>
        new(owner, tail, tail);

    private static Task<McpToolCallResult> Call(PluginsToolset toolset, string tool,
        JsonObject? args = null, McpToolCallContext? ctx = null) =>
        toolset.CallAsync(tool, args ?? new JsonObject(), ctx ?? Ctx(), default);

    // ── Состав: 5 инструментов, без установки/согласия/выдачи прав ─────────────

    [Fact]
    public void Состав_ПятьИнструментов_БезУстановкиИВыдачиПрав()
    {
        var names = Toolset().ToolsFor(Ctx()).Select(t => t.Name).ToList();

        names.Should().BeEquivalentTo([
            PluginsToolset.ToolCreate, PluginsToolset.ToolWriteFile, PluginsToolset.ToolValidate,
            PluginsToolset.ToolPreview, PluginsToolset.ToolDeleteFile]);
        names.Should().NotContain(n => n.Contains("install") || n.Contains("approve")
            || n.Contains("grant") || n.Contains("enable"),
            "принять и выдать права может только человек кнопкой (шаг 4.3)");
    }

    // ── Владелец из JWT, не из аргументов ───────────────────────────────────────

    [Fact]
    public async Task Владелец_ИзJwt_Черновики_Раздельны()
    {
        AddChat("chat-b", Stranger, ProjectId);
        var toolset = Toolset();

        // Оба владельца создают черновик с одним slug: каждый — в своём каталоге (владелец из JWT).
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" })).IsError.Should().BeFalse();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" },
            Ctx(owner: Stranger, tail: "chat-b"))).IsError.Should().BeFalse(
            "черновик владельца B — в его каталоге, не «уже существует»");

        File.Exists(Path.Combine(_store.DraftDir(Owner, "my-plugin"), "ccs-plugin.json")).Should().BeTrue();
        File.Exists(Path.Combine(_store.DraftDir(Stranger, "my-plugin"), "ccs-plugin.json")).Should().BeTrue();
    }

    // ── Изоляция: чужой владелец не видит чужой черновик ───────────────────────

    [Fact]
    public async Task ЧужойВладелец_ЧерновикНеНайден()
    {
        AddChat("chat-b", Stranger, ProjectId);
        var toolset = Toolset();
        // Владелец A создал черновик; владелец B (свой чат) того же slug — нет (у B свой пустой каталог).
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" })).IsError.Should().BeFalse();

        var result = await Call(toolset, PluginsToolset.ToolValidate,
            new JsonObject { ["slug"] = "my-plugin" }, Ctx(owner: Stranger, tail: "chat-b"));

        result.IsError.Should().BeTrue("черновик владельца A невидим владельцу B");
        result.Text.Should().Contain("не найден");
    }

    // ── Отказы на вызове: флаг, подсистема, локальный проект ─────────────────────

    [Fact]
    public async Task ФлагВыключен_ОтказСПричиной()
    {
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.PluginsMvp)).Returns(true);
        flags.Setup(f => f.IsEnabled(Owner, PluginsToolset.BuildYourselfFlag)).Returns(false);
        var toolset = Toolset(flags: flags.Object);

        var result = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Собрать плагин", "отказ с понятной причиной, не исключение");
        File.Exists(Path.Combine(_store.DraftDir(Owner, "my-plugin"), "ccs-plugin.json")).Should().BeFalse(
            "флаг выключен — черновик не создан");
    }

    [Fact]
    public async Task ПодсистемаВыключена_ОтказСПричиной()
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

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Подсистема плагинов выключена");
    }

    [Fact]
    public async Task ЛокальныйПроект_ОтказСПричиной()
    {
        var toolset = Toolset(deviceId: "dev-1");

        var result = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("локального проекта", "MVP: каталог пакетов и секреты на устройство не едут");
        File.Exists(Path.Combine(_store.DraftDir(Owner, "my-plugin"), "ccs-plugin.json")).Should().BeFalse();
    }

    // ── plugins-mvp off, plugins-build-yourself on → draft_create отказ (дефект dcbd3609) ──
    //
    // Черновик без возможности установки бессмыслен: человек не увидит его (REST 403).
    // Отказ агенту — понятным текстом. Состав tools/list не меняется.

    [Fact]
    public async Task MvpOff_BuildYourselfOn_DraftCreate_Отказ()
    {
        var flags = new Mock<IFeatureFlagGate>();
        // plugins-mvp off, plugins-build-yourself on.
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.PluginsMvp)).Returns(false);
        flags.Setup(f => f.IsEnabled(Owner, PluginsToolset.BuildYourselfFlag)).Returns(true);
        var toolset = Toolset(flags: flags.Object);

        var result = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Плагины отключены", "отказ с понятной причиной");
        File.Exists(Path.Combine(_store.DraftDir(Owner, "my-plugin"), "ccs-plugin.json")).Should().BeFalse(
            "mvp off — черновик не создан");
    }

    // ── Чужая/несуществующая сессия: fail-closed ─────────────────────────────────

    [Fact]
    public async Task ЧужаяСессия_Отказ()
    {
        var toolset = Toolset();

        // Сессия принадлежит Owner; вызов от Stranger (JWT) — чужая сессия → отказ.
        var result = await Call(toolset, PluginsToolset.ToolCreate,
            new JsonObject { ["slug"] = "my-plugin" }, Ctx(owner: Stranger));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Чат не найден");
    }

    // ── Повторный create того же slug → ошибка, а не перезапись ─────────────────

    [Fact]
    public async Task ПовторныйCreate_Ошибка()
    {
        var toolset = Toolset();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" })).IsError.Should().BeFalse();

        var second = await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" });

        second.IsError.Should().BeTrue();
        second.Text.Should().Contain("already-exists");
    }

    // ── Отклонённые черновики: агентский тулсет их не видит (шаг 4.3c) ─────────

    [Fact]
    public async Task ОтклонённыйЧерновик_АгентНеВидит()
    {
        var toolset = Toolset();
        (await Call(toolset, PluginsToolset.ToolCreate, new JsonObject { ["slug"] = "my-plugin" })).IsError.Should().BeFalse();

        // Отклонить черновик (перенос в _rejected) — агентский тулсет его больше не найдёт.
        _drafts.Reject(Owner, "my-plugin").Ok.Should().BeTrue();

        var result = await Call(toolset, PluginsToolset.ToolValidate, new JsonObject { ["slug"] = "my-plugin" });
        result.IsError.Should().BeTrue("отклонённый черновик невидим агенту (не в _drafts)");
        result.Text.Should().Contain("не найден");
    }
}
