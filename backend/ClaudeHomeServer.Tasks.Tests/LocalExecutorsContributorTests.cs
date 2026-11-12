using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.DependencyInjection;
using static ClaudeHomeServer.Tasks.Tests.LocalExecutorFlowKit;

namespace ClaudeHomeServer.Tasks.Tests;

// Счётчик локальной модели в хвосте хода оркестратора (часть B). Мутации (вывод — в отчёте):
//  • убрать InTurnTail — тест «хвост хода» красный;
//  • считать ходы только по этой фиче (фильтр FeatureId) — тест «все фичи» красный;
//  • брать слоты не из шва SlotsFor (например, Limit − ходы задач) — тест «слоты из шва» красный;
//  • не сверять сессию с ключевым чатом — тест «чужой чат» красный;
//  • вернуть счёт ходов только по ростеру исполнителей (LocalTurnOf → проверка PersonaId в
//    Locals) — тесты «ревью вне ростера» и «без персоны» красные.
// Подставной ростер держит подставную модель локальности: тир weak — локальный, без явного
// тира — модель персоны из Locals (реальный резолвер в этом тесте не нужен, он свой).
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
        section.Text.Should().Be("Локальная модель: свободно слотов 4 из 4. Локальных ходов задач на движке: 2 — Кира (фича «X», S2), Денис (фича «Y», R-3). Локальные исполнители: Кира, Денис, Кузьма — одного можно запускать на несколько шагов параллельно.");
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

        (await Build(KeyChat))!.Text.Should().Contain("Локальных ходов задач на движке: 1 — Кира (фича «Z», S5).");
    }

    [Fact]
    public async Task НиктоНеРаботает_СлотыИИсполнители()
    {
        ThreeLocals();
        _kit.NewFeature("X");

        (await Build(KeyChat))!.Text.Should().Be("Локальная модель: свободно слотов 4 из 4. Локальных ходов задач на движке: 0. Локальные исполнители: Кира, Денис, Кузьма — одного можно запускать на несколько шагов параллельно.");
    }

    // Слоты — счётчик допуска движка (в нём и чаты, и фон), не «лимит минус ходы задач»;
    // очередь к движку обнуляет свободные и видна в строке
    [Fact]
    public async Task СлотыИзШва_СОчередью()
    {
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        _kit.Roster.Slots = new LocalEngineSlots(6, 5, 2);
        var x = _kit.NewFeature("X");
        _kit.NewStep(x, "kira", "S1", running: true);

        (await Build(KeyChat))!.Text.Should().Be("Локальная модель: свободно слотов 0 из 6, в очереди 2. " +
            "Локальных ходов задач на движке: 1 — Кира (фича «X», S1). Локальные исполнители: Кира — одного можно запускать на несколько шагов параллельно.");
    }

    [Fact]
    public async Task ИсполнительЖдётЧеловека_НеСчитаетсяЗанятым()
    {
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        var x = _kit.NewFeature("X");
        var step = _kit.NewStep(x, "kira", "S1", running: true);
        _kit.Sessions.GetById(step.LinkedSessionId!)!.Status = SessionStatus.Waiting;

        // ждущая человека сессия — не ход на движке
        (await Build(KeyChat))!.Text.Should().Contain("Локальных ходов задач на движке: 0.");
    }

    // c1+c2: карточка ревью с переопределённым слабым тиром (локальная модель подставного
    // резолвера), персона-ревьюер НЕ в ростере исполнителей — считается локальным ходом
    // на движке; в список исполнителей ревьюер не попадает.
    [Fact]
    public async Task РевьюНаСлабомТиреВнеРостера_СчитаетсяЛокальнымХодом()
    {
        ThreeLocals();
        _kit.Roster.LocalNonExecutors.Add(new LocalExecutor("gleb", "Глеб"));
        var x = _kit.NewFeature("X");
        _kit.NewStep(x, "gleb", "R-1", running: true, role: TaskRoles.Review, modelTier: ModelTier.Weak);

        (await Build(KeyChat))!.Text.Should().Be(
            "Локальная модель: свободно слотов 4 из 4. " +
            "Локальных ходов задач на движке: 1 — Глеб (фича «X», R-1). Локальные исполнители: Кира, Денис, Кузьма — одного можно запускать на несколько шагов параллельно.");
    }

    // Локальный исполнитель на сильном тире задачи: ход идёт не на локальном движке — «ходов: 0».
    [Fact]
    public async Task ИсполнительНаСильномТире_ХодаНаДвижкеНет()
    {
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        var x = _kit.NewFeature("X");
        _kit.NewStep(x, "kira", "S1", running: true, modelTier: ModelTier.Strong);

        (await Build(KeyChat))!.Text.Should().Contain("Локальных ходов задач на движке: 0.");
    }

    // «иначе слот владельца»: карточка без персоны с явным слабым тиром — модель по слоту
    // владельца — тоже локальный ход; имя в строке — заголовок задачи (персоны нет).
    [Fact]
    public async Task КарточкаБезПерсоныНаСлабомТире_СчитаетсяЛокальнымХодом()
    {
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        var x = _kit.NewFeature("X");
        var t = _kit.Tasks.Create(null, Owner, new CreateTaskRequest("Хвост фичи"));
        t.FeatureId = x.Id;
        t.ModelTier = ModelTier.Weak;
        t.Status = TaskItemStatus.InProgress;
        t.LinkedSessionId = "exec-tail";
        _kit.Sessions.Add("exec-tail", SessionStatus.Working);

        (await Build(KeyChat))!.Text.Should().Contain(
            "Локальных ходов задач на движке: 1 — Хвост фичи (фича «X», Хвост фичи)");
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
