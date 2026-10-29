using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S6b-2 (ADR-035, §12б этапы 6–8, §12г меры 1–3): проверки кода над планом модели до
// сборки пакета. Общая база: текст источника и страницы раздела, в которых цитаты
// стоят дословно; «плохие» кейсы меняют ровно одну вещь — источник, страницу или цель.
public class WikiPlanValidatorTests
{
    private const string Section = "projects/p1";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Проекты/p1/Карточка-чата.md";
    private const string PagePath = "Проекты/p1/Сущность.md";

    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows.";

    private const string OnPageOnly = "план был перенести сборку на macOS";

    private static readonly WikiPlanValidator.Claim GoodClaim =
        new("перенести сборку на Linux", "перенести сборку на Linux");
    private static readonly WikiPlanValidator.Claim ForeignClaim =
        new("сборка на macOS была бы удобнее", "сборка на macOS была бы удобнее");
    private static readonly WikiPlanValidator.Claim PageQuoteClaim =
        new("о прежнем плане", OnPageOnly);
    private static readonly WikiPlanValidator.Claim RepeatClaim =
        new("Прежний план был остаться на Windows", "Прежний план был остаться на Windows");

    private static readonly WikiPlanValidator.Source Src =
        new(WikiGate.OriginSession, CardTitle, SourceText);

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

