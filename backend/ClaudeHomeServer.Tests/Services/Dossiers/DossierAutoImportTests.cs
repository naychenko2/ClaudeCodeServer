using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Интеграционные тесты автоимпорта паспортов по новому tip ветки ccs/dossiers/v1
// (задача 3 спринта автоматизации): настоящий git CLI и реальные сторы на temp-конфиге,
// ветка пишется тем же plumbing-методом WriteDossiersBranchAsync, что и при экспорте.
// Сценарий «вторая машина / сосед по общей папке»: ветка в репозитории меняется
// «извне» (pull/сосед), поллер обязан заметить новый tip и подтянуть записи — и только
// чтением из git, без checkout/fetch/pull.
[Trait("Category", "Slow")]
public class DossierAutoImportTests : IDisposable
{
    private readonly string _temp;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly FeatureFlagService _flags;
    private readonly DossierStore _store;
    private readonly DossierCaptureState _state;
    private readonly GitService _git = new(TestLauncherFactory.Instance);
    private readonly DossierAutoImporter _auto;

    public DossierAutoImportTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_autoimport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_temp, "projects.json"),
            })
            .Build();

        _users = new UserStore(_config,
            new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _flags = new FeatureFlagService(_users);
        _store = new DossierStore(_config);
        _state = new DossierCaptureState(_config);
        _auto = new DossierAutoImporter(_projects, _store, _state, _git,
            new InstanceSecretsProvider(_config), _flags);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // --- Фикстуры (по образцу DossierImporterTests) ---

    private async Task<(Project Project, string OwnerId)> MkRepoProjectAsync(string name, bool flagOn)
    {
        var user = _users.Add("autoimp-" + name, "password-123456", "user");
        if (flagOn)
            _users.SetFeatureFlag(user.Id, FeatureFlagKeys.ChangeDossiersRecall, true).Should().BeTrue();

        var dir = Path.Combine(_temp, name);
        Directory.CreateDirectory(dir);
        await _git.InitAsync(null, dir);
        await _git.RunAsync(null, dir, ["config", "user.email", "dossiers@test"]);
        await _git.RunAsync(null, dir, ["config", "user.name", "Тест Досье"]);
        await File.WriteAllTextAsync(Path.Combine(dir, "readme.md"), "содержимое\n");
        await _git.StageAllAsync(null, dir);
        await _git.CommitAsync(null, dir, "начальный коммит");
        return (_projects.Create(name, dir, user.Id, user.Username), user.Id);
    }

    private static ChangeDossier Dossier(string projectId, string sha, string subject) => new()
    {
        OwnerId = "branch-owner",
        ProjectId = projectId,
        CommitSha = sha,
        CommitSubject = subject,
        CommittedAt = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero),
        SessionId = "sess-src",
        Why = "почему изменение сделано",
        Decisions = ["решили так"],
    };

    private static readonly JsonSerializerOptions IndexOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Ветка паспортов, как её написал бы экспортёр соседней машины
    private async Task WriteBranchAsync(Project p, params ChangeDossier[] dossiers)
    {
        var files = new List<GitDossierFile>();
        var entries = new List<DossierIndexEntry>();
        foreach (var d in dossiers)
        {
            var path = DossierGitExporter.DossierPath(d.CommittedAt, d.CommitSha, d.CommitSubject);
            files.Add(new GitDossierFile(path, DossierGitExporter.FormatDossier(d, [])));
            entries.Add(new DossierIndexEntry(d.CommitSha, path, d.CommitSubject, d.CommittedAt,
                Discussion: null, TaskId: d.TaskId, SupersededSha: d.SupersededSha));
        }
        files.Add(new GitDossierFile("index.json",
            JsonSerializer.Serialize(new DossierBranchIndex(1, entries), IndexOpts)));
        await _git.WriteDossiersBranchAsync(p.OwnerId, p.RootPath, files, "test: ветка паспортов");
    }

    private Task TickAsync() => _auto.TickAsync();

    // --- (а) новый tip: записи добавлены, tip зафиксирован в state ---

    [Fact]
    public async Task НовыйTip_ИмпортируетЗаписиИФиксируетState()
    {
        var (p, owner) = await MkRepoProjectAsync("repo_tip1", flagOn: true);
        _projects.Update(p.Id, name: null, rootPath: null, autoImportDossiers: true).AutoImportDossiers
            .Should().BeTrue("тумблер проекта выставлен через Update (проводка эндпоинта PUT)");
        await WriteBranchAsync(p, Dossier(p.Id, "aa11aa11", "feat: паспорт из ветки"));

        await TickAsync();

        var imported = _store.List(owner, p.Id).Should().ContainSingle().Subject;
        imported.Origin.Should().Be(DossierOrigin.Imported);
        imported.CommitSha.Should().Be("aa11aa11");
        var tip = await _git.GetDossiersTipAsync(owner, p.RootPath);
        _state.Get(DossierCaptureState.ImportKey(owner, p.Id)).Should().Be(tip!.CommitSha,
            "tip зафиксирован — повторный тик того же состояния не зовёт импортёр");
    }

    // --- (б) повторный тик того же tip: ничего не добавляется ---

    [Fact]
    public async Task ПовторныйТик_ТогоЖеTip_НичегоНеДобавляет()
    {
        var (p, owner) = await MkRepoProjectAsync("repo_tip2", flagOn: true);
        _projects.Update(p.Id, name: null, rootPath: null, autoImportDossiers: true);
        await WriteBranchAsync(p, Dossier(p.Id, "bb22bb22", "feat: паспорт один"));
        await TickAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_temp, "dossiers", owner, p.Id + ".json"));

        await TickAsync();

        _store.List(owner, p.Id).Should().ContainSingle("дублей не появилось");
        (await File.ReadAllBytesAsync(Path.Combine(_temp, "dossiers", owner, p.Id + ".json")))
            .Should().Equal(bytes, "no-op тик не переписывает файл стора");
    }

    // --- (в) ветка выросла (новый коммит ветки): следующий тик подтягивает только новое ---

    [Fact]
    public async Task ВеткаВырослаСледующимТиком_ПодтягиваетсяНовое()
    {
        var (p, owner) = await MkRepoProjectAsync("repo_tip3", flagOn: true);
        _projects.Update(p.Id, name: null, rootPath: null, autoImportDossiers: true);
        await WriteBranchAsync(p, Dossier(p.Id, "cc33cc33", "feat: паспорт один"));
        await TickAsync();

        // «Сосед» докладывает в ветку второй паспорт — tip меняется
        await WriteBranchAsync(p,
            Dossier(p.Id, "cc33cc33", "feat: паспорт один"),
            Dossier(p.Id, "dd44dd44", "feat: паспорт два"));
        await TickAsync();

        var shas = _store.List(owner, p.Id).Select(d => d.CommitSha).ToList();
        shas.Should().HaveCount(2, "новый tip дал только новую запись, старая на месте");
        shas.Should().Equal("cc33cc33", "dd44dd44");
    }

    // --- (г) выключенный тумблер: ветка есть, но ничего не импортируется ---

    [Fact]
    public async Task ТумблерВыключен_НичегоНеИмпортируется()
    {
        var (p, owner) = await MkRepoProjectAsync("repo_off", flagOn: true);
        // AutoImportDossiers по умолчанию false — тумблер не включаем
        await WriteBranchAsync(p, Dossier(p.Id, "ee55ee55", "feat: паспорт без тумблера"));

        await TickAsync();

        _store.List(owner, p.Id).Should().BeEmpty("тумблер проекта выключен — ветку не наблюдаем");
        _state.Get(DossierCaptureState.ImportKey(owner, p.Id)).Should().BeNull(
            "state не фиксируется: после включения тумблера первый тик импортирует ветку целиком");
    }

    // --- (д) флаг владельца выключен: та же граница, что у ручного POST /dossiers/import ---

    [Fact]
    public async Task ФлагВладельцаВыключен_НичегоНеИмпортируется()
    {
        var (p, owner) = await MkRepoProjectAsync("repo_noflag", flagOn: false);
        _projects.Update(p.Id, name: null, rootPath: null, autoImportDossiers: true);
        await WriteBranchAsync(p, Dossier(p.Id, "ff66ff66", "feat: паспорт без флага"));

        await TickAsync();

        _store.List(owner, p.Id).Should().BeEmpty("флаг change-dossiers-recall выключен — автоимпорта нет");
        _state.Get(DossierCaptureState.ImportKey(owner, p.Id)).Should().BeNull();
    }

    // --- (е) ветки нет: тик молчит и не фиксирует state впустую; приезд ветки позже
    // (pull соседа) подхватывается следующим тиком ---

    [Fact]
    public async Task ВеткиНет_ПозжеВетка_Импортируется()
    {
        var (p, owner) = await MkRepoProjectAsync("repo_nobranch", flagOn: true);
        _projects.Update(p.Id, name: null, rootPath: null, autoImportDossiers: true);

        await TickAsync();   // ветки ещё нет
        _store.List(owner, p.Id).Should().BeEmpty();
        _state.Get(DossierCaptureState.ImportKey(owner, p.Id)).Should().BeNull(
            "state без ветки не пишется — иначе первый же tip ветки посчитали бы уже внесённым");

        await WriteBranchAsync(p, Dossier(p.Id, "99aa99aa", "feat: ветка приехала позже"));
        await TickAsync();

        _store.List(owner, p.Id).Should().ContainSingle().Which.CommitSha.Should().Be("99aa99aa");
    }
}
