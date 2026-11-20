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

    // Карточка прошлой ночи: страница накоплена ею, текста этого источника у текущего
    // пакета нет. Цитата её сноски намеренно НЕ совпадает с SourceText: иначе базовые
    // тесты зелёные случайно, а живой случай (вторая ночь на той же странице) падает
    // (ревью S6b-2, M2).
    private const string ForeignCardTitle = "Карточка прежнего чата";
    private const string ForeignCardPath = "Проекты/p1/Карточка-прежнего-чата.md";
    private const string ForeignQuote = "Собрано в прошлом чате: план был остаться на другой системе";

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

    private static readonly string ForeignCardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:s0#m1-4
        hash: 0000000000000000000000000000000000000000000000000000000000000000
        captured: 2026-10-26
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{ForeignCardTitle}}
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

        [^s1]: [[{{ForeignCardTitle}}]] — «{{ForeignQuote}}»
        """;

    private static WikiPlanValidator.PlanOp Op(string op, string page, int claim, string? target = null) =>
        new(op, page, claim, target);

    private static WikiPlanValidator.Plan Plan(params WikiPlanValidator.PlanOp[] ops) => new([.. ops]);

    private static IReadOnlyList<WikiPlanValidator.Claim> Claims(params WikiPlanValidator.Claim[] c) => [.. c];

    // Снимок «второй ночи»: карточка текущего чата, карточка прошлой ночи и накопленная
    // страница, чья сноска ведёт чужую карточку.
    private static IReadOnlyList<WikiGate.Draft> Snapshot() =>
        [new(CardPath, CardFile), new(ForeignCardPath, ForeignCardFile), new(PagePath, PageFile)];

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
    public void Validate_МодельнаяОперацияMerge_Отброшена_ОстальнаяОперацияПринята()
    {
        var r = Run(Plan(
            Op(WikiPackage.OpMerge, "Сущность", 0, "Сущность"),
            Op(WikiPackage.OpAppend, "Сущность", 0)));

        r.Accepted.Should().ContainSingle(op => op.Op == WikiPackage.OpAppend);
        r.Rejected.Should().ContainSingle()
            .Which.Reason.Should().Contain("merge").And.Contain("код подбора");
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

    // ─── WC-2: операция в карточку источника — прямая причина, а не «нет секции» ──

    [Fact]
    public void AppendВКарточкуИсточника_ОтказСПодсказкойCreate()
    {
        // Карточка источника стоит в снимке раздела: append в неё раньше отклонялся как
        // «нет секции „Сводка“» — в ремонте модель на это не отвечала. Теперь отказ
        // называется прямо: в карточку операцию не принять, новое знание — через create.
        var r = Run(Plan(Op(WikiPackage.OpAppend, CardTitle, 0)));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().HaveCount(1);
        r.Rejected[0].Reason.Should().Contain("карточка источника").And.Contain("create");
        r.Rejected[0].Index.Should().Be(0);
    }

    [Fact]
    public void LinkВКарточкуИсточника_ТаЖеПричина()
    {
        // Та же карточка и для link: проверка стоит до проверки секции, поэтому причина
        // одна для append и link.
        var r = Run(Plan(Op(WikiPackage.OpLink, CardTitle, 0, "Сущность")));

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().HaveCount(1);
        r.Rejected[0].Reason.Should().Contain("карточка источника").And.Contain("create");
    }

    [Fact]
    public void CreateВРазделСОднимиКарточками_ПринятКакОбычно()
    {
        // Отказ по карточке — только для операций не-create: create новой страницы в
        // раздел, где лежат одни карточки, принимается как раньше.
        var r = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpCreate, "Знание-новое", 0)),
            Claims(GoodClaim), Src, Section, [WikiSections.Common],
            [new(CardPath, CardFile)], new());

        r.Accepted.Should().ContainSingle(op => op.Op == WikiPackage.OpCreate);
        r.Rejected.Should().BeEmpty();
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

    // ─── M1 (ревью S6b-2): проверка цели и рендер — в одном состоянии ──────────

    [Fact]
    public void Validate_ДваSupersedeНаОднуЦель_ВтораяОтклонена_ИсключенияНаружуНет()
    {
        // Штатный план модели: две правки одного факта. После первой операции цель уже не
        // на странице — вторая отклоняется как операция без цели, а не роняет валидатор
        // исключением: и проверка цели, и рендер видят то состояние, что будет записан.
        var target = "Прежний план был остаться на Windows";

        var r = Run(Plan(
            Op(WikiPackage.OpSupersede, "Сущность", 0, target),
            Op(WikiPackage.OpSupersede, "Сущность", 0, target)));

        r.Accepted.Should().ContainSingle(op => op.Op == WikiPackage.OpSupersede);
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("цель supersede не на странице");
        r.Waiting.Should().BeEmpty();
    }

    // ─── B4a: класс и автор цели замены — по сноскам утверждения, не по ответу модели ──

    [Theory]
    [InlineData(WikiPackage.OpSupersede)]
    [InlineData(WikiPackage.OpConflict)]
    public void Validate_ЦельСоСноскойHuman_КлассСХвостомHuman_АвторЦелиHuman(string op)
    {
        var humanCard = ForeignCardFile.Replace("origin: session", "origin: human")
            .Replace("ref: session:s0#m1-4", "ref: human:1");
        var snapshot = new WikiGate.Draft[]
            { new(CardPath, CardFile), new(ForeignCardPath, humanCard), new(PagePath, PageFile) };

        var r = WikiPlanValidator.Validate(
            Plan(Op(op, "Сущность", 0, "Прежний план был остаться на Windows")),
            Claims(GoodClaim), Src, Section, [WikiSections.Common], snapshot, new());

        r.Accepted.Should().ContainSingle().Which.Class.Should().Be("session×" + op + WikiPackage.HumanTargetSuffix);
    }

    [Fact]
    public void Validate_ЦельСоСноскойИИ_КлассБезХвоста()
    {
        var r = Run(Plan(Op(WikiPackage.OpSupersede, "Сущность", 0, "Прежний план был остаться на Windows")));

        r.Accepted.Should().ContainSingle().Which.Class.Should().Be("session×supersede");
    }

    // ─── B4c: время цели замены — из карточек сносок утверждения ──────────────

    private static WikiPlanValidator.Result RunReplacement(
        string targetCardFile, DateTimeOffset? newSource, string op = WikiPackage.OpSupersede)
    {
        var snapshot = new WikiGate.Draft[]
            { new(CardPath, CardFile), new(ForeignCardPath, targetCardFile), new(PagePath, PageFile) };
        return WikiPlanValidator.Validate(
            Plan(Op(op, "Сущность", 0, "Прежний план был остаться на Windows")),
            Claims(GoodClaim), Src, Section, [WikiSections.Common], snapshot,
            new() { NewSourceTime = newSource });
    }

    private static string WithWritten(string cardFile, string written) =>
        cardFile.Replace("captured: 2026-10-26", $"captured: 2026-10-26\nwritten: {written}");

    [Fact]
    public void Validate_Замена_ИсточникНовееЦели_Само_ИсточникСтарше_ВоВходящих()
    {
        var target = WithWritten(ForeignCardFile, "2026-10-01T00:00:00+00:00");

        RunReplacement(target, DateTimeOffset.Parse("2026-10-02T00:00:00+00:00"))
            .Accepted.Should().ContainSingle().Which.Decision.Should().Be(WikiPackage.DecisionAuto);
        var older = RunReplacement(target, DateTimeOffset.Parse("2026-09-30T00:00:00+00:00")).Accepted.Single();
        older.Decision.Should().Be(WikiPackage.DecisionInbox);
        older.Reason.Should().Contain("не новее заменяемого");
    }

    [Fact]
    public void Validate_Замена_КарточкаЦелиБезWritten_ВремяНеизвестно_ВоВходящих_NotCaptured()
    {
        // captured карточки цели (2026-10-26) старше источника, но он НЕ время написания: подставлять нельзя.
        var op = RunReplacement(ForeignCardFile, DateTimeOffset.Parse("2026-12-01T00:00:00+00:00")).Accepted.Single();

        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.Reason.Should().Contain("Неизвестно, когда написан источник утверждения");
    }

    [Fact]
    public void Validate_Замена_ЦельЧеловека_ВремяЦели_ЭтоCaptured_ТеПравилаЧтоУИИ()
    {
        var human = ForeignCardFile.Replace("origin: session", "origin: human")
            .Replace("ref: session:s0#m1-4", "ref: human:1");

        var later = RunReplacement(human, DateTimeOffset.Parse("2026-10-27T00:00:00+00:00")).Accepted.Single();
        var earlier = RunReplacement(human, DateTimeOffset.Parse("2026-10-25T00:00:00+00:00")).Accepted.Single();

        later.Decision.Should().Be(WikiPackage.DecisionAuto);
        later.Class.Should().EndWith(WikiPackage.HumanTargetSuffix);
        earlier.Decision.Should().Be(WikiPackage.DecisionInbox);
    }

    // ─── TL-3: на хронике (нет секции «История») ИИ не стирает формулировку человека молча ──

    private const string ChroniclePath = "Проекты/p1/Хроники/Чат.md";
    private const string HumanCardPath = "Проекты/p1/Карточка-человека.md";

    private static string ChronicleFile(bool withHistory) => $$"""
        ---
        type: {{(withHistory ? "topic" : "chronicle")}}
        status: active
        ---

        # Чат

        {{(withHistory
            ? "## Сводка\n- Бюджет на GPU — до 15 тыс. ₽ в месяц [^s1]\n- Лимит поднимать нельзя [^s2]\n\n## Хронология\n\n## Противоречия\n\n## История"
            : "## О чём\nОбсуждали бюджет.\n\n## Что решили\n- Бюджет на GPU — до 15 тыс. ₽ в месяц [^s1]\n- Лимит поднимать нельзя [^s2]\n\n## Что выяснили\n\n## Открытые вопросы")}}

        [^s1]: [[Карточка человека]] — «Бюджет на GPU — до 15 тыс. ₽ в месяц»
        [^s2]: [[{{ForeignCardTitle}}]] — «Лимит поднимать нельзя»
        """;

    private static WikiPlanValidator.Result RunOnChronicle(bool withHistory, string target)
    {
        var humanCard = ForeignCardFile.Replace("origin: session", "origin: human")
            .Replace("ref: session:s0#m1-4", "ref: human:1")
            .Replace(ForeignCardTitle, "Карточка человека");
        var aiCard = WithWritten(ForeignCardFile, "2026-10-01T00:00:00+00:00");
        var humanCardW = WithWritten(humanCard, "2026-10-01T00:00:00+00:00");
        var snapshot = new WikiGate.Draft[]
        {
            new(CardPath, CardFile), new(ForeignCardPath, aiCard), new(HumanCardPath, humanCardW),
            new(ChroniclePath, ChronicleFile(withHistory)),
        };
        return WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpSupersede, "Чат", 0, target)),
            Claims(GoodClaim), Src, Section, [WikiSections.Common], snapshot,
            new() { NewSourceTime = DateTimeOffset.Parse("2026-10-27T00:00:00+00:00") });
    }

    [Fact]
    public void Validate_ХроникаБезИстории_ЗаменаСтрокиЧеловека_ВоВходящих_СПричиной()
    {
        var op = RunOnChronicle(withHistory: false, "Бюджет на GPU — до 15 тыс. ₽ в месяц").Accepted.Single();

        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.Reason.Should().Be("На этой странице нет секции «История», прежняя формулировка не сохранится — решите сами");
    }

    [Fact]
    public void Validate_ХроникаБезИстории_ЗаменаСтрокиИИ_Само_КакРаньше()
    {
        var op = RunOnChronicle(withHistory: false, "Лимит поднимать нельзя").Accepted.Single();

        op.Decision.Should().Be(WikiPackage.DecisionAuto);
    }

    [Fact]
    public void Validate_СтраницаСИсторией_ЗаменаСтрокиЧеловека_Само_СтрокаУходитВИсторию()
    {
        var r = RunOnChronicle(withHistory: true, "Бюджет на GPU — до 15 тыс. ₽ в месяц");

        r.Accepted.Single().Decision.Should().Be(WikiPackage.DecisionAuto);
        var draft = WikiPlanValidator.RenderApply(
            [new(ChroniclePath, ChronicleFile(true))],
            r.Accepted.Single(), GoodClaim.Quote!, CardTitle)!.Value;
        var history = WikiPage.Parse(draft.Content).Model!.Sections.Single(x => x.Name == "История");
        history.Lines.Should().ContainSingle(l => l.Contains("Бюджет на GPU"));
    }

    // ─── M2 (ревью S6b-2): дословность — только для сносок этого пакета ───────

    [Fact]
    public void Validate_СноскаПрошлойНочиНаЧужуюКарточку_НовуюОперациюНеОтвергает()
    {
        // Страница накоплена прошлой ночью: её сноска ведёт карточку другого источника,
        // текста которого у этого пакета нет. Новая операция с цитатой, подтверждённой по
        // своему источнику, принимается: чужая сноска не может её отвергнуть.
        var r = Run(Plan(Op(WikiPackage.OpAppend, "Сущность", 0)));

        r.Accepted.Should().HaveCount(1).And.ContainSingle(op =>
            op.Op == WikiPackage.OpAppend && op.Text == GoodClaim.Text);
        r.Rejected.Should().BeEmpty();
        r.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Validate_СноскаНаКарточкуЭтогоПакетаСЦитатойНеИзИсточника_ОтклоняетсяГейтом()
    {
        // Контраст: текст этой карточки у пакета ЕСТЬ — её сноска проверяется по-прежнему,
        // даже если добавлена не этим пакетом. GivenOnly не отменяет дословность, он
        // ограничивает её теми источниками, чей текст вызывающий дал.
        var page = PageFile.Replace(
            $"[^s1]: [[{ForeignCardTitle}]] — «{ForeignQuote}»",
            $"[^s1]: [[{CardTitle}]] — «{ForeignQuote}»");

        var r = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)),
            Claims(GoodClaim), Src, Section, [WikiSections.Common],
            [new(CardPath, CardFile), new(PagePath, page)], new());

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("отсутствует в тексте источника");
    }

    // ─── M3 (ревью S6b-2): цепочка вверх валидируется до снимка и дедупа ──────

    [Fact]
    public void Validate_ЧужойПроектВЦепочкеВверх_ПакетОтвержен_ЧужойПутьВПричинуНеУходит()
    {
        // Цепочку даёт вызывающий (WikiSections.UpChain): «projects/q2» в ней — ошибка
        // вызывающего, и она отвергает весь пакет ДО дедупа. Снимок с чужой страницей того
        // же заголовка не успевает попасть ни в дедуп-пул, ни в причину операции.
        var foreign = new WikiGate.Draft("Проекты/q2/Знание-новое.md", PageFile.Replace("# Сущность", "# Знание-новое"));

        var r = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpCreate, "Знание-новое", 0)),
            Claims(GoodClaim), Src, Section, ["projects/q2"],
            [.. Snapshot(), foreign], new());

        r.Accepted.Should().BeEmpty();
        r.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("цепочка вверх");
        r.Rejected.Should().NotContain(i => i.Reason.Contains("Проекты/q2"));
        r.Violations.Should().Contain(v => v.Contains("цепочка вверх"));
    }

    [Fact]
    public void Validate_ПовторИлиСамРазделВЦепочке_ПакетОтвергнут_ДедупНеСтроится()
    {
        var repeated = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)), Claims(GoodClaim), Src, Section,
            [WikiSections.Common, WikiSections.Common], Snapshot(), new());
        repeated.Accepted.Should().BeEmpty();
        repeated.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("повтор");

        var self = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)), Claims(GoodClaim), Src, Section,
            [Section], Snapshot(), new());
        self.Accepted.Should().BeEmpty();
        self.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("цепочка вверх");
    }

    // ─── B1 (ADR-038): «Заметки» человека не мешают ai-плану, который их не трогает ──────────

    [Fact]
    public void Validate_СтраницаСЗаметкамиПланНеТрогаетЗаметки_ОперацияПринята()
    {
        // Мутация «без базы» (убрать baseState из WikiGate.Check в WikiPlanValidator): «Заметки»
        // считаются новыми, операция страницы отклонена — HaveCount(1) и Rejected.BeEmpty краснеют.
        var withNotes = PageFile + "\n\n## Заметки\nМоя мысль.\n";
        var snapshot = new List<WikiGate.Draft>
        {
            new(CardPath, CardFile), new(ForeignCardPath, ForeignCardFile), new(PagePath, withNotes),
        };

        var r = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)), Claims(GoodClaim), Src, Section,
            [WikiSections.Common], snapshot, new());

        r.Accepted.Should().ContainSingle(op => op.Text == GoodClaim.Text);
        r.Rejected.Should().BeEmpty();
        r.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Validate_КарточкаHumanУжеВРазделе_НарушениеНеШумит()
    {
        var human = ForeignCardFile.Replace("origin: session", "origin: human");
        var snapshot = new List<WikiGate.Draft>
        {
            new(CardPath, CardFile), new(ForeignCardPath, human), new(PagePath, PageFile),
        };

        var r = WikiPlanValidator.Validate(
            Plan(Op(WikiPackage.OpAppend, "Сущность", 0)), Claims(GoodClaim), Src, Section,
            [WikiSections.Common], snapshot, new());

        r.Accepted.Should().ContainSingle();
        r.Violations.Should().BeEmpty("карточка human была в разделе до плана — ai её не создавал");
    }
}
