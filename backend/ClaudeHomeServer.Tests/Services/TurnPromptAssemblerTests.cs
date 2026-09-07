using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using VoicePrompts = ClaudeHomeServer.Services.Prompts.VoicePrompts;

namespace ClaudeHomeServer.Tests.Services;

// Склейка системного промпта хода из секций. Сторож главного инварианта фичи «какой промпт
// ушёл»: показанное пользователю и отправленное модели собираются из ОДНОГО списка секций,
// поэтому разойтись не могут. Раньше текст копился отдельной переменной, и тихая потеря
// секции в рефакторинге не поймалась бы ничем.
public class TurnPromptAssemblerTests
{
    private static PromptSectionDto S(string key, string text, string kind = "system") =>
        new(key, key, text, kind);

    [Fact]
    public void Секции_КлеятсяЧерезПустуюСтроку_ВПорядкеСписка()
    {
        var text = TurnPromptAssembler.Combine([S("a", "Первая"), S("b", "Вторая")], null);

        text.Should().Be("Первая\n\nВторая");
    }

    [Fact]
    public void ПустыеСекции_НеДаютДвойныхРазделителей()
    {
        var text = TurnPromptAssembler.Combine(
            [S("a", "Первая"), S("empty", "   "), S("b", "Вторая")], null);

        text.Should().Be("Первая\n\nВторая");
        text.Should().NotContain("\n\n\n");
    }

    [Fact]
    public void СлойПерсоны_ОтделяетсяГоризонтальнойЧертой()
    {
        var text = TurnPromptAssembler.Combine([S("a", "Правила")], "Ты — Дмитрий");

        text.Should().Be("Правила\n\n---\n\nТы — Дмитрий");
    }

    [Fact]
    public void ТолькоСлойПерсоны_ИдётБезРазделителя()
    {
        var text = TurnPromptAssembler.Combine([], "Ты — Дмитрий");

        text.Should().Be("Ты — Дмитрий");
    }

    [Fact]
    public void ТекстХода_ВСистемныйПромптНеПопадает()
    {
        // Kind = turn — это сообщение с обвязками, оно уходит в stdin, а не в
        // --append-system-prompt: иначе «скопировать промпт» отдавало бы склейку,
        // которой модели никогда не отправляли
        var text = TurnPromptAssembler.Combine(
            [S("a", "Правила"), S("turn-text", "Почини сборку", "turn")], null);

        text.Should().Be("Правила");
    }

    [Fact]
    public void ПустойНабор_ДаётПустуюСтроку()
    {
        TurnPromptAssembler.Combine([], null).Should().BeEmpty();
    }

    [Fact]
    public void ГолосовойРежим_СлойПерсоныИдётПослеСекцииVoiceMode()
    {
        // Порядок — фундамент фичи голосового режима: слой персоны (с оговоркой
        // VoicePrompts.PersonaOverride в конце) обязан клеиться ПОСЛЕ секции voice-mode,
        // иначе оговорка теряет смысл «последнего слова»
        var personaLayer = "Ты — Дмитрий\n\n" + VoicePrompts.PersonaOverride;
        var text = TurnPromptAssembler.Combine(
            [S("voice-mode", VoicePrompts.SectionText)], personaLayer);

        text.IndexOf(VoicePrompts.PersonaOverride)
            .Should().BeGreaterThan(text.IndexOf(VoicePrompts.SectionText));
        text.Should().EndWith(VoicePrompts.PersonaOverride);
    }

