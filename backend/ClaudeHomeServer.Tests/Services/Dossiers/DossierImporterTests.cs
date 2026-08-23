using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Интеграционные тесты импорта паспортов из ветки ccs/dossiers/v1 (этап 4 «Историй
// решений», волна 2): настоящий git CLI — ветка пишется тем же plumbing-методом
// WriteDossiersBranchAsync, что и при экспорте, содержимое файлов — реальным
// DossierGitExporter.FormatDossier (контракт «что экспорт написал, то импорт прочитал»);
// SessionManager не нужен — фильтр сессий относится к выгрузке. Общая папка репо у двух
// владельцев — штатный сценарий CLAUDE.md, он же модель «подтянули репо на новую машину».
[Trait("Category", "Slow")]
public class DossierImporterTests : IDisposable
{
    private const string Owner = "dossier-owner";
    private const string Owner2 = "dossier-owner-2";

    // Как в DossierGitExportTests: длиннее MinExactSecretLength и не матчится regex-ами —
    // маскирует только точное значение из InstanceSecretsProvider
    private const string Secret = "ccs-import-secret-zq81xw40v7";

    private readonly string _temp;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly DossierStore _store;
    private readonly GitService _git = new(TestLauncherFactory.Instance);

    public DossierImporterTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_import_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_temp, "projects.json"),
            })
            .Build();

        var userStore = new UserStore(_config,
            new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, userStore, new AppSettingsService(_config));
        _store = new DossierStore(_config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // --- Фикстуры ---

    private async Task<Project> MkRepoProjectAsync(string name, string owner)
    {
        var dir = Path.Combine(_temp, name);
        Directory.CreateDirectory(dir);
        await _git.InitAsync(null, dir);
        await _git.RunAsync(null, dir, ["config", "user.email", "dossiers@test"]);
        await _git.RunAsync(null, dir, ["config", "user.name", "Тест Досье"]);
        await File.WriteAllTextAsync(Path.Combine(dir, "readme.md"), "содержимое\n");
        await _git.StageAllAsync(null, dir);
        await _git.CommitAsync(null, dir, "начальный коммит");
        return _projects.Create(name, dir, owner, owner + "-user");
    }

    private static ChangeDossier Dossier(string projectId, string sha, string subject) => new()
    {
        OwnerId = Owner,
        ProjectId = projectId,
        CommitSha = sha,
        CommitSubject = subject,
        CommittedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
        SessionId = "sess-src",
        TaskId = "task-77",
        Why = "почему изменение сделано",
        Decisions = ["решили так"],
        Rejected = ["отвергли эдак"],
        Pitfalls = ["грабли на пути"],
        Invariants = ["инвариант системы"],
        Files = ["backend/a.cs", "backend/b.cs"],
        Symbols = ["ClaudeHomeServer.Services.A"],
    };

    // Ветка паспорта, как её написал бы экспортёр: dossiers/{yyyy}/{mm}/{sha7}-{slug}.md из
    // FormatDossier + index.json; subject редактируется ДО пути и index (поведение BuildFiles).
    // secretsEmpty=true — выжимка в markdown БЕЗ редакции (симуляция чужой ветки, где секрет
    // просочился мимо старой версии редактора) — для теста секретов.
    private static readonly JsonSerializerOptions IndexOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private async Task WriteBranchAsync(Project p, string owner, bool secretsEmpty, params ChangeDossier[] dossiers)
    {
        var secrets = secretsEmpty ? [] : new[] { Secret };
        var files = new List<GitDossierFile>();
        var entries = new List<DossierIndexEntry>();
        foreach (var d in dossiers)
        {
            var subject = SecretRedactor.Redact(d.CommitSubject, secrets);
            var path = DossierGitExporter.DossierPath(d.CommittedAt, d.CommitSha, subject);
            files.Add(new GitDossierFile(path, DossierGitExporter.FormatDossier(d, secrets)));
            entries.Add(new DossierIndexEntry(d.CommitSha, path, subject, d.CommittedAt,
                Discussion: null, TaskId: d.TaskId, SupersededSha: d.SupersededSha));
        }
        files.Add(new GitDossierFile("index.json",
            JsonSerializer.Serialize(new DossierBranchIndex(1, entries), IndexOpts)));
        await _git.WriteDossiersBranchAsync(owner, p.RootPath, files, "test: ветка паспортов");
    }

    private DossierImporter MkImporter(int maxImportBatchEntries = 1000) =>
        new(_store, _git, new InstanceSecretsProvider(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_temp, "projects.json"),
                ["LlmProviders:ccs-dummy:ApiKey"] = Secret,
            }).Build()),
            maxImportBatchEntries: maxImportBatchEntries);

    private string StoreFile(Project p, string owner) =>
        Path.Combine(_temp, "dossiers", owner, p.Id + ".json");

    // --- (а) импорт раскладывает ветку в стор с пометкой происхождения и автором ---

    [Fact]
    public async Task Импорт_КладётЗаписиСПроисхождениемИАвтором()
    {
        var p1 = await MkRepoProjectAsync("repo_a1", Owner);
        var p2 = _projects.Create("repo_a2", p1.RootPath, Owner2, "owner2-user");
        var d = Dossier(p1.Id, "11aa22bb", "feat: первая фича");
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, d);

        var result = await MkImporter().ImportAsync(Owner2, p2);

        result.BranchFound.Should().BeTrue();
        result.Added.Should().Be(1);
        var imported = _store.List(Owner2, p2.Id).Should().ContainSingle().Subject;
        imported.Origin.Should().Be(DossierOrigin.Imported);
        // Идентичность коммиттера ветки экспорта (GitService.DossiersIdentity) и нормализованная ветка
        imported.ImportedAuthor.Should().Be("AI Home");
        imported.ImportedFromBranch.Should().Be("ccs/dossiers/v1");
        imported.CommitSha.Should().Be("11aa22bb");
        imported.CommitSubject.Should().Be(d.CommitSubject);
        imported.CommittedAt.Should().Be(d.CommittedAt);
        imported.TaskId.Should().Be(d.TaskId, "задача приходит из index.json");
        imported.SessionId.Should().Be("sess-src", "чат-источник парсится из markdown");
        imported.Why.Should().Be(d.Why);
        imported.Decisions.Should().Equal(d.Decisions);
        imported.Rejected.Should().Equal(d.Rejected);
        imported.Pitfalls.Should().Equal(d.Pitfalls);
        imported.Invariants.Should().Equal(d.Invariants);
        imported.Files.Should().Equal(d.Files);
        imported.Symbols.Should().Equal(d.Symbols);
    }

    // --- (б) повторный импорт того же состояния ветки — no-op: файл стора байт-в-байт ---

    [Fact]
    public async Task ПовторныйИмпорт_НеМеняетФайлСтора()
    {
        var p1 = await MkRepoProjectAsync("repo_b1", Owner);
        var p2 = _projects.Create("repo_b2", p1.RootPath, Owner2, "owner2-user");
        await WriteBranchAsync(p1, Owner, secretsEmpty: false,
            Dossier(p1.Id, "33cc33cc", "feat: паспорт один"),
            Dossier(p1.Id, "44dd44dd", "feat: паспорт два"));
        var importer = MkImporter();

        (await importer.ImportAsync(Owner2, p2)).Added.Should().Be(2);
        var bytes = await File.ReadAllBytesAsync(StoreFile(p2, Owner2));

        var second = await importer.ImportAsync(Owner2, p2);

        second.Added.Should().Be(0, "все sha уже представлены импортированными записями");
        second.Skipped.Should().Be(2);
        (await File.ReadAllBytesAsync(StoreFile(p2, Owner2)))
            .Should().Equal(bytes, "no-op импорт не переписывает файл стора");
        _store.List(Owner2, p2.Id).Should().HaveCount(2, "дублей не появилось");
    }

    // --- (в) коллизия со своим паспортом: свой нетронут, импортированный рядом ---

    [Fact]
    public async Task СвойПаспортПоТомуЖеSha_Нетронут_ИмпортированныйРядом()
    {
        var p1 = await MkRepoProjectAsync("repo_c1", Owner);
        var p2 = _projects.Create("repo_c2", p1.RootPath, Owner2, "owner2-user");
        var branchD = Dossier(p1.Id, "55ee55ee", "feat: коллизионный коммит");
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, branchD);

        var own = new ChangeDossier
        {
            OwnerId = Owner2,
            ProjectId = p2.Id,
            CommitSha = "55ee55ee",
            CommitSubject = "feat: своя версия паспорта",
            CommittedAt = branchD.CommittedAt,
            Why = "своя выжимка",
            Decisions = ["своё решение"],
        };
        _store.Add(own);

        var result = await MkImporter().ImportAsync(Owner2, p2);

        result.Added.Should().Be(1, "импортированный сохранён отдельной записью рядом со своим");
        var bySha = _store.List(Owner2, p2.Id).Where(x => x.CommitSha == "55ee55ee").ToList();
        bySha.Should().HaveCount(2, "свой и импортированный живут рядом");

        var ownAfter = bySha.Single(x => x.Origin == DossierOrigin.Own);
        ownAfter.Why.Should().Be("своя выжимка", "свой паспорт не перезаписан никогда");
        ownAfter.Decisions.Should().Equal("своё решение");
        ownAfter.ImportedAuthor.Should().BeNull();

        var imported = bySha.Single(x => x.Origin == DossierOrigin.Imported);
        imported.Why.Should().Be(branchD.Why);
        imported.ImportedAuthor.Should().Be("AI Home");
    }

    // --- (г) всё импортируемое содержимое проходит через SecretRedactor (чужая ветка,
    // где редакция не прошла или прошла старой версией редактора) ---

    [Fact]
    public async Task СекретИзВетки_МаскируетсяПриИмпорте()
    {
        var p1 = await MkRepoProjectAsync("repo_d1", Owner);
        var p2 = _projects.Create("repo_d2", p1.RootPath, Owner2, "owner2-user");
        var dirty = new ChangeDossier
        {
            OwnerId = Owner,
            ProjectId = p1.Id,
            CommitSha = "ab12cd34",
            CommitSubject = $"feat: подключить провайдер ключом {Secret}",
            CommittedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            Why = $"нужен был обходной маршрут через ключ {Secret}",
            Decisions = [$"ключ {Secret} держать в Local.json"],
        };
        // secretsEmpty: секрет уезжает в ветку как есть — импортёр обязан поймать его сам
        await WriteBranchAsync(p1, Owner, secretsEmpty: true, dirty);

        var result = await MkImporter().ImportAsync(Owner2, p2);

        result.Added.Should().Be(1);
        var imported = _store.List(Owner2, p2.Id).Single();
        imported.CommitSubject.Should().Contain("[REDACTED:instance-secret]").And.NotContain(Secret);
        imported.Why.Should().Contain("[REDACTED:instance-secret]").And.NotContain(Secret);
        imported.Decisions.Should().ContainSingle()
            .Which.Should().Contain("[REDACTED:instance-secret]").And.NotContain(Secret);
    }

    // --- (д) битые записи index (файл не читается / кривой sha) не роняют импорт и
    // честно считаются пропущенными ---

    [Fact]
    public async Task БитыеЗаписиIndex_ПропускаютсяСоСчётчиком()
    {
        var p1 = await MkRepoProjectAsync("repo_e1", Owner);
        var p2 = _projects.Create("repo_e2", p1.RootPath, Owner2, "owner2-user");
        var good = Dossier(p1.Id, "66ff66aa", "feat: годный паспорт");
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, good);

        // Портим index.json: у годной записи sha невалиден, добавляем фантомный файл
        var indexJson = await _git.ReadDossiersFileAsync(Owner, p1.RootPath, "index.json");
        var index = JsonSerializer.Deserialize<DossierBranchIndex>(indexJson!, IndexOpts)!;
        var entries = new List<DossierIndexEntry>(index.Entries)
        {
            index.Entries[0] with { Sha = "../traversal" },
            index.Entries[0] with { Sha = "77ff77ff", File = "dossiers/2026/08/нет-файла.md" },
        };
        var broken = JsonSerializer.Serialize(new DossierBranchIndex(1, entries), IndexOpts);
        await _git.WriteDossiersBranchAsync(Owner, p1.RootPath,
            [.. (await BranchFilesWithContentAsync(p1)).Where(f => f.Path != "index.json"),
             new GitDossierFile("index.json", broken)],
            "test: битый index");

        var result = await MkImporter().ImportAsync(Owner2, p2);

        result.Added.Should().Be(1, "годная запись импортируется, невзирая на соседний мусор");
        result.Skipped.Should().Be(2, "кривой sha + нечитаемый файл");
        _store.List(Owner2, p2.Id).Single().CommitSha.Should().Be("66ff66aa");
    }

    // --- (е1) чужой index.json без доверия датам (разбор консилиума 23.08): даты
    // управляют вытеснением EvictIfNeeded, неправдоподобные (будущее дальше суток,
    // до 2000 года) отсекаются наравне с кривыми sha — по образцу IsValidSha ---

    [Fact]
    public async Task ДатаИзБудущегоИДревняя_Отсекаются()
    {
        var p1 = await MkRepoProjectAsync("repo_dates1", Owner);
        var p2 = _projects.Create("repo_dates2", p1.RootPath, Owner2, "owner2-user");
        var future = Dossier(p1.Id, "2100aa01", "feat: паспорт из 2099 года");
        future.CommittedAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ancient = Dossier(p1.Id, "1999bb02", "feat: паспорт из 1999 года");
        ancient.CommittedAt = new DateTimeOffset(1999, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var ok = Dossier(p1.Id, "2026cc03", "feat: паспорт с нормальной датой");
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, future, ancient, ok);

        var result = await MkImporter().ImportAsync(Owner2, p2);

        result.Added.Should().Be(1, "прошла только запись с правдоподобной датой");
        result.Skipped.Should().Be(2, "дата 2099 и дата 1999 — пропущенные записи");
        _store.List(Owner2, p2.Id).Single().CommitSha.Should().Be("2026cc03");
    }

    // Граница «ровно сутки в будущее» — ещё правдоподобна (дрейф часов машин общей
    // папки): запись проходит.
    [Fact]
    public async Task ДатаВБудущемВПределахСуток_Проходит()
    {
        var p1 = await MkRepoProjectAsync("repo_dates3", Owner);
        var p2 = _projects.Create("repo_dates4", p1.RootPath, Owner2, "owner2-user");
        var skew = Dossier(p1.Id, "2359dd04", "feat: часы соседа спешат");
        skew.CommittedAt = DateTimeOffset.UtcNow.AddHours(2);
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, skew);

        var result = await MkImporter().ImportAsync(Owner2, p2);

        result.Added.Should().Be(1, "сутки вперёд — допустимый дрейф часов, запись жива");
    }

    // --- (е2) потолок партии импорта (разбор консилиума 23.08): чужая ветка с тысячами
    // записей не должна заваливать стор и Dify за один вызов — берём свежие по дате ---

    [Fact]
    public async Task ПартияСверхПотолка_ОбрезаетсяДоСвежих()
    {
        var p1 = await MkRepoProjectAsync("repo_cap1", Owner);
        var p2 = _projects.Create("repo_cap2", p1.RootPath, Owner2, "owner2-user");
        var d1 = Dossier(p1.Id, "0a000001", "feat: старейший");
        d1.CommittedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var d2 = Dossier(p1.Id, "0a000002", "feat: второй");
        d2.CommittedAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        var d3 = Dossier(p1.Id, "0a000003", "feat: третий");
        d3.CommittedAt = new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);
        var d4 = Dossier(p1.Id, "0a000004", "feat: свежейший");
        d4.CommittedAt = new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero);
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, d1, d2, d3, d4);

        var result = await MkImporter(maxImportBatchEntries: 2).ImportAsync(Owner2, p2);

        result.Added.Should().Be(2, "за потолком остались только две свежие записи");
        result.Skipped.Should().Be(2, "хвост партии честно посчитан пропущенным");
        var shas = _store.List(Owner2, p2.Id).Select(d => d.CommitSha).ToList();
        shas.Should().BeEquivalentTo(["0a000003", "0a000004"],
            "партия обрезана по CommittedAt: свежие записи, хвост не заведён");
    }

    // --- (е3) предикат stillForeign (окно гонки «update-ref → MarkOwnTip»): если за
    // долгое чтение ветки tip стал НАШИМ — Imported-копии собственного снапшота не заводим ---

    [Fact]
    public async Task StillForeignFalse_ПартияОтброшена()
    {
        var p1 = await MkRepoProjectAsync("repo_guard1", Owner);
        var p2 = _projects.Create("repo_guard2", p1.RootPath, Owner2, "owner2-user");
        await WriteBranchAsync(p1, Owner, secretsEmpty: false,
            Dossier(p1.Id, "90aa0001", "feat: паспорт уже-нашей ветки"));

        var result = await MkImporter().ImportAsync(Owner2, p2, stillForeign: _ => false);

        result.Added.Should().Be(0, "tip стал своим за время чтения — партия выброшена");
        result.BranchFound.Should().BeTrue();
        _store.List(Owner2, p2.Id).Should().BeEmpty("Imported-дубли собственного снапшота не заведены");
    }

    [Fact]
    public async Task StillForeignTrue_ОбычныйИмпорт()
    {
        var p1 = await MkRepoProjectAsync("repo_guard3", Owner);
        var p2 = _projects.Create("repo_guard4", p1.RootPath, Owner2, "owner2-user");
        await WriteBranchAsync(p1, Owner, secretsEmpty: false,
            Dossier(p1.Id, "90aa0002", "feat: паспорт чужой ветки"));

        var result = await MkImporter().ImportAsync(Owner2, p2, stillForeign: _ => true);

        result.Added.Should().Be(1, "предикат подтвердил чужой tip — импорт штатный");
        _store.List(Owner2, p2.Id).Should().ContainSingle();
    }

    // --- (е) ветки нет (свежая машина без ветки) — нули с явным признаком ---

    [Fact]
    public async Task БезВетки_НулиИBranchFoundFalse()
    {
        var p = await MkRepoProjectAsync("repo_f1", Owner);

        var result = await MkImporter().ImportAsync(Owner, p);

        result.BranchFound.Should().BeFalse();
        result.Added.Should().Be(0);
        result.Skipped.Should().Be(0);
    }

    // --- (ж) парсер markdown: полный раунд FormatDossier → ParseMarkdown ---

    [Fact]
    public void ParseMarkdown_РазбираетФорматЭкспортёра()
    {
        var d = Dossier("p", "88aa88aa", "feat: раунд-трип");
        d.SupersededSha = ["77aa77aa"];

        var parsed = DossierImporter.ParseMarkdown(DossierGitExporter.FormatDossier(d, []));

        parsed.SessionId.Should().Be("sess-src");
        parsed.Files.Should().Equal(d.Files);
        parsed.Symbols.Should().Equal(d.Symbols);
        parsed.Why.Should().Be(d.Why);
        parsed.Decisions.Should().Equal(d.Decisions);
        parsed.Rejected.Should().Equal(d.Rejected);
        parsed.Pitfalls.Should().Equal(d.Pitfalls);
        parsed.Invariants.Should().Equal(d.Invariants);
    }

    [Fact]
    public void ParseMarkdown_НезнакомыеСекцииНеРоняютПарсер()
    {
        var md = "# subject\n\n- Коммит: `abc` (2026-07-01)\n\n## БудущееПоле\n\n- что-то новое\n\n## Зачем\n\nтекст\n";
        var parsed = DossierImporter.ParseMarkdown(md);
        parsed.Why.Should().Be("текст");
        parsed.Decisions.Should().BeEmpty();
        parsed.SessionId.Should().BeNull();
    }

    // --- (и) импорт не сдвигает HEAD и не оставляет следов в рабочей папке ---
    // Read-only-семантика импорта (этап 4, ADR-004 §6): пишем в ветку ccs/dossiers/v1
    // plumbing-командами, текущая ветка и рабочая папка не задеты. Проверяем по снимкам
    // git status --porcelain и git rev-parse HEAD ДО, ПОСЛЕ WriteDossiersBranchAsync и
    // ПОСЛЕ ImportAsync — все три равны baseline.
    [Fact]
    public async Task Импорт_НеСдвигаетHeadИНеМеняетРабочееДерево()
    {
        var p1 = await MkRepoProjectAsync("repo_head", Owner);
        var p2 = _projects.Create("repo_head_2", p1.RootPath, Owner2, "owner2-user");
        var d = Dossier(p1.Id, "99aabbcc", "feat: проверка HEAD");

        // Baseline: голый репо с одним начальным коммитом, рабочее дерево чистое
        var headBefore = (await _git.RunAsync(Owner, p1.RootPath, ["rev-parse", "HEAD"])).Stdout.Trim();
        var statusBefore = (await _git.RunAsync(Owner, p1.RootPath, ["status", "--porcelain"])).Stdout;

        // Запись ветки (промежуточный шаг — пререквизит импорта, тоже не должна задеть baseline)
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, d);
        var headAfterWrite = (await _git.RunAsync(Owner, p1.RootPath, ["rev-parse", "HEAD"])).Stdout.Trim();
        var statusAfterWrite = (await _git.RunAsync(Owner, p1.RootPath, ["status", "--porcelain"])).Stdout;
        headAfterWrite.Should().Be(headBefore, "WriteDossiersBranchAsync коммитит в ccs/dossiers/v1, не в текущую");
        statusAfterWrite.Should().Be(statusBefore, "WriteDossiersBranchAsync не загрязняет рабочую папку");

        var result = await MkImporter().ImportAsync(Owner2, p2);

        result.Added.Should().Be(1);
        var headAfter = (await _git.RunAsync(Owner2, p1.RootPath, ["rev-parse", "HEAD"])).Stdout.Trim();
        var statusAfter = (await _git.RunAsync(Owner2, p1.RootPath, ["status", "--porcelain"])).Stdout;
        headAfter.Should().Be(headBefore, "импорт не смещает HEAD — это plumbing в другую ветку");
        statusAfter.Should().Be(statusBefore, "импорт не оставляет следов в рабочей папке");
    }

    // --- (к) импортированная запись попадает в пассивный recall персон ---
    // DossierRecallService.BuildRecallBlockAsync — пассивный канал досье, в который
    // PersonaMemoryService.BuildRecallAsync подмешивает блок (строка 494 того сервиса).
    // Импортированная запись — обычный ChangeDossier с Origin=Imported: проверяем, что
    // recall её подбирает по якорю-файлу и сохраняет происхождение в Used.
    [Fact]
    public async Task ИмпортированнаяЗапись_ПопадаетВRecall()
    {
        var p1 = await MkRepoProjectAsync("repo_recall", Owner);
        var p2 = _projects.Create("repo_recall_2", p1.RootPath, Owner2, "owner2-user");
        var d = Dossier(p1.Id, "1ff00ff0", "feat: recall проверка");
        d.Files = ["backend/A.cs"];   // уникальный якорь для матча
        await WriteBranchAsync(p1, Owner, secretsEmpty: false, d);
        (await MkImporter().ImportAsync(Owner2, p2)).Added.Should().Be(1);

        // harness подменяет protected virtual методы: head=null → кеш статусов не
        // задействуется, фоновый пересчёт не запускается, recall отдаёт стейбл-результат
        var recall = new RecallTestHarness(_store);
        var req = new DossierRecallRequest(
            p2.Id, p1.RootPath, TaskId: null,
            AnchorFiles: ["backend/A.cs"],
            TurnText: "правлю backend/A.cs");
        var result = await recall.BuildRecallBlockAsync(Owner2, req);

        result.Text.Should().NotBeNull("якорь файла из импортированной записи обязан её подхватить");
        var hit = result.Used.Should().ContainSingle().Subject;
        hit.CommitSha.Should().Be("1ff00ff0");
        hit.Origin.Should().Be(DossierOrigin.Imported, "импортированная запись сохраняет происхождение в Used");
        hit.ImportedAuthor.Should().Be("AI Home");
        hit.ImportedFromBranch.Should().Be("ccs/dossiers/v1");
        result.Text.Should().Contain("dossier_get(", "recall ссылается на dossier_get по id записи");
    }

    // Локальный аналог FakeRecall из DossierRecallTests (там private nested — через
    // тестовые сборки не достать). Подменяем только ResolveHeadAsync на null, остальная
    // логика (ранжирование, форматирование, бюджет) — продакшн.
    private sealed class RecallTestHarness : DossierRecallService
    {
        public RecallTestHarness(DossierStore store) : base(store) { }
        protected override Task<string?> ResolveHeadAsync(string ownerId, string root) =>
            Task.FromResult<string?>(null);
    }

    // Текущее дерево ветки (путь + содержимое) — для пересборки с порченным index.json
    private async Task<List<GitDossierFile>> BranchFilesWithContentAsync(Project p)
    {
        var r = await _git.RunAsync(Owner, p.RootPath, ["ls-tree", "-r", "--name-only", GitService.DossiersRef]);
        r.Ok.Should().BeTrue();
        var files = new List<GitDossierFile>();
        foreach (var f in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var c = await _git.ReadDossiersFileAsync(Owner, p.RootPath, f);
            files.Add(new GitDossierFile(f, c!));
        }
        return files;
    }
}
