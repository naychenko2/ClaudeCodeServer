using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.LocalBench.Places;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Проверки оракулов шести мест оси A и их банков кейсов.
///
/// Категории LocalBench у них НЕТ намеренно — живой модели они не требуют и обязаны идти
/// в CI по той же причине, что и проверки пилотного оракула: молча сломавшийся оракул
/// выдал бы 100% валидности на любом мусоре, а замер, который нельзя провалить, ничего
/// не измеряет.
/// </summary>
public class LocalBenchOraclesTests
{
    private static readonly string[] Vocabulary = ["qa", "архитектура", "локальная-модель"];

    public static TheoryData<string> Places => new(
        LocalActionCatalog.TaskNormalizeTitle, LocalActionCatalog.NotesTags,
        LocalActionCatalog.TaskClassify, LocalActionCatalog.TaskDedup,
        LocalActionCatalog.PersonaVoice, LocalActionCatalog.ProjectIcon,
        LocalActionCatalog.ProjectBackground);

    [Theory]
    [MemberData(nameof(Places))]
    public void Банк_места_читается_и_несёт_двадцать_кейсов(string place)
    {
        // Двадцать — знаменатель из спецификации Батареи II: места сравнимы между собой
        // только на одном числе кейсов.
        var bank = LocalBenchCases.Load(place);

        bank.Place.Should().Be(place);
        bank.Cases.Should().HaveCount(20);
        bank.Cases.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Id));
        bank.Cases.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Input));
    }

    // ---------- notes-tags ----------

    [Theory]
    [InlineData("[\"qa\", \"архитектура\"]")]
    [InlineData("Вот теги:\n```json\n[\"локальная-модель\"]\n```")]
    public void Оракул_тегов_принимает_ответ_по_контракту(string raw) =>
        NotesTagsOracle.Violation(raw, Vocabulary).Should().BeNull();

    [Theory]
    [InlineData("", "пустой ответ")]
    [InlineData("qa, архитектура", "нет JSON-массива")]
    [InlineData("[]", "пустой массив тегов")]
    [InlineData("[\"qa\", 5]", "элемент массива не строка")]
    [InlineData("[\"#qa\"]", "решёткой")]
    [InlineData("[\"локальная модель\"]", "из нескольких слов")]
    [InlineData("[\"тестирование\"]", "вне словаря")]
    [InlineData("[\"qa\",\"qa\",\"qa\",\"qa\",\"qa\",\"qa\"]", "тегов больше 5")]
    public void Оракул_тегов_ловит_нарушение(string raw, string fragment) =>
        NotesTagsOracle.Violation(raw, Vocabulary).Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain(fragment);

    // ---------- task-classify ----------

    [Theory]
    [InlineData("{\"priority\":\"high\",\"labels\":[\"бэкенд\"]}")]
    [InlineData("{\"priority\":\"low\",\"labels\":[]}")]
    [InlineData("```json\n{\"priority\":\"urgent\",\"labels\":[\"сборка прода\"]}\n```")]
    public void Оракул_классификации_принимает_ответ_по_контракту(string raw) =>
        TaskClassifyOracle.Violation(raw).Should().BeNull();

    [Theory]
    [InlineData("", "пустой ответ")]
    [InlineData("high", "нет JSON-объекта")]
    [InlineData("{\"labels\":[]}", "нет ключа priority")]
    [InlineData("{\"priority\":\"высокий\",\"labels\":[]}", "вне перечисления")]
    [InlineData("{\"priority\":\"High\",\"labels\":[]}", "вне перечисления")]
    [InlineData("{\"priority\":\"high\"}", "нет ключа labels")]
    [InlineData("{\"priority\":\"high\",\"labels\":\"бэкенд\"}", "labels не массив")]
    [InlineData("{\"priority\":\"high\",\"labels\":[\"a\",\"b\",\"c\",\"d\"]}", "меток больше 3")]
    [InlineData("{\"priority\":\"high\",\"labels\":[\"метка из трёх слов\"]}", "длиннее двух слов")]
    public void Оракул_классификации_ловит_нарушение(string raw, string fragment) =>
        TaskClassifyOracle.Violation(raw).Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain(fragment);

    // ---------- task-dedup ----------

    [Theory]
    [InlineData("{\"duplicateId\":null,\"reason\":\"нет дубля\"}")]
    [InlineData("{\"duplicateId\":\"id-1\",\"reason\":\"та же работа\"}")]
    public void Оракул_дедупа_принимает_ответ_по_контракту(string raw) =>
        TaskDedupOracle.Violation(raw, ["id-1", "id-2"]).Should().BeNull();

    [Theory]
    // Выдуманный id продукт отбивает молча и возвращает «дубля нет» — по выходу места
    // этот отказ неотличим от честного null, увидеть его можно только оракулом.
    [InlineData("{\"duplicateId\":\"id-99\"}", "не из списка кандидатов")]
    [InlineData("{\"duplicateId\":\"null\"}", "строкой «null»")]
    [InlineData("{\"duplicateId\":42}", "не строка и не null")]
    [InlineData("{\"reason\":\"похоже\"}", "нет ключа duplicateId")]
    [InlineData("дубля нет", "нет JSON-объекта")]
    [InlineData("", "пустой ответ")]
    public void Оракул_дедупа_ловит_нарушение(string raw, string fragment) =>
        TaskDedupOracle.Violation(raw, ["id-1", "id-2"]).Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain(fragment);

    // ---------- persona-voice ----------

    [Theory]
    [InlineData("alena")]
    [InlineData("marina whisper")]
    [InlineData("none")]
    public void Оракул_голоса_принимает_ответ_по_контракту(string raw) =>
        PersonaVoiceOracle.Violation(raw).Should().BeNull();

    [Theory]
    [InlineData("", "пустой ответ")]
    [InlineData("голос подобрать не удалось", "голос не распознан")]
    // Два голоса в ответе: продукт возьмёт первый по тексту, то есть выберет за модель.
    [InlineData("alena или zahar", "несколько голосов")]
    // Амплуа, которого у голоса нет: SpeechKit отвечает 400, озвучка падает на дефолт.
    [InlineData("zahar whisper", "не поддержано голосом")]
    public void Оракул_голоса_ловит_нарушение(string raw, string fragment) =>
        PersonaVoiceOracle.Violation(raw).Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain(fragment);

    [Fact]
    public void Оракул_голоса_ловит_рассуждение_вместо_строки()
    {
        var raw = "Для этой персоны, пожалуй, лучше всего подойдёт голос alena — "
                  + "он спокойный и ровный, как раз под описанный характер";

        PersonaVoiceOracle.Violation(raw).Should().Contain("не одна строка");
    }

    // ---------- project-icon ----------

    [Fact]
    public void Оракул_значка_принимает_цепочку_из_двух_ходов() =>
        ProjectIconOracle.Violation(Turns(
            "{\"words\":[\"flag\",\"map\"]}",
            "{\"glyphs\":[{\"name\":\"flag\"}]}")).Should().BeNull();

    [Fact]
    public void Оракул_значка_ловит_негодный_ход_слов() =>
        ProjectIconOracle.Violation(Turns("слова: флаг, карта", "{\"glyphs\":[{\"name\":\"flag\"}]}"))
            .Should().Contain("ход слов отвергнут");

    [Fact]
    public void Оракул_значка_ловит_выдуманное_имя_иконки() =>
        // Имени «samovar» в наборе lucide нет — ровно та ошибка, ради которой схему
        // сделали двухходовой.
        ProjectIconOracle.Violation(Turns(
            "{\"words\":[\"samovar\"]}",
            "{\"glyphs\":[{\"name\":\"samovar\"}]}")).Should().Contain("ход выбора отвергнут");

    [Fact]
    public void Оракул_значка_ловит_несостоявшийся_ход_выбора() =>
        // Один ход в цепочке означает, что меню собрать не удалось и выбирать было не из
        // чего: слова по контракту, а значка у проекта нет.
        ProjectIconOracle.Violation(Turns("{\"words\":[\"flag\"]}"))
            .Should().Contain("ход выбора не состоялся");

    // ---------- project-background ----------

    [Fact]
    public void Оракул_фона_принимает_ответ_по_контракту() =>
        ProjectBackgroundOracle.Violation(Doodle(10, "green")).Should().BeNull();

    [Fact]
    public void Оракул_фона_ловит_полупустой_дудл() =>
        // Меньше порога годных фигур — продукт отбрасывает тайл как брак.
        ProjectBackgroundOracle.Violation(Doodle(3, "green")).Should().Contain("годных фигур меньше");

    [Fact]
    public void Оракул_фона_ловит_потерянный_ключ_цвета() =>
        // Тайл собрался, но цвет проекта из этого ответа взять неоткуда — молчаливая
        // потеря половины результата места.
        ProjectBackgroundOracle.Violation(Doodle(10, null)).Should().Contain("нет ключа цвета");

    [Theory]
    [InlineData("", "пустой ответ")]
    [InlineData("<svg><path d=\"M0 0\"/></svg>", "нет годного JSON")]
    public void Оракул_фона_ловит_ответ_без_данных(string raw, string fragment) =>
        ProjectBackgroundOracle.Violation(raw).Should().Contain(fragment);

    [Fact]
    public void Оборванный_ответ_фона_виден_как_обрыв_а_не_как_успех()
    {
        // Обрыв по потолку вывода приходит от движка причиной остановки "length": JSON
        // фигур не закрыт, и место молча оставляет проект на стандартном фоне.
        var turns = new LocalBenchTurns([new LocalBenchShot(
            ActionKey: LocalActionCatalog.ProjectBackground, Prompt: "…",
            RawAnswer: "{\"colorKey\":\"green\",\"shapes\":[{\"x\":10,\"y\":10,\"paths\":[\"M0 0 h10",
            FinishReason: "length", StatusCode: 200, PromptTokens: 300, CompletionTokens: 1024,
            NumPredict: 1024, DurationMs: 5600, Error: null)]);

        turns.Truncated.Should().BeTrue();
        turns.Answered.Should().BeTrue();
        ProjectBackgroundOracle.Violation(turns.Last.RawAnswer).Should().NotBeNull();
    }

    // Цепочка ходов кейса из сырых ответов модели по порядку.
    private static LocalBenchTurns Turns(params string[] answers) =>
        new(answers.Select(a => new LocalBenchShot(
            ActionKey: LocalActionCatalog.ProjectIcon, Prompt: "…", RawAnswer: a,
            FinishReason: "stop", StatusCode: 200, PromptTokens: 100, CompletionTokens: 20,
            NumPredict: 1024, DurationMs: 200, Error: null)).ToArray());

    // Ответ фона с n годными фигурами (квадрат и круг у каждой).
    private static string Doodle(int shapes, string? colorKey)
    {
        var parts = Enumerable.Range(0, shapes).Select(i =>
            $"{{\"x\":{10 + i * 5},\"y\":{20 + i * 3},\"rotate\":0,"
            + "\"paths\":[\"M0 0 h12 v12 h-12 z\"],\"circles\":[{\"cx\":6,\"cy\":6,\"r\":3}]}");
        var color = colorKey is null ? "" : $"\"colorKey\":\"{colorKey}\",";
        return $"{{{color}\"shapes\":[{string.Join(",", parts)}]}}";
    }
}