    // Развилка «какой формат увидит модель» — единственная точка (VoicePrompts.SectionFor),
    // вынесенная из ClaudeSession ради этого шва. Тут решается, придёт ответ коротким
    // целиком или полным с маркером <voice>, поэтому проверяем все четыре исхода.
    // Озвучка выключена — секция всё равно едет, но другая: выдержку у длинных ответов
    // просим и в обычном текстовом чате, она нужна глазами не меньше, чем вслух
    [Fact]
    public void ВыборСекции_БезОзвучки_ПроситВыдержкуУДлинных()
    {
        VoicePrompts.SectionFor(voiceMode: false, digest: false, heard: true)
            .Should().Be(VoicePrompts.LongAnswerSectionText);

        // Ход без живого читателя (исполнитель задачи, автоматизация, делегированный)
        // выдержку не просит: смотреть на плашку некому, а маркер засорит транскрипт
        VoicePrompts.SectionFor(voiceMode: false, digest: false, heard: false)
            .Should().BeNull();
    }

    [Fact]
    public void ВыборСекции_Разговор_ЭтоКороткийФормат()
    {
        VoicePrompts.SectionFor(voiceMode: true, digest: false, heard: true)
            .Should().Be(VoicePrompts.SectionText);
        // Разговор формат не теряет и без живого слушателя — поведение оставлено как было
        VoicePrompts.SectionFor(voiceMode: true, digest: false, heard: false)
            .Should().Be(VoicePrompts.SectionText);
    }

    [Fact]
    public void ВыборСекции_Digest_ТолькоКогдаЕстьКомуСлушать()
    {
        VoicePrompts.SectionFor(voiceMode: true, digest: true, heard: true)
            .Should().Be(VoicePrompts.DigestSectionText);

        // Ход исполнителя задачи / правила автоматизации / делегированный: озвучивать
        // некому, а маркер <voice> засорил бы транскрипт
        VoicePrompts.SectionFor(voiceMode: true, digest: true, heard: false)
            .Should().BeNull();
    }

    [Fact]
    public void ВыборСекции_ОзвучкаПеребиваетВыдержку()
    {
        // Разговор: ответ и так короткий целиком, отдельный блок выдержки в нём не нужен
        VoicePrompts.SectionFor(voiceMode: true, digest: false, heard: true)
            .Should().Be(VoicePrompts.SectionText);

        // Озвучка выжимки: там блок обязателен ВСЕГДА, а не только у длинных ответов
        VoicePrompts.SectionFor(voiceMode: true, digest: true, heard: true)
            .Should().Be(VoicePrompts.DigestSectionText);
    }

    // ===== ApplyBudget: бюджет промпта + срезка нестабильных секций (задача dc641949) =====

    private static PromptSectionDto Sys(string key, string text) =>
        new(key, key, text, "system", Stable: false);

    private static PromptSectionDto StableSec(string key, string text) =>
        new(key, key, text, "system", Stable: true);

    // Минимальная нагрузка args + workingDir + cli, чтобы итоговая формула Estimate
    // была короткой и не съедала бюджет. Тесты далее штурмуют 30 000 символов в основном
    // через --append-system-prompt.
    private static readonly string[] TinyArgs = ["--print", "--input-format", "stream-json"];
    private const string TinyCli = "claude";
    private const string TinyCwd = "C:\\proj";

    [Fact]
    public void ApplyBudget_КороткийПромпт_НеРежет()
    {
        var sections = new List<PromptSectionDto> { Sys("code-graph", "граф"), Sys("recall-memory", "память") };
        var r = TurnPromptAssembler.ApplyBudget(sections, "persona", TinyArgs, TinyCli, TinyCwd);

        r.Overflowed.Should().BeFalse();
        r.TruncatedSections.Should().BeEmpty();
        r.CombinedPrompt.Should().Contain("граф").And.Contain("память").And.Contain("persona");
    }

    [Fact]
    public void ApplyBudget_ПревышениеПорога_РежетВУказанномПорядке()
    {
        // Заполняем каждую из 5 секций приоритета по 12 000 символов. Без срезки суммарно
        // 60 000 — далеко за бюджетом 30 000. После срезания 3 секций остаётся 24 000 +
        // personaLayer — это уже влезает. Проверяем порядок И что срезка успела остановиться.
        var sections = new List<PromptSectionDto>
        {
            Sys("code-graph", new string('a', 12_000)),
            Sys("dossier-recall", new string('b', 12_000)),
            Sys("recall-notes", new string('c', 12_000)),
            Sys("recall-memory", new string('d', 12_000)),
            Sys("persona-mentions", new string('e', 12_000)),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, "слой персоны", TinyArgs, TinyCli, TinyCwd);

        r.Overflowed.Should().BeFalse("срезка трёх секций уложила остаток в бюджет 30 000");
        r.TruncatedSections.Should().HaveCount(3,
            "три секции суммарно 36 000 символов — этого хватило, чтобы влезть в бюджет");
        r.TruncatedSections[0].Key.Should().Be("(truncated:code-graph)",
            "первый приоритет — code-graph");
        r.TruncatedSections[1].Key.Should().Be("(truncated:dossier-recall)",
            "второй — dossier-recall");
        r.TruncatedSections[2].Key.Should().Be("(truncated:recall-notes)",
            "третий — recall-notes");
    }

    [Fact]
    public void ApplyBudget_ПереполнениеПослеВсехСрезок_Overflowed()
    {
        // Сценарий, который приводит к PromptOverflowException в ClaudeSession: все 5
        // секций приоритета огромные, и даже после их срезания остаётся стабильная
        // обвязка выше бюджета. Стабильные рубить НЕЛЬЗЯ, поэтому ClaudeSession бросает
        // исключение. 5 × 10 000 = 50 000 нестабильных + 35 000 стабильных = 85 000.
        // После срезания 5 нестабильных остаётся 35 000 стабильных + cli/cwd > 30 000.
        var sections = new List<PromptSectionDto>
        {
            Sys("code-graph", new string('a', 10_000)),
            Sys("dossier-recall", new string('b', 10_000)),
            Sys("recall-notes", new string('c', 10_000)),
            Sys("recall-memory", new string('d', 10_000)),
            Sys("persona-mentions", new string('e', 10_000)),
            StableSec("project-builtin-0", new string('Z', 35_000)),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, TinyCli, TinyCwd);

        r.Overflowed.Should().BeTrue(
            "5 нестабильных срезаны, осталась 35 000 стабильных + cli/cwd — НЕ влезает " +
            "(стабильные рубить нельзя, иначе сломается контракт секций, которые модель ожидает)");
        r.TruncatedSections.Should().HaveCount(5,
            "все пять нестабильных срезаны, иначе бы осталась ещё одна");
    }

    // Длины подобраны так, чтобы первая секция вытолкнула сумму ЗА порог, а после её
    // удаления оставшиеся секции + personaLayer УЖЕ укладываются в бюджет. Это основной
    // сценарий задачи dc641949: 30-32к чаты режут ОДНУ секцию и стартуют штатно.
    [Fact]
    public void ApplyBudget_СрезкаОднойСекции_ВозвращаетКороткийПромпт()
    {
        // 35 000 — выше BudgetThreshold (30 000) даже с учётом коротких секций.
        // Без срезки суммарно 35 000 + ~50 символов обвязки — за порогом.
        var bigBlock = new string('a', 35_000);
        var sections = new List<PromptSectionDto>
        {
            Sys("code-graph", bigBlock),
            Sys("recall-memory", "короткая память"),
            StableSec("mcp-tasks", "короткие таски"),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, "persona", TinyArgs, TinyCli, TinyCwd);

        r.Overflowed.Should().BeFalse(
            "после срезания code-graph 35 000 символов + стабильные секции + personaLayer " +
            "укладываются в порог 30 000");
        r.TruncatedSections.Should().HaveCount(1);
        r.TruncatedSections[0].Key.Should().Be("(truncated:code-graph)");
        // Урезанный code-graph НЕ попал в склейку
        r.CombinedPrompt.Should().NotContain(bigBlock);
        // Стабильные секции и persona-layer не тронуты
        r.CombinedPrompt.Should().Contain("короткие таски").And.Contain("короткая память")
            .And.Contain("persona");
    }

