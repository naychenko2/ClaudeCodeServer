using System.Reflection;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.LocalBench.Places;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Проверки самого харнесса: банк кейсов читается, оракул контракта ловит нарушения.
/// Категории LocalBench у них НЕТ намеренно — живой модели они не требуют и обязаны
/// идти в CI: молча сломавшийся оракул выдал бы 100% валидности на любом мусоре, а
/// замер, который нельзя провалить, ничего не измеряет.
/// </summary>
public class LocalBenchHarnessTests
{
    [Fact]
    public void Банк_кейсов_места_читается_из_файла()
    {
        var bank = LocalBenchCases.Load(LocalActionCatalog.TaskNormalizeTitle);

        bank.Place.Should().Be(LocalActionCatalog.TaskNormalizeTitle);
        bank.Cases.Should().NotBeEmpty();
        bank.Cases.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Id));
        bank.Cases.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Input));
    }

    [Fact]
    public void Банк_несуществующего_места_даёт_понятную_ошибку()
    {
        var act = () => LocalBenchCases.Load("такого-места-нет");

        act.Should().Throw<FileNotFoundException>().WithMessage("*такого-места-нет*");
    }

    [Theory]
    // Ответ по контракту — с преамбулой и ```-обёрткой тоже: их снимает продуктовый разбор.
    [InlineData("{\"title\":\"Сделать отчёт\",\"dueHint\":\"завтра\"}")]
    [InlineData("{\"title\":\"Позвонить в банк\",\"dueHint\":null}")]
    [InlineData("{\"title\":\"Позвонить в банк\"}")]
    [InlineData("Вот результат:\n```json\n{\"title\":\"Купить молоко\",\"dueHint\":null}\n```")]
    public void Оракул_принимает_ответ_по_контракту(string raw) =>
        TaskNormalizeTitleOracle.Violation(raw).Should().BeNull();

    [Theory]
    // Каждая строка — нарушение, которое место переживает молча: продукт вернёт исходный
    // заголовок или потеряет срок, и без оракула это выглядело бы как успешный прогон.
    [InlineData("", "пустой ответ")]
    [InlineData("   ", "пустой ответ")]
    [InlineData("Сделать отчёт", "в ответе нет JSON-объекта")]
    [InlineData("{\"title\":\"Сделать отчёт\",", "в ответе нет JSON-объекта")]
    [InlineData("{\"dueHint\":\"завтра\"}", "нет ключа title")]
    [InlineData("{\"title\":123}", "title не строка")]
    [InlineData("{\"title\":\"\"}", "title пустой")]
    [InlineData("{\"title\":\"Сделать отчёт\\nи отправить\"}", "title в несколько строк")]
    [InlineData("{\"title\":\"\\\"Сделать отчёт\\\"\"}", "title в обрамляющих кавычках")]
    [InlineData("{\"title\":\"«Сделать отчёт»\"}", "title в обрамляющих кавычках")]
    [InlineData("{\"title\":\"Задача: сделать отчёт\"}", "служебный префикс")]
    [InlineData("{\"title\":\"Заголовок: сделать отчёт\"}", "служебный префикс")]
    [InlineData("{\"title\":\"Сделать отчёт\",\"dueHint\":42}", "dueHint не строка и не null")]
    public void Оракул_ловит_нарушение_контракта(string raw, string expectedFragment) =>
        TaskNormalizeTitleOracle.Violation(raw).Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain(expectedFragment);

    [Fact]
    public void Оракул_ловит_заголовок_длиннее_лимита()
    {
        var longTitle = new string('а', TaskNormalizeTitleOracle.MaxTitleChars + 1);

        TaskNormalizeTitleOracle.Violation($"{{\"title\":\"{longTitle}\"}}")
            .Should().Contain($"длиннее {TaskNormalizeTitleOracle.MaxTitleChars}");
    }

    [Fact]
    public void Оборванный_вывод_виден_как_обрыв_а_не_как_успех()
    {
        // Обрыв по потолку NumPredict приходит от движка причиной остановки "length";
        // JSON при этом не закрыт, и место молча вернёт исходный заголовок.
        var shot = new LocalBenchShot(
            ActionKey: LocalActionCatalog.TaskNormalizeTitle, Prompt: "…",
            RawAnswer: "{\"title\":\"Сделать отч", FinishReason: "length", StatusCode: 200,
            PromptTokens: 120, CompletionTokens: 256, NumPredict: 256, DurationMs: 900, Error: null);

        shot.Truncated.Should().BeTrue();
        shot.Answered.Should().BeTrue();
        TaskNormalizeTitleOracle.Violation(shot.RawAnswer).Should().NotBeNull();
    }

    [Fact]
    public void Отчёт_считает_валидность_обрывы_и_молчания_раздельно()
    {
        var report = new LocalBenchReport(LocalActionCatalog.TaskNormalizeTitle);
        report.Add(new LocalBenchRow("ок", true, null, false, true, 100, 20, "→ «Сделать отчёт»"));
        report.Add(new LocalBenchRow("обрыв", false, "JSON не разбирается", true, true, 300, 256, null));
        report.Add(new LocalBenchRow("молчание", false, "пустой ответ", false, false, 20_000, 0, null));

        report.Total.Should().Be(3);
        report.ValidCount.Should().Be(1);
        report.TruncatedCount.Should().Be(1);
        report.SilentCount.Should().Be(1);
        report.MedianMs.Should().Be(300);
        report.MinMs.Should().Be(100);
        report.MaxMs.Should().Be(20_000);
    }

    [Fact]
    public void Время_сводится_медианой_и_выброс_её_не_утягивает()
    {
        // Средним эти четыре кейса дали бы ~5 200 мс — цифру, которой не было ни у одного
        // вызова. Медиана держит сводку на том, что стенд показывает обычно.
        var report = new LocalBenchReport(LocalActionCatalog.TaskNormalizeTitle);
        foreach (var ms in new[] { 200L, 210L, 220L, 20_000L })
            report.Add(new LocalBenchRow($"к{ms}", true, null, false, true, ms, 20, null));

        report.MedianMs.Should().Be(215);
        report.MinMs.Should().Be(200);
        report.MaxMs.Should().Be(20_000);
    }

    [Fact]
    public void Каждый_бенч_класс_состоит_в_коллекции_LocalBench()
    {
        // Забытый атрибут коллекции у нового места — молчаливый дефект: класс полетит
        // параллельно соседям, вызовы встанут в очередь одной модели, и время в таблице
        // будет измерять очередь, а не место. Глазами это не ловится — только сторожем.
        var benchClasses = typeof(LocalBenchHarnessTests).Assembly.GetTypes()
            .Where(HasLocalBenchTrait)
            .ToArray();

        benchClasses.Should().NotBeEmpty("иначе сторож проходит вхолостую");
        foreach (var type in benchClasses)
            CollectionNameOf(type).Should().Be(TestCollections.LocalBench,
                $"класс {type.Name} с трейтом LocalBench обязан быть в коллекции LocalBench");
    }

    // Имя коллекции и значения трейта живут в аргументах атрибутов, публичных свойств
    // у них нет — читаем через метаданные.
    private static string? CollectionNameOf(Type type) =>
        type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(CollectionAttribute)
                        && a.ConstructorArguments.Count == 1)
            .Select(a => (string?)a.ConstructorArguments[0].Value)
            .FirstOrDefault();

    private static bool HasLocalBenchTrait(Type type) =>
        type.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute)
            && a.ConstructorArguments.Count == 2
            && (string?)a.ConstructorArguments[0].Value == "Category"
            && (string?)a.ConstructorArguments[1].Value == "LocalBench");

    [Fact]
    public void Раннер_берёт_ограничители_из_профиля_места()
    {
        // Профиль места — источник правды ограничителей: у task-normalize-title это Small
        // с потолком вывода 256, на котором и ловится обрыв. Разойдётся — замер будет про
        // другие лимиты, чем продуктовый маршрут.
        var action = LocalActionCatalog.Find(LocalActionCatalog.TaskNormalizeTitle);

        action.Should().NotBeNull();
        action!.Profile.Should().Be(CheapProfile.Small);
        LocalActionCatalog.ProfileDefaults[action.Profile].NumPredict.Should().Be(256);
    }
}
