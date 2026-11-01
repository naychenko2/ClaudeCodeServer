using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// S2c-3: замок Write в <see cref="ProjectAccessService"/> цел при существующем WriteContent —
/// участнику Write закрыт (двери откроются поимённо переводом на WriteContent), WriteContent
/// редактору доступен, читателю — нет, владельцу — всё.
/// </summary>
public sealed class ProjectAccessWriteLockTests : IDisposable
{
    private readonly string _dir;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _readerId;
    private readonly string _projectId;
    private readonly ProjectAccessService _access;

    public ProjectAccessWriteLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "write_lock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
        }).Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = users.GetFirst()!.Id;
        _editorId = users.Add("lock-editor", "pwd", "user").Id;
        _readerId = users.Add("lock-reader", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _readerId })
            users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        _projectId = projects.Create("Общий проект", root, _ownerId, users.GetFirst()!.Username).Id;
        projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _readerId, Role = ProjectRole.Reader, AddedBy = _ownerId },
        ]);
        _access = new ProjectAccessService(projects, new NoSessions(),
            new SharedProjectsGate(config, new FeatureFlagService(users)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Resolve_РедакторWrite_ЗамокЗакрыт_WriteContentОткрыт()
    {
        _access.Resolve(_editorId, _projectId, ProjectRight.Write).Should().BeNull();
        _access.Resolve(_editorId, _projectId, ProjectRight.WriteContent).Should().NotBeNull();
        _access.Resolve(_editorId, _projectId, ProjectRight.Configure).Should().BeNull();
    }

    [Fact]
    public void Resolve_ЧитательНеПолучаетНиWriteContent_НиWrite_НиConfigure()
    {
        foreach (var right in new[] { ProjectRight.WriteContent, ProjectRight.Write, ProjectRight.Configure })
            _access.Resolve(_readerId, _projectId, right).Should().BeNull(right.ToString());
        _access.Resolve(_readerId, _projectId, ProjectRight.Read).Should().NotBeNull();
    }

    [Fact]
    public void Resolve_Владелец_ПолучаетВсеПрава()
    {
        foreach (var right in Enum.GetValues<ProjectRight>())
            _access.Resolve(_ownerId, _projectId, right).Should().NotBeNull(right.ToString());
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }
}