    [Fact]
    public void ApplyBudget_СтабильныеСекцииНеРежутся()
    {
        // 5 стабильных секций (не в приоритете) общей длиной сильно за 30 000 — но они
        // не должны срезаться. Если даже после этого строка не влезает — overflowed=true,
        // но TruncatedSections пустой.
        var sections = new List<PromptSectionDto>
        {
            StableSec("mcp-tasks", new string('a', 10_000)),
            StableSec("mcp-memory", new string('b', 10_000)),
            StableSec("mcp-personas", new string('c', 10_000)),
            StableSec("voice-mode", new string('d', 5_000)),
            StableSec("images", new string('e', 5_000)),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, TinyCli, TinyCwd);

        r.TruncatedSections.Should().BeEmpty(
            "стабильные секции не в приоритете срезания — кэш промпта их держит, ими нельзя рубить");
        // Содержимое всех стабильных секций доехало
        r.CombinedPrompt.Should().Contain(new string('a', 10_000));
        r.CombinedPrompt.Should().Contain(new string('b', 10_000));
    }

    [Fact]
    public void ApplyBudget_PersonaLayerНеТрогается()
    {
        // Переполнение от code-graph, personaLayer огромный и СТАБИЛЬНЫЙ — после срезки
        // code-graph проверяем, что personaLayer доехал целиком и в склейке, и в TruncatedSections.
        var sections = new List<PromptSectionDto>
        {
            Sys("code-graph", new string('a', 25_000)),
            Sys("recall-memory", "mem"),
        };
        var bigPersona = "ты — " + new string('P', 5_000);
        var r = TurnPromptAssembler.ApplyBudget(sections, bigPersona, TinyArgs, TinyCli, TinyCwd);

        r.Overflowed.Should().BeFalse();
        r.CombinedPrompt.Should().Contain(bigPersona,
            "personaLayer — отдельный аргумент, в TruncationOrder не входит, режется только промпт");
    }

    [Fact]
    public void ApplyBudget_БезСекций_ПустойCombinedPrompt()
    {
        // Защита от регрессии: секций нет, personaLayer есть — порция промпта идёт
        // от персоны, порог не превышен, TruncatedSections пуст.
        var r = TurnPromptAssembler.ApplyBudget([], "Только персона", TinyArgs, TinyCli, TinyCwd);
        r.Overflowed.Should().BeFalse();
        r.TruncatedSections.Should().BeEmpty();
        r.CombinedPrompt.Should().Be("Только персона");
    }

    [Fact]
    public void ApplyBudget_УчитываетРабочийКаталогИИмяCli()
    {
        // Увеличим cli-путь и рабочий каталог так, чтобы они САМИ съели приличный кусок
        // бюджета. Один длинный стабильный блок сверху вытолкнет за порог, и мы увидим,
        // что Estimate учитывает и cli, и cwd.
        var sections = new List<PromptSectionDto>
        {
            StableSec("mcp-tasks", new string('a', 10_000)),
        };
        var longCli = new string('C', 5_000);
        var longCwd = new string('D', 5_000);
        var r = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, longCli, longCwd);

        // 10 000 + 5 000 + 5 000 + (TinyArgs суммарно < 100) ≈ 20 100 символов, в пороге.
        // Конкретная цифра не важна — нам важно, что оценка учитывает оба компонента, иначе
        // на проде (cwd = "C:\Sources\ClaudeCodeServer" плюс имя cli) мы бы считали меньше
        // и резали в момент, когда реальный cmdline бы и так влез.
        r.TotalCmdlineChars.Should().BeGreaterThan(15_000);
    }

    [Fact]
    public void TruncationOrder_СодержитТолькоСтабильныеFalseКлючи()
    {
        // Регрессия: TruncationOrder — единственный источник правды для порядка срезания.
        // Ключи должны совпадать с теми, что ClaudeSession помечает stable: false.
        TurnPromptAssembler.TruncationOrder.Should().Equal(
            "code-graph", "dossier-recall", "recall-notes", "recall-memory", "persona-mentions");
    }
}
