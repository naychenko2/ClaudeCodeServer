using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож полноты таблицы <see cref="SubsystemBoundaryTests.Boundaries"/>:
/// каждая реализация <see cref="IAppSubsystem"/> в сборке имеет строку в таблице,
/// а вертикали без подсистемы (например, <c>ClaudeHomeServer.Services.Watchdog</c>)
/// перечислены явно. Без этой проверки новая подсистема/вертикаль, забытая в
/// таблице, проходит молча — рефлексия <see cref="SubsystemBoundaryTests"/>
/// смотрит только типы из namespace, перечисленных в Boundaries, и свежедобавленная
/// вертикаль остаётся за бортом.
///
/// Живой пример: <c>ClaudeHomeServer.Services.Watchdog</c> — 5 файлов в
/// <c>backend/ClaudeHomeServer/Services/Watchdog/</c>, ни одного <c>IAppSubsystem</c>,
/// но неймспейс вертикально оформленный и подключается в Program.cs как
/// обычные <c>AddSingleton</c>/<c>AddHostedService</c>. До этой проверки вертикаль
/// в Boundaries отсутствовала — сторож границ по ней не работал, и любая новая
/// зависимость из Watchdog в другую вертикаль прошла бы мимо.
/// </summary>
public class SubsystemBoundaryCoverageTests
{
    // Загрузка Main — иначе AppDomain.CurrentDomain.GetAssemblies() её не увидит
    // (см. комментарий в SubsystemBoundaryTests).
    static SubsystemBoundaryCoverageTests()
    {
        _ = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;
    }

    [Fact]
    public void AllIAppSubsystemImplementations_HaveBoundaryEntry()
    {
        // Раньше typeof(VideoSubsystem).Assembly давал единственную сборку ClaudeHomeServer.dll,
        // где жили все реализации IAppSubsystem. После выделения Core (и в будущем — отдельных
        // вертикальных сборок) нужен перебор ВСЕХ ClaudeHomeServer.* сборок: без него страж
        // увидит только Main (одну из многих) и решит, что остальных реализаций не
        // существует — забудешь добавить вертикаль в Boundaries, тест пройдёт зелёным.
        // Главная сборка `ClaudeHomeServer` (имя без суффикса — этап 0/1 ещё не вынес
        // вертикали, и она содержит почти все реализации) тоже входит в выборку.
        // Тестовая сборка `ClaudeHomeServer.Tests` намеренно исключена: там живут
        // `StubSubsystem`/`FakeSubsystem` (тестовые стабы IAppSubsystem), они нам
        // не нужны в реестре продовых вертикалей.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                    && (name == "ClaudeHomeServer" || name.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && name != "ClaudeHomeServer.Tests"
                    && !name.StartsWith("ClaudeHomeServer.Tests.", StringComparison.Ordinal);
            });

        // Не-интерфейсные реализации IAppSubsystem, не абстрактные. Distinct по namespace —
        // две реализации из одного namespace (теоретически) не должны раздувать отчёт.
        var subsystemNamespaces = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => typeof(IAppSubsystem).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract)
            .Select(t => t.Namespace!)
            .Where(ns => !string.IsNullOrEmpty(ns))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Namespace корневой вертикали из таблицы Boundaries. Берём первый элемент массива
        // Boundaries (там object[] из [0] = VerticalBoundary record).
        var boundaryRoots = SubsystemBoundaryTests.Boundaries
            .Select(o => ((SubsystemBoundaryTests.VerticalBoundary)o[0]).NamespaceRoot)
            .ToHashSet(StringComparer.Ordinal);

        // Вертикали без подсистемы. На сегодня — единственная (Watchdog). Список явный:
        // если появится ещё одна «вертикаль с неймспейсом, но без IAppSubsystem»,
        // добавляется строка сюда, и тест сразу это отразит.
        var verticalOnlyNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "ClaudeHomeServer.Services.Watchdog",
        };

        var allExpected = new HashSet<string>(boundaryRoots, StringComparer.Ordinal);
        foreach (var ns in verticalOnlyNamespaces) allExpected.Add(ns);

        var missingSubsystems = subsystemNamespaces
            .Where(ns => !allExpected.Contains(ns))
            .ToList();

        missingSubsystems.Should().BeEmpty(
            "каждая реализация IAppSubsystem в сборке должна быть в SubsystemBoundaryTests.Boundaries " +
            "(см. комментарий `// Reader/Images/Tts/Git/Deploy/...` в том файле). " +
            "Если подсистема новая — добавь её в Boundaries с собственным allow-list. " +
            "Если это вертикаль БЕЗ IAppSubsystem — добавь её в `verticalOnlyNamespaces` ниже. " +
            "Отсутствуют:\n" + string.Join("\n", missingSubsystems));
    }
}