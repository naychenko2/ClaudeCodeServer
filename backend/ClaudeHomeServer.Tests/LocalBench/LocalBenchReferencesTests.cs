using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.LocalBench.Places;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Проверки ВТОРОЙ метрики замера — совпадения выбора места с эталоном банка.
///
/// Категории LocalBench здесь нет по той же причине, что и у проверок оракулов: живой
/// модели они не требуют и обязаны идти в CI. Метрика, которую нельзя провалить, ничего
/// не измеряет — молча сломавшийся сличитель выдал бы ровное «совпало» на любом ответе.
/// </summary>
public class LocalBenchReferencesTests
{
    // ---------- отчёт: две метрики независимы ----------

    [Fact]
    public void Совпадение_с_эталоном_считается_отдельно_от_валидности()
    {
        // Ровно тот случай, ради которого метрики и разведены: формат безупречен, а выбор
        // мимо — и наоборот. Сложи их в одно число, и оба кейса стали бы неотличимы.
        var report = new LocalBenchReport(LocalActionCatalog.TaskDedup, "", PlaceReferences.TaskDedup);
        report.Add(Row("формат ок, выбор мимо", valid: true, LocalBenchMatch.Differs("другой id")));
        report.Add(Row("формат мимо, выбор верен", valid: false, LocalBenchMatch.Same("тот же id")));

        report.ValidCount.Should().Be(1);
        report.MatchCount.Should().Be(1);
        report.MismatchCount.Should().Be(1);
        report.ReferencedCount.Should().Be(2);
    }

    [Fact]
    public void Знаменатель_совпадения_это_кейсы_с_эталоном_а_не_весь_банк()
    {
        // Кейс без эталона нечем сверять. Посчитай его несовпадением — метрика поехала бы
        // вниз на дырах банка, а не на промахах места.
        var report = new LocalBenchReport(LocalActionCatalog.TaskDedup, "", PlaceReferences.TaskDedup);
        report.Add(Row("с эталоном", valid: true, LocalBenchMatch.Same()));
        report.Add(Row("без эталона", valid: true, LocalBenchMatch.None));

        report.Total.Should().Be(2);
        report.ReferencedCount.Should().Be(1);
        report.MatchSummary().Should().Contain("1/1").And.Contain("без эталона 1");
    }

    [Fact]
    public void Однозначный_эталон_подписан_ошибкой_а_нижняя_граница_взглядом_человека()
    {
        var exact = new LocalBenchReport(LocalActionCatalog.TaskDedup, "", PlaceReferences.TaskDedup);
        exact.Add(Row("мимо", valid: true, LocalBenchMatch.Differs("другой id")));

        var lower = new LocalBenchReport(LocalActionCatalog.ProjectIcon, "", PlaceReferences.ProjectIcon);
        lower.Add(Row("мимо", valid: true, LocalBenchMatch.Differs("другой выбор")));

        // Подпись — часть метрики, а не украшение: без неё таблицу нижней границы
        // прочитают приговором месту, которое всего лишь выбрало иначе.
        exact.MatchSummary().Should().Contain("ОДНОЗНАЧЕН").And.Contain("ошибка места");
        lower.MatchSummary().Should().Contain("НИЖНЯЯ ГРАНИЦА")
            .And.Contain("взгляд человека").And.NotContain("ошибка места");
    }

    [Fact]
    public void Место_без_эталона_честно_говорит_что_метрика_не_считается()
    {
        // notes-tags: эталона в банке нет вовсе. Пустая метрика лучше выдуманной.
        var report = new LocalBenchReport(LocalActionCatalog.NotesTags);
        report.Add(Row("кейс", valid: true, LocalBenchMatch.None));

        report.Reference.Should().BeNull();
        report.MatchSummary().Should().Contain("не считается").And.Contain("эталона у места нет");
    }

    // ---------- persona-voice: пол голоса, эталон однозначен ----------

