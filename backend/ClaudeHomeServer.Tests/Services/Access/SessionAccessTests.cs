using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// Приватность проектных чатов (S2d-1, правило И.2): чат с Session.PrivateToUserId виден
/// ТОЛЬКО автору — ни другим участникам, ни владельцу проекта (решение 28.10). Проверяются
/// обе точки: чистая SessionAccess.CanSee и путь ResolveSession (через который ходят хаб,
/// GetAccessible/GetWithRight, ProjectScope и MCP chats_*). Общий чат и чат вне проекта —
/// снимок прежнего поведения; поля аддитивны: старая запись sessions.json читается штатно.
/// </summary>
public sealed class SessionAccessTests : IDisposable
{
    private const string StaticOwnerId = "owner-1";
    private const string StaticOtherId = "other-2";
    private const string StaticEditorA = "editor-a";
    private const string StaticEditorB = "editor-b";

    private readonly string _dir;
    private readonly string _ownerId;
    private readonly string _editorAId;
    private readonly string _editorBId;
    private readonly string _projectId;
    private readonly ProjectAccessService _access;

    private readonly string _privateChatOfEditorA;
    private readonly string _privateChatOfOwner;
    private readonly string _commonChatId;
    private readonly string _ownChatOutsideProjectId;

    public SessionAccessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "session_access_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
        }).Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = users.GetFirst()!.Id;
        _editorAId = users.Add("s2d-editor-a", "pwd", "user").Id;
        _editorBId = users.Add("s2d-editor-b", "pwd", "user").Id;
        foreach (var id in new[] { _editorAId, _editorBId })
            users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        _projectId = projects.Create("Общий проект", root, _ownerId, users.GetFirst()!.Username).Id;
        projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorAId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _editorBId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        var sessions = new List<Session>
        {
            new() { ProjectId = _projectId, PrivateToUserId = _editorAId, CreatedByUserId = _editorAId },
            new() { ProjectId = _projectId, PrivateToUserId = _ownerId, CreatedByUserId = _ownerId },
            new() { ProjectId = _projectId },
            new() { OwnerId = _editorAId },
        };
        _privateChatOfEditorA = sessions[0].Id;
        _privateChatOfOwner = sessions[1].Id;
        _commonChatId = sessions[2].Id;
        _ownChatOutsideProjectId = sessions[3].Id;

        _access = new ProjectAccessService(projects, new SessionsDir(sessions),
            new SharedProjectsGate(config, new FeatureFlagService(users)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // --- чистая точка SessionAccess.CanSee (без сервиса)

    private static Project OwnedProject() => new() { Name = "p", RootPath = "/srv/p", OwnerId = StaticOwnerId };
    private static Project SharedProject(params (string UserId, ProjectRole Role)[] members)
    {
        var project = OwnedProject();
        project.Members = [.. members.Select(m => new ProjectMember { UserId = m.UserId, Role = m.Role, AddedBy = StaticOwnerId })];
        return project;
    }

    private static Session PrivateProjectChat(string? privateTo) => new() { ProjectId = "p-1", PrivateToUserId = privateTo };

    [Fact]
    public void CanSee_ПриватныйЧат_АвторВидит()
    {
        var project = SharedProject((StaticEditorA, ProjectRole.Editor), (StaticEditorB, ProjectRole.Editor));
        SessionAccess.CanSee(PrivateProjectChat(StaticEditorA), project, StaticEditorA).Should().BeTrue();
    }

    [Fact]
    public void CanSee_ПриватныйЧат_ДругойРедакторНеВидит()
    {
        var project = SharedProject((StaticEditorA, ProjectRole.Editor), (StaticEditorB, ProjectRole.Editor));
        SessionAccess.CanSee(PrivateProjectChat(StaticEditorA), project, StaticEditorB).Should().BeFalse();
    }

    [Fact]
    public void CanSee_ПриватныйЧат_ВладелецПроектаНеВидит()
    {
        // Решение 28.10: владелец проекта чужой приватный чат НЕ видит, хотя проект — его
        var project = SharedProject((StaticEditorA, ProjectRole.Editor));
        SessionAccess.CanSee(PrivateProjectChat(StaticEditorA), project, StaticOwnerId).Should().BeFalse();
    }

    [Fact]
    public void CanSee_ПриватныйЧатВладельца_ВладелецВидит_УчастникиНет()
    {
        var project = SharedProject((StaticEditorA, ProjectRole.Editor));
        SessionAccess.CanSee(PrivateProjectChat(StaticOwnerId), project, StaticOwnerId).Should().BeTrue();
        SessionAccess.CanSee(PrivateProjectChat(StaticOwnerId), project, StaticEditorA).Should().BeFalse();
    }

    [Theory]
    [InlineData(StaticEditorA)]
    [InlineData(StaticOwnerId)]
    public void CanSee_ПриватныйЧат_МусорныйPrivateToUserIdНикомуНеДаёт(string? userId)
    {
        // Пустая строка — не «общий» (это выразил бы null), а неопознанный автор: fail-closed
        var project = SharedProject((StaticEditorA, ProjectRole.Editor));
        SessionAccess.CanSee(PrivateProjectChat(""), project, userId).Should().BeFalse();
    }

    [Fact]
    public void CanSee_ОбщийЧат_СнимокПрежнегоПоведения()
    {
        // PrivateToUserId null — видимость ровно как до S2d-1: доступ к проекту
        var project = SharedProject((StaticEditorA, ProjectRole.Editor), (StaticEditorB, ProjectRole.Editor));
        var chat = new Session { ProjectId = "p-1" };
        SessionAccess.CanSee(chat, project, StaticOwnerId).Should().BeTrue();
        SessionAccess.CanSee(chat, project, StaticEditorA).Should().BeTrue();
        SessionAccess.CanSee(chat, project, StaticEditorB).Should().BeTrue();
        SessionAccess.CanSee(chat, project, StaticOtherId).Should().BeFalse();
    }

    [Fact]
    public void CanSee_ПриватныйЧатБезПроекта_Никто()
    {
        // Проект не найден — резолвить владельца проектного чата нечем (fail-closed), и приватность это не меняет
        SessionAccess.CanSee(PrivateProjectChat(StaticEditorA), null, StaticEditorA).Should().BeFalse();
    }

    [Fact]
    public void CanSee_ЧатВнеПроекта_СнимокПрежнегоПоведения()
    {
        var chat = new Session { OwnerId = StaticEditorA };
        SessionAccess.CanSee(chat, null, StaticEditorA).Should().BeTrue();
        SessionAccess.CanSee(chat, null, StaticOwnerId).Should().BeFalse();
    }

    // --- путь ResolveSession: хаб, GetAccessible/GetWithRight, ProjectScope, MCP chats_* — через него

    [Fact]
    public void ResolveSession_ПриватныйЧат_АвторВидит()
    {
        _access.ResolveSession(_editorAId, _privateChatOfEditorA).Should().NotBeNull();
    }

    [Fact]
    public void ResolveSession_ПриватныйЧат_ДругойРедакторНеВидит()
    {
        _access.ResolveSession(_editorBId, _privateChatOfEditorA).Should().BeNull();
    }

    [Fact]
    public void ResolveSession_ПриватныйЧат_ВладелецПроектаНеВидит()
    {
        _access.ResolveSession(_ownerId, _privateChatOfEditorA).Should().BeNull();
    }

    [Fact]
    public void ResolveSession_ПриватныйЧатВладельца_ВладелецСоВсемиПравами_УчастникНет()
    {
        // Приватность не снимает автору его прав: владелец видит свой приватный чат на любом праве
        foreach (var right in Enum.GetValues<ProjectRight>())
            _access.ResolveSession(_ownerId, _privateChatOfOwner, right).Should().NotBeNull(right.ToString());
        _access.ResolveSession(_editorAId, _privateChatOfOwner).Should().BeNull();
    }

    [Fact]
    public void ResolveSession_ОбщийЧат_СнимокПрежнегоПоведения()
    {
        _access.ResolveSession(_ownerId, _commonChatId).Should().NotBeNull();
        _access.ResolveSession(_editorAId, _commonChatId).Should().NotBeNull();
        _access.ResolveSession(_editorBId, _commonChatId).Should().NotBeNull();
    }

    [Fact]
    public void ResolveSession_ЧатВнеПроекта_СнимокПрежнегоПоведения()
    {
        _access.ResolveSession(_editorAId, _ownChatOutsideProjectId).Should().NotBeNull();
        _access.ResolveSession(_ownerId, _ownChatOutsideProjectId).Should().BeNull();
    }

    // --- аддитивность полей: старая запись sessions.json читается штатно, новая — round-trip

    [Fact]
    public void Стор_СтараяЗаписьБезНовыхПолей_ЧитаетсяШтатно()
    {
        // Запись до появления полей: ни одного из них в JSON нет
        var json = """{"Id":"s-1","ProjectId":"p-1"}""";
        var restored = JsonSerializer.Deserialize<Session>(json);
        restored!.Id.Should().Be("s-1");
        restored.PrivateToUserId.Should().BeNull();
        restored.CreatedByUserId.Should().BeNull();
    }

    [Fact]
    public void Стор_ПриватныеПоля_РондТриПолный()
    {
        var session = new Session { ProjectId = "p-1", PrivateToUserId = StaticEditorA, CreatedByUserId = StaticEditorA };
        var json = JsonSerializer.Serialize(session);
        json.Should().Contain("PrivateToUserId").And.Contain("CreatedByUserId");
        var restored = JsonSerializer.Deserialize<Session>(json);
        restored!.PrivateToUserId.Should().Be(StaticEditorA);
        restored.CreatedByUserId.Should().Be(StaticEditorA);
    }

    private sealed class SessionsDir : ISessionDirectory
    {
        private readonly Dictionary<string, Session> _byId;
        public SessionsDir(IEnumerable<Session> sessions) => _byId = sessions.ToDictionary(s => s.Id);
        public Session? GetById(string id) => _byId.GetValueOrDefault(id);
        public IReadOnlyCollection<Session> GetAll() => _byId.Values;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }
}
