using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

// В Main есть одноимённый `Services.SectionSource` (`PersonaBindingsService`, Off/Preset/Explicit)
// — без алиаса короткое имя становится неоднозначным (как в SpecialtySettingsStoreTests).
using SectionSource = ClaudeHomeServer.Services.Llm.SectionSource;

namespace ClaudeHomeServer.Tests.Services;

// P1a (разрез 2 «ai-fabrika-workflow-2», Г6): «Наложение секций — одна функция».
// Юнит-тесты чистой функции PromptSectionOverlay.Apply и golden-снимок эффективных секций
// КАЖДОЙ специальности на СТАРОМ коде стора (инлайновый цикл EffectivePromptSectionStates).
// Golden-таблица — литералы, снятые старым кодом 12.11.2026: при переводе стора на Apply
// она не правится; зелёный без правки ожидаемых — поведение байт в байт.
public class PromptSectionOverlayTests : IDisposable
{
    private const string Owner = "owner-1";
    private readonly string _dir;

    public PromptSectionOverlayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_prompt_overlay_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();

    // --- Unit-тесты Apply: «параметр задан — перекрывает, не задан — не затирает» ---

    private static EffectivePromptSection Base(string id, bool enabled, string text) =>
        new(id, enabled, text, SectionSource.Code, SectionSource.Code);

    [Fact]
    public void Apply_EnabledNull_КакВБазе()
    {
        IReadOnlyList<EffectivePromptSection> baseStates =
            [Base("history", true, "базовый текст"), Base("codeGraph", false, "граф")];
        var result = PromptSectionOverlay.Apply(
            baseStates, [new PromptSectionOverride("history", null, null)], SectionSource.Persona);

        result[0].Enabled.Should().BeTrue("Enabled не задан — базовое значение остаётся");
        result[0].EnabledSource.Should().Be(SectionSource.Code);
        result[1].Enabled.Should().BeFalse("чужая секция не тронута");
        result[1].EnabledSource.Should().Be(SectionSource.Code);
    }

    [Fact]
    public void Apply_EnabledFalse_ВыключаетПриВключённойБазе()
    {
        var result = PromptSectionOverlay.Apply(
            [Base("history", true, "текст")], [new PromptSectionOverride("history", false, null)],
            SectionSource.Persona);
        result[0].Enabled.Should().BeFalse();
        result[0].EnabledSource.Should().Be(SectionSource.Persona);
        result[0].TextSource.Should().Be(SectionSource.Code, "переопределение Enabled не трогает текст");
    }

    [Fact]
    public void Apply_EnabledTrue_ВключаетПриВыключеннойБазе()
    {
        var result = PromptSectionOverlay.Apply(
            [Base("codeGraph", false, "текст")], [new PromptSectionOverride("codeGraph", true, null)],
            SectionSource.Persona);
        result[0].Enabled.Should().BeTrue("явный on сильнее выключенной базы");
        result[0].EnabledSource.Should().Be(SectionSource.Persona);
    }

    [Fact]
    public void Apply_ТекстПустой_ТекстИИсточникБазы()
    {
        // null, "" и пробелы — «не задан»: остаются и базовый текст, и его источник
        foreach (var text in new string?[] { null, "", "   " })
        {
            var result = PromptSectionOverlay.Apply(
                [Base("history", true, "базовый текст")],
                [new PromptSectionOverride("history", null, text)], SectionSource.Persona);
            result[0].Text.Should().Be("базовый текст");
            result[0].TextSource.Should().Be(SectionSource.Code);
        }
    }

    [Fact]
    public void Apply_СвойТекст_ИсточникТекстаПереданныйSource()
    {
        var result = PromptSectionOverlay.Apply(
            [Base("history", true, "базовый текст")],
            [new PromptSectionOverride("history", null, "  свой текст  ")], SectionSource.Persona);
        result[0].Text.Should().Be("свой текст", "свой текст триммится — как в прежнем сторе");
        result[0].TextSource.Should().Be(SectionSource.Persona);
        result[0].Enabled.Should().BeTrue("переопределение Text не трогает Enabled");
        result[0].EnabledSource.Should().Be(SectionSource.Code);
    }

