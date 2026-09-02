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
    [Fact]
    public void AllIAppSubsystemImplementations_HaveBoundaryEntry()
    {
        var assembly = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;

        // Не-интерфейсные реализации IAppSubsystem, не абстрактные. Distinct по namespace —
        // две реализации из одного namespace (теоретически) не должны раздувать отчёт.
        var subsystemNamespaces = assembly.GetTypes()
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