using System.Reflection;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Tests.Services;

// Тест-генератор карты событий v1 шины TurnEventBus (ADR-013).
// Сторож двух инвариантов:
//   1) у каждого публичного неабстрактного типа в Services.Turn, реализующего
//      ITurnNotification/ITurnFilter, есть уникальная константа Event;
//   2) карта в docs/adr/ADR-013-turn-event-bus.md совпадает с множеством Event-констант
//      в коде — единственный способ добавить событие это поменять и код, и ADR одним
//      коммитом; иначе CI красный.
public sealed class TurnEventsMapContractTests
{
    private static readonly string EventNamespace = typeof(PromptAssembling).Namespace!;

    private static IEnumerable<Type> EnumerateEventTypes()
    {
        // Якорь берём по СОБЫТИЮ из вертикали, а не по интерфейсу: контракты шины
        // (ITurnNotification/ITurnFilter) живут в Core (ADR-013), а сами события —
        // в ClaudeHomeServer.Turn. По интерфейсу мы получили бы сборку Core, где
        // событий нет, и тест прошёл бы вхолостую на пустом множестве.
        var asm = typeof(PromptAssembling).Assembly;
        return asm.GetTypes()
            .Where(t => !t.IsAbstract
                && !t.IsGenericTypeDefinition
                && t.IsClass
                && t.Namespace == EventNamespace
                && (typeof(ITurnNotification).IsAssignableFrom(t)
                    || typeof(ITurnFilter).IsAssignableFrom(t)));
    }

    private static string? TryGetEventConst(Type t)
    {
        // const string Event — самый дешёвый и стабильный канал для ADR. ReadOnly-поле тоже
        // годится, но `const` лучше тем, что литерал видно в самом типе без зависимости
        // от того, как поле инициализируется.
        var field = t.GetField("Event",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field is null) return null;
        if (field.FieldType != typeof(string)) return null;
        return (string?)field.GetRawConstantValue();
    }

    [Fact]
    public void ВсеТипыСобытийИмеютУникальныйКонстантуEvent()
    {
        var types = EnumerateEventTypes().ToList();
        Assert.NotEmpty(types);

        var seen = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            var evt = TryGetEventConst(t);
            Assert.False(string.IsNullOrEmpty(evt),
                $"Тип события {t.FullName} не объявил const string Event — добавьте его, иначе карта в ADR расходится с кодом.");

            if (seen.TryGetValue(evt!, out var previous))
            {
                Assert.Fail(
                    $"Событие '{evt}' объявлено дважды: в {previous.FullName} и {t.FullName}. " +
                    "Константа Event должна быть уникальной — иначе фильтр подписчика по Event неоднозначен.");
            }
            seen[evt!] = t;
        }
    }

    [Fact]
    public void Карта_ADR_Совпадает_СКодом()
    {
        // События из кода — единственный источник правды. ADR — его отражение в markdown.
        var codeEvents = EnumerateEventTypes()
            .Select(TryGetEventConst)
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var adrPath = ResolveAdrPath();
        Assert.True(File.Exists(adrPath),
            $"ADR не найден по пути '{adrPath}'. Тест-генератор карты обязан читать файл — без него карта в доке собирается из кода не работает.");

        var adrText = File.ReadAllText(adrPath);
        // Ищем идентификаторы событий в backticks: `prompt/assembling`,
        // `subagent/completed` и т.п. Префикс из четырёх имён — канонические пространства
        // имён шины; в коде других префиксов быть не должно, и ADR их не упоминает.
        var adrEvents = Regex.Matches(adrText, @"`(turn|prompt|tool|subagent)/[a-z0-9\-]+`")
            .Select(m => m.Value.Trim('`'))
            .ToHashSet(StringComparer.Ordinal);

        var missingInAdr = codeEvents.Except(adrEvents, StringComparer.Ordinal).OrderBy(s => s).ToList();
        var extraInAdr = adrEvents.Except(codeEvents, StringComparer.Ordinal).OrderBy(s => s).ToList();

        Assert.Empty(missingInAdr);
        Assert.Empty(extraInAdr);
    }

    // Тестовый бинарник лежит в backend/ClaudeHomeServer.Tests/bin/<cfg>/<tfm>/; корень репо — на 4 уровня выше.
    private static string ResolveAdrPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "adr", "ADR-013-turn-event-bus.md");
            if (File.Exists(candidate)) return candidate;
        }
        // Фолбэк для совсем уж нестандартной раскладки — пусть тест упадёт с понятным сообщением.
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "adr", "ADR-013-turn-event-bus.md");
    }
}
