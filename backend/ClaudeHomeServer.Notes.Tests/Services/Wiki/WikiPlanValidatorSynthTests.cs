using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S2-6 (ADR-039, решения 2–3): ValidateSynth — операция синтеза «утверждение + цитаты».
// База — как у WikiPlanValidatorTests: карточка, накопленная страница и текст источника.
public class WikiPlanValidatorSynthTests
{
    private const string Section = "projects/p1";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Проекты/p1/Карточка-чата.md";
    private const string PagePath = "Проекты/p1/Сущность.md";
    private const string OtherPath = "Проекты/p1/Другая.md";

    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Сборка занимает 12 минут.";

    private const string QLinux = "перенести сборку на Linux";
    private const string QWindows = "остаться на Windows";
    private const string QMinutes = "Сборка занимает 12 минут";

    private static readonly WikiPlanValidator.Source Src = new(WikiGate.OriginSession, CardTitle, SourceText);

    private static readonly string CardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
        """;

    private static readonly string PageFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка

        ## Хронология

        ## Противоречия

        ## История
        """;

    // Страница, на которой цитата QLinux уже занята карточкой текущего источника.
    private static readonly string OtherFile = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Другая

        ## Сводка
        Сборка переезжает [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «{{QLinux}}»
        """;

    // Страница чужого раздела: в снимок валидатора не входит (снимок — раздел и цепочка),
    // ссылка на неё — «вбок», в ветке синтеза снимается.
    private const string ForeignPath = "Проекты/p2/Чужое.md";
    private static readonly string ForeignFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Чужое

        ## Сводка

        ## Хронология

        ## Противоречия

        ## История
        """;

    // Страница раздела common — вверх по цепочке: ссылка на неё уцелевает.
    private const string CommonPath = "Общее/Знание.md";
    private static readonly string CommonFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Знание

        ## Сводка

        ## Хронология

        ## Противоречия

        ## История
        """;

    // Страница раздела с числом в заголовке: проверка чисел — по claim без текста
    // уцелевших ссылок, это число — не число утверждения.
    private const string ReportPath = "Проекты/p1/Отчёт-2023.md";
    private static readonly string ReportFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Отчёт 2023

        ## Сводка

        ## Хронология

        ## Противоречия

        ## История
        """;

    private static WikiPlanValidator.SynthOp Op(
        string op, string page, string claim, string[]? quotes, string? kind = null, string? asOf = null, string? target = null) =>
        new(op, kind, page, claim, quotes, target, asOf);

    private static WikiPlanValidator.Result Run(
        IReadOnlyList<WikiGate.Draft>? snapshot = null,
        params WikiPlanValidator.SynthOp[] ops) =>
        WikiPlanValidator.ValidateSynth(
            new([.. ops]),
            Src,
            Section,
            [WikiSections.Common],
            snapshot ?? [new(CardPath, CardFile), new(PagePath, PageFile)],
            new());

    private static WikiPlanValidator.Result Run(params WikiPlanValidator.SynthOp[] ops) => Run(null, ops);

    [Fact]
    public void ValidateSynth_ДвеЦитаты_ПринятаСТекстомРавнымClaimИЦитатами()
    {
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux, Windows оставляем", [QLinux, QWindows],
            kind: "state", asOf: "2026-11-01"));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("Сборка уходит на Linux, Windows оставляем");
        op.Claim.Should().Be(op.Text);
        op.Quotes.Should().Equal(QLinux, QWindows);
        op.Decision.Should().Be(WikiPackage.DecisionAuto);
        // Kind и as_of осмысленны только у create: у append они обнуляются, а не отказ.
        op.Kind.Should().BeNull();
        op.AsOf.Should().BeNull();
    }

    [Fact]
    public void RenderApply_ДвеЦитаты_СтрокаСДвумяСноскамиПоПорядку()
    {
        var op = new WikiPackage.Operation
        {
            Op = WikiPackage.OpAppend, Page = PagePath, Text = "Итог", Class = "x", Decision = WikiPackage.DecisionAuto,
        };
        List<WikiGate.Draft> state = [new(PagePath, PageFile)];

        var draft = WikiPlanValidator.RenderApply(state, op, [QLinux, QWindows], CardTitle)!.Value;

        var page = WikiPage.Parse(draft.Content).Model!;
        page.Sections.Single(s => s.Name == "Сводка").Lines.Should().ContainSingle()
            .Which.Should().Be("Итог [^s1][^s2]");
        page.Footnotes.Select(f => f.Quote).Should().Equal(QLinux, QWindows);
    }

    [Fact]
    public void RenderApply_СтараяСигнатура_РавнаНовойСОднойЦитатой()
    {
        var op = new WikiPackage.Operation
        {
            Op = WikiPackage.OpAppend, Page = PagePath, Text = "Итог", Class = "x", Decision = WikiPackage.DecisionAuto,
        };
        List<WikiGate.Draft> state = [new(PagePath, PageFile)];

        WikiPlanValidator.RenderApply(state, op, QLinux, CardTitle)!.Value.Content
            .Should().Be(WikiPlanValidator.RenderApply(state, op, [QLinux], CardTitle)!.Value.Content);
    }

    [Fact]
    public void ValidateSynth_ЦитатNull_Отказ() => БезЦитат(null);

    [Fact]
    public void ValidateSynth_ЦитатПусто_Отказ() => БезЦитат([]);

    private static void БезЦитат(string[]? quotes)
    {
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux", quotes));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("без цитат");
    }

    // Границы цитат на операцию (ревью S2-6, M5): та же мера, что у формы пакета
    // (WikiPackage.cs:355-370), но нарушение отклоняет одну операцию с причиной,
    // а не роняет форму всего пакета.
    [Fact]
    public void ValidateSynth_ЧетыреЦитаты_ОтказОднойОперации_ВтораяПринята_ФормаПакетаЧиста()
    {
        var r = Run(
            Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux", [QLinux, QWindows, QMinutes, "Прежний план был"]),
            Op(WikiPackage.OpAppend, "Сущность", "Сборка занимает 12 минут", [QMinutes]));

        var rej = r.Rejected.Should().ContainSingle().Subject;
        rej.Index.Should().Be(0);
        rej.Reason.Should().Contain("цитат").And.Contain("допустимо 1–3");
        r.Accepted.Should().ContainSingle().Which.Page.Should().EndWith("Сущность.md");
        r.Violations.Should().BeEmpty();

        // Пакет из оставшейся принятой операции — формы без ошибок: отказ одной операции
        // не касается целого пакета (в отличие от формы пакета, где 4 цитаты давали бы
        // ошибку на весь пакет).
        var pkg = new WikiPackage
        {
            Id = Guid.NewGuid().ToString("N"),
            Section = Section,
            Sources = [new WikiPackage.SourceRef(WikiGate.OriginSession, "session:s1#m1-9")],
            Operations = r.Accepted,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackage.ShapeErrors(pkg).Should().BeEmpty();
    }

    [Fact]
    public void ValidateSynth_ЦитатаНижеНижнейГраницы_ОтказПоДлине()
    {
        // «Linux» (5 символов) дословно есть в источнике, но ниже MinQuoteLength:
        // отказывает форма — причина называет длину, а не меру 1.
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux", ["Linux"]));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason
            .Should().Contain("5 символов").And.Contain("допустимо 8–300");
    }

    [Fact]
    public void ValidateSynth_ЦитатаРовноMaxQuoteLength_Принята()
    {
        // Верхняя граница включительно: MeasureQuoteLength out-of-contract — только raw > Max.
        var longQ = new string('к', WikiPage.MaxQuoteLength);
        var src = new WikiPlanValidator.Source(WikiGate.OriginSession, CardTitle, "Решение: " + longQ + ". Остальное.");

        var r = WikiPlanValidator.ValidateSynth(
            new([Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux", [longQ])]),
            src, Section, [WikiSections.Common],
            [new(CardPath, CardFile), new(PagePath, PageFile)], new());

        r.Rejected.Should().BeEmpty();
        r.Accepted.Should().ContainSingle().Which.Quotes.Should().Equal(longQ);
    }

    [Fact]
    public void ValidateSynth_ВтораяЦитатаНеИзИсточника_ОтказСНомером()
    {
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка", [QLinux, "этого в источнике нет вовсе"]));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("цитата 2 отсутствует в источнике дословно");
    }

    [Fact]
    public void ValidateSynth_ЦитатаДословноНаСтраницеАНеВИсточнике_AntiLoop()
    {
        var page = PageFile.Replace("## Сводка\n", "## Сводка\nпрежняя формулировка про макос\n");
        var r = Run([new(CardPath, CardFile), new(PagePath, page)],
            Op(WikiPackage.OpAppend, "Сущность", "Пересказ", ["прежняя формулировка про макос"]));

        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("anti_loop");
    }

    // Хвост S2-5 (минор 1 R-S2-5): строка-маркер пониженной реплики процесса буквально стоит
    // в тексте окна, поэтому её текст проходит меру 1 как дословный. Цитата, составленная из
    // маркера (часть, равная, с соседней строкой), отклоняется кодом; обычная цитата из того
    // же окна проходит. Текст маркера — из константы WikiChatSlicer.ProcessMarker, не литерал.
    private static readonly WikiPlanValidator.Source MarkerSrc = new(WikiGate.OriginSession, CardTitle,
        "Решение: перенести сборку на Linux.\n" + WikiChatSlicer.ProcessMarker + "\nСборка занимает 12 минут.");

    private static WikiPlanValidator.Result RunMarker(params WikiPlanValidator.SynthOp[] ops) =>
        WikiPlanValidator.ValidateSynth(new([.. ops]), MarkerSrc, Section, [WikiSections.Common],
            [new(CardPath, CardFile), new(PagePath, PageFile)], new());

    [Theory]
    [InlineData("служебная реплика")]
    [InlineData(WikiChatSlicer.ProcessMarker)]
    [InlineData("перенести сборку на Linux. " + WikiChatSlicer.ProcessMarker)]
    public void ValidateSynth_ЦитатаСоставленнаяИзМаркераПроцесса_ОтказСПричиной(string quote)
    {
        var r = RunMarker(Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux", [quote]));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Be("цитата из служебной реплики");
    }

    [Fact]
    public void ValidateSynth_ОбычнаяЦитатаИзОкнаСМаркером_Принята()
    {
        var r = RunMarker(Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux", [QLinux]));

        r.Rejected.Should().BeEmpty();
        r.Accepted.Should().ContainSingle();
    }

    [Fact]
    public void ValidateSynth_ЦитатаЗанятаДругойСтраницей_Отказ()
    {
        var r = Run([new(CardPath, CardFile), new(PagePath, PageFile), new(OtherPath, OtherFile)],
            Op(WikiPackage.OpAppend, "Сущность", "Сборка на Linux", [QLinux]));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("цитата уже на странице").And.Contain(OtherPath);
    }

    [Fact]
    public void ValidateSynth_ЦитатаНаТойЖеСтраницеСвоейСамой_ПовторАНеЗанятаДругой()
    {
        // QLinux уже стоит занятой сноской на «Другой»: exceptPage исключает свою страницу
        // из проверки владельца, поэтому отказ — «повтор» (IsDuplicateStatement), а не «цитата уже на странице».
        var r = Run([new(CardPath, CardFile), new(OtherPath, OtherFile)],
            Op(WikiPackage.OpAppend, "Другая", "Совсем иное утверждение", [QLinux]));

        r.Accepted.Should().BeEmpty();
        var reason = r.Rejected.Should().ContainSingle().Subject.Reason;
        reason.Should().StartWith("повтор");
        reason.Should().NotContain("цитата уже на странице");
    }

    [Fact]
    public void ValidateSynth_ОднаЦитатаДвумНовымСтраницамПлана_ВтораяОтклонена()
    {
        var r = Run(
            Op(WikiPackage.OpCreate, "Первая", "Утверждение один", [QLinux], kind: "decision"),
            Op(WikiPackage.OpCreate, "Вторая", "Утверждение два", [QLinux], kind: "decision"));

        r.Accepted.Should().ContainSingle().Which.Page.Should().EndWith("Первая.md");
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("цитата уже на странице").And.Contain("Первая.md");
    }

    [Fact]
    public void ValidateSynth_ЧислоНеИзЦитаты_ВоВходящихСПричиной_ПодтверждённоеПроходит()
    {
        var bad = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка занимает 15 минут", [QMinutes]));
        var op = bad.Accepted.Should().ContainSingle().Subject;
        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.Reason.Should().Contain("число не подтверждено цитатой").And.Contain("15");
        bad.Rejected.Should().BeEmpty();

        var good = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка занимает 12 минут", [QMinutes]));
        good.Accepted.Should().ContainSingle().Which.Decision.Should().Be(WikiPackage.DecisionAuto);
    }

    [Fact]
    public void ValidateSynth_СпорнаяПолитикаИНеподтверждённоеЧисло_ПричинаДописывается()
    {
        // Ревью S2-6, M5: причина чисел не заменяет причину политики, а дописывается к ней —
        // человек видит и риск политики, и причину чисел.
        var r = WikiPlanValidator.ValidateSynth(
            new([Op(WikiPackage.OpAppend, "Сущность", "Сборка занимает 15 минут", [QMinutes])]),
            Src, Section, [WikiSections.Common],
            [new(CardPath, CardFile), new(PagePath, PageFile)],
            new() { PackageAfterModelRepair = true });

        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.Reason.Should().Contain("пакет после ремонта модели")
            .And.Contain("число не подтверждено цитатой").And.Contain("15");
    }

    [Fact]
    public void ValidateSynth_AutoПолитикаИНеподтверждённоеЧисло_ПричинаТолькоПроЧисла()
    {
        // Ревью aaf6dacf, находка 2: при вердикте политики «auto» его причина не дописывается —
        // «применяется само (§12г)» противоречит тому, что из-за чисел операция ушла во «Входящие».
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка занимает 15 минут", [QMinutes]));

        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.Reason.Should().StartWith("число не подтверждено цитатой").And.Contain("15")
            .And.NotContain("применяется само");
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("rule")]
    [InlineData("term")]
    public void ValidateSynth_CreateВида_ВидПопадаетВTypeШапки(string kind)
    {
        var r = Run(Op(WikiPackage.OpCreate, "Новая", "Сборка уходит на Linux", [QLinux], kind: kind, asOf: "2026-11-01"));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Kind.Should().Be(kind);
        // as_of учитывается только у state: у прочих видов — обнулён, а не отказ.
        op.AsOf.Should().BeNull();
        List<WikiGate.Draft> state = [new(CardPath, CardFile)];
        var draft = WikiPlanValidator.RenderApply(state, op, op.Quotes!, CardTitle)!.Value;
        WikiPage.Parse(draft.Content).Model!.Frontmatter.Type.Should().Be(kind);
    }

    [Fact]
    public void ValidateSynth_CreateState_AsOfИдётВШапкуЧерновика()
    {
        var r = Run(Op(WikiPackage.OpCreate, "Состояние", "Сборка уходит на Linux", [QLinux], kind: "state", asOf: "2026-11-01"));

        var op = r.Accepted.Should().ContainSingle().Subject;
        op.AsOf.Should().Be("2026-11-01");
        List<WikiGate.Draft> state = [new(CardPath, CardFile)];
        var fm = WikiPage.Parse(WikiPlanValidator.RenderApply(state, op, op.Quotes!, CardTitle)!.Value.Content).Model!.Frontmatter;
        fm.Type.Should().Be("state");
        fm.AsOf.Should().Be("2026-11-01");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("chronicle")]
    [InlineData("topic")]
    [InlineData(null)]
    public void ValidateSynth_CreateВидВнеПеречня_Отказ(string? kind)
    {
        var r = Run(Op(WikiPackage.OpCreate, "Новая", "Сборка уходит на Linux", [QLinux], kind: kind));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("вид");
    }

    [Fact]
    public void ValidateSynth_StateБезAsOf_Отказ()
    {
        var r = Run(Op(WikiPackage.OpCreate, "Состояние", "Сборка уходит на Linux", [QLinux], kind: "state"));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("as_of");
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("вчера")]
    public void ValidateSynth_StateСНеверымAsOf_ОтказСтрогогоРазбораШапки(string asOf)
    {
        var r = Run(Op(WikiPackage.OpCreate, "Состояние", "Сборка уходит на Linux", [QLinux], kind: "state", asOf: asOf));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("as_of");
    }

    [Fact]
    public void ValidateSynth_Supersede_ВнеСинтеза()
    {
        var r = Run(Op(WikiPackage.OpSupersede, "Сущность", "Сборка", [QLinux], target: "что-то"));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("не входит в синтез");
    }

    [Fact]
    public void ValidateSynth_ПустойПланИNull_НеПадает()
    {
        WikiPlanValidator.ValidateSynth(null, Src, Section, [], [], new()).Violations.Should().NotBeEmpty();
        Run(Array.Empty<WikiPlanValidator.SynthOp>()).Accepted.Should().BeEmpty();
    }

    [Fact]
    public void ValidateSynth_ЧислоБлизкоеНоНеТо_2_28ПриЦитате2_27_ВоВходящих()
    {
        var src = new WikiPlanValidator.Source(WikiGate.OriginSession, CardTitle, "Версия 2.27 вышла вчера, замеры идут.");
        var r = WikiPlanValidator.ValidateSynth(
            new([Op(WikiPackage.OpAppend, "Сущность", "Версия 2.28 вышла вчера", ["Версия 2.27 вышла вчера"])]),
            src, Section, [WikiSections.Common], [new(CardPath, CardFile), new(PagePath, PageFile)], new());

        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.Reason.Should().Contain("2.28");
    }

    [Fact]
    public void RenderApply_ДвеЦитатыПринятойОперации_ТочнаяСтрокаДвеСноскиИЧистыйГейт()
    {
        const string claim = "Сборка уходит на Linux, Windows оставляем";
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", claim, [QLinux, QWindows]));
        var op = r.Accepted.Should().ContainSingle().Subject;
        List<WikiGate.Draft> state = [new(CardPath, CardFile), new(PagePath, PageFile)];
        var baseState = state.ToArray();

        var draft = WikiPlanValidator.RenderApply(state, op, op.Quotes!, CardTitle)!.Value;

        var page = WikiPage.Parse(draft.Content).Model!;
        page.Sections.Single(s => s.Name == "Сводка").Lines.Should().ContainSingle()
            .Which.Should().Be($"{claim} [^s1][^s2]");
        page.Footnotes.Should().HaveCount(2).And.OnlyContain(f => f.Target.IsLink && f.Target.Text == CardTitle);
        state.RemoveAll(d => d.Path == PagePath);
        state.Add(draft);
        WikiGate.Check(state, Section, [WikiSections.Common], new Dictionary<string, string> { [CardPath] = SourceText },
            WikiGate.VerbatimScope.GivenOnly, WikiPackage.AuthorAi, baseState).Should().BeEmpty();
    }

    [Fact]
    public void RenderApply_CreateState_ШапкаСодержитTypeИAsOfИНеСодержитKind()
    {
        var op = Run(Op(WikiPackage.OpCreate, "Состояние", "Сборка уходит на Linux", [QLinux], kind: "state", asOf: "2026-11-01"))
            .Accepted.Should().ContainSingle().Subject;
        List<WikiGate.Draft> state = [new(CardPath, CardFile)];

        var content = WikiPlanValidator.RenderApply(state, op, op.Quotes!, CardTitle)!.Value.Content;

        var lines = content.Split('\n');
        lines.Should().Contain("type: state").And.Contain("as_of: 2026-11-01");
        lines.Should().NotContain(l => l.StartsWith("kind:"));
    }

    // —— Очередь 1, 2А: неразрешимые ссылки в claim снимаются, а не валят план ——
    // Инвариант: боковая ссылка не попадает на диск ни в каком виде. Резолв — тот же,
    // что у гейта (WikiGate.ResolveLink); «иное» — чужой раздел, призрак, нераспознанный
    // префикс и страница, которую создаёт этот же план. Снятая ссылка заменяется своим
    // текстом без скобок: алиасом, а если его нет — заголовком без префикса раздела.

    [Fact]
    public void ValidateSynth_ПризракВClaim_СнятТекстомБезСкобок_ОперацияПроходитВалидаторИГейт()
    {
        // Без снятия план отвергался бы итоговым гейтом («ссылка не найдена в разделе и
        // вверх по цепочке»); с снятием — принимается, и на страницу идёт текст без скобок.
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux, см. [[Призрак]]", [QLinux]));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("Сборка уходит на Linux, см. Призрак");
        op.StrippedLinks.Should().Be(1);
    }

    [Fact]
    public void ValidateSynth_ГолаяСсылкаВбок_Снята_ИмяОстаётсяТекстом()
    {
        var r = Run([new(CardPath, CardFile), new(PagePath, PageFile), new(ForeignPath, ForeignFile)],
            Op(WikiPackage.OpAppend, "Сущность", "См. [[Чужое]] — решение принято", [QWindows]));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("См. Чужое — решение принято");
        op.StrippedLinks.Should().Be(1);
    }

    [Fact]
    public void ValidateSynth_СсылкаСПрефиксомЧужогоРаздела_СнятаБезПрефикса()
    {
        var r = Run([new(CardPath, CardFile), new(PagePath, PageFile), new(ForeignPath, ForeignFile)],
            Op(WikiPackage.OpAppend, "Сущность", "См. [[Проекты/p2/Чужое]] — решение", [QWindows]));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("См. Чужое — решение");
        op.StrippedLinks.Should().Be(1);
    }

    [Fact]
    public void ValidateSynth_СсылкаСАлиасом_РезолвитсяЦель_СсылкаОстаётся()
    {
        // B0: цель — то, что до первого «|» (WikiPage.LinkTarget): «Сущность» — существующая
        // страница этого раздела, ссылка резолвится и остаётся как написана. В алиас снимается
        // только нерезолвимая (тот же PlainLinkText, что прежде).
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "Сборка уходит на Linux [[Сущность|подробнее о сборке]]", [QLinux]));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("Сборка уходит на Linux [[Сущность|подробнее о сборке]]");
        op.StrippedLinks.Should().Be(0);
    }

    [Fact]
    public void ValidateSynth_СсылкаНаCreateТогоЖеПлана_Снята_ОбеОперацииПриняты()
    {
        // Страниц самого плана в снимке нет: «Вторая» создаётся этим же планом — ссылка
        // на неё «иное» и снимается до текста.
        var r = Run(
            Op(WikiPackage.OpCreate, "Вторая", "Новая страница", [QLinux], kind: "decision"),
            Op(WikiPackage.OpAppend, "Сущность", "Связано с [[Вторая]]", [QWindows]));

        r.Rejected.Should().BeEmpty();
        var created = r.Accepted.Single(o => o.Page.EndsWith("Вторая.md"));
        created.StrippedLinks.Should().Be(0);
        var linked = r.Accepted.Single(o => o.Page.EndsWith("Сущность.md"));
        linked.Text.Should().Be("Связано с Вторая");
        linked.StrippedLinks.Should().Be(1);
    }

    [Fact]
    public void ValidateSynth_СсылкиВРазделеИВверх_УцелелиКакНаписаны()
    {
        var r = Run([new(CardPath, CardFile), new(PagePath, PageFile), new(OtherPath, OtherFile), new(CommonPath, CommonFile)],
            Op(WikiPackage.OpAppend, "Сущность", "Связано с [[Другая]] и [[Знание]] и [[Общее/Знание]]", [QWindows]));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("Связано с [[Другая]] и [[Знание]] и [[Общее/Знание]]");
        op.StrippedLinks.Should().Be(0);
    }

    [Fact]
    public void ValidateSynth_ЧислоТолькоВЗаголовкеУцелевшейСсылки_НеДаётNumberUnconfirmed()
    {
        var r = Run([new(CardPath, CardFile), new(PagePath, PageFile), new(ReportPath, ReportFile)],
            Op(WikiPackage.OpAppend, "Сущность", "См. [[Отчёт 2023]] — решение принято", [QLinux]));

        r.Rejected.Should().BeEmpty();
        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("См. [[Отчёт 2023]] — решение принято");
        op.StrippedLinks.Should().Be(0);
        op.Decision.Should().Be(WikiPackage.DecisionAuto);
        op.Reason.Should().NotContain("число не подтверждено цитатой");
    }

    [Fact]
    public void ValidateSynth_ЧислоВСнятойСсылке_СчитаетсяЧисломClaim_ВоВходящих()
    {
        // Та же фраза, но «Отчёт 2023» — призрак: текст стал обычным текстом утверждения,
        // его число больше не подтверждено цитатой → «Входящие» с причиной чисел.
        var r = Run(Op(WikiPackage.OpAppend, "Сущность", "См. [[Отчёт 2023]] — решение принято", [QLinux]));

        var op = r.Accepted.Should().ContainSingle().Subject;
        op.Text.Should().Be("См. Отчёт 2023 — решение принято");
        op.StrippedLinks.Should().Be(1);
        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.Reason.Should().Contain("число не подтверждено цитатой").And.Contain("2023");
    }

    [Fact]
    public void Validate_СтараяФорма_СсылкиClaimНеТрогаются_СтрогийГейтБезИзменений()
    {
        // Снятие — только ветка синтеза. Старая форма остаётся при строгом гейте:
        // неразрешимая ссылка по-прежнему отклоняет операцию.
        var claims = new[] { new WikiPlanValidator.Claim("См. [[Призрак]] — решение", QLinux) };
        var plan = new WikiPlanValidator.Plan([new WikiPlanValidator.PlanOp(WikiPackage.OpAppend, "Сущность", 0, null)]);
        var r = WikiPlanValidator.Validate(plan, claims, Src, Section, [WikiSections.Common],
            [new(CardPath, CardFile), new(PagePath, PageFile)], new());

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("не найдена в разделе");
    }
}
