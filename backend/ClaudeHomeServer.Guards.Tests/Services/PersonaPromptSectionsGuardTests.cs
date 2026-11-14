using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// П1e (разрез 2, §3 Г4/Г6): сторожа слоя секций промпта персоны — по сборке, не по тексту.
///
/// 1. <c>Persona.PromptSections</c> читают и пишут только <see cref="Services.PersonaManager"/>
///    (нормализация и сброс: PersonaManager.cs:376, 1046-1047) и
///    <see cref="Services.Turn.PromptSectionsContributor"/> (чтение для наложения:
///    PromptSectionsContributor.cs:62). Всё остальное — чужой читатель слоя: он либо
///    обошёл бы нормализацию менеджера, либо наложил бы слой мимо единой функции
///    (Г6, PromptSectionOverlay.Apply).
///
/// 2. Методы <see cref="IPromptSectionProvider"/> зовёт только тот же контрибьютор
///    (PromptSectionsContributor.cs:61). Реализация <see cref="SpecialtyPromptSectionProvider"/>
///    — НЕ вызов: её форвард (SpecialtyPromptSectionProvider.cs:17) зовёт конкретный
///    <c>SpecialtySettingsStore</c>, declaring-тип этого вызова — стор, не интерфейс, и
///    скан его не считает. Регистрация в DI — generic-аргумент резолва, тоже не вызов.
///
/// Свойства с тем же именем на чужих типах (<c>SpecialtyTemplateSettings.PromptSections</c>
/// у стора, <c>CreatePersonaRequest.PromptSections</c> / <c>UpdatePersonaRequest.PromptSections</c>
/// у DTO) — другие declaring-типы, под фильтр <c>DeclaringType == typeof(Persona)</c>
/// не попадают: это не слой персоны.
///
/// Источник правды — IL тел (через <see cref="BoundaryIlScanner.AllMethodsWithNested"/> и
/// <see cref="BoundaryIlScanner.CalledMethods"/>): доступ к свойству в IL — call/callvirt
/// к <c>get_PromptSections</c>/<c>set_PromptSections</c> с declaring-типом <c>Persona</c>.
/// Обход nested-типов (async-state-машины, замыкания) обязателен — без него доступ из
/// async-метода не виден. Рефлексия по сигнатурам и чтение исходников здесь не годятся —
/// тот же вывод, что у SubsystemBoundaryTests (сторож по тексту ломается на переименовании).
///
/// Предел метода (тот же, что у всех IL-сторожей): сериализация System.Text.Json
/// (персоны в personas.json и ответах REST) идёт рефлексией рантайма и в IL вызовом не
/// является — она полем владеет, но слоем не пользуется: ни нормализует, ни налагает.
/// </summary>
public class PersonaPromptSectionsGuardTests
{
    // Форс-загрузка сборок (CLAUDE.md, «Внутренние подсистемы»): сторож перебирает
    // AppDomain.CurrentDomain.GetAssemblies(), и неназванная сборка ленивая — без явного
    // референса тип не загружен и сторож проходит вакуумно по целой вертикали.
    static PersonaPromptSectionsGuardTests()
    {
        // Main: PersonaManager — единственный пишущий слой.
        _ = typeof(ClaudeHomeServer.Services.PersonaManager).Assembly;
        // Turn: PromptSectionsContributor — единственный читатель слоя и единственный
        // потребитель шва IPromptSectionProvider.
        _ = typeof(ClaudeHomeServer.Services.Turn.PromptSectionsContributor).Assembly;
        // Core: сама модель (Persona) и шов (IPromptSectionProvider).
        _ = typeof(Persona).Assembly;
        _ = typeof(IPromptSectionProvider).Assembly;
        // Llm: реализация шва — чтобы её форвард точно был в выборке и оставался
        // «не вызовом интерфейса», а не выпадал из проверки за незагруженностью.
        _ = typeof(SpecialtyPromptSectionProvider).Assembly;
    }

    /// <summary>Allow-list доступа к <c>Persona.PromptSections</c> по факту кода (разрез 2,
    /// Г4): менеджер — единственная запись с нормализацией, контрибьютор — единственное
    /// чтение для наложения. Третий легальный читатель — не добавляется молча: он повод
    /// пересмотреть границу (в отчёте шага).</summary>
    private static readonly string[] AllowedSectionAccessors =
    [
        "ClaudeHomeServer.Services.PersonaManager",
        "ClaudeHomeServer.Services.Turn.PromptSectionsContributor",
    ];

    /// <summary>Allow-list вызовов <see cref="IPromptSectionProvider"/> (Г6): базовое
    /// состояние специальности получает только контрибьютор — иначе появляется второй
    /// читатель базового слоя, а наложение поверх него было бы вне единой функции.</summary>
    private static readonly string[] AllowedProviderCallers =
    [
        "ClaudeHomeServer.Services.Turn.PromptSectionsContributor",
    ];

