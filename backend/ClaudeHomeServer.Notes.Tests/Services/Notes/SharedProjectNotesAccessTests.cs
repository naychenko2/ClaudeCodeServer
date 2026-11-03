using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Notes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Заметки общего проекта для участника (shared-projects, план S1-3b). Проверяется то, что
/// в этом шаге и решает доступ: источники заметок собирает точка доступа IProjectAccess
/// (роль по членству + гейт «рубильник и флаг», S1-2), а корень проектного источника —
/// notes/ самого проекта: он один на всех и лежит у арендатора, а не выводится из id
/// вызывающего. Личный vault при этом остаётся личным с обеих сторон.
///
/// Край HTTP здесь — тип отказа: маппинг делает NotesController — KeyNotFoundException → 404
/// (NotFound), UnauthorizedAccessException → 403 (Forbid); см. NotesController.cs:101-102
/// (вложение), :458-459 (Create), :472 (Update), :485 (Delete). Полный хост в этом тестовом
/// проекте не поднимается по его построению: ClaudeHomeServer.Notes.Tests.csproj — «Тесты Notes —
/// чистый парсинг / файловая система», TestWebApplicationFactory здесь нет.
/// </summary>
public class SharedProjectNotesAccessTests : IDisposable
{
    private const string ProjectNote = "Заметка проекта";
    private const string OwnerPersonal = "Личное владельца";
    private const string MemberPersonal = "Личное участника";

    private readonly string _dir;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _readerId;
    private readonly string _editorId;
    private readonly string _strangerId;

