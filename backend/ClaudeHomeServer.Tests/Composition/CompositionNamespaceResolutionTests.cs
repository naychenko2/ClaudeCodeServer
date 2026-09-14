using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож резолва ВСЕХ DI-швов из Core/Services/Composition: поднимает настоящий контейнер
// (TestWebApplicationFactory → Program) и проверяет, что каждый интерфейс из namespace
// ClaudeHomeServer.Services.Composition резолвится в не-null.
//
// Типы берутся РЕФЛЕКСИЕЙ по namespace (а не жёстким списком), поэтому новый шов,
// добавленный в Core, покрывается автоматически. Исключения:
//   IAppSubsystem / IAppPhaseSubsystem — маркерные интерфейсы подсистем;
//   резолв идёт по конкретным классам (SkillsSubsystem, TasksSubsystem, ...),
//   а `IAppPhaseSubsystem` — только type-test (is-проверка в UseSubsystems).
//
// Критерий приёмки: мутация — снятая одна регистрация = падение; вернёшь = зелёный.
// Второй раз за два дня «забытая регистрация»: Skills (позавчера) и Triggers/Watchdog (сегодня).
public class CompositionNamespaceResolutionTests
{
    // Маркерные интерфейсы: резолвятся/проверяются по конкретному типу, не через DI.
    private static readonly HashSet<Type> Excluded = new()
    {
        typeof(ClaudeHomeServer.Services.Composition.IAppSubsystem),
        typeof(ClaudeHomeServer.Services.Composition.IAppPhaseSubsystem),
    };

    [Fact]
    public void AllCompositionInterfaces_Resolve_NonNull()
    {
        // Рефлексия: все интерфейсы из namespace ClaudeHomeServer.Services.Composition
        // в сборке ClaudeHomeServer.Core.
        var coreAssembly = typeof(ClaudeHomeServer.Services.Composition.IAppSubsystem).Assembly;
        var interfaces = coreAssembly.GetTypes()
            .Where(t => t.IsInterface)
            .Where(t => t.Namespace == "ClaudeHomeServer.Services.Composition")
            .Where(t => !Excluded.Contains(t))
            .ToArray();

        // Защита: если список пуст — рефлексия/namespace сломались, тест должен упасть.
        interfaces.Should().NotBeEmpty(
            "namespace ClaudeHomeServer.Services.Composition не содержит интерфейсов — " +
            "проверь, что GetTypes() видит сборку Core");

        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        var unresolved = interfaces
            .Where(t => sp.GetService(t) is null)
            .Select(t => t.Name)
            .ToArray();

        unresolved.Should().BeEmpty(
            $"Швы Composition-namespace, не зарегистрированные в DI: {string.Join(", ", unresolved)}");
    }
}