    [Fact]
    public void PersonaPromptSections_ЧитаютИПишут_ТолькоРазрешённыеТипы()
    {
        var edges = FindCalls(callee =>
            callee.DeclaringType == typeof(Persona)
            && callee.Name is "get_PromptSections" or "set_PromptSections");

        // Неввакуумность: эти обращения есть в коде сейчас (PersonaManager.cs:1046-1047,
        // PromptSectionsContributor.cs:62). Если скан их не нашёл — сломан сам скан или
        // слой исчез из кода; в обоих случаях зелёный здесь был бы пустой проверкой.
        edges.Should().Contain(e => e.Owner == typeof(ClaudeHomeServer.Services.PersonaManager)
                && e.Callee.Name == "set_PromptSections",
            "слой пишется только через PersonaManager.Update — если этого не видит скан, " +
            "он сломан (или запись ушла из менеджера — тогда allow-list пересматривается явно)");
        edges.Should().Contain(e => e.Owner == typeof(ClaudeHomeServer.Services.Turn.PromptSectionsContributor)
                && e.Callee.Name == "get_PromptSections",
            "слой читает контрибьютор (PromptSectionsContributor.cs:62) — без этого edge проверка вакуумна");

        var violations = edges
            .Where(e => !AllowedSectionAccessors.Contains(e.Owner.FullName ?? e.Owner.Name))
            .Select(e => $"{e.Owner.FullName} → {e.Callee.Name}")
            .Distinct()
            .ToList();

        violations.Should().BeEmpty(
            "Persona.PromptSections вне allow-list читают/пишут: " +
            string.Join("; ", violations) +
            " — новый читатель слоя означает обход нормализации менеджера или наложение " +
            "мимо PromptSectionOverlay.Apply (Г6)");
    }

    [Fact]
    public void IPromptSectionProvider_Зовёт_ТолькоPromptSectionsContributor()
    {
        var edges = FindCalls(callee => callee.DeclaringType == typeof(IPromptSectionProvider));

        // Неввакуумность: единственный потребитель шва есть в коде сейчас
        // (PromptSectionsContributor.cs:61).
        edges.Should().Contain(e => e.Owner == typeof(ClaudeHomeServer.Services.Turn.PromptSectionsContributor),
            "контрибьютор зовёт шов (PromptSectionsContributor.cs:61) — без этого edge проверка вакуумна");

        var violations = edges
            .Where(e => !AllowedProviderCallers.Contains(e.Owner.FullName ?? e.Owner.Name))
            .Select(e => $"{e.Owner.FullName} → {e.Callee.Name}")
            .Distinct()
            .ToList();

        violations.Should().BeEmpty(
            "базовое состояние специальности через IPromptSectionProvider получает кто-то кроме " +
            "контрибьютора: " + string.Join("; ", violations) +
            " — это второй читатель базового слоя вне единой точки наложения (Г6)");
    }

    /// <summary>Все обращения к целевому методу в продовых сборках процесса: для каждого
    /// типа каждой прод-сборки — IL всех его методов (включая nested) и целевые callee.
    /// Влажитель edge — верхний тип вызывающего: async-state-машина
    /// <c>PersonaManager+&lt;Update&gt;d__12</c> принадлежит PersonaManager.</summary>
    private static List<(Type Owner, MethodBase Callee)> FindCalls(Func<MethodBase, bool> isTarget)
    {
        var found = new List<(Type, MethodBase)>();
        foreach (var assembly in ProductionAssemblies())
        {
            foreach (var type in assembly.GetTypes())
            {
                var owner = TopLevelType(type);
                foreach (var method in BoundaryIlScanner.AllMethodsWithNested(type))
                {
                    foreach (var callee in BoundaryIlScanner.CalledMethods(method))
                    {
                        if (isTarget(callee)) found.Add((owner, callee));
                    }
                }
            }
        }
        return found;
    }

    /// <summary>Прод-сборки процесса: всё под <c>ClaudeHomeServer</c> кроме <c>*.Tests</c>
    /// (тот же фильтр, что у SubsystemBoundaryTests.cs:1559-1565). Тестовые сборки со своими
    /// стабами и Moq-прокси — не потребители слоя; TestKit/TestKit.Host остаются в выборке
    /// (они не «.Tests») — стаб, дёргающий шов вне контрибьютора, был бы находкой.</summary>
    private static IEnumerable<Assembly> ProductionAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                    && (name == "ClaudeHomeServer" || name.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && !name.EndsWith(".Tests", StringComparison.Ordinal);
            });

    private static Type TopLevelType(Type t)
    {
        while (t.DeclaringType is { } outer) t = outer;
        return t;
    }
}
