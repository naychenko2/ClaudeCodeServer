using System.Reflection;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2c-6 (доработка): полнота таблицы «инструмент → право» по ВСЕМ зарегистрированным IMcpToolset.
/// </summary>
[Trait("Category", "Host")]
public class ActorRightToolCatalogTests
{
    /// <summary>
    /// Все инструменты всех тулсетов реестра: статические каталоги тулсета (поля/свойства типа
    /// IEnumerable&lt;McpToolSchema&gt; и статические методы без параметров или с одним bool), плюс
    /// Tools статических тулсетов. Рефлексия по типам из DI-реестра, а не по списку имён: новый
    /// тулсет попадает в сверку сам.
    /// </summary>
    internal static List<(string Server, string Tool)> LiveTools(IServiceProvider services)
    {
        var registry = services.GetRequiredService<McpToolsetRegistry>();
        var result = new List<(string, string)>();
        foreach (var name in registry.Names)
        {
            var toolset = registry.Find(name)!;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (toolset is IMcpStaticToolset st) foreach (var t in st.Tools) names.Add(t.Name);
            foreach (var schemas in StaticCatalogs(toolset.GetType()))
                foreach (var t in schemas) names.Add(t.Name);
            foreach (var n in names) result.Add((name, n));
        }
        return result;
    }

    private static IEnumerable<IEnumerable<McpToolSchema>> StaticCatalogs(Type type)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var f in type.GetFields(flags))
            if (typeof(IEnumerable<McpToolSchema>).IsAssignableFrom(f.FieldType) && f.GetValue(null) is IEnumerable<McpToolSchema> v)
                yield return v;
        foreach (var p in type.GetProperties(flags))
            if (p.GetIndexParameters().Length == 0 && typeof(IEnumerable<McpToolSchema>).IsAssignableFrom(p.PropertyType)
                && p.GetValue(null) is IEnumerable<McpToolSchema> v)
                yield return v;
        foreach (var m in type.GetMethods(flags))
        {
            if (!typeof(IEnumerable<McpToolSchema>).IsAssignableFrom(m.ReturnType)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 0)
            {
                if (m.Invoke(null, null) is IEnumerable<McpToolSchema> v) yield return v;
            }
            else if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
            {
                foreach (var flag in new[] { true, false })
                    if (m.Invoke(null, [flag]) is IEnumerable<McpToolSchema> v) yield return v;
            }
        }
    }

    // Глаголы записи/исполнения в имени инструмента. Читающая строка таблицы (Read) с таким глаголом —
    // подозрение на «записывающий инструмент записан как чтение»; осознанные исключения — в ReadVerbExceptions.
    private static readonly string[] WriteVerbs =
    [
        "create", "update", "delete", "write", "start", "run", "send", "set", "add", "remove", "save",
        "generate", "build", "shoot", "apply", "grant", "attach", "detach", "move", "rename", "commit",
        "stage", "index", "forget", "remember", "rethink", "mkdir", "archive", "continue", "adopt",
        "cancel", "concat", "mark", "stop", "train", "convert", "edit",
    ];

    // Читающие инструменты, в имени которых есть глагол записи, но записи нет
    private static readonly HashSet<string> ReadVerbExceptions = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void ТаблицаПравАктора_ПокрываетВсеИнструментыВсехТулсетовРеестра_БезЛишних()
    {
        using var factory = new TestWebApplicationFactory();
        _ = factory.CreateClient();
        var live = LiveTools(factory.Services);
        var liveKeys = live.Select(x => ActorToolRights.Key(x.Server, x.Tool)).ToHashSet();

        live.Select(x => x.Server).Distinct().Count().Should().BeGreaterThan(15, "сверка не должна быть вакуумной");
        liveKeys.Count.Should().BeGreaterThan(180);

        liveKeys.Except(ActorToolRights.All.Keys).Should().BeEmpty(
            "у каждого инструмента каждого тулсета должна быть строка таблицы прав актора: пишущий без строки "
            + "участнику закрыт default-deny, но строка обязана быть осознанной (Read/Needs/OwnerOnly)");

        // Мёртвые строки — только по серверам, чей каталог мы видим (higgsfield динамический и в таблице не нужен)
        var servers = live.Select(x => x.Server).ToHashSet();
        ActorToolRights.All.Keys.Where(k => servers.Contains(k.Split('/')[0])).Except(liveKeys).Should().BeEmpty(
            "в таблице нет мёртвых строк");
        ActorToolRights.All.Keys.Select(k => k.Split('/')[0]).Except(servers).Should().BeEmpty(
            "в таблице нет строк несуществующих серверов");
    }

    [Fact]
    public void ЧитающаяСтрокаТаблицы_НеНесётГлаголаЗаписиВИмени()
    {
        var suspicious = ActorToolRights.All
            .Where(kv => kv.Value.Requires is null && !ReadVerbExceptions.Contains(kv.Key))
            .Select(kv => kv.Key)
            .Where(k => k.Split('/')[1].Split('_').Any(w => WriteVerbs.Contains(w)))
            .ToList();
        suspicious.Should().BeEmpty(
            "инструмент с глаголом записи в имени записан как чтение — это дыра default-allow для агента участника; "
            + "перенесите его в OwnerOnly/Needs либо добавьте осознанное исключение в ReadVerbExceptions");
    }
}