    [Fact]
    public void Apply_НеизвестныйId_Игнорируется()
    {
        var result = PromptSectionOverlay.Apply(
            [Base("history", true, "текст")],
            [new PromptSectionOverride("нет-такой-секции", false, "чужой текст")], SectionSource.Persona);
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("history");
        result[0].Enabled.Should().BeTrue();
        result[0].Text.Should().Be("текст");
        result[0].TextSource.Should().Be(SectionSource.Code);
    }

    [Fact]
    public void Apply_ПорядокКакВБазе_ИПустыеOverrides_БезИзменений()
    {
        IReadOnlyList<EffectivePromptSection> baseStates =
            [Base("processes", false, "п"), Base("history", true, "х"), Base("roleRules", true, "р")];
        var result = PromptSectionOverlay.Apply(
            baseStates,
            [new PromptSectionOverride("roleRules", false, null), new PromptSectionOverride("history", false, null)],
            SectionSource.Persona);
        result.Select(s => s.Id).Should().Equal(new[] { "processes", "history", "roleRules" },
            "порядок результата — порядок базы, а не порядок overrides");
        result[1].Enabled.Should().BeFalse();
        result[2].Enabled.Should().BeFalse();

        // overrides null/пусто — база как есть
        PromptSectionOverlay.Apply(baseStates, null, SectionSource.Persona).Should().Equal(baseStates);
        PromptSectionOverlay.Apply(baseStates, [], SectionSource.Persona).Should().Equal(baseStates);
    }

    // --- Golden: снимок EffectivePromptSectionStates СТАРОГО кода (не править при рефакторинге) ---
    // Сценарии слоя админа: a — пусто; b — у «history» Enabled инвертирован от дефолта кода;
    // c — у «roleRules» свой текст. Ожидаемое — (Id, Enabled, Text, EnabledSource, TextSource)
    // по всем секциям каталога.

