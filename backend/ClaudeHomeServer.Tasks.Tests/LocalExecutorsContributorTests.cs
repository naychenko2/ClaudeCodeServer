using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.DependencyInjection;
using static ClaudeHomeServer.Tasks.Tests.LocalExecutorFlowKit;

namespace ClaudeHomeServer.Tasks.Tests;

// Счётчик локальных исполнителей в хвосте хода оркестратора (часть B). Мутации (вывод — в отчёте):
//  • убрать InTurnTail — тест «хвост хода» красный;
//  • считать занятость только по этой фиче (фильтр FeatureId) — тест «все фичи» красный;
//  • не сверять сессию с ключевым чатом — тест «чужой чат» красный.
public class LocalExecutorsContributorTests : IDisposable
{
    private readonly LocalExecutorFlowKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private async Task<PromptSection?> Build(string sessionId, bool withRoster = true)
    {
        var services = new ServiceCollection().AddSingleton<ISessionDirectory>(_kit.Sessions);
        if (withRoster) services.AddSingleton<ILocalExecutorRoster>(_kit.Roster);
        var sut = new LocalExecutorsContributor(_kit.Tasks, services.BuildServiceProvider());
        var ctx = new PromptSessionContext(new Session { Id = sessionId }, Owner, null, "/root");
        return (await sut.BuildAsync(ctx, null))?.Sections.SingleOrDefault();
    }

    private void ThreeLocals() => _kit.Roster.Locals.AddRange(
        [new LocalExecutor("kira", "Кира"), new LocalExecutor("denis", "Денис"), new LocalExecutor("kuzma", "Кузьма")]);

    [Fact]
    public async Task КлючевойЧат_ЗанятыВРазныхФичах_СтрокаСчётчика_ХвостомХода()
    {
        ThreeLocals();
        var x = _kit.NewFeature("X");
        var y = _kit.NewFeature("Y");
        _kit.NewStep(x, "kira", "S2", running: true);
        _kit.NewStep(y, "denis", "R-3", running: true);

        var section = await Build(KeyChat);

        section.Should().NotBeNull();
        section!.InTurnTail.Should().BeTrue("секция в системном блоке обнулила бы prefix cache");
        section.Text.Should().Be("Локальные исполнители сейчас: занято 2 из 3 — Кира (фича «X», S2), Денис (фича «Y», R-3); свободны: Кузьма.");
    }

    // Критерий c3: две локальные персоны, одна ведёт ход в ДРУГОЙ фиче со своим ключевым чатом
    [Fact]
    public async Task ЗанятаВЧужойФиче_СДругимКлючевымЧатом_Считается()
    {
        _kit.Roster.Locals.AddRange([new LocalExecutor("kira", "Кира"), new LocalExecutor("kuzma", "Кузьма")]);
        _kit.NewFeature("X");
        _kit.Sessions.Add("chat-other-key", SessionStatus.Active);
        var z = _kit.Tasks.Create(null, Owner, new CreateTaskRequest("Z", Role: TaskRoles.Feature));
        z.ExecutorLaunchedBySessionId = "chat-other-key";
        _kit.NewStep(z, "kira", "S5", running: true);

        (await Build(KeyChat))!.Text.Should().Be("Локальные исполнители сейчас: занято 1 из 2 — Кира (фича «Z», S5); свободны: Кузьма.");
    }

    [Fact]
    public async Task Никто_НеЗанят_ВсеСвободны()
    {
        ThreeLocals();
        _kit.NewFeature("X");

        (await Build(KeyChat))!.Text.Should().Be("Локальные исполнители сейчас: занято 0 из 3; свободны: Кира, Денис, Кузьма.");
    }

    [Fact]
    public async Task ВсеЗаняты_БезХвостаСвободны()
    {
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        var x = _kit.NewFeature("X");
        _kit.NewStep(x, "kira", "S1", running: true);

        (await Build(KeyChat))!.Text.Should().Be("Локальные исполнители сейчас: занято 1 из 1 — Кира (фича «X», S1).");
    }

    [Fact]
    public async Task ИсполнительЖдётЧеловека_НеСчитаетсяЗанятым()
    {
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        var x = _kit.NewFeature("X");
        var step = _kit.NewStep(x, "kira", "S1", running: true);
        _kit.Sessions.GetById(step.LinkedSessionId!)!.Status = SessionStatus.Waiting;

        (await Build(KeyChat))!.Text.Should().Contain("занято 0 из 1").And.Contain("свободны: Кира");
    }

    [Fact]
    public async Task ЛокальныхПерсонНет_Молчит()
    {
        _kit.NewFeature("X");

        (await Build(KeyChat)).Should().BeNull();
    }

    [Fact]
    public async Task ШваРеестраНет_Молчит()
    {
        ThreeLocals();
        _kit.NewFeature("X");

        (await Build(KeyChat, withRoster: false)).Should().BeNull();
    }

    [Fact]
    public async Task ЧужойЧат_Молчит()
    {
        ThreeLocals();
        _kit.NewFeature("X");
        _kit.Sessions.Add("chat-other", SessionStatus.Active);

        (await Build("chat-other")).Should().BeNull("счётчик только в ключевом чате открытой фичи");
    }

    [Fact]
    public async Task ФичаЗакрыта_Молчит()
    {
        ThreeLocals();
        _kit.NewFeature("X").Status = TaskItemStatus.Done;

        (await Build(KeyChat)).Should().BeNull();
    }
}
