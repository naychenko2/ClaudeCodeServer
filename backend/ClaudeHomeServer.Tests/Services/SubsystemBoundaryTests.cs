using System.Reflection;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож границ вертикалей: типы из <c>Services/Video</c> не должны ссылаться на
/// типы других сервисных вертикалей. Правило — default-deny: разрешено ссылаться
/// только на «спинку» (BCL, ASP.NET, модели) и явно перечисленные служебные
/// вертикали, остальные <c>ClaudeHomeServer.Services.*</c> считаются чужой территорией.
///
/// Под «спинкой» понимается минимальный шов, через который любая вертикаль
/// пользуется платформой и общими сервисами:
/// <c>System.*</c>, <c>Microsoft.*</c>, <c>ClaudeHomeServer.Models</c>,
/// <c>ClaudeHomeServer.Services.Http</c> (общие HTTP-утилиты),
/// <c>ClaudeHomeServer.Services.Security</c> (общие защитные примитивы),
/// <c>ClaudeHomeServer.Services.Composition</c> (контракт <c>IAppSubsystem</c>),
/// <c>ClaudeHomeServer.Services.Mcp</c> (сознательная граница для
/// <c>McpSecretStore</c>) и сам <c>ClaudeHomeServer.Services.Video</c>.
///
/// Всё прочее под <c>ClaudeHomeServer.Services.*</c> (Desktop, Backup, Llm,
/// Images, Tts, Deploy, Memory, Turn, Docs, Git и т.п.) — нарушение. Список
/// не дописывается под каждую новую вертикаль: появилась новая — тест автоматом
/// ловит любую ссылку на неё, и повод обсудить шов. Подход — как в <c>PiiRules</c>
/// (default-deny с явным allow-list).
///
/// Тест работает через рефлексию типов, а не через чтение исходного файла:
/// один из существующих стражей (<c>McpToolsetStabilityTests</c> через <c>FindSource</c>
/// и <c>MethodBody</c>) сломался на прошлом этапе просто от переименования метода
/// <c>PromptToolsSinkFor</c> → <c>SafePromptSnapshotAttach</c>, и пришлось чинить
/// отдельной задачей. Источник правды тут — сборка, а не текст.
///
/// Что НЕ проверяется осознанно: интерфейсы и базовые классы (за пределами четырёх
/// мест ниже). Если потребуется — расширим в следующем шаге.
/// </summary>
public class SubsystemBoundaryTests
{
    /// <summary>Неймспейсы, на которые Video ИМЕТ ПРАВО ссылаться. Совпадение по
    /// неймспейсу или по префиксу «X.» (на случай поднеймспейсов).</summary>
    private static readonly string[] AllowedNamespacePrefixes =
    {
        // BCL и платформа — спинка для всех.
        "System",
        "Microsoft",
        // Доменные модели — разделяемые POCO, не сервисная логика.
        "ClaudeHomeServer.Models",
        // Спинка из общего кода сервисов: HTTP-утилиты, защитные примитивы,
        // контракт подсистем и MCP-секреты (сознательная граница, см. ADR-014).
        "ClaudeHomeServer.Services.Http",
        "ClaudeHomeServer.Services.Security",
        "ClaudeHomeServer.Services.Composition",
        "ClaudeHomeServer.Services.Mcp",
        // Сам проверяемый — внутри вертикали ссылаться на себя можно.
        "ClaudeHomeServer.Services.Video",
    };

    [Fact]
    public void Video_НеСсылаетсяНаДругиеВертикали()
    {
        // Привязка к типу из Video как точке входа в нужную сборку: проект тестов
        // ссылается на ClaudeHomeServer, typeof(...).Assembly гарантированно даёт её.
        var videoAssembly = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;

        var videoTypes = CollectTypesInNamespaceTree(
            videoAssembly,
            "ClaudeHomeServer.Services.Video");

        var violations = new List<string>();

        foreach (var type in videoTypes)
        {
            // Дедупликация: одно поле record-а порождает несколько упоминаний (поле,
            // конструктор, свойство, get/set, Equals/PrintMembers). В отчёте достаточно
            // одной строки на пару «тип-источник → тип-нарушитель».
            var seen = new HashSet<(string, string)>();

            foreach (var referenced in CollectReferencedTypes(type))
            {
                if (!IsAllowed(referenced))
                {
                    seen.Add((type.FullName ?? type.Name, referenced.FullName ?? referenced.Name));
                }
            }

            foreach (var (owner, forbidden) in seen)
            {
                violations.Add(
                    $"{owner} ссылается на {forbidden} из чужой вертикали " +
                    "(нет в AllowedNamespacePrefixes)");
            }
        }

        violations.Should().BeEmpty(
            "типы из Services/Video должны ссылаться только на спинку (System.*, " +
            "Microsoft.*, Models) и явно разрешённые служебные вертикали " +
            "(Services.Http/Security/Composition/Mcp) либо на самих себя. " +
            "Любая ссылка на прочие Services.* — нарушение архитектурного правила " +
            "(см. CLAUDE.md/ADR-014). Найденные нарушения:\n" +
            string.Join("\n", violations));
    }