    // Типовые тексты секций — литералы-снимки дефолтов SpecialtyPromptPresets (12.11.2026).
    // Analyst
    private const string T_history_Analyst =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• перед формулировкой требований или гипотезы — dossier_lookup по файлам темы: что уже обсуждали и решили;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "Не предлагай заново выводы, которые уже зафиксированы, и отвергнутые альтернативы.";
    // Planner
    private const string T_history_Planner =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• строя план по существующему коду — dossier_lookup по файлам, которых план касается: там инварианты и известные грабли;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "План, противоречащий зафиксированному решению, обязан это явно проговорить.";
    // Reviewer
    private const string T_history_Reviewer =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• ревьюя изменения — dossier_lookup по тронутым файлам: замечания против зафиксированных решений — главный источник шума в ревью;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "Не предлагай отвергнутые альтернативы как улучшения.";
    // Executor
    private const string T_history_Executor =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• перед правкой файла — dossier_lookup по его пути или символу: инварианты, которые нельзя нарушить, и отвергнутые подходы;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "Не приноси обратно отвергнутые варианты.";
    // Secretary
    private const string T_history_Secretary =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать: обсуждая прошлые изменения проекта — dossier_lookup по упомянутым файлам или запросу; id паспорта в подсказке — dossier_get." + "\n" +
        "Не пересказывай отвергнутые варианты как действующие.";
    // Coordinator
    private const string T_history_Coordinator =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать: распределяя работу по коду — dossier_lookup по файлам задачи: где уже копали и что решили; id паспорта в подсказке — dossier_get." + "\n" +
        "Не поручай повторно отвергнутое.";
    // Mentor
    private const string T_history_Mentor =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать: объясняя устройство кода — dossier_lookup по файлу: живые примеры решений и их причин; id паспорта в подсказке — dossier_get." + "\n" +
        "Отвергнутые альтернативы — лучший учебный материал: объясняй, почему не подошли.";
    // Designer
    private const string T_history_Designer =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать: перед правкой UI — dossier_lookup по файлу компонента: договорённости и их причины; id паспорта в подсказке — dossier_get." + "\n" +
        "Не предлагай решения, от которых команда уже отказалась.";
    // Consultant
    private const string T_history_Consultant =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• советуя по коду или архитектуре проекта — dossier_lookup по файлам темы: уже принятые решения и их причины;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "Совет, противоречащий зафиксированному решению, отмечай явно, а не молча.";
    // Librarian
    private const string T_history_Librarian =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать: ища первоисточник решения — dossier_lookup по файлу или запросу; id паспорта — dossier_get." + "\n" +
        "Ссылайся на паспорт, а не на пересказ.";
    // Tester
    private const string T_history_Tester =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• проверяя фичу — dossier_lookup по её файлам: что намеренно отвергли (это не баг) и какие грабли уже ловили;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "В отчёте отличай «нарушено решение» от «решение было другим».";
    // BackendExecutor
    private const string T_history_BackendExecutor =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• перед правкой серверного кода — dossier_lookup по файлу: инварианты слоя и отвергнутые подходы;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "Не приноси обратно отвергнутые варианты.";
    // FrontendExecutor
    private const string T_history_FrontendExecutor =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• перед правкой компонента или модуля фронта — dossier_lookup по файлу: договорённости и отвергнутые подходы;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "Не приноси обратно отвергнутые варианты.";
    // DevopsExecutor
    private const string T_history_DevopsExecutor =
        "История решений проекта — зачем меняли код и что при этом отвергли. Структуру кода смотри codegraph_*, историю решений — dossier_*." + "\n" +
        "Когда звать:" + "\n" +
        "• перед правкой сборки, контейнеров или конфигов — dossier_lookup по файлу: почему конфиг устроен так;" + "\n" +
        "• id паспорта в подсказке — dossier_get читает его целиком." + "\n" +
        "Не приноси обратно отвергнутые варианты.";
    // Analyst
    private const string T_codeGraph_Analyst =
        "Навигация по коду — два уровня: типы (codegraph), текст (Grep)." + "\n" +
        "Когда звать: карта подсистемы — codegraph_hubs; «где объявлен термин» — codegraph_find; связи понятия — codegraph_neighbors." + "\n" +
        "Архитектуру подтверждай инструментом, а не догадкой.";
    // Planner
    private const string T_codeGraph_Planner =
        "Навигация по коду — два уровня: типы (codegraph), текст (Grep)." + "\n" +
        "Когда звать:" + "\n" +
        "• «с чего начать» в незнакомом модуле — codegraph_hubs;" + "\n" +
        "• «где объявлен X» — codegraph_find: точное место;" + "\n" +
        "• «что связано с X» — codegraph_neighbors;" + "\n" +
        "План по коду строй от графа и символов, а не от имён файлов.";
    // Reviewer
    private const string T_codeGraph_Reviewer =
        "Навигация по коду — два уровня: типы (codegraph), текст (Grep)." + "\n" +
        "Когда звать:" + "\n" +
        "• оценивая влияние изменений — codegraph_neighbors по тронутым типам: кто ещё зависит;" + "\n" +
        "• «где объявлен X» — codegraph_find: точное место и вид типа;" + "\n" +
        "• незнакомая подсистема — codegraph_hubs;" + "\n" +
        "Находку «правка заденет N мест» подтверждай инструментом, а не прикидкой.";
    // Executor
    private const string T_codeGraph_Executor =
        "Навигация по коду — два уровня, не взаимозаменяемы; текстовый поиск по типам промахивается." + "\n" +
        "Когда звать:" + "\n" +
        "• «где объявлен X» — codegraph_find: файл, строка и вид типа, без шума совпадений;" + "\n" +
        "• «что сломается, если правлю X» — codegraph_neighbors: входящие Calls/Implements/References;" + "\n" +
        "• «с чего начать в незнакомом модуле» — codegraph_hubs;" + "\n" +
        "Grep — только для текстовых вхождений и файлов вне графа (конфиги, .md, разметка).";
    // общий текст (фолбэк каталога)
    private const string T_codeGraph_shared =
        "Навигация по коду — два уровня, не взаимозаменяемы: codegraph (типы и связность), Grep (текст)." + "\n" +
        "Когда звать: «где объявлен X» — codegraph_find; «что связано с X» — codegraph_neighbors; обзор подсистемы — codegraph_hubs." + "\n" +
        "Структуру кода уточняй инструментами, а не пересказом из памяти. Grep — только для текстовых вхождений и файлов вне графа (конфиги, .md, разметка).";
    // Tester
    private const string T_codeGraph_Tester =
        "Навигация по коду — два уровня: типы (codegraph), текст (Grep)." + "\n" +
        "Когда звать:" + "\n" +
        "• «что затронет эта правка» — codegraph_neighbors: границы влияния для объёма проверки;" + "\n" +
        "• «где объявлен X» — codegraph_find;" + "\n" +
        "• незнакомая подсистема — codegraph_hubs: точки входа;" + "\n" +
        "Проверяй не только изменённый файл, но и его входящих соседей.";
    // BackendExecutor
    private const string T_codeGraph_BackendExecutor =
        "Навигация по серверному коду (.cs): codegraph — типы и связности, Grep — текст." + "\n" +
        "Когда звать:" + "\n" +
        "• «где объявлен X» — codegraph_find: файл, строка и вид типа;" + "\n" +
        "• «что сломается, если правлю X» — codegraph_neighbors: входящие Calls/Implements/References;" + "\n" +
        "• незнакомая подсистема — codegraph_hubs: точки входа;" + "\n" +
        "Grep — только для текстовых вхождений и файлов вне графа.";
    // FrontendExecutor
    private const string T_codeGraph_FrontendExecutor =
        "Навигация по коду фронта (.tsx/.ts): codegraph — типы и связности, Grep — текст." + "\n" +
        "Когда звать:" + "\n" +
        "• «где объявлен X» — codegraph_find: файл, строка и вид типа;" + "\n" +
        "• «что сломается, если правлю X» — codegraph_neighbors: входящие Calls/Implements/References;" + "\n" +
        "• незнакомый модуль — codegraph_hubs: точки входа;" + "\n" +
        "Grep — только для текстовых вхождений и файлов вне графа.";
    // DevopsExecutor
    private const string T_codeGraph_DevopsExecutor =
        "Навигация по коду — два уровня, не взаимозаменяемы; текстовый поиск по типам промахивается." + "\n" +
        "Когда звать:" + "\n" +
        "• «где объявлен X» — codegraph_find: файл, строка и вид типа;" + "\n" +
        "• «что сломается, если правлю X» — codegraph_neighbors: входящие связи;" + "\n" +
        "• незнакомая подсистема — codegraph_hubs: точки входа;" + "\n" +
        "Grep — только для текстовых вхождений и файлов вне графа.";
    // общий текст (фолбэк каталога)
    private const string T_processes_shared =
        "До сдачи результата: проверь его по критериям задачи; что проверено и что нет — фиксируй честно." + "\n" +
        "Куда складывать: итог — в задачу трекера, артефакты — по месту задачи." + "\n" +
        "Коммит — только по явной просьбе.";
    // Planner
    private const string T_processes_Planner =
        "До сдачи плана:" + "\n" +
        "• у каждого шага — критерий готовности и способ проверки;" + "\n" +
        "• риски и развилки названы явно;" + "\n" +
        "• объём честный: что в плане, что вне его." + "\n" +
        "Куда складывать: план — в задачу трекера, важные договорённости — заметкой.";
    // Reviewer
    private const string T_processes_Reviewer =
        "До сдачи ревью:" + "\n" +
        "• каждая находка — с файлом и строкой, по severity (блокер/важно/желательно);" + "\n" +
        "• спорь с решением, а не с оформлением; стиль — отдельным списком;" + "\n" +
        "• что проверено и что НЕ проверено — фиксируй." + "\n" +
        "Куда складывать: находки — ответом в чат или задачу; баг вне тронутого диффа → tasks_create(kind:\"defect\", repro: шаги/ожидаемое/фактическое), а не строка в находках.";
    // Executor
    private const string T_processes_Executor =
        "До сдачи результата:" + "\n" +
        "• сборка проекта зелёная, тесты по затронутому — прогнаны;" + "\n" +
        "• стиль — как в соседнем коде; лишнего в диффе нет." + "\n" +
        "Куда складывать: итог — в задачу трекера, файлы — по месту задачи." + "\n" +
        "Попутный баг вне задачи → не чинить молча и не описывать в чате: tasks_create(kind:\"defect\", repro: шаги/ожидаемое/фактическое), продолжай свою работу." + "\n" +
        "Коммит — только по явной просьбе.";
    // Secretary
    private const string T_processes_Secretary =
        "До закрытия задачи:" + "\n" +
        "• итог сформулирован и прикреплён (текст, файлы);" + "\n" +
        "• даты и напоминания расставлены;" + "\n" +
        "• ничего из обсуждения не потеряно." + "\n" +
        "Куда складывать: задачи — в трекере, мысли — в заметки; дублей не плодить.";
    // Librarian
    private const string T_processes_Librarian =
        "До сдачи выжимки:" + "\n" +
        "• каждый факт атрибутирован источником;" + "\n" +
        "• заметка связана [[]]-ссылками с соседними по теме;" + "\n" +
        "• конспект отличим от собственного мнения." + "\n" +
        "Куда складывать: заметки — в vault проекта или владельца.";
    // Tester
    private const string T_processes_Tester =
        "До сдачи проверки:" + "\n" +
        "• шаги воспроизводимы — напиши их;" + "\n" +
        "• краевые случаи и негативные пути — пройдены;" + "\n" +
        "• «не проверено» фиксируй явно — это тоже результат." + "\n" +
        "Куда складывать: отчёт — в задачу (что проверено/что нет, как воспроизвести); найденный баг вне текущей задачи → tasks_create(kind:\"defect\", repro: шаги/ожидаемое/фактическое), а не абзац внутри отчёта.";
    // BackendExecutor
    private const string T_processes_BackendExecutor =
        "До сдачи результата:" + "\n" +
        "• dotnet build — 0 ошибок; dotnet test по затронутым наборам — зелёный;" + "\n" +
        "• стиль — как в соседнем коде; лишнего в диффе нет." + "\n" +
        "Куда складывать: итог — в задачу трекера, файлы — по месту задачи." + "\n" +
        "Попутный баг вне задачи → не чинить молча и не описывать в чате: tasks_create(kind:\"defect\", repro: шаги/ожидаемое/фактическое), продолжай свою работу." + "\n" +
        "Коммит — только по явной просьбе.";
    // FrontendExecutor
    private const string T_processes_FrontendExecutor =
        "До сдачи результата:" + "\n" +
        "• npx tsc -b — чисто; тесты/сборка по затронутому — зелёные; lint дизайн-системы — зелёный;" + "\n" +
        "• стиль — как в соседнем коде; лишнего в диффе нет." + "\n" +
        "Куда складывать: итог — в задачу трекера, файлы — по месту задачи." + "\n" +
        "Попутный баг вне задачи → не чинить молча и не описывать в чате: tasks_create(kind:\"defect\", repro: шаги/ожидаемое/фактическое), продолжай свою работу." + "\n" +
        "Коммит — только по явной просьбе.";
    // DevopsExecutor
    private const string T_processes_DevopsExecutor =
        "До сдачи результата:" + "\n" +
        "• сборка и старт окружения воспроизводимы с чистого состояния;" + "\n" +
        "• секреты — только в локальных конфигах вне git; откат возможен." + "\n" +
        "Куда складывать: итог — в задачу трекера, изменения конфигов — в дифф задачи." + "\n" +
        "Попутный баг вне задачи → не чинить молча и не описывать в чате: tasks_create(kind:\"defect\", repro: шаги/ожидаемое/фактическое), продолжай свою работу." + "\n" +
        "Коммит и выкатка — только по явной просьбе.";
    // Analyst
    private const string T_roleRules_Analyst =
        "Данные — без домыслов: чего не знаешь — так и говори." + "\n" +
        "Выводы отличай от гипотез и помечай, чем считаешь. Каждая цифра — с источником." + "\n" +
        "Прежде чем предлагать решение — проверь, что проблема понята.";
    // Planner
    private const string T_roleRules_Planner =
        "План — это шаги с критерием готовности у каждого, а не список пожеланий." + "\n" +
        "Риски и развилки называй явно: план, который их прячет, дороже отсутствия плана." + "\n" +
        "Честно оценивай, что можно делать параллельно и что критично по порядку.";
    // Reviewer
    private const string T_roleRules_Reviewer =
        "Спорь с решением, а не с оформлением." + "\n" +
        "Каждая находка — с местом и последствием, а не «плохо». Хвали конкретно, критикуй по существу." + "\n" +
        "Промолчать о сомнении в блокере хуже ложной тревоги.";
    // Executor
    private const string T_roleRules_Executor =
        "Доводи до конца: сборка зелёная, тесты прогнаны, итог в задаче." + "\n" +
        "Минимальный дифф: правь то, что просили. Не выдумывай дополнительную работу и не трогай несвязанное." + "\n" +
        "Застрял — докладывай сразу, а не молча.";
    // Secretary
    private const string T_roleRules_Secretary =
        "Кратко и ничего не терять: выжимка короче обсуждения, но не беднее его по фактам." + "\n" +
        "Даты, сроки и ответственные — явно. Дублей задач и заметок не плодить.";
    // Coordinator
    private const string T_roleRules_Coordinator =
        "Распределяй по силам и зоне персон, а не по порядку списка." + "\n" +
        "Блокер эскалируй сразу — поздняя эскалация дороже ложной." + "\n" +
        "Следи, чтобы две персоны не делали одну и ту же работу.";
    // Mentor
    private const string T_roleRules_Mentor =
        "Сначала вопросы, потом советы: не решай за ученика." + "\n" +
        "Сложное — по шагам, с проверкой понимания на каждом. Хвали за процесс, а не только за результат." + "\n" +
        "Не сравнивай с другими в унизительном ключе.";
    // Designer
    private const string T_roleRules_Designer =
        "Система прежде витрины: новый экран собирается из токенов и компонентов дизайн-системы, а не с нуля." + "\n" +
        "Проверяй обе темы и мобильную ширину. Красота не оправдывает нарушение конвенции.";
    // Consultant
    private const string T_roleRules_Consultant =
        "Отвечай на заданный вопрос, а не на смежный." + "\n" +
        "Альтернативы давай с компромиссами, без «идеального варианта»." + "\n" +
        "Границы знания признавай явно: неверный совет хуже честного «не знаю».";
    // Librarian
    private const string T_roleRules_Librarian =
        "Атрибуция прежде скорости: каждый факт — с источником и пермалинком." + "\n" +
        "Не выдумывай ссылки — лучше честное «источник не найден»." + "\n" +
        "Конспект — нейтрально, без правки смысла первоисточника.";
    // Tester
    private const string T_roleRules_Tester =
        "Воспроизводимость прежде вердикта: «не работает» без шагов — не находка." + "\n" +
        "Ищи края: пустые значения, границы, негативные пути." + "\n" +
        "Отчёт честный: что проверено, что нет, что спорно.";
    // BackendExecutor
    private const string T_roleRules_BackendExecutor =
        "Инварианты слоя важнее локального удобства: изоляция per-owner, валидация на входе, стиль соседнего кода." + "\n" +
        "Сборка и тесты — зелёные до сдачи. Машинно-специфичное — в локальные конфиги, не в код.";
    // FrontendExecutor
    private const string T_roleRules_FrontendExecutor =
        "Только токены дизайн-системы: сырой hex и самодельные контролы — дефект." + "\n" +
        "Экран обязан жить на мобильной ширине. Компоненты — из ui-кита, состояние — по паттернам соседних экранов.";
    // DevopsExecutor
    private const string T_roleRules_DevopsExecutor =
        "Воспроизводимость и откатываемость прежде скорости." + "\n" +
        "Секреты — никогда в git. Изменение окружения проверяй с чистого старта." + "\n" +
        "Скрипт, который нельзя перезапустить, — не готов.";