    [Fact]
    public void Голос_того_же_пола_засчитывается_совпадением() =>
        // «jane» вместо «alena» — оба женских: персоне такой голос идёт, и ошибкой это не
        // является. А вот точное имя — вкус, и мерить по нему было бы придиркой.
        Match(PlaceReferences.PersonaVoice, Expect("{\"voice\":\"alena\"}"), "jane")
            .Should().Match<LocalBenchMatch>(m =>
                m.Kind == LocalBenchMatchKind.Match && m.Detail!.Contains("голос другой"));

    [Fact]
    public void Тот_же_голос_виден_в_детали_как_точное_попадание() =>
        Match(PlaceReferences.PersonaVoice, Expect("{\"voice\":\"alena\"}"), "alena")
            .Detail.Should().Contain("тот же голос");

    [Fact]
    public void Мужской_голос_женской_персоне_считается_ошибкой() =>
        Match(PlaceReferences.PersonaVoice, Expect("{\"voice\":\"alena\"}"), "zahar")
            .Should().Match<LocalBenchMatch>(m =>
                m.Kind == LocalBenchMatchKind.Mismatch && m.Detail!.Contains("пол не тот"));

    [Fact]
    public void Невыбранный_голос_считается_несовпадением() =>
        // «none» — законный ответ по контракту (оракул его пропускает), но выбора персоне
        // он не даёт: для второй метрики это промах.
        Match(PlaceReferences.PersonaVoice, Expect("{\"voice\":\"alena\"}"), "none")
            .Kind.Should().Be(LocalBenchMatchKind.Mismatch);

    // ---------- task-dedup: тот же дубль, эталон однозначен ----------

    [Fact]
    public void Тот_же_id_дубля_засчитывается_совпадением() =>
        Match(PlaceReferences.TaskDedup, Expect("{\"duplicateId\":\"abc\"}"),
                "{\"duplicateId\":\"abc\",\"reason\":\"та же работа\"}")
            .Kind.Should().Be(LocalBenchMatchKind.Match);

    [Fact]
    public void Отсутствие_дубля_при_эталоне_без_дубля_тоже_совпадение() =>
        // Половина банка именно такая. Считай её «эталона нет» — из знаменателя выпали бы
        // ровно те кейсы, где место охотнее всего выдумывает дубль.
        Match(PlaceReferences.TaskDedup, Expect("{}"), "{\"duplicateId\":null}")
            .Should().Match<LocalBenchMatch>(m =>
                m.Kind == LocalBenchMatchKind.Match && m.Detail!.Contains("дубля нет"));

    [Fact]
    public void Пропущенный_дубль_и_выдуманный_дубль_различимы_в_детали()
    {
        Match(PlaceReferences.TaskDedup, Expect("{\"duplicateId\":\"abc\"}"),
            "{\"duplicateId\":null}").Detail.Should().Contain("пропущен");

        Match(PlaceReferences.TaskDedup, Expect("{}"), "{\"duplicateId\":\"xyz\"}")
            .Detail.Should().Contain("место его нашло");
    }

    // ---------- task-normalize-title: распознан ли срок ----------

    [Theory]
    [InlineData(true, "{\"title\":\"Сделать отчёт\",\"dueHint\":\"завтра\"}")]
    [InlineData(false, "{\"title\":\"Позвонить в банк\",\"dueHint\":null}")]
    public void Факт_распознавания_срока_сверяется_с_эталоном(bool expected, string answer) =>
        Match(PlaceReferences.TaskNormalizeTitle,
                Expect($"{{\"hasDueHint\":{(expected ? "true" : "false")}}}"), answer)
            .Kind.Should().Be(LocalBenchMatchKind.Match);

