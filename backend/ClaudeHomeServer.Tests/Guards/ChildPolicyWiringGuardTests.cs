using System.Reflection;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// Сторож проводки детской политики на реальном контейнере хоста (KM-B3b, пункт 2 ревью):
/// пять потребителей берут политику или гейт опциональным параметром с null по умолчанию, поэтому
/// ошибка DI-регистрации не ломает ни сборку, ни старт, а молча открывает ребёнку фон (fail-open).
/// Проверка смотрит на сами поля резолвленных экземпляров: непустое и именно то, что в контейнере.
/// </summary>
[Trait("Category", "Host")]
public class ChildPolicyWiringGuardTests
{
    // Потребитель → тип детской зависимости, которую он обязан держать
    private static readonly (string Name, Type Instance, Type Dependency)[] Consumers =
    [
        ("CheapTextRunner", typeof(ClaudeHomeServer.Services.Llm.CheapTextRunner), typeof(IChildModePolicy)),
        ("OneShotClaudeRunner", typeof(ClaudeHomeServer.Services.Llm.OneShotClaudeRunner), typeof(IChildModePolicy)),
        ("PersonaAutomationService", typeof(PersonaAutomationService), typeof(IChildModeGate)),
        ("PersonasCrudService", typeof(PersonasCrudService), typeof(IChildModeGate)),
        ("PersonaLayerBuilder", typeof(PersonaLayerBuilder), typeof(IChildModeGate)),
    ];

    // ICheapTextRunner в тестовом хосте подменён стабом, поэтому боевой CheapTextRunner собираем тем же
    // способом, каким контейнер строит тип по `AddSingleton<ICheapTextRunner, CheapTextRunner>()`:
    // самый жадный конструктор, параметры из контейнера, необязательные без регистрации — по умолчанию.
    private static object Resolve(IServiceProvider sp, Type instance) =>
        instance == typeof(ClaudeHomeServer.Services.Llm.CheapTextRunner)
            ? ActivatorUtilities.CreateInstance(sp, instance)
            : sp.GetRequiredService(instance);

    // Значения полей экземпляра заданного типа (в том числе захваченные параметры первичных конструкторов)
    internal static List<object?> HeldDependencies(object instance, Type dependency) =>
        instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => dependency.IsAssignableFrom(f.FieldType))
            .Select(f => f.GetValue(instance)).ToList();

    [Fact]
    public void ПятьПотребителей_НаРеальномХосте_ДержатНепустуюДетскуюЗависимость()
    {
        using var factory = new TestWebApplicationFactory();
        _ = factory.CreateClient();
        var sp = factory.Services;
        var policy = sp.GetRequiredService<IChildModePolicy>();
        var gate = sp.GetRequiredService<IChildModeGate>();

        var problems = new List<string>();
        foreach (var (name, instanceType, dependency) in Consumers)
        {
            var instance = Resolve(sp, instanceType);
            instance.GetType().Should().Be(instanceType, $"{name}: в контейнере должен жить ровно этот тип");
            var held = HeldDependencies(instance, dependency);
            var expected = dependency == typeof(IChildModePolicy) ? (object)policy : gate;
            if (held.Count == 0) problems.Add($"{name}: поля типа {dependency.Name} нет вовсе");
            else if (held.Any(h => h is null)) problems.Add($"{name}: {dependency.Name} = null (fail-open)");
            else if (!held.Any(h => ReferenceEquals(h, expected)))
                problems.Add($"{name}: {dependency.Name} не тот экземпляр, что в контейнере");
        }
        problems.Should().BeEmpty("каждый из пяти потребителей обязан получить детскую политику из контейнера");
    }
}
