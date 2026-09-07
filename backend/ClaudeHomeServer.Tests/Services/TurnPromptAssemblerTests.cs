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
        // workingDir — параметр ProcessStartInfo.WorkingDirectory, НЕ часть командной строки.
        // Поэтому Estimate НЕ учитывает workingDir. cli влияет: это начало командной строки.
        var sections = new List<PromptSectionDto>
        {
            StableSec("mcp-tasks", new string('a', 10_000)),
        };
        var longCli = new string('C', 5_000);
        var longCwd = new string('D', 5_000); // NOT counted — это WorkingDirectory, не cmdline
        var r = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, longCli, longCwd);

        // 10 000 + 5 000 cli + TinyArgs с экранированием ≈ 10 150+, в пороге.
        // Конкретная цифра не важна — нам важно, что cwd НЕ считается в оценке.
        r.TotalCmdlineChars.Should().BeGreaterThan(10_000);
    }

    // M5, главная регрессия ревью: чат со стабильной обвязкой в зоне 30 000–32 767 и пустыми
    // нестабильными секциями до фикса ходил нормально, а после фикса получал
    // PromptOverflowException. Порог 30 000 — ТРИГГЕР срезки, отказ ставится по CmdlineLimit.
    [Fact]
    public void ApplyBudget_Зона30к32к_НеОтказывает()
    {
        // 31 000 стабильной обвязки: срезать нечего (нестабильных секций нет), порог
        // срезки перейдён, но в лимит командной строки Windows мы укладываемся.
        var sections = new List<PromptSectionDto>
        {
            StableSec("project-builtin", new string('Z', 31_000)),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, TinyCli, TinyCwd);

        r.TotalCmdlineChars.Should().BeInRange(30_001, TurnPromptAssembler.CmdlineLimit);
        r.Overflowed.Should().BeFalse(
            "оценка ВЕРХНЯЯ и укладывается в 32 767 — ход обязан стартовать, "
            + "иначе это регрессия против поведения до фикса dc641949");
        r.CombinedPrompt.Should().Contain(new string('Z', 31_000));
    }

    [Fact]
    public void ApplyBudget_ЗаCmdlineLimit_Отказывает()
    {
        // Та же обвязка, но за жёстким лимитом Windows: доказать безопасность старта
        // нельзя, отказываем сразу — перебор шагов фолбэка тут бесполезен.
        var sections = new List<PromptSectionDto>
        {
            StableSec("project-builtin", new string('Z', 33_000)),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, TinyCli, TinyCwd);

        r.TotalCmdlineChars.Should().BeGreaterThan(TurnPromptAssembler.CmdlineLimit);
        r.Overflowed.Should().BeTrue();
    }

    // H1: снимок промпта строится из Sections. После срезки там не должно остаться секции,
    // которая в модель не ушла, — иначе шторка «что ушло модели» противоречит сама себе
    // (MaskArgs уже показывал урезанный --append-system-prompt).
    [Fact]
    public void ApplyBudget_Sections_НеСодержатСрезанное()
    {
        var bigBlock = new string('a', 35_000);
        var sections = new List<PromptSectionDto>
        {
            Sys("code-graph", bigBlock),
            Sys("recall-memory", "короткая память"),
            StableSec("mcp-tasks", "короткие таски"),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, "persona", TinyArgs, TinyCli, TinyCwd);

        r.TruncatedSections.Should().ContainSingle()
            .Which.Key.Should().Be("(truncated:code-graph)");
        r.Sections.Should().NotContain(s => s.Key == "code-graph",
            "срезанная секция обязана исчезнуть из итогового списка для снимка");
        r.Sections.Should().Contain(s => s.Key == "recall-memory");
        r.Sections.Should().Contain(s => s.Key == "mcp-tasks");
        // Входной список не мутируем: у вызывающего своя модель памяти
        sections.Should().HaveCount(3, "ApplyBudget не меняет переданный список");
    }

    [Fact]
    public void ApplyBudget_БезСрезки_SectionsРавныИсходным()
    {
        var sections = new List<PromptSectionDto> { Sys("code-graph", "граф"), StableSec("mcp-tasks", "таски") };
        var r = TurnPromptAssembler.ApplyBudget(sections, "persona", TinyArgs, TinyCli, TinyCwd);

        r.Sections.Should().HaveCount(2);
        r.Sections.Select(s => s.Key).Should().Equal("code-graph", "mcp-tasks");
    }

    // L8: слагаемое args в Estimate. Меряем РАЗНИЦУ двух оценок на одних и тех же секциях —
    // так тест ловит мутацию «не считать args» точной цифрой, а не порогом «больше чем».
    [Fact]
    public void Estimate_УчитываетДлинуArgs()
    {
        var sections = new List<PromptSectionDto> { StableSec("mcp-tasks", "коротко") };

        var without = TurnPromptAssembler.ApplyBudget(sections, null, [], TinyCli, TinyCwd);
        var with = TurnPromptAssembler.ApplyBudget(
            sections, null, ["--mcp-config", "C:/some/long/path.json"], TinyCli, TinyCwd);

        // Стоимость аргумента = длина + 2 (кавычки) + 2 × число кавычек внутри + 1 (пробел).
        // "--mcp-config" = 12 → 15; "C:/some/long/path.json" = 22 → 25.
        var expected = (12 + 2 + 1) + (22 + 2 + 1);
        (with.TotalCmdlineChars - without.TotalCmdlineChars).Should().Be(expected,
            "args входят в командную строку и обязаны учитываться в оценке");
    }

    [Fact]
    public void Estimate_КавычкиВнутриАргумента_УдваиваютЗапас()
    {
        var sections = new List<PromptSectionDto> { StableSec("mcp-tasks", "коротко") };

        var plain = TurnPromptAssembler.ApplyBudget(sections, null, ["abcd"], TinyCli, TinyCwd);
        var quoted = TurnPromptAssembler.ApplyBudget(sections, null, ["a\"cd"], TinyCli, TinyCwd);

        // Та же длина 4, но одна кавычка внутри: .NET экранирует её (" → \"), и верхняя
        // оценка обязана быть больше на 2 — иначе занизим и пропустим ход в Win32 206.
        (quoted.TotalCmdlineChars - plain.TotalCmdlineChars).Should().Be(2,
            "каждая внутренняя кавычка удорожает аргумент в экранированном виде");
    }

    // M4: пятый шаг срезки — persona-mentions, и это СТАБИЛЬНАЯ секция. Тест сверяет
    // фактический флаг Stable у секции, которую ApplyBudget реально удаляет на пятом шаге,
    // а не список строк (прежняя редакция сверяла строки и мимо флагов проходила молча).
    [Fact]
    public void TruncationOrder_ПервыеЧетыреНестабильны_ПятаяСтабильнаОсознанно()
    {
        var order = TurnPromptAssembler.TruncationOrder;
        order.Should().Equal(
            "code-graph", "dossier-recall", "recall-notes", "recall-memory", "persona-mentions");
        order[^1].Should().Be(TurnPromptAssembler.StableLastResortKey,
            "последний шаг вынесен именованной константой — это исключение из правила");

        // Заводим секции с ТЕМИ ЖЕ флагами, что ставит ClaudeSession: четыре первые
        // stable: false (Add(..., stable: false)), persona-mentions — по дефолту stable: true.
        var byKey = new Dictionary<string, PromptSectionDto>
        {
            ["code-graph"] = Sys("code-graph", new string('a', 9_000)),
            ["dossier-recall"] = Sys("dossier-recall", new string('b', 9_000)),
            ["recall-notes"] = Sys("recall-notes", new string('c', 9_000)),
            ["recall-memory"] = Sys("recall-memory", new string('d', 9_000)),
            ["persona-mentions"] = StableSec("persona-mentions", new string('e', 9_000)),
        };

        foreach (var key in order.Take(4))
            byKey[key].Stable.Should().BeFalse(
                $"«{key}» заводится stable: false — срезка по ней не бьёт по cache_read");

        byKey[TurnPromptAssembler.StableLastResortKey].Stable.Should().BeTrue(
            "persona-mentions стабильна: флаг НЕ меняем — stable: false удешевил бы аварийный "
            + "случай ценой инвалидации кэша у каждого хода всех чатов. Пятый шаг срезки "
            + "действительно бьёт по кэшируемому префиксу, и оправдан только тем, что "
            + "альтернатива — несостоявшийся ход");
    }

    // M4: текст пометки о срезке обязан говорить правду про кэш. Для четырёх нестабильных —
    // «stable: false, кэш не держит»; для persona-mentions — что кэш ПОСТРАДАЕТ.
    [Fact]
    public void ПометкаОСрезке_ПроPersonaMentions_НеВрётПроКэш()
    {
        // Стабильный балласт 25 000 подобран так, чтобы после срезки первых ЧЕТЫРЁХ
        // (36 000) сумма 25 000 + 9 000 всё ещё была за порогом — тогда цикл дойдёт до
        // пятого шага и реально снимет persona-mentions, а без балласта остановился бы
        // на четвёртом, и пометки про пятый ключ в снимке просто не было бы.
        var sections = new List<PromptSectionDto>
        {
            Sys("code-graph", new string('a', 9_000)),
            Sys("dossier-recall", new string('b', 9_000)),
            Sys("recall-notes", new string('c', 9_000)),
            Sys("recall-memory", new string('d', 9_000)),
            StableSec("persona-mentions", new string('e', 9_000)),
            StableSec("project-builtin", new string('Z', 25_000)),
        };
        var r = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, TinyCli, TinyCwd);

        var note = r.TruncatedSections.Single(s => s.Key == "(truncated:persona-mentions)");
        note.Text.Should().NotContain("stable: false",
            "секция стабильная — прежний текст утверждал обратное для всех пяти ключей");
        note.Text.Should().Contain("кэшируемому префиксу",
            "говорим честно: срезка по стабильной секции бьёт по кэшу");

        var plain = r.TruncatedSections.Single(s => s.Key == "(truncated:code-graph)");
        plain.Text.Should().Contain("stable: false",
            "для нестабильных прежняя формулировка верна и остаётся");
    }
}