    [Fact]
    public void Потерянный_срок_и_выдуманный_срок_различимы_в_детали()
    {
        Match(PlaceReferences.TaskNormalizeTitle, Expect("{\"hasDueHint\":true}"),
            "{\"title\":\"Сделать отчёт\",\"dueHint\":null}").Detail.Should().Contain("потерян");

        Match(PlaceReferences.TaskNormalizeTitle, Expect("{\"hasDueHint\":false}"),
            "{\"title\":\"Позвонить в банк\",\"dueHint\":\"на неделе\"}")
            .Detail.Should().Contain("выдуман");
    }

    // ---------- task-classify: приоритет, нижняя граница ----------

    [Fact]
    public void Приоритет_как_в_трекере_засчитывается_совпадением_а_метки_идут_деталью() =>
        Match(PlaceReferences.TaskClassify,
                Expect("{\"priority\":\"urgent\",\"labels\":[\"печать\",\"очная сессия\"]}"),
                "{\"priority\":\"urgent\",\"labels\":[\"печать\"]}")
            .Should().Match<LocalBenchMatch>(m =>
                m.Kind == LocalBenchMatchKind.Match && m.Detail!.Contains("меток из трекера 1/2"));

    [Fact]
    public void Другой_приоритет_расходится_с_трекером_но_это_не_приговор() =>
        // «high против urgent» у живых задач спорно и для человека — сила эталона здесь
        // нижняя граница, и подпись сводки об этом говорит прямо.
        Match(PlaceReferences.TaskClassify, Expect("{\"priority\":\"urgent\"}"),
                "{\"priority\":\"high\",\"labels\":[]}")
            .Should().Match<LocalBenchMatch>(m =>
                m.Kind == LocalBenchMatchKind.Mismatch && m.Detail!.Contains("high против urgent"));

    // ---------- project-icon: значок, нижняя граница ----------

    [Fact]
    public void Эталонный_значок_среди_названных_считается_совпадением() =>
        // Место отдаёт несколько кандидатов, и человек выбирает из них: попадание эталона
        // в этот список и есть то, что метрика умеет доказать.
        PlaceReferences.ProjectIcon.Compare(
                Case(Expect("{\"glyph\":\"flag\"}")),
                Turns("{\"words\":[\"flag\",\"map\"]}", "{\"glyphs\":[{\"name\":\"map\"},{\"name\":\"flag\"}]}"))
            .Kind.Should().Be(LocalBenchMatchKind.Match);

    [Fact]
    public void Другой_значок_расходится_с_поставленным_у_проекта() =>
        PlaceReferences.ProjectIcon.Compare(
                Case(Expect("{\"glyph\":\"flag\"}")),
                Turns("{\"words\":[\"folder\"]}", "{\"glyphs\":[{\"name\":\"folder\"}]}"))
            .Should().Match<LocalBenchMatch>(m =>
                m.Kind == LocalBenchMatchKind.Mismatch && m.Detail!.Contains("у проекта flag"));

    [Fact]
    public void Несостоявшийся_выбор_значка_считается_несовпадением() =>
        // Цепочка оборвалась на ходе слов: значка у проекта нет, и эталон не достигнут.
        PlaceReferences.ProjectIcon.Compare(
                Case(Expect("{\"glyph\":\"flag\"}")), Turns("{\"words\":[\"flag\"]}"))
            .Kind.Should().Be(LocalBenchMatchKind.Mismatch);

    // ---------- project-background: цвет, нижняя граница ----------

    [Fact]
    public void Цвет_как_у_проекта_засчитывается_совпадением() =>
        Match(PlaceReferences.ProjectBackground, Expect("{\"colorKey\":\"green\"}"),
            Doodle("green")).Kind.Should().Be(LocalBenchMatchKind.Match);

    [Fact]
    public void Другой_цвет_расходится_с_выбранным_у_проекта() =>
        Match(PlaceReferences.ProjectBackground, Expect("{\"colorKey\":\"green\"}"),
                Doodle("blue"))
            .Should().Match<LocalBenchMatch>(m =>
                m.Kind == LocalBenchMatchKind.Mismatch && m.Detail!.Contains("blue против green"));

