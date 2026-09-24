using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// ADR-016, вариант А плана §5: регулярная автоматизация проектной персоны локального проекта
// при офлайн-устройстве не копит ходы — срабатывание пропускается с отметкой, а после выхода
// устройства в онлайн догоняется ОДНО последнее. Онлайн-статус — фейковый гейт.
public class PersonaAutomationServiceDeviceWaitTests : IDisposable
{
    private DeviceWaitTestHost H { get; } = new();
    private readonly PersonaAutomationService _sut;
    private readonly AutomationStateStore _state;

    public PersonaAutomationServiceDeviceWaitTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(H._dir, "automation", "projects.json"),
            })
            .Build();
        _state = new AutomationStateStore(config);
        var roots = new AutomationRootResolver(H._projects, UserHomeResolver.WithoutOverrides(H._appSettings));
        _sut = new PersonaAutomationService(H._personas, H._sessions, H._push, H._notif,
            _state, new MentionTriggerSource(H._personas), H._projects, H._userStore, roots,
            Array.Empty<ITriggerSource>(), config, new Mock<ICheapTextRunner>().Object,
            NullLogger<PersonaAutomationService>.Instance, deviceGate: H._gate);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        H.Dispose();
    }

    private (User Owner, Persona Persona, PersonaAutomationRule Rule) LocalRule()
    {
        var (owner, project) = H.LocalProject();
        var persona = H._personas.Create(owner.Id, "Дежурный", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: project.Id, color: null, greeting: null, memoryEnabled: false);
        var rule = new PersonaAutomationRule { Name = "Проверка билда", Trigger = new AutomationTrigger() };
        persona = H._personas.UpdateRules(persona.Id, owner.Id, [rule]);
        return (owner, persona, persona.AutomationRules!.Single());
    }

    private Task FireAsync(Persona persona, PersonaAutomationRule rule, string summary)
    {
        var fire = typeof(PersonaAutomationService).GetMethod("FireAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ev = new TriggerEvent(rule.Id, AutomationTriggerType.Timer, summary);
        return (Task)fire.Invoke(_sut, [persona, rule, TimeZoneInfo.Utc, ev, CancellationToken.None, false])!;
    }

    [Fact]
    public async Task Срабатывание_УстройствоОфлайн_ПропускСОтметкойБезХодаИБезТроттлинга()
    {
        var (_, persona, rule) = LocalRule();

        await FireAsync(persona, rule, "Тик 09:00");
        await FireAsync(persona, rule, "Тик 10:00");

        var state = _state.GetRule(persona.Id, rule.Id);
        state.LastResult.Should().StartWith("skipped").And.Contain("офлайн");
        state.DeviceSkippedAt.Should().NotBeNull();
        state.DeviceSkippedSummary.Should().Be("Тик 10:00", "периодика не копится — хранится одно последнее");
        state.SessionId.Should().BeNull("чат правила не создаётся под обречённый ход");
        state.LastFiredAt.Should().BeNull("пропуск не тратит кулдаун");
        state.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task ВыходУстройстваВОнлайн_ДогоняетсяОдноПоследнееСрабатывание()
    {
        var (owner, persona, rule) = LocalRule();
        await FireAsync(persona, rule, "Тик 09:00");
        await FireAsync(persona, rule, "Тик 10:00");
        // Закреплённый чат правила с подставным процессом — ход «дойдёт до CLI»
        var chat = await H._sessions.CreatePersonaChatAsync(owner.Id, persona.Id, ClaudeMode.AcceptEdits,
            automationRuleId: rule.Id);
        var delivered = H.StubProcess(chat);
        _state.GetRule(persona.Id, rule.Id).SessionId = chat.Id;

        H._gate.GoOnline();
        await _sut.OnDeviceOnlineAsync(owner.Id, "dev-1");
        await _sut.OnDeviceOnlineAsync(owner.Id, "dev-1");

        var prompt = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        prompt.Should().Contain("пропущено").And.Contain("Тик 10:00").And.NotContain("Тик 09:00");
        var state = _state.GetRule(persona.Id, rule.Id);
        state.DeviceSkippedAt.Should().BeNull();
        state.LastResult.Should().Be("fired");
        state.RunCount.Should().Be(1, "повторное онлайн-событие догон не дублирует");
    }

    [Fact]
    public async Task ПроходБезСобытия_УстройствоВсёЕщёОфлайн_ОтметкаОстаётся()
    {
        var (_, persona, rule) = LocalRule();
        await FireAsync(persona, rule, "Тик 09:00");

        await _sut.SweepAsync(DateTime.UtcNow);

        _state.GetRule(persona.Id, rule.Id).DeviceSkippedAt.Should().NotBeNull();
    }
}