    private static IEnumerable<Type> CollectTypesInNamespaceTree(Assembly assembly, string namespaceRoot)
    {
        // Сам неймспейс + все вложенные поднеймспейсы.
        return assembly.GetTypes()
            .Where(t => t.Namespace == namespaceRoot
                        || (t.Namespace?.StartsWith(namespaceRoot + ".", StringComparison.Ordinal) ?? false));
    }

    private static IEnumerable<Type> CollectReferencedTypes(Type type)
    {
        // Поля: declared-only, чтобы не утонуть в чужом базовом классе; private тоже —
        // границу нарушает любой член, а не только публичный контракт.
        var memberBinding = BindingFlags.Public | BindingFlags.NonPublic
                          | BindingFlags.Instance | BindingFlags.Static
                          | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(memberBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(field.FieldType))
                yield return t;
        }

        foreach (var ctor in type.GetConstructors(memberBinding))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                foreach (var t in EnumerateTypeAndArgs(parameter.ParameterType))
                    yield return t;
            }
        }

        // Публичные методы и свойства — все, включая унаследованные. Унаследованный
        // метод с типом из запрещённой вертикали — это часть публичного контракта
        // проверяемого типа (через него ссылка «торчит наружу»), и сторож должен
        // её ловить. Обход свойств отдельным проходом: get/set не попадают в GetMethods
        // под теми именами, по которым мы ищем нарушение.
        var publicBinding = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        foreach (var property in type.GetProperties(publicBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(property.PropertyType))
                yield return t;
        }

        foreach (var method in type.GetMethods(publicBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(method.ReturnType))
                yield return t;

            foreach (var parameter in method.GetParameters())
            {
                foreach (var t in EnumerateTypeAndArgs(parameter.ParameterType))
                    yield return t;
            }
        }
    }

    private static IEnumerable<Type> EnumerateTypeAndArgs(Type? type)
    {
        if (type is null) yield break;

        // Byref (ref/out/in SomeType) — ParameterType вернёт SomeType&, у которого
        // Namespace = null и IsAllowed безусловно пропустит (не Services.*). Снимаем обёртку сразу.
        if (type.IsByRef)
        {
            foreach (var t in EnumerateTypeAndArgs(type.GetElementType()))
                yield return t;
            yield break;
        }

        // Nullable<T> → T (System.Nullable<...> не интересует, зато интересует аргумент).
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            yield return underlying;
            yield break;
        }

        // Сам тип (например, List<DesktopFoo> сам по себе не из разрешённого неймспейса,
        // но мы его всё равно отдаём — IsAllowed отфильтрует).
        yield return type;

        if (type.IsGenericType)
        {
            // Рекурсия: вложенные generic-аргументы тоже надо раскрыть, иначе тип вида
            // Nested<Wrap<DesktopFoo>> пройдёт мимо стора (Namespace внешнего generic
            // может быть «нейтральным»).
            foreach (var arg in type.GetGenericArguments())
            {
                yield return arg;
                foreach (var t in EnumerateTypeAndArgs(arg))
                    yield return t;
            }
        }

        // Массивы, указатели — раскрываем рекурсивно (на глубину 1 достаточно:
        // массив массивов экзотика, на которую обопрёмся, если встретим).
        if (type.HasElementType)
        {
            foreach (var t in EnumerateTypeAndArgs(type.GetElementType()))
                yield return t;
        }
    }

    private static bool IsAllowed(Type type)
    {
        var ns = type.Namespace;
        if (ns is null) return true; // Безымянный namespace — не Services.*, разрешаем.

        foreach (var allowed in AllowedNamespacePrefixes)
        {
            if (ns == allowed
                || ns.StartsWith(allowed + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