    private static readonly (
        PersonaSpecialty Specialty, string Scenario,
        (string Id, bool Enabled, string? Text)[] Admin,
        (string Id, bool Enabled, string Text, SectionSource EnabledSource, SectionSource TextSource)[] Expected)[] Golden =
    [
        (PersonaSpecialty.Analyst, "a", [],
            [("history", true, T_history_Analyst, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_Analyst, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Analyst, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Analyst, "b", [("history", false, null)],
            [("history", false, T_history_Analyst, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_Analyst, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Analyst, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Analyst, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_Analyst, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_Analyst, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Planner, "a", [],
            [("history", true, T_history_Planner, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Planner, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Planner, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Planner, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Planner, "b", [("history", false, null)],
            [("history", false, T_history_Planner, SectionSource.Global, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Planner, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Planner, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Planner, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Planner, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_Planner, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Planner, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Planner, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Reviewer, "a", [],
            [("history", true, T_history_Reviewer, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Reviewer, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Reviewer, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Reviewer, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Reviewer, "b", [("history", false, null)],
            [("history", false, T_history_Reviewer, SectionSource.Global, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Reviewer, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Reviewer, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Reviewer, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Reviewer, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_Reviewer, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Reviewer, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Reviewer, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Executor, "a", [],
            [("history", true, T_history_Executor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_Executor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Executor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Executor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Executor, "b", [("history", false, null)],
            [("history", false, T_history_Executor, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_Executor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Executor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Executor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Executor, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_Executor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_Executor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Executor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Secretary, "a", [],
            [("history", false, T_history_Secretary, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Secretary, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Secretary, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Secretary, "b", [("history", true, null)],
            [("history", true, T_history_Secretary, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Secretary, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Secretary, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Secretary, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", false, T_history_Secretary, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Secretary, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Coordinator, "a", [],
            [("history", false, T_history_Coordinator, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Coordinator, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Coordinator, "b", [("history", true, null)],
            [("history", true, T_history_Coordinator, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Coordinator, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Coordinator, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", false, T_history_Coordinator, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Mentor, "a", [],
            [("history", false, T_history_Mentor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Mentor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Mentor, "b", [("history", true, null)],
            [("history", true, T_history_Mentor, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Mentor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Mentor, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", false, T_history_Mentor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Designer, "a", [],
            [("history", false, T_history_Designer, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Designer, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Designer, "b", [("history", true, null)],
            [("history", true, T_history_Designer, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Designer, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Designer, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", false, T_history_Designer, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Consultant, "a", [],
            [("history", true, T_history_Consultant, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Consultant, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Consultant, "b", [("history", false, null)],
            [("history", false, T_history_Consultant, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Consultant, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Consultant, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_Consultant, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", false, T_processes_shared, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Librarian, "a", [],
            [("history", false, T_history_Librarian, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Librarian, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Librarian, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Librarian, "b", [("history", true, null)],
            [("history", true, T_history_Librarian, SectionSource.Global, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Librarian, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Librarian, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Librarian, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", false, T_history_Librarian, SectionSource.Code, SectionSource.Code),
             ("codeGraph", false, T_codeGraph_shared, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Librarian, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.Tester, "a", [],
            [("history", true, T_history_Tester, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Tester, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Tester, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Tester, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Tester, "b", [("history", false, null)],
            [("history", false, T_history_Tester, SectionSource.Global, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Tester, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Tester, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_Tester, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.Tester, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_Tester, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_Tester, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_Tester, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.BackendExecutor, "a", [],
            [("history", true, T_history_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_BackendExecutor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.BackendExecutor, "b", [("history", false, null)],
            [("history", false, T_history_BackendExecutor, SectionSource.Global, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_BackendExecutor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.BackendExecutor, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_BackendExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.FrontendExecutor, "a", [],
            [("history", true, T_history_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_FrontendExecutor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.FrontendExecutor, "b", [("history", false, null)],
            [("history", false, T_history_FrontendExecutor, SectionSource.Global, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_FrontendExecutor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.FrontendExecutor, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_FrontendExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
        (PersonaSpecialty.DevopsExecutor, "a", [],
            [("history", true, T_history_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_DevopsExecutor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.DevopsExecutor, "b", [("history", false, null)],
            [("history", false, T_history_DevopsExecutor, SectionSource.Global, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, T_roleRules_DevopsExecutor, SectionSource.Code, SectionSource.Code)]),
        (PersonaSpecialty.DevopsExecutor, "c", [("roleRules", true, "свой текст секции roleRules")],
            [("history", true, T_history_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("codeGraph", true, T_codeGraph_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("processes", true, T_processes_DevopsExecutor, SectionSource.Code, SectionSource.Code),
             ("roleRules", true, "свой текст секции roleRules", SectionSource.Global, SectionSource.Global)]),
    ];

    public static IEnumerable<object[]> GoldenCases() =>
        Golden.Select(g => new object[] { g.Specialty, g.Scenario });

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Golden_ЭффективныеСекции_СнимокСтарогоКода(PersonaSpecialty specialty, string scenario)
    {
        var row = Golden.First(g => g.Specialty == specialty && g.Scenario == scenario);
        var store = TestSpecialtyStore.Create(Config());
        if (row.Admin.Length > 0)
            store.SetGlobal(new SpecialtySettingsLayer
            {
                Specialties =
                {
                    [SpecialtyCatalog.KeyOf(specialty)] = new SpecialtyTemplateSettings
                    {
                        PromptSections = row.Admin.Select(a => new SpecialtyPromptSectionSettings
                        {
                            Id = a.Id,
                            Enabled = a.Enabled,
                            Text = a.Text,
                        }).ToList(),
                    },
                },
            });

        var states = store.EffectivePromptSectionStates(Owner, specialty);
        states.Select(s => s.Id).Should().Equal(row.Expected.Select(e => e.Id), "порядок — порядок каталога");
        for (var i = 0; i < row.Expected.Length; i++)
        {
            var e = row.Expected[i];
            states[i].Enabled.Should().Be(e.Enabled, $"{specialty}/{scenario}/{e.Id}.Enabled");
            states[i].Text.Should().Be(e.Text, $"{specialty}/{scenario}/{e.Id}.Text");
            states[i].EnabledSource.Should().Be(e.EnabledSource, $"{specialty}/{scenario}/{e.Id}.EnabledSource");
            states[i].TextSource.Should().Be(e.TextSource, $"{specialty}/{scenario}/{e.Id}.TextSource");
        }
    }
}