    [Fact]
    public void Потерянный_ключ_цвета_считается_несовпадением() =>
        Match(PlaceReferences.ProjectBackground, Expect("{\"colorKey\":\"green\"}"),
            Doodle(null)).Kind.Should().Be(LocalBenchMatchKind.Mismatch);

    // ---------- сторож: эталоны банка без сличителя не остаются ----------

    [Theory]
    [InlineData(LocalActionCatalog.PersonaVoice)]
    [InlineData(LocalActionCatalog.TaskDedup)]
    [InlineData(LocalActionCatalog.TaskNormalizeTitle)]
    [InlineData(LocalActionCatalog.TaskClassify)]
    [InlineData(LocalActionCatalog.ProjectIcon)]
    [InlineData(LocalActionCatalog.ProjectBackground)]
    public void Банк_места_со_сличителем_несёт_блок_эталона_у_каждого_кейса(string place)
    {
        // Кейс вовсе без expect молча выпал бы из знаменателя, и метрика осталась бы
        // зелёной на меньшем числе кейсов — а по числу «17/20» этого не видно.
        //
        // Пустое ПОЛЕ внутри expect сторож не ловит намеренно: у проекта «стратсессия»
        // цвет не выбран и в продукте (colorKey: null), и выдумать его значило бы мерить
        // выдумку. Такие дыры честно выпадают из знаменателя, и сводка печатает их числом
        // отдельно — «без эталона N кейсов».
        var bank = LocalBenchCases.Load(place);

        bank.Cases.Should().OnlyContain(c => c.Expect != null,
            $"у места «{place}» подключён сличитель — блок expect обязан быть у каждого кейса");
    }

    [Fact]
    public void Место_notes_tags_остаётся_без_эталона_намеренно()
    {
        // Заведись у банка expect — метрику надо подключать, а не оставлять непосчитанной.
        var bank = LocalBenchCases.Load(LocalActionCatalog.NotesTags);

        bank.Cases.Should().OnlyContain(c => c.Expect == null,
            "у notes-tags эталона нет намеренно: теги живой базы проставлены неравномерно. "
            + "Появился expect — подключите сличитель в PlaceReferences");
    }

    // ---------- вспомогательное ----------

    private static LocalBenchRow Row(string id, bool valid, LocalBenchMatch match) =>
        new(id, valid, valid ? null : "нарушение", false, true, 200, 20, "→ …") { Match = match };

    private static JsonElement Expect(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static LocalBenchCase Case(JsonElement expect) => new("кейс", "вход", expect);

    // Вердикт сличителя по одному сырому ответу места.
    private static LocalBenchMatch Match(LocalBenchReference reference, JsonElement expect,
        string answer) => reference.Compare(Case(expect), Turns(answer));

    private static LocalBenchTurns Turns(params string[] answers) =>
        new(answers.Select(a => new LocalBenchShot(
            ActionKey: LocalActionCatalog.ProjectIcon, Prompt: "…", RawAnswer: a,
            FinishReason: "stop", StatusCode: 200, PromptTokens: 100, CompletionTokens: 20,
            NumPredict: 1024, DurationMs: 200, Error: null)).ToArray());

    // Ответ фона: десять годных фигур (порога сборки хватает) и ключ цвета.
    private static string Doodle(string? colorKey)
    {
        var parts = Enumerable.Range(0, 10).Select(i =>
            $"{{\"x\":{10 + i * 5},\"y\":{20 + i * 3},\"rotate\":0,"
            + "\"paths\":[\"M0 0 h12 v12 h-12 z\"],\"circles\":[{\"cx\":6,\"cy\":6,\"r\":3}]}");
        var color = colorKey is null ? "" : $"\"colorKey\":\"{colorKey}\",";
        return $"{{{color}\"shapes\":[{string.Join(",", parts)}]}}";
    }
}
