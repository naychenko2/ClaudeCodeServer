using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож DI-контейнера: одна реализация (ImplementationType) не должна быть зарегистрирована
// как Singleton несколько раз. `AddSingleton<I, C>()` без фабрики создаёт ВТОРОЙ экземпляр C
// рядом с первым (от `AddSingleton<C>()`) — два параллельных стейта, рассинхрон кэшей и
// драка за статические резолверы. На этом выезжал TaskManager 2026-09-13/14, когда экземпляр
// за `ITaskStatusReader` перезаписывал статические резолверы `Session.*` и иерархия чатов для
// задач, созданных после старта процесса, переставала резолвиться.
//
// Форвардер `AddSingleton<I>(sp => sp.GetRequiredService<C>())` под сторож НЕ попадает
// (ImplementationType == null у фабрики) — это и есть правильная форма.
//
// Критерий приёмки: мутация — снятая форвардер-регистрация (или возврат `AddSingleton<I, C>()`)
// = падение сторожа; вернёшь форвардер = зелёный.
//
// Защита от пустого фильтра: явный ассерт, что singleton-дескрипторы с ImplementationType
// вообще есть в коллекции (без него пустой набор проходил бы зелёным — прецедент у сторожей
// границ подсистем: 17/17 зелёных при нулевом наборе).
public class DuplicateSingletonRegistrationTests
{
    // Дескрипторы с `ImplementationFactory` (форвардеры `sp.GetRequiredService<T>()`
    // и `AddHostedService(factory)` из `AddGatedHostedFrom`) под сторож не попадают —
    // фильтр ниже отсеивает их по `ImplementationType is not null`. Поэтому явного
    // исключения по `ServiceType == typeof(IHostedService)` тут нет: оно прикрыло бы
    // дефект `AddSingleton<X>() + AddHostedService<X>()` (два экземпляра X — стейт
    // фонового цикла и подписки на одном объекте, резолвы на другом), и сторож бы
    // его не поймал. См. `KnowledgeSubsystemRegistrationTests` и
    // `DossiersSubsystemRegistrationTests` — там этот инвариант проверяется явно.

    // Внешние библиотеки (`Microsoft.*`, `Yarp.*`) регистрируют собственные реализации
    // паттерном «один класс под несколько интерфейсов» (`IConfigureOptions<T1>/<T2>/<T3>`,
    // `IApplicationLifetime` + сам класс, `DirectForwardingHttpClientProvider` под два типа).
    // Мы их не контролируем и не правим — обходят сторож по namespace реализации. Если
    // внутри `ClaudeHomeServer.*` появится аналогичный паттерн — это дефект, и allow-list
    // не должен его прикрыть.
    private static bool IsExternalImplementationType(Type t)
    {
        var ns = t.Namespace;
        return ns is not null && (ns.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                                  ns.StartsWith("Yarp.", StringComparison.Ordinal));
    }

    private static bool IsExcluded(ServiceDescriptor d) =>
        d.ImplementationType is not null && IsExternalImplementationType(d.ImplementationType);

    [Fact]
    public void SingletonImplementations_НеПусты_СторожНеПроходитВпустую()
    {
        // Хост поднимается через WebApplicationFactory: `ConfigureServices` зовётся при сборке
        // IServiceProvider, и в этот момент `services` уже содержит весь набор регистраций
        // из Program.cs. Захватываем ссылку на коллекцию — после `BuildServiceProvider` менять
        // её поздно, но читать можно.
        using var factory = new TestWebApplicationFactory();
        IServiceCollection? captured = null;
        factory.WithWebHostBuilder(b => b.ConfigureServices(s => captured = s))
            .CreateClient();

        captured.Should().NotBeNull("хост должен подняться и пробросить IServiceCollection в хук");

        var singletonsWithType = captured!
            .Where(d => d.Lifetime == ServiceLifetime.Singleton)
            .Where(d => !IsExcluded(d))
            .Where(d => d.ImplementationType is not null)
            .ToArray();

        singletonsWithType.Should().NotBeEmpty(
            "если singleton-дескрипторов с ImplementationType нет вообще — фильтр или хук " +
            "сломаны, и сторож ниже будет проходить впустую. Прецедент 17/17 зелёных при " +
            "нулевом наборе у сторожей границ подсистем.");
    }

    [Fact]
    public void SingletonImplementations_ГруппаРазмеромБольше1_ДубльДефект()
    {
        using var factory = new TestWebApplicationFactory();
        IServiceCollection? captured = null;
        factory.WithWebHostBuilder(b => b.ConfigureServices(s => captured = s))
            .CreateClient();

        captured.Should().NotBeNull();

        // Группировка по ImplementationType: фабрики (ImplementationType == null) под сторож не
        // попадают — это и есть правильная форма форвардера. Случай «один класс реализует
        // несколько интерфейсов и зарегистрирован под каждым» — это и есть наш дефект
        // (второй экземпляр).
        var duplicates = captured!
            .Where(d => d.Lifetime == ServiceLifetime.Singleton)
            .Where(d => !IsExcluded(d))
            .Where(d => d.ImplementationType is not null)
            .GroupBy(d => d.ImplementationType!)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(
                ImplementationType: g.Key.FullName ?? g.Key.Name,
                ServiceTypes: g.Select(d => d.ServiceType.FullName ?? d.ServiceType.Name).ToArray(),
                Count: g.Count()))
            .OrderBy(x => x.ImplementationType, StringComparer.Ordinal)
            .ToArray();

        duplicates.Should().BeEmpty(string.Join("\n", duplicates.Select(d =>
            $"{d.ImplementationType} зарегистрирован как Singleton {d.Count} раз под " +
            $"ServiceType: [{string.Join(", ", d.ServiceTypes)}]")));
    }

    private readonly record struct DuplicateGroup(
        string ImplementationType,
        string[] ServiceTypes,
        int Count);
}
