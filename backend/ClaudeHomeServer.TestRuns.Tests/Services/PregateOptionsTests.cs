using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

// Набор проверок предгейта (секция Pregate:Checks) собирается из КОНФИГА: базовый
// appsettings.json плюс машинная правка appsettings.Local.json. Набор — объект по имени
// проверки: массив в конфигурации .NET склеивается по индексу, и локальная запись тихо
// забирала у базового набора первый элемент, а своей проверке оставляла чужой WhenChanged.
// Проверка идёт на РЕАЛЬНОМ appsettings.json: иначе тест врал бы про форму, которой в
// проекте нет.
public class PregateOptionsTests
{
    private const string OwnCheckName = "моя проверка";

    private static string ServerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ClaudeHomeServer", "appsettings.json");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "ClaudeHomeServer");
        }
        throw new FileNotFoundException("Не найден appsettings.json сервера от " + AppContext.BaseDirectory);
    }

    private static string BaseJson() => File.ReadAllText(Path.Combine(ServerDir(), "appsettings.json"));

    private static JsonNode BaseChecks()
    {
        var root = JsonNode.Parse(BaseJson())!;
        return root["Pregate"]!["Checks"]!;
    }

    // Локальная правка повторяет ФОРМУ базовой секции — так её и пишет человек в
    // appsettings.Local.json. Форма берётся из базы намеренно: если базовый набор снова
    // станет массивом, локальная правка станет массивом же, и склейка по индексу отнимет
    // у базы первый элемент — тест обязан покраснеть именно на этом.
    private static JsonNode LocalChecks(JsonNode baseChecks)
    {
        var own = new JsonObject
        {
            ["Name"] = OwnCheckName,
            ["Command"] = "sh",
            ["Args"] = new JsonArray("-c", "true"),
        };

        if (baseChecks is JsonArray)
            return new JsonArray(own);

        return new JsonObject { [OwnCheckName] = own };
    }

    private static IConfiguration ConfigWithLocalOverride(JsonNode baseChecks) =>
        ConfigWithLocal(LocalChecks(baseChecks));

    private static IConfiguration ConfigWithLocal(JsonNode localChecks)
    {
        var local = new JsonObject
        {
            ["Pregate"] = new JsonObject { ["Checks"] = localChecks },
        };

        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(BaseJson())))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(local.ToJsonString())))
            .Build();
    }

    // Базовая проверка не должна выпадать: локальная правка добавляется к набору, а не
    // замещает элемент с тем же индексом
    [Fact]
    public void ЛокальнаяПравка_СохраняетБазовыйНаборЦеликом()
    {
        var baseChecks = BaseChecks();

        var options = PregateOptions.From(ConfigWithLocalOverride(baseChecks));

        options.Checks.Select(c => c.Name).Should().Contain(["tsc", "lint:design", "build", "быстрые тесты", "сторож длительности", "сторожа границ", OwnCheckName],
            "базовый набор сохраняется целиком, своя проверка добавляется к нему");
        options.Problem.Should().BeNull();
        baseChecks.Should().BeOfType<JsonObject>(
            "секция Pregate:Checks обязана быть объектом по имени проверки: массив склеивается между файлами по индексу");
    }

    // Своя проверка не наследует фильтр WhenChanged чужой: без заданного — применяется всегда
    // (пустой WhenChanged); базовая проверка сохраняет свой
    [Fact]
    public void СвояПроверка_НеНаследуетWhenChangedЧужой()
    {
        var baseChecks = BaseChecks();

        var options = PregateOptions.From(ConfigWithLocalOverride(baseChecks));

        var own = options.Checks.Single(c => c.Name == OwnCheckName);
        own.WhenChanged.Should().BeEmpty("своя проверка без заданного WhenChanged применяется всегда, а не по чужому фильтру");
        own.Command.Should().Be("sh");
        own.Args.Should().Equal(["-c", "true"]);

        var tsc = options.Checks.Single(c => c.Name == "tsc");
        tsc.WhenChanged.Should().Equal(["frontend/"], "базовая проверка сохраняет свой фильтр");
    }

    // Явно заданный WhenChanged — свой: применяется по своему фильтру
    [Fact]
    public void СвояПроверка_СЯвнымWhenChanged_ПрименяетсяПоСвоему()
    {
        var local = new JsonObject
        {
            [OwnCheckName] = new JsonObject
            {
                ["Command"] = "sh",
                ["WhenChanged"] = new JsonArray("backend/"),
            },
        };

        var options = PregateOptions.From(ConfigWithLocal(local));

        var own = options.Checks.Single(c => c.Name == OwnCheckName);
        own.WhenChanged.Should().Equal(["backend/"], "явно заданный фильтр — свой, а не чужой");
    }

    // Порядок: базовый набор не задвигается своей правкой, добавка уходит в конец
    [Fact]
    public void СвояПроверка_УходитВКонецНабора()
    {
        var baseChecks = BaseChecks();

        var options = PregateOptions.From(ConfigWithLocalOverride(baseChecks));

        options.Checks.Select(c => c.Name).Should().Equal(["tsc", "lint:design", "build", "быстрые тесты", "сторож длительности", "сторожа границ", OwnCheckName]);
    }

    // Быстрый гейт (фича «Ускорение тестов», Э3): набор Fast — исключением категорий, после сборки
    // (Order 30) и с --no-build, только при правках backend/
    [Fact]
    public void БыстрыеТесты_ПослеСборки_ИсключениемКатегорий()
    {
        var options = PregateOptions.From(ConfigWithLocalOverride(BaseChecks()));

        var fast = options.Checks.Single(c => c.Name == "быстрые тесты");
        fast.Order.Should().Be(35);
        fast.WhenChanged.Should().Equal("backend/");
        // Монолит и Llm.Tests идут шардами по классам (scripts/test-shards.py --run-fast), остальное — одним процессом
        fast.Command.Should().Be("python3");
        fast.Args.Should().ContainInOrder("-I", "scripts/test-shards.py", "--run-fast", "backend/ClaudeHomeServer.slnx",
            "--split", "backend/ClaudeHomeServer.Tests=3,backend/ClaudeHomeServer.Llm.Tests=2", "--filter",
            "Category!=Host&Category!=Slow&Category!=Integration&Category!=Node&Category!=Bench",
            "--results", ".cc-attachments/pregate-fast");

        // Сторож длительности читает TRX этого же прогона, порог — дефолт скрипта (5 с)
        var guard = options.Checks.Single(c => c.Name == "сторож длительности");
        guard.Order.Should().Be(36);
        guard.Args.Should().Equal("-I", "scripts/test-profile.py", ".cc-attachments/pregate-fast", "--check", "--latest-run");
    }

    // Старая форма (массив) — не молчаливый пустой набор: предгейт обязан отказать с текстом
    [Fact]
    public void СекцияМассивом_НаборНеСобираетсяИВиднаПричина()
    {
        // база — объект, локальная правка — массив: ключи «0», «1» рядом с именами проверок
        var options = PregateOptions.From(ConfigWithLocal(new JsonArray(new JsonObject { ["Command"] = "sh" })));

        options.Problem.Should().NotBeNull("набор из массива собирать нельзя — причину обязан видеть и человек, и предгейт");
        options.Problem.Should().Contain("МАССИВОМ");
        options.Checks.Should().BeEmpty();
    }

    // Списки ВНУТРИ проверки остаются массивами: .NET склеивает их между файлами по индексу,
    // и «задать целиком» из Local нельзя. Правило записано в docs/architecture/conventions.md —
    // этот тест доказывает, что оно правдиво, чтобы правило не разошлось с кодом молча.
    [Fact]
    public void СписокВнутриПроверки_СклеиваетсяПоИндексу_ИЦеликомНеПереопределяется()
    {
        var local = new JsonObject
        {
            // Переопределение существующей базовой проверки: Args короче базовых
            ["tsc"] = new JsonObject { ["Args"] = new JsonArray("tsc") },
        };

        var options = PregateOptions.From(ConfigWithLocal(local));

        var tsc = options.Checks.Single(c => c.Name == "tsc");
        tsc.Args.Should().Equal(["tsc", "-b"],
            "списки внутри проверки склеиваются по индексу: базовый хвост остаётся, задать список "
            + "целиком из Local нельзя — свою проверку заводят под своим ключом");
    }

    // Имя с «:» конфигурация разъехала бы по пути («lint:design» → «lint» → «design»):
    // такая проверка осталась бы без Command — ловим это при сборке набора
    [Fact]
    public void ИмяСДвоеточием_НаборНеСобираетсяИВиднаПричина()
    {
        var options = PregateOptions.From(ConfigWithLocal(new JsonObject
        {
            ["lint:design"] = new JsonObject { ["Command"] = "npm" },
        }));

        options.Problem.Should().NotBeNull();
        options.Problem.Should().Contain("«:");
        options.Checks.Should().BeEmpty();
    }

    // Два двоеточия в имени разъезжают его по трём уровням («a:b:c» → «a» → «b» → «c» →
    // Command). Признак разъезда ищется на любой глубине: проверка одного уровня такой набор
    // пропускала — он собирался как проверка с пустым Command, и предгейт падал красным
    // «в конфиге Pregate не задан Command» вместо внятного отказа
    [Fact]
    public void ИмяСДвумяДвоеточиями_НаборНеСобираетсяИВиднаПричина()
    {
        var options = PregateOptions.From(ConfigWithLocal(new JsonObject
        {
            ["a:b:c"] = new JsonObject { ["Command"] = "npm" },
        }));

        options.Problem.Should().NotBeNull();
        options.Problem.Should().Contain("«a:b:c»", "причина называет разъехавшееся имя целиком");
        options.Checks.Should().BeEmpty();
    }

    // Признак ловушки — именно имя с двоеточием, а не «вложенный объект с нечисловыми
    // ключами»: свойство проверки объектом (например «Env») собирается, а не отказывает.
    // Без этого будущее свойство объектом убило бы весь набор с ложным диагнозом.
    [Fact]
    public void ВложенныйОбъектВСвойствеПроверки_НеСчитаетсяИменемСДвоеточием()
    {
        var options = PregateOptions.From(ConfigWithLocal(new JsonObject
        {
            [OwnCheckName] = new JsonObject
            {
                ["Command"] = "sh",
                ["Env"] = new JsonObject { ["FOO"] = "1" },
            },
        }));

        options.Problem.Should().BeNull(
            "вложенный объект — это свойство проверки объектом, а не разъехавшееся по «:» имя");
        options.Checks.Should().ContainSingle(c => c.Name == OwnCheckName);
    }

    // Даже когда у проверки нет ни одного собственного свойства, вложенный объект с чужими
    // ключами (не свойствами проверки) — не разъезд имени
    [Fact]
    public void ВложенныйОбъектСЧужимиКлючами_НеСчитаетсяИменемСДвоеточием()
    {
        var options = PregateOptions.From(ConfigWithLocal(new JsonObject
        {
            [OwnCheckName] = new JsonObject { ["Env"] = new JsonObject { ["FOO"] = "1" } },
        }));

        options.Problem.Should().BeNull("признак ловушки — свойства проверки во вложенном объекте, а не любой вложенный объект");
        options.Checks.Should().ContainSingle(c => c.Name == OwnCheckName);
    }
}