    public SharedProjectNotesAccessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shared_notes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var config = Config(instanceEnabled: true);
        _users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        // При пустом users.json UserStore создаёт дефолтного admin — он и есть владелец проекта
        _ownerId = _users.GetFirst()!.Id;
        _readerId = _users.Add("s13b-reader", "pwd", "user").Id;
        _editorId = _users.Add("s13b-editor", "pwd", "user").Id;
        _strangerId = _users.Add("s13b-stranger", "pwd", "user").Id;
        // Флаг «Общие проекты» включён у участников (без него участник — посторонний, см. отдельный тест)
        foreach (var id in new[] { _readerId, _editorId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(config, _users, new AppSettingsService(config));

        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        File.WriteAllText(Path.Combine(root, "notes", ProjectNote + ".md"), $"# {ProjectNote}\n\nТекст проектной заметки.\n");
        _projectId = _projects.Create("Общий проект", root, _ownerId, _users.GetFirst()!.Username).Id;

        // Личные vault'ы владельца и участника — по заметке в каждом (тест на несмешение)
        WritePersonal(_ownerId, OwnerPersonal);
        WritePersonal(_readerId, MemberPersonal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private IConfiguration Config(bool instanceEnabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = instanceEnabled ? "true" : "false",
        }).Build();

    /// <summary>
    /// Новый экземпляр сервиса на каждое изменение состава (роль, флаг, рубильник): кэш модели
    /// в NotesService держится 2 с (NotesService.cs:CacheTtl), и на старом экземпляре следующий
    /// вызов вернул бы модель, построенную ДО изменения.
    /// </summary>
    private NotesService Service(bool instanceEnabled = true)
    {
        var config = Config(instanceEnabled);
        return new NotesService(_projects, config, NullLogger<NotesService>.Instance, null,
            new ProjectAccessService(_projects, new NoSessions(),
                new SharedProjectsGate(config, new FeatureFlagService(_users))));
    }

    private string AddMember(string role)
    {
        var memberId = role == "reader" ? _readerId : _editorId;
        var members = _projects.GetById(_projectId)!.Members?.ToList() ?? [];
        members.Add(new ProjectMember
        {
            UserId = memberId,
            Role = role == "reader" ? ProjectRole.Reader : ProjectRole.Editor,
            AddedBy = _ownerId,
        });
        _projects.SetMembers(_projectId, members);
        return memberId;
    }

    private void WritePersonal(string userId, string title)
    {
        var dir = Path.Combine(_dir, "notes", userId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, title + ".md"), $"# {title}\n");
    }

    // id проектной заметки глазами владельца: он видит свой проект всегда. id построен из
    // (источник, путь) и не зависит от того, кто смотрит — тот же id обязан дать и участник.
    private string ProjectNoteId() =>
        Service().GetSummaries(_ownerId, _projectId, null).Single(n => n.Title == ProjectNote).Id;

    // --- Чтение: 200 ---

    [Theory]
    [InlineData("reader")]
    [InlineData("editor")]
    public void Участник_ЧитаетЗаметкуПроекта_СписокДетальИИсточник(string role)
    {
        var memberId = AddMember(role);
        var sut = Service();

        var list = sut.GetSummaries(memberId, _projectId, null);
        list.Should().ContainSingle(n => n.Title == ProjectNote);

        var id = list.Single(n => n.Title == ProjectNote).Id;
        id.Should().Be(ProjectNoteId(), "id заметки — из (источник, путь), а не из смотрящего");

        var detail = sut.GetDetail(memberId, id);
        detail.Should().NotBeNull();
        detail!.Content.Should().Contain("Текст проектной заметки.");
        detail.Source.Should().Be(_projectId);

        // Источник проекта — в перечне источников участника: его заметки ему доступны
        sut.GetSources(memberId).Should().Contain(s => s.Key == _projectId);
    }

    // --- Запись читателя: 403 (редактор с S2c-7 пишет — SharedProjectNotesWriteContentTests) ---

    [Fact]
    public void Читатель_ПишетВПроект_Отказ403()
    {
        var memberId = AddMember("reader");
        var sut = Service();
        var id = ProjectNoteId();

        Action create = () => sut.Create(memberId, new CreateNoteRequest("Новая в проекте", "тело", _projectId));
        Action update = () => sut.Update(memberId, id, new UpdateNoteRequest(Content: "правка участника"));
        Action delete = () => sut.Delete(memberId, id);

        // «Пишет» = право WriteContent: у читателя его нет (ProjectAccess.cs:89 — Reader только Read)
        create.Should().Throw<UnauthorizedAccessException>();
        update.Should().Throw<UnauthorizedAccessException>();
        delete.Should().Throw<UnauthorizedAccessException>();

        // Файл проекта не изменился ни одной из этих попыток
        File.ReadAllText(Path.Combine(_projects.GetById(_projectId)!.RootPath!, "notes", ProjectNote + ".md"))
            .Should().Contain("Текст проектной заметки.");
    }

    // --- Запись владельца: 200, и файл — в папке проекта ---

    [Fact]
    public void Владелец_СоздаётЗаметкуВПроект_ФайлВПапкеПроекта()
    {
        var memberId = AddMember("reader"); // проект остаётся общим: корень один на всех
        var sut = Service();

        var created = sut.Create(_ownerId, new CreateNoteRequest("Заметка владельца", "тело", _projectId));
        created.Source.Should().Be(_projectId);

        // Проектная заметка лежит в папке проекта, а не в личном vault'е создавшего:
        // иначе она была бы видна только владельцу (мутация c3 бьёт ровно здесь)
        File.Exists(Path.Combine(_projects.GetById(_projectId)!.RootPath!, "notes", "Заметка владельца.md"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_dir, "notes", _ownerId, "Заметка владельца.md")).Should().BeFalse();

        // И её видит участник: тот же файл, тот же источник
        sut.GetSummaries(_readerId, _projectId, null).Should().Contain(n => n.Title == "Заметка владельца");
    }

    // --- Посторонний: 404 ---

    [Fact]
    public void Посторонний_НеВидитЗаметкуПроекта_Отказ404()
    {
        var sut = Service();
        var id = ProjectNoteId();

        sut.GetSummaries(_strangerId, _projectId, null).Should().BeEmpty();
        sut.GetDetail(_strangerId, id).Should().BeNull();
        sut.GetSources(_strangerId).Should().NotContain(s => s.Key == _projectId);

        Action create = () => sut.Create(_strangerId, new CreateNoteRequest("Чужая", "тело", _projectId));
        Action update = () => sut.Update(_strangerId, id, new UpdateNoteRequest(Content: "правка"));
        create.Should().Throw<KeyNotFoundException>();
        update.Should().Throw<KeyNotFoundException>();
    }

    // --- Участник с выключенным флагом: 404 ---

    [Fact]
    public void ФлагУчастникаВыключен_ЗаметкаПроектаНеВидна_Отказ404()
    {
        var memberId = AddMember("reader");
        _users.SetFeatureFlag(memberId, FeatureFlagKeys.SharedProjects, false);
        var sut = Service();

        sut.GetSummaries(memberId, _projectId, null).Should().BeEmpty();
        sut.GetSources(memberId).Should().NotContain(s => s.Key == _projectId);

        Action create = () => sut.Create(memberId, new CreateNoteRequest("Чужая", "тело", _projectId));
        create.Should().Throw<KeyNotFoundException>();
    }

    // --- Выключенный рубильник инстанса: 404 ---

    [Fact]
    public void РубильникИнстансаВыключен_ЗаметкаПроектаНеВидна_Отказ404()
    {
        var memberId = AddMember("reader");
        var sut = Service(instanceEnabled: false);

        sut.GetSummaries(memberId, _projectId, null).Should().BeEmpty();
        sut.GetSources(memberId).Should().NotContain(s => s.Key == _projectId);

        Action create = () => sut.Create(memberId, new CreateNoteRequest("Чужая", "тело", _projectId));
        create.Should().Throw<KeyNotFoundException>();
    }

    // --- Личные vault'ы не смешиваются ---

    [Fact]
    public void ЛичныйVaultУчастникаИВладелца_НеСмешиваются()
    {
        var memberId = AddMember("reader");
        var sut = Service();

        // Участник: своё личное и проект — но не личное владельца
        var memberView = sut.GetSummaries(memberId, null, null);
        memberView.Should().Contain(n => n.Title == MemberPersonal);
        memberView.Should().Contain(n => n.Title == ProjectNote);
        memberView.Should().NotContain(n => n.Title == OwnerPersonal);

        // Владелец: своё личное и свой проект — но не личное участника
        var ownerView = sut.GetSummaries(_ownerId, null, null);
        ownerView.Should().Contain(n => n.Title == OwnerPersonal);
        ownerView.Should().Contain(n => n.Title == ProjectNote);
        ownerView.Should().NotContain(n => n.Title == MemberPersonal);

        // Источников «Личный» у каждого ровно один — свой
        foreach (var userId in new[] { memberId, _ownerId })
            sut.GetSources(userId).Count(s => s.Key == "personal").Should().Be(1);
    }

    // --- Уборка истёкших заметок — запись: чужой проект в неё не попадает ---

    [Fact]
    public void УборкаИстёкших_УчастникНеТронетЧужойПроект()
    {
        var memberId = AddMember("reader");
        var expiredNote = Path.Combine(_projects.GetById(_projectId)!.RootPath!, "notes", "Истекшая.md");
        File.WriteAllText(expiredNote, "---\nexpires: 2020-01-01T00:00:00Z\n---\n# Истекшая\n");

        var sut = Service();
        var now = DateTime.UtcNow;

        // Участник не считает чужие проектные заметки своими на удаление: иначе 403
        // посреди фоновой уборки остановил бы очистку его собственных заметок
        sut.GetExpiredNotes(memberId, now).Should().NotContain(n => n.Title == "Истекшая");

        // Владелец — cleaning-оператор своего проекта: он её видит
        sut.GetExpiredNotes(_ownerId, now).Should().Contain(n => n.Title == "Истекшая");
    }

    // Чаты в этом сценарии не участвуют: доступ решается по проекту.
    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }
}
