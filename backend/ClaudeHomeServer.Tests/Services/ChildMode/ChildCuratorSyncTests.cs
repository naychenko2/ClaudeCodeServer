using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Threading;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Б14a (ADR-041 ред. 6, решения 61–62, разрез §12): кураторы проектов-предметов = опекуны
/// ChildProfile.GuardianUserIds, одна точка правды. Синк приводит Members каждого проекта-
/// предмета к опекунам с ролью Editor; админ и ребёнок-владелец в список не попадают;
/// снятый опекун уходит из всех проектов. Идемпотентность: повтор без правки профиля — no-op.
/// Гонка двух сохранений: итоговый ACL — опекуны последнего записанного профиля, без дублей
/// (синк перечитывает профиль под per-childId замком). Сбой одного проекта — лог, остальные
/// доходят, наружу исключения нет.
/// Фикстура — SessionManagerTestBase (живые UserStore/ProjectManager/SessionManager — тот же
/// состав, что собирает ProjectMembershipService в ProjectMembershipRevokeTurnTests): гонка
/// идёт реальным SetChildProfile, реальным SetMembers — подменах здесь нечему гнаться.
/// Мутации: снять замок в ChildCuratorSync (GetOrAdd → новый семафор на вызов) — гонка
/// краснеет на 20 итерациях (окно «прочитал desired → не записал» стоит весь обход, и
/// пред-наведение обоих потоков через SetChildProfile даёт ему шанс на каждой итерации);
/// снять early-return в SyncSubjectProjectMembersAsync — краснеет идемпотентность
/// (СоставНеМеняется_ПовторНичегоНеПишет); снять «id != project.OwnerId» — краснеет
/// ВладелецНеПопадает (в фикстуре с прямым присваиванием ChildMode стор не сохранён, и
/// SetMembers переписал бы projects.json битым JSON — падение на чтении стора).
/// </summary>
public class ChildCuratorSyncTests : SessionManagerTestBase
{
    private readonly ChildCuratorSync _sync;
    private readonly ChildSubjectProjectProvisioner _subjectProjects;
    private readonly CapturingLogger _log = new();
    // Группа хаба, на которой мок бросает (сбой одного проекта): null — работает как живой.
    private string? _poisonGroup;
    // >0 — следующая отправке хаба медленная (60 мс), остальные быстрые: тестам, которым
    // нужно живое окно «прочитал → не дописал», достаточно одного медленного вещания.
    private int _slowSends;