    private static readonly string PageFile = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        Прежний план был остаться на Windows — {{OnPageOnly}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
        """;

    private static WikiPlanValidator.PlanOp Op(string op, string page, int claim, string? target = null) =>
        new(op, page, claim, target);

    private static WikiPlanValidator.Plan Plan(params WikiPlanValidator.PlanOp[] ops) => new([.. ops]);

    private static IReadOnlyList<WikiPlanValidator.Claim> Claims(params WikiPlanValidator.Claim[] c) => [.. c];

    private static IReadOnlyList<WikiGate.Draft> Snapshot() =>
        [new(CardPath, CardFile), new(PagePath, PageFile)];

    private static WikiPlanValidator.Result Run(
        WikiPlanValidator.Plan? plan,
        IReadOnlyList<WikiPlanValidator.Claim>? claims = null,
        WikiPlanValidator.Source? source = null,
        string? section = null,
        WikiApplyPolicy.ApplyContext? context = null) =>
        WikiPlanValidator.Validate(
            plan,
            claims ?? Claims(GoodClaim, ForeignClaim, PageQuoteClaim, RepeatClaim),
            source ?? Src,
            section ?? Section,
            [WikiSections.Common],
            Snapshot(),
            context ?? new());

    // ─── Мера 1: цитата подтверждается по тексту источника (c1) ─────────────────

    [Fact]
    public void Validate_УтверждениеСЦитатойНеИзИсточникаОтброшено_ОстальноеПринято()
    {
        var r = Run(Plan(
            Op(WikiPackage.OpAppend, "Сущность", 0),
            Op(WikiPackage.OpAppend, "Сущность", 1)));

        // В пакет идёт только операция с подтверждённой цитатой: в принятых нет ни одного
        // утверждения без сноски — сноска каждой принятой строки ведёт ту цитату, что
        // подтверждена мерой 1 (она же стоит в рендере страницы).
        r.Accepted.Should().HaveCount(1).And.ContainSingle(op =>
            op.Op == WikiPackage.OpAppend && op.Text == GoodClaim.Text);
        r.Rejected.Should().ContainSingle()
            .Which.Reason.Should().Contain("цитата").And.Contain("источник");

        // Класс — по источнику этой операции (хвост S4d, г), решение — по политике.
        r.Accepted[0].Class.Should().Be(WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend));
        r.Accepted[0].Decision.Should().Be(WikiPackage.DecisionAuto);
        r.Accepted[0].BaseHashes.Should().ContainKey(PagePath)
            .WhoseValue.Should().Match<string>(h => WikiPackage.IsSha256Hex(h));
    }

    [Fact]
    public void Validate_КлассИПолитикаПоUrlИсточнику_ВоВходящих_СПричиной()
    {
        var r = Run(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)),
            source: new(WikiGate.OriginUrl, CardTitle, SourceText));

        r.Accepted.Should().ContainSingle();
        r.Accepted[0].Class.Should().Be(WikiPackage.ClassKey(WikiGate.OriginUrl, WikiPackage.OpAppend));
        r.Accepted[0].Decision.Should().Be(WikiPackage.DecisionInbox);
        r.Accepted[0].Reason.Should().Contain("url").And.NotBeNullOrWhiteSpace();
    }

    // ─── Анти-петля (c3) и повтор утверждения (мера 2) ─────────────────────────

    [Fact]
    public void Validate_ЦитатаСТекстомСтраницыВикиАНеИсточника_ОтбитаПричинойAntiLoop()
    {
        var r = Run(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)),
            Claims(PageQuoteClaim));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle()
            .Which.Reason.Should().Contain("anti_loop").And.Contain(PagePath);
    }

    [Fact]
    public void Validate_ПовторУтвержденияУжеНаСтранице_ОтбрасываетсяМолча()
    {
        var r = Run(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)),
            Claims(RepeatClaim));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("повтор");
    }

    // ─── Лимиты меры 3 и проекции (c2) ─────────────────────────────────────────

    [Fact]
    public void Validate_ЗаписьВ_проекциюИДесятьНовыхСтраниц_НольНаПроецию_ПятьПринято_ОстальноеЖдёт()
    {
        var plan = Plan(
            [.. Enumerable.Range(1, 10).Select(n => Op(WikiPackage.OpCreate, $"Знание-{n}", 0)),
                Op(WikiPackage.OpAppend, "Проекты/p1/_schema.md", 0)]);

        var r = Run(plan);

        // '_*' — не страница для плана: ни одной принятой или ждущей операции над проекцией.
        r.Rejected.Should().ContainSingle()
            .Which.Reason.Should().Contain("_schema").And.Contain("пишет код");
        r.Accepted.Should().HaveCount(5)
            .And.OnlyContain(op => op.Op == WikiPackage.OpCreate);
        r.Accepted.Select(op => op.Page).Should().Equal(
            Enumerable.Range(1, 5).Select(n => $"Проекты/p1/Знание-{n}.md"));
        r.Waiting.Should().HaveCount(5)
            .And.OnlyContain(i => i.Reason.Contains("ждёт"));
        r.Violations.Should().BeEmpty();
    }

    // ─── Раздел и цель операции (c4) ───────────────────────────────────────────

    [Fact]
    public void Validate_СтраницаСоседнегоРазделаИОперацияБезЦели_Отклонены_ВПакетНеИдут()
    {
        var r = Run(Plan(
            Op(WikiPackage.OpAppend, "Проекты/q2/Сущность.md", 0),
            Op(WikiPackage.OpSupersede, "Сущность", 0),
            Op(WikiPackage.OpConflict, "Сущность", 0),
            Op(WikiPackage.OpLink, "Сущность", 0)));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().HaveCount(4);
        r.Rejected[0].Reason.Should().Contain("не раздела").And.Contain("Проекты/q2");
        r.Rejected.Skip(1).Should().OnlyContain(i => i.Reason.Contains("без цели"));
    }

    [Fact]
    public void Validate_LinkВСоседнийРазделИLinkВНикуда_ОтбитыИтогомГейта()
    {
        // Обе цели — вне «внутри раздела и вверх» (ADR-036, решение 4): их видит весь
        // WikiGate на итоговом состоянии, обе операции в пакет не идут. Каждая плохая
        // ссылка трогает свою страницу: отказ атрибутуется своей операции (на одной
        // странице одна ошибка отклоняет все её операции — это правило вызывающего,
        // не проверяемое здесь).
        var other = new WikiGate.Draft("Проекты/p1/Другая.md", PageFile.Replace("# Сущность", "# Другая"));
        var r = WikiPlanValidator.Validate(
            Plan(
                Op(WikiPackage.OpLink, "Сущность", 0, "Проекты/q2/Другое"),
                Op(WikiPackage.OpLink, "Другая", 0, "Нет-такой-страницы")),
            Claims(GoodClaim), Src, Section, [WikiSections.Common],
            [.. Snapshot(), other], new());

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().HaveCount(2);
        r.Rejected[0].Reason.Should().Contain("projects/q2");
        r.Rejected[1].Reason.Should().Contain("не найдена");
    }

    [Fact]
    public void Validate_CreateСДублемЗаголовка_ВозможныйДубльРешаетЧеловек()
    {
        var r = Run(Plan(Op(WikiPackage.OpCreate, "Сущность", 0)));

        // Дедуп раздела (S4d) + строка таблицы «create при совпадении» → «Входящие»
        // со ссылкой на найденную страницу; в черновом состоянии новой страницы нет —
        // её применение (переименование/слияние) решает человек.
        r.Accepted.Should().ContainSingle(op => op.Decision == WikiPackage.DecisionInbox);
        r.Accepted[0].Reason.Should().Contain("дубль").And.Contain(PagePath);
        r.Violations.Should().BeEmpty();
    }

    // ─── Fail-closed входа (ни исключения наружу, ни «само») ───────────────────

    [Fact]
    public void Validate_НеизвестныйВходFailClosed_НичегоНеПринято_ИсключенияНаружуНет()
    {
        var nullPlan = Run(null);
        nullPlan.Violations.Should().Contain(v => v.Contains("план null"));
        nullPlan.Accepted.Should().BeEmpty();

        var nullOps = Run(new WikiPlanValidator.Plan(null));
        nullOps.Violations.Should().Contain(v => v.Contains("план null"));

        var nullOpPlan = Run(Plan([null!]));
        nullOpPlan.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("null");

        var unknownOp = Run(Plan(Op("retract", "Сущность", 0)));
        unknownOp.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("вне перечня");

        var badIndex = Run(Plan(Op(WikiPackage.OpAppend, "Сущность", 9)));
        badIndex.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("без утверждения");

        var noClaims = Run(Plan(Op(WikiPackage.OpAppend, "Сущность", 0)), Claims([]));
        noClaims.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("без утверждения");

        var emptySection = Run(Plan(Op(WikiPackage.OpAppend, "Сущность", 0)), section: "");
        emptySection.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("раздел");

        var noSource = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)), Claims(GoodClaim), null, Section,
            [WikiSections.Common], Snapshot(), new());
        noSource.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("источник");
    }

    [Fact]
    public void Validate_СоседнийРазделИЛичноеНеДаютДедупа_ДубльИщетсяТолькоПоРазделуИВверх()
    {
        // Снимок с страницей соседнего проекта того же имени: дедуп новой страницы
        // (хвост S4d, б) её не видит — новая страница в своём разделе чиста.
        var foreign = new WikiGate.Draft("Проекты/q2/Сущность.md", PageFile);
        var r = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpCreate, "Знание-новое", 0)),
            Claims(GoodClaim), Src, Section, [WikiSections.Common],
            [.. Snapshot(), foreign], new());

        r.Accepted.Should().ContainSingle().Which.Decision.Should().Be(WikiPackage.DecisionAuto);
        // Чужой раздел не вошёл ни в дедуп, ни в состояние: нарушений по нему нет.
        r.Violations.Should().BeEmpty();
    }
}
