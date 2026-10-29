using System.Reflection;
using Xunit;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Сторож локальности определений xunit-коллекций.
///
/// Зачем: xunit 2.9.2 находит [CollectionDefinition] только в прогоняемой сборке; определения
/// из TestKit не применялись никогда. [Collection(имя)] без локального определения молча
/// становится неявной коллекцией «по имени»: классы сериализованы внутри неё, но идут
/// параллельно со всем прогоном. Так классы TestCollections.Inotify в монолите упёрлись
/// в общий бюджет inotify пользователя («max_user_instances has been reached»), а под
/// setrlimit из InotifyProbe соседи падали бы на EMFILE (разрез 3908fd11).
///
/// Что проверяет:
/// 1) каждое имя TestCollections, употреблённое через [Collection(...)] в вызывающей сборке,
///    имеет [CollectionDefinition] в ЭТОЙ же сборке;
/// 2) Inotify определён с DisableParallelization — без флага сериализация коллекции не
///    защищает от конкурентов за fd/inotify-бюджет процесса;
/// 3) в самом TestKit определений нет — чтобы удалённые «мёртвые» не вернулись.
///
/// Имена атрибутов публичными свойствами не отдаются (проверено рефлексией по 2.9.2:
/// у CollectionAttribute/CollectionDefinitionAttribute имя только в аргументе конструктора),
/// поэтому чтение — через CustomAttributeData.
///
/// Граница: сторож проверяет только те сборки, которые его вызывают. Новый тест-проект,
/// использующий TestCollections без вызова сторожа, он не поймает — правило записано
/// в шапке TestCollections.cs.
/// </summary>
internal static class CollectionDefinitionGuard
{
    internal static void AssertLocalDefinitions(Assembly testsAssembly)
    {
        // Имена берём из констант TestCollections рефлексией — новое имя попадает в сторож само.
        var knownNames = typeof(TestCollections)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue())
            .ToHashSet();

        var usedNames = testsAssembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData()
                .Where(a => a.AttributeType == typeof(CollectionAttribute))
                .Select(a => AttributeName(a, t)))
            .Where(knownNames.Contains)
            .ToHashSet();

        var definitions = testsAssembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData()
                .Where(a => a.AttributeType == typeof(CollectionDefinitionAttribute))
                .Select(a => (Name: AttributeName(a, t),
                    DisableParallelization: NamedFlag(a, nameof(CollectionDefinitionAttribute.DisableParallelization)))))
            .GroupBy(d => d.Name)
            .ToDictionary(g => g.Key, g => g.Any(d => d.DisableParallelization));

        var assemblyName = testsAssembly.GetName().Name ?? "<сборка>";
        foreach (var name in usedNames)
        {
            var defined = definitions.TryGetValue(name, out var serializing);
            Assert.True(defined,
                $"Коллекция «{name}» используется в {assemblyName}, но [CollectionDefinition] в этой сборке нет: " +
                "xunit не подхватывает определения из TestKit — они обязаны лежать в сборке самих тестов " +
                "(образец: TestCollectionDefinitions.cs в ClaudeHomeServer.Tests и ClaudeHomeServer.Llm.Tests).");

            if (name == TestCollections.Inotify)
            {
                Assert.True(serializing,
                    $"Определение коллекции «{TestCollections.Inotify}» в {assemblyName} без DisableParallelization: " +
                    "без флага её классы идут параллельно со всем прогоном и упёрлись бы в общий бюджет " +
                    "inotify и лимит дескрипторов процесса (см. InotifyProbe).");
            }
        }

        var testKitDefinitions = typeof(TestCollections).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData()
                .Where(a => a.AttributeType == typeof(CollectionDefinitionAttribute)))
            .ToList();
        Assert.True(testKitDefinitions.Count == 0,
            "В TestKit снова появились [CollectionDefinition]: TestKit не прогоняется как тест-сборка, " +
            "такие определения молчат и ничего не дают — определения должны лежать в сборках тестов.");
    }

    // Имя коллекции — из аргумента конструктора атрибута; у параметного [CollectionDefinition]
    // имени нет, берём FullName определяющего типа: с константным именем из TestCollections
    // так определение не совпадёт, и сторож потребует явного имени.
    private static string AttributeName(CustomAttributeData attribute, Type carrier) =>
        attribute.ConstructorArguments.Count > 0 && attribute.ConstructorArguments[0].Value is string name
            ? name
            : carrier.FullName ?? carrier.Name;

    private static bool NamedFlag(CustomAttributeData attribute, string name) =>
        attribute.NamedArguments.Any(n => n.MemberInfo.Name == name && n.TypedValue.Value as bool? == true);
}
