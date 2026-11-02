using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// S2d-4f (дефект e127cef0, правило И.2): уведомление о результате автоматизации читается
// по приватности чата: приватный чат — ТОЛЬКО его автору, а не владельцу проекта. Триггер —
// ответ завершённого хода (ResultMessage) в чате, закреплённом за правилом; человека в чате
// нет (HasViewers = false) — уведомление положено. Общий чат — прежний адресат (контроль ниже).
public class PersonaAutomationNotificationTests : IDisposable
{
    private const string Author = "editor-1";

    private readonly DeviceWaitTestHost H = new();
    private readonly PersonaAutomationService _sut;
    private readonly AutomationStateStore _state;

    public PersonaAutomationNotificationTests()
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
        _sut.Dispose();
        H.Dispose();
    }

    // Правило проектной персоны владельца (тот же arrange, что у DeviceWait-тестов)
    private (User Owner, Persona Persona, PersonaAutomationRule Rule) OwnerRule()
    {
        var (owner, project) = H.LocalProject();
        var persona = H._personas.Create(owner.Id, "Дежурный", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: project.Id, color: null, greeting: null, memoryEnabled: false);
        var rule = new PersonaAutomationRule { Name = "Проверка билда", Trigger = new AutomationTrigger() };
        persona = H._personas.UpdateRules(persona.Id, owner.Id, [rule]);
        return (owner, persona, persona.AutomationRules!.Single());
    }

    // Точка входа — приватный обработчик события чата (тот же white-box приём, что FireAsync
    // в DeviceWait-тестах): сигнал моделируется ровно тем сообщением, на котором он срабатывает
    private Task FireResultAsync(Session session, ServerMessage msg)
    {
        var mi = typeof(PersonaAutomationService).GetMethod("OnSessionMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)mi.Invoke(_sut, [session, msg])!;
    }

    [Fact]
    public async Task ОтветВПриватномЧате_УведомлениеТолькоАвтору()
    {
        var (owner, persona, rule) = OwnerRule();
        var chat = await H._sessions.CreatePersonaChatAsync(owner.Id, persona.Id, ClaudeMode.AcceptEdits,
            automationRuleId: rule.Id);
        H._sessions.GetById(chat.Id)!.PrivateToUserId = Author;

        await FireResultAsync(H._sessions.GetById(chat.Id)!, new ResultMessage("done", 1, 1, null, null));

        (await H.NotificationTitlesAsync(Author)).Should().ContainSingle(
            "ответ по правилу в приватном чате — его автору");
        (await H.NotificationTitlesAsync(owner.Id)).Should().BeEmpty(
            "владелец проекта не получает ни имя, ни id чужого приватного чата");
    }

    [Fact]
    public async Task ОтветВОбщемЧате_УведомлениеВладельцу_ПрежнийАдресат()
    {
        var (owner, persona, rule) = OwnerRule();
        var chat = await H._sessions.CreatePersonaChatAsync(owner.Id, persona.Id, ClaudeMode.AcceptEdits,
            automationRuleId: rule.Id);

        await FireResultAsync(H._sessions.GetById(chat.Id)!, new ResultMessage("done", 1, 1, null, null));

        (await H.NotificationTitlesAsync(owner.Id)).Should().ContainSingle(
            "общий чат правила — прежний адресат (владелец), контроль надмены не внесла");
    }
}