    public ChildCuratorSyncTests()
    {
        // Рубильник инстанса включён: синк его не проверяет (ACL пишет в обход гейта),
        // но живой ProjectMembershipService конструируется с живым гейтом — как в Program.
        var gateConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SharedProjects:Enabled"] = "true" })
            .Build();
        var gate = new SharedProjectsGate(gateConfig, new FeatureFlagService(_userStore));

        // Имя группы знает Clients.Group(name) — не SendCoreAsync: поэтому отравляется
        // прокси группы, а не метод вещания. Отравленная группа бросает — так вызывается
        // сбой одного проекта в обходе (операция «запись ACL + широковещание», решение 61).
        // Живой хаб асинхронен по-настоящему (не CompletedTask). Тесты, которым нужна
        // асимметрия времени, ставят _slowSends=1: следующий отправке — медленная (60 мс),
        // остальные — быстрые (5 мс). Так окно «прочитал desired → не дописал остальные
        // проекты» первого обхода становится достаточно долгим, чтобы быстрый второй обход
        // уложился в него целиком — та асимметрия, которую даёт и цепь контроллера
        // (длинный провижна первого сохранения против короткого идемпотентного пути второго).
        var live = new Mock<IClientProxy>();
        live.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Decrement(ref _slowSends) >= 0
                ? Task.Run(async () => await Task.Delay(60))
                : Task.Run(async () => await Task.Delay(5)));
        var poisoned = new Mock<IClientProxy>();
        poisoned.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(new IOException("нет диска")));
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns<string>(
            group => group == _poisonGroup ? poisoned.Object : live.Object);
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        hub.SetupGet(h => h.Groups).Returns(groups.Object);

        var membership = new ProjectMembershipService(_projectManager, _userStore, gate,
            new GitService(TestLauncherFactory.Instance), _sut, new UserConnectionRegistry(),
            hub.Object, NullLogger<ProjectMembershipService>.Instance);

        var homes = UserHomeResolver.WithoutOverrides(_appSettings);
        // Проекты-предметы создаются через провижер — тот же путь, что у контроллера (Б17a).
        _subjectProjects = new ChildSubjectProjectProvisioner(_userStore, _projectManager, homes,
            NullLogger<ChildSubjectProjectProvisioner>.Instance);

        _sync = new ChildCuratorSync(_userStore, _projectManager, membership, _log);
    }

    private const string Pw = "curator-pw-123456";

    // Ребёнок с живым профилем и N проектов-предметов (имена уникальны на тест: фикстура
    // одна на класс). Возвращает id ребёнка и id его проектов.
    private (string ChildId, List<string> ProjectIds) NewChild(string name, params string[] subjectIds)
    {
        var child = _userStore.Add(name, Pw, "user");
        _userStore.GetById(child.Id)!.ChildMode = new ChildProfile { Enabled = true };
        var projectIds = new List<string>();
        foreach (var subjectId in subjectIds)
        {
            var subject = new ChildSubject
            {
                Id = subjectId, Label = "Предмет " + subjectId, Glyph = "Аа", Color = "info",
            };
            _userStore.GetById(child.Id)!.ChildMode!.Subjects.Add(subject);
            var project = _subjectProjects.EnsureSubjectProjectAsync(child.Id, subject)
                .GetAwaiter().GetResult();
            project.Should().NotBeNull($"проект-предмет {subjectId} обязан провиться (фикстура)");
            projectIds.Add(project!.Id);
        }
        return (child.Id, projectIds);
    }

    private void SetGuardians(string childId, params string[] guardianIds) =>
        _userStore.GetById(childId)!.ChildMode!.GuardianUserIds = [.. guardianIds];

    private List<ProjectMember> MembersOf(string projectId) =>
        _projectManager.GetById(projectId)!.Members ?? [];

    // ── c1: Desired = опекуны, роль Editor; ребёнок и админ в список не попадают ──

    [Fact]
    public async Task Опекуны_СтановятсяEditor_ВоВсехПроектахПредмета()
    {
        var (childId, projectIds) = NewChild("kid-cur-add", "math", "nature");
        projectIds.Should().HaveCount(2);
        var guardianA = _userStore.Add("cur.a1", Pw, "user").Id;
        var guardianB = _userStore.Add("cur.b1", Pw, "user").Id;
        SetGuardians(childId, guardianA, guardianB);

        await _sync.SyncChildCuratorsAsync(childId);

        foreach (var projectId in projectIds)
        {
            var members = MembersOf(projectId);
            members.Should().HaveCount(2, $"проект {projectId}: оба опекуна стали участниками");
            members.Select(m => m.UserId).Should().BeEquivalentTo([guardianA, guardianB]);
            members.Should().AllSatisfy(m => m.Role.Should().Be(ProjectRole.Editor,
                "куратор — существующая роль Editor, новой роли нет (решение 60)"));
        }
    }

    [Fact]
    public async Task ВладелецНеПопадает_АдминНеСтановитсяКуратором()
    {
        var (childId, projectIds) = NewChild("kid-cur-owner", "math");
        // Опекун — тот же админ фикстуры, что правит профиль: «админ куратором автоматически
        // не становится» проверяется обратным случаем — в desired его нет, пока он не опекун.
        var guardian = _userStore.Add("cur.a2", Pw, "user").Id;
        SetGuardians(childId, guardian);

        await _sync.SyncChildCuratorsAsync(childId);

        var members = MembersOf(projectIds[0]);
        members.Select(m => m.UserId).Should().BeEquivalentTo([guardian]);
        members.Should().NotContain(m => m.UserId == childId,
            "владелец выводится из OwnerId, в Members он не пишется (инвариант ProjectMember)");
        var adminId = _userStore.GetFirst()!.Id;
        members.Should().NotContain(m => m.UserId == adminId,
            "админ, сохранивший профиль, куратором не становится (решение 61)");
    }

    [Fact]
    public async Task ОпекунСнят_СнятВоВсехПроектах_ОставшийСохранён()
    {
        var (childId, projectIds) = NewChild("kid-cur-remove", "math", "nature");
        var stay = _userStore.Add("cur.stay", Pw, "user").Id;
        var gone = _userStore.Add("cur.gone", Pw, "user").Id;
        SetGuardians(childId, stay, gone);
        await _sync.SyncChildCuratorsAsync(childId);

        SetGuardians(childId, stay);
        await _sync.SyncChildCuratorsAsync(childId);

        foreach (var projectId in projectIds)
        {
            var members = MembersOf(projectId);
            members.Should().ContainSingle($"проект {projectId}: лишнего в списке нет (решение 61)");
            members[0].UserId.Should().Be(stay);
            members[0].Role.Should().Be(ProjectRole.Editor);
        }
    }

    [Fact]
    public async Task СоставНеМеняется_ПовторНичегоНеПишет() {
        var (childId, projectIds) = NewChild("kid-cur-idem", "math");
        var guardian = _userStore.Add("cur.idem", Pw, "user").Id;
        SetGuardians(childId, guardian);

        await _sync.SyncChildCuratorsAsync(childId);
        var firstAddedAt = MembersOf(projectIds[0])[0].AddedAt;
        var stampFirst = File.ReadAllText(ProjectStorePath());
        // Мутация «убрать early-return» (состав тот же → всё равно SetMembers): краснеет здесь —
        // повторный обход не должен трогать ни записи стора, ни дат существующих строк.
        await _sync.SyncChildCuratorsAsync(childId);

        var members = MembersOf(projectIds[0]);
        members.Should().ContainSingle();
        members[0].AddedAt.Should().Be(firstAddedAt, "живая строка сохраняется со своей датой");
        File.ReadAllText(ProjectStorePath()).Should().Be(stampFirst,
            "идемпотентный обход не пишет стор (no-op без изменения состава)");
    }

    private string ProjectStorePath() => Path.Combine(_tempDir, "projects.json");

    // ── c2: гонка двух сохранений ────────────────────────────────────────────

    [Fact]
    public async Task ГонкаДвухСохранений_MembersРавныПоследнемуПрофилю_БезДублей()
    {
        // 20 итераций, ДВА предмета на ребёнка, асимметрия как в живой цепи: первый обход
        // медленный (его первое широковещание — 60 мс), второй стартует ПОСЛЕ того, как
        // первый уже прочитал профиль и записал первый проект (появление A в P1 — видимый
        // факт этого), и весь быстрый второй обход укладывается в окно первого. Без
        // per-childId замка первый дописывает свой второй проект старым desired ПОСЛЕ
        // быстрого второго — его ACL отстаёт от живого профиля (красно на всех 20).
        // С замком второй обход ждёт первого за семафором и перечитывает уже [c,d] —
        // зелёно детерминированно.
        for (var iter = 0; iter < 20; iter++)
        {
            var (childId, projectIds) = NewChild($"kid-cur-race{iter}", "r" + iter + "a", "r" + iter + "b");
            var a = _userStore.Add($"cur.r{iter}a", Pw, "user").Id;
            var b = _userStore.Add($"cur.r{iter}b", Pw, "user").Id;
            var c = _userStore.Add($"cur.r{iter}c", Pw, "user").Id;
            var d = _userStore.Add($"cur.r{iter}d", Pw, "user").Id;
            var p1 = projectIds[0];

            _slowSends = 1; // первое широковещание итерации — медленное: окно первого обхода
            var first = Task.Run(async () =>
            {
                _userStore.SetChildProfile(childId,
                    new ChildProfile { Enabled = true, GuardianUserIds = [a, b] }, resetRules: false);
                await _sync.SyncChildCuratorsAsync(childId);
            });

            // Ждём факта «первый уже внутри своего окна»: A записан в P1, P2 ещё нет.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!MembersOf(p1).Any(m => m.UserId == a))
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"итерация {iter}: первый обход не записал P1 — сценарий гонки не собрался");
                await Task.Delay(1);
            }
            _userStore.SetChildProfile(childId,
                new ChildProfile { Enabled = true, GuardianUserIds = [c, d] }, resetRules: false);
            var second = Task.Run(() => _sync.SyncChildCuratorsAsync(childId));
            await Task.WhenAll(first, second);

            // Инвариант: Members равны опекунам ЖИВОГО профиля — того, что записан последним.
            var expected = _userStore.GetById(childId)!.ChildMode!.GuardianUserIds;
            expected.Should().BeEquivalentTo([c, d], $"итерация {iter}: второй save обязан быть последним");
            foreach (var projectId in projectIds)
            {
                var members = MembersOf(projectId);
                members.Select(m => m.UserId).Should().BeEquivalentTo(expected,
                    $"итерация {iter}: ACL отстал от последнего записанного профиля");
                members.Select(m => m.UserId).Distinct().Should().HaveCount(expected.Count,
                    $"итерация {iter}: дубль в Members");
            }
        }
    }

    // ── сбой одного проекта: остальные доходят, наружу исключения нет ────────

    [Fact]
    public async Task СбойОдногоПроекта_ОстальныеСинхронизированы_БезИсключенияНаружу()
    {
        // Сбой вызывается удалением проекта между перечислением обхода и записью членства
        // (живая последовательность: обход идёт по снимку GetByOwner, проект мог уйти под
        // параллельной правкой). Остальные проекты синхронизируются, exception наружу не
        // выходит — контракт класса; «ошибка в лог» меряется ниже отдельным кейсом.
        var (childId, projectIds) = NewChild("kid-cur-fail", "math", "nature");
        var guardian = _userStore.Add("cur.fail", Pw, "user").Id;
        SetGuardians(childId, guardian);

        _projectManager.Delete(projectIds[0]);

        await _sync.SyncChildCuratorsAsync(childId); // без исключения

        var members = MembersOf(projectIds[1]);
        members.Should().ContainSingle("остальной проект синхронизирован");
        members[0].UserId.Should().Be(guardian);
    }

    [Fact]
    public async Task СбойОдногоПроекта_ОшибкаВЛог_ОбходНеПруется_ОстальныеДоходят()
    {
        // Сбой одного проекта вызывается хабом на его группе (операция «запись ACL +
        // широковещание» — одно, решение 61). Синк ловит его как сбой одного проекта:
        // Warning, обход продолжается, наружу — ничего (тот же контракт, что у
        // RemoveOrphanTutorsAsync). Полусостояния нет: ACL обоих проектов записаны до
        // широковещания — повторный обход доводит любой оставшийся разлад.
        var (childId, projectIds) = NewChild("kid-cur-log", "math", "nature");
        var guardian = _userStore.Add("cur.log", Pw, "user").Id;
        SetGuardians(childId, guardian);
        _poisonGroup = "project_" + projectIds[0];

        await _sync.SyncChildCuratorsAsync(childId); // без исключения
        _poisonGroup = null;

        _log.Warnings.Should().Contain(w => w.Contains("синхронизация кураторов", StringComparison.Ordinal),
            "сбой назван в Warning");
        // Остальной проект дошёл этим же обходом; отказанный — с своим ACL: он записан
        // до широковещания, полусостояния нет.
        MembersOf(projectIds[1]).Should().ContainSingle("остальной проект дошёл обходом");
        MembersOf(projectIds[0]).Should().ContainSingle("ACL отказанного проекта записан до широковещания");

        // «Повтор при следующем сохранении доводит»: опекун снят из профиля, хаб жив —
        // оба ACL доводятся до нового desired.
        SetGuardians(childId);
        await _sync.SyncChildCuratorsAsync(childId);
        MembersOf(projectIds[0]).Should().BeEmpty("повтор доводит отказанный");
        MembersOf(projectIds[1]).Should().BeEmpty();
    }

    // Минимальный собирающий логгер: только Warning-строки (контракт проверки «ошибка в лог»).
    private sealed class CapturingLogger : ILogger<ChildCuratorSync>
    {
        private readonly List<string> _warnings = [];
        public List<string> Warnings { get { lock (_warnings) return [.. _warnings]; } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public ILogger GetChildLogger(string name) => this;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }
}
