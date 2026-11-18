using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// П.18 ADR-041 (KM-B3b, пункт 3 ревью): автоматизация персон детскому владельцу не срабатывает ни
// из тика, ни из общей точки FireAsync (её же зовут @упоминание и ручной прогон). Парные
// контрольные случаи без ребёнка не дают тестам пройти вакуумно.
public class PersonaAutomationChildGateTests : IDisposable
{
    private readonly DeviceWaitTestHost _h = new();
    private readonly AutomationStateStore _state;
    private readonly CountingSource _source = new();

    private sealed class CountingSource : ITriggerSource
    {
        public int Evaluations;
        public AutomationTriggerType Type => AutomationTriggerType.Timer;
        public Task<IReadOnlyList<TriggerEvent>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Evaluations);
            return Task.FromResult<IReadOnlyList<TriggerEvent>>([new TriggerEvent(ctx.Rule.Id, Type, "Тик")]);
        }
    }

    public PersonaAutomationChildGateTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_h._dir, "automation", "projects.json"),
            })
            .Build();
        _state = new AutomationStateStore(config);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _h.Dispose();
    }

    private PersonaAutomationService Build(bool child)
    {
        var config = new ConfigurationBuilder().Build();
        var gate = new Mock<IChildModeGate>();
        gate.Setup(g => g.IsChild(It.IsAny<string?>())).Returns(child);
        var roots = new AutomationRootResolver(_h._projects, UserHomeResolver.WithoutOverrides(_h._appSettings));
        return new PersonaAutomationService(_h._personas, _h._sessions, _h._push, _h._notif,
            _state, new MentionTriggerSource(_h._personas), _h._projects, _h._userStore, roots,
            [_source], config, new Mock<ICheapTextRunner>().Object,
            NullLogger<PersonaAutomationService>.Instance, deviceGate: _h._gate, childGate: gate.Object);
    }

    private (User Owner, Persona Persona, PersonaAutomationRule Rule) LocalRule()
    {
        var (owner, project) = _h.LocalProject();
        var persona = _h._personas.Create(owner.Id, "Дежурный", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: project.Id, color: null, greeting: null, memoryEnabled: false);
        var rule = new PersonaAutomationRule { Name = "Проверка билда", Trigger = new AutomationTrigger() };
        persona = _h._personas.UpdateRules(persona.Id, owner.Id, [rule]);
        return (owner, persona, persona.AutomationRules!.Single());
    }

    private static Task Fire(PersonaAutomationService sut, Persona persona, PersonaAutomationRule rule)
    {
        var fire = typeof(PersonaAutomationService).GetMethod("FireAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ev = new TriggerEvent(rule.Id, AutomationTriggerType.Timer, "Тик 09:00");
        return (Task)fire.Invoke(sut, [persona, rule, TimeZoneInfo.Utc, ev, CancellationToken.None, false])!;
    }

    [Fact]
    public async Task Тик_ДетскийВладелец_ИсточникНеОпрашивается()
    {
        var (owner, _, _) = LocalRule();
        using var sut = Build(child: true);

        await sut.MaybeRunAutomationsAsync(owner, TimeZoneInfo.Utc, DateTime.UtcNow, CancellationToken.None);

        _source.Evaluations.Should().Be(0);
    }

    [Fact]
    public async Task Тик_ОбычныйВладелец_ИсточникОпрашивается()
    {
        var (owner, _, _) = LocalRule();
        using var sut = Build(child: false);

        await sut.MaybeRunAutomationsAsync(owner, TimeZoneInfo.Utc, DateTime.UtcNow, CancellationToken.None);

        _source.Evaluations.Should().Be(1, "контроль: без ребёнка правило доходит до источника");
    }

    [Fact]
    public async Task FireAsync_ДетскийВладелец_НичегоНеДелаетИНеПишетСостояние()
    {
        var (_, persona, rule) = LocalRule();
        using var sut = Build(child: true);

        await Fire(sut, persona, rule);

        var state = _state.GetRule(persona.Id, rule.Id);
        state.LastResult.Should().BeNull();
        state.DeviceSkippedAt.Should().BeNull();
        state.RunCount.Should().Be(0);
        state.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task FireAsync_ОбычныйВладелец_ДоходитДоСледующихГейтов()
    {
        var (_, persona, rule) = LocalRule();
        using var sut = Build(child: false);

        await Fire(sut, persona, rule);

        _state.GetRule(persona.Id, rule.Id).LastResult.Should().StartWith("skipped",
            "контроль: устройство офлайн — без ребёнка срабатывание доходит до гейта устройства");
    }
}
