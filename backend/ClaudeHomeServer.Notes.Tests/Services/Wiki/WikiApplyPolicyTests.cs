using System.Text.Json;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S4d (ADR-035, решение 12г): политика применения «само / Входящие». Таблица §12г —
// источник правды теста: по кейсу на каждую из 8 строк «Входящие» и на каждый из 4
// пунктов «само». Порядок проверок фиксирован (выключатель → класс → риск), причина
// называет первую сработавшую; неизвестное — fail-closed в «Входящие».
public class WikiApplyPolicyTests
{
    private const string ProjectPage = "Проекты/p1/Сущность.md";
    private const string CommonPage = "Общее/Сущность.md";
    private const string ChroniclePage = "Личное/Хроники/Чат-2026-10-27.md";

    private static WikiPackage.Operation Op(
        string op,
        string origin = WikiGate.OriginSession,
        string page = ProjectPage) => new()
        {
            Op = op,
            Page = page,
            Class = WikiPackage.ClassKey(origin, op),
            // До решения политики значение placeholder: Decide его не читает.
            Decision = WikiPackage.DecisionAuto,
            Text = op is WikiPackage.OpCreate or WikiPackage.OpAppend or WikiPackage.OpSupersede ? "утверждение разбора" : null,
            Target = op is WikiPackage.OpSupersede or WikiPackage.OpConflict or WikiPackage.OpLink ? "действующее утверждение" : null,
        };

    private static WikiApplyPolicy.ApplyContext Ctx(
        bool autoApply = true,
        bool repaired = false,
        bool personalHome = false,
        string? pageStatus = null,
        WikiDedup.Duplicate? duplicate = null,
        bool chronicle = false,
        WikiApplyPolicy.NightCounters counters = default,
        bool backlog = false,
        params string[] disabledClasses) => new()
        {
            AutoApplyEnabled = autoApply,
            PackageAfterModelRepair = repaired,
            SourceHomeIsPersonal = personalHome,
            TargetPageStatus = pageStatus,
            PageDuplicate = duplicate,
            TargetIsChronicle = chronicle,
            Counters = counters,
            IsBacklogPass = backlog,
            DisabledClasses = new HashSet<string>(disabledClasses, StringComparer.Ordinal),
        };

    // Двенадцать обычных кейсов: 8 строк таблицы «Входящие» + 4 пункта «само».
    private static (string Name, WikiPackage.Operation Op, WikiApplyPolicy.ApplyContext Ctx)[] TwelveCases() =>
    [
        ("supersede", Op(WikiPackage.OpSupersede), Ctx()),
        ("conflict", Op(WikiPackage.OpConflict), Ctx()),
        ("дубль страницы", Op(WikiPackage.OpCreate), Ctx(duplicate: new WikiDedup.Duplicate("Общее/Сущность.md", "алиас", "заголовок"))),
        ("сверх лимита страниц", Op(WikiPackage.OpCreate), Ctx(counters: new(6, 1, 1))),
        ("внешний текст url", Op(WikiPackage.OpAppend, WikiGate.OriginUrl), Ctx()),
        ("личное в Общем", Op(WikiPackage.OpAppend, page: CommonPage), Ctx(personalHome: true)),
        ("страница disputed", Op(WikiPackage.OpAppend), Ctx(pageStatus: WikiFrontmatter.StatusDisputed)),
        ("пакет после ремонта", Op(WikiPackage.OpAppend), Ctx(repaired: true)),
        ("create хроники", Op(WikiPackage.OpCreate, page: ChroniclePage), Ctx(chronicle: true)),
        ("append", Op(WikiPackage.OpAppend), Ctx()),
        ("link", Op(WikiPackage.OpLink), Ctx()),
        ("create страницы знаний", Op(WikiPackage.OpCreate), Ctx()),
    ];

    // ─── Строки таблицы «Входящие» (8) ────────────────────────────────────────

    [Theory]
    [InlineData(WikiPackage.OpSupersede)]
    [InlineData(WikiPackage.OpConflict)]
    public void Decide_ЗамещениеИПротиворечие_БезВремениЦели_ВоВходящих(string op)
    {
        var v = WikiApplyPolicy.Decide(Op(op), Ctx());

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    // ── B4c: правило замены (ADR-038, решение 4) — каждое условие со своей причиной ──

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static WikiApplyPolicy.ApplyContext ReplCtx(
        DateTimeOffset? source = null, DateTimeOffset? target = null, DateTimeOffset? humanEdit = null,
        string author = WikiPackage.AuthorAi, params string[] disabledClasses) => new()
        {
            NewSourceTime = source ?? T0.AddDays(10),
            TargetSourceTime = target ?? T0,
            TargetAuthor = author,
            LastHumanEditAt = humanEdit is { } e
                ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { [ProjectPage] = e }
                : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal),
            DisabledClasses = new HashSet<string>(disabledClasses, StringComparer.Ordinal),
        };

    private static WikiPackage.Operation ReplOp(string op, bool humanTarget = false) =>
        Op(op) with { Class = WikiPackage.ClassKey(WikiGate.OriginSession, op, humanTarget) };

    [Theory]
    [InlineData(WikiPackage.OpSupersede, false)]
    [InlineData(WikiPackage.OpConflict, false)]
    [InlineData(WikiPackage.OpSupersede, true)]
    [InlineData(WikiPackage.OpConflict, true)]
    public void Замена_ВсеУсловияВыполнены_Само_ОдниПравилаДляЦелиИИиЧеловека(string op, bool human)
    {
        var v = WikiApplyPolicy.Decide(ReplOp(op, human),
            ReplCtx(author: human ? WikiPackage.AuthorHuman : WikiPackage.AuthorAi));

        v.IsAuto.Should().BeTrue(v.Reason);
        v.Reason.Should().Contain("источник новее");
    }

    [Theory]
    [InlineData(WikiPackage.OpSupersede)]
    [InlineData(WikiPackage.OpConflict)]
    public void Замена_ВремяНовогоИсточникаНеизвестно_ВоВходящих_СоСвоейПричиной(string op)
    {
        var ctx = ReplCtx() with { NewSourceTime = null };

        var v = WikiApplyPolicy.Decide(ReplOp(op), ctx);

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().Contain("Неизвестно, когда написан источник:");
    }

    [Fact]
    public void Замена_ВремяЦелиНеизвестно_ВоВходящих_СоСвоейПричиной()
    {
        var v = WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), ReplCtx() with { TargetSourceTime = null });

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().Contain("Неизвестно, когда написан источник утверждения");
    }

    [Fact]
    public void Замена_ИсточникРовноТогоЖеМоментаЧтоЦель_НеНовее_ВоВходящих_ПоМиллисекундеНовее_Само()
    {
        WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), ReplCtx(source: T0, target: T0))
            .Reason.Should().Contain("не новее заменяемого");
        WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), ReplCtx(source: T0.AddDays(-1), target: T0))
            .Decision.Should().Be(WikiPackage.DecisionInbox);

        WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), ReplCtx(source: T0.AddMilliseconds(1), target: T0))
            .IsAuto.Should().BeTrue("строго новее — на миллисекунду достаточно");
    }

    [Theory]
    [InlineData(WikiPackage.AuthorAi)]
    [InlineData(WikiPackage.AuthorHuman)]
    public void Замена_ИсточникСтаршеПравкиЧеловека_ВоВходящих_ГраницаРавенствоНеНовее(string author)
    {
        var edit = T0.AddDays(20);
        var op = ReplOp(WikiPackage.OpSupersede, author == WikiPackage.AuthorHuman);

        var older = WikiApplyPolicy.Decide(op, ReplCtx(source: edit.AddDays(-1), humanEdit: edit, author: author));
        var equal = WikiApplyPolicy.Decide(op, ReplCtx(source: edit, humanEdit: edit, author: author));
        var newer = WikiApplyPolicy.Decide(op, ReplCtx(source: edit.AddMilliseconds(1), humanEdit: edit, author: author));

        older.Reason.Should().Contain("Источник старше вашей правки");
        equal.Decision.Should().Be(WikiPackage.DecisionInbox, "ровно в момент правки — не новее");
        newer.IsAuto.Should().BeTrue(newer.Reason);
    }

    [Fact]
    public void Замена_ПравкаЧеловекаНаДругойСтранице_НеМешает_ПустойЖурнал_УсловиеНеСрабатывает()
    {
        var other = ReplCtx() with
        {
            LastHumanEditAt = new Dictionary<string, DateTimeOffset> { ["Проекты/p1/Другая.md"] = T0.AddYears(5) },
        };

        WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), other).IsAuto.Should().BeTrue();
        WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), ReplCtx()).IsAuto.Should().BeTrue();
    }

    [Fact]
    public void Замена_ПравкаЧеловека_ТолькоЗамены_AppendНаТойЖеСтраницеИдётПоОбщимПравилам()
    {
        var ctx = ReplCtx(humanEdit: T0.AddYears(5)) with { NewSourceTime = null };

        WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), ctx).IsAuto.Should().BeTrue("правка защищает только от замен");
    }

    [Fact]
    public void Замена_КарантинN7d_ИдётРаньшеПравилаЗамены_ПричинаN7d()
    {
        var ctx = ReplCtx() with
        {
            RecentlyRevertedPages = new Dictionary<string, DateTimeOffset> { [ProjectPage] = RevertedAt },
        };

        WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), ctx).Reason.Should().Contain("откатили вручную");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Замена_КлассВыключен_ВоВходящих_КлассАИиКлассHumanНезависимы(bool humanTarget)
    {
        var own = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpSupersede, humanTarget);
        var other = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpSupersede, !humanTarget);
        var author = humanTarget ? WikiPackage.AuthorHuman : WikiPackage.AuthorAi;
        var op = ReplOp(WikiPackage.OpSupersede, humanTarget);

        WikiApplyPolicy.Decide(op, ReplCtx(author: author, disabledClasses: own))
            .Reason.Should().Contain("выключен").And.Contain(own);
        WikiApplyPolicy.Decide(op, ReplCtx(author: author, disabledClasses: other))
            .IsAuto.Should().BeTrue("выключенный класс соседней цели замену не трогает");
    }

    [Fact]
    public void Замена_ОбщиеРискиПослеПравила_UrlИЛичноеВОбщем_ВоВходящих()
    {
        var url = Op(WikiPackage.OpSupersede, WikiGate.OriginUrl) with
        { Class = WikiPackage.ClassKey(WikiGate.OriginUrl, WikiPackage.OpSupersede) };

        WikiApplyPolicy.Decide(url, ReplCtx()).Reason.Should().Contain("origin url");
        WikiApplyPolicy.Decide(ReplOp(WikiPackage.OpSupersede), ReplCtx() with { TargetPageStatus = WikiFrontmatter.StatusDisputed })
            .Reason.Should().Contain("disputed");
    }

    [Fact]
    public void Decide_ВозможныйДубльСтраницы_ВоВходящих_СоСсылкойНаНайденную()
    {
        var v = WikiApplyPolicy.Decide(
            Op(WikiPackage.OpCreate),
            Ctx(duplicate: new WikiDedup.Duplicate("Общее/Сущность.md", "алиас", "заголовок")));

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().Contain("дубль").And.Contain("Общее/Сущность.md").And.NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_СозданиеСтраницыСверхЛимита_ВоВходящих_АНеСамо()
    {
        // Строка таблицы §12г: «create … сверх лимита новых страниц» — спорное. То, что
        // в лимит не вошло вообще, до Decide не доходит (CheckLimits: «ждёт»).
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpCreate), Ctx(counters: new(6, 1, 1)));

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_ВнешнийТекстUrl_ВоВходящих()
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend, WikiGate.OriginUrl), Ctx());

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_ЛичноеВОбщем_ВоВходящих_АВЛичноеСвоемРазделеСамо()
    {
        var common = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend, page: CommonPage), Ctx(personalHome: true));
        var own = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend, page: "Личное/Сущность.md"), Ctx(personalHome: true));

        common.Decision.Should().Be(WikiPackage.DecisionInbox);
        common.Reason.Should().NotBeNullOrWhiteSpace();
        // Дом в своём разделе не спорен: personal → personal применяется само.
        own.Decision.Should().Be(WikiPackage.DecisionAuto);
    }

    [Theory]
    [InlineData(WikiFrontmatter.StatusDisputed)]
    [InlineData(WikiFrontmatter.StatusDrift)]
    public void Decide_СтраницаВСпорномСтатусе_ВоВходящих(string status)
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx(pageStatus: status));

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().Contain(status).And.NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_ПакетПослеРемонтаМодели_ВоВходящих()
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx(repaired: true));

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_КлассВыключен_ВоВходящих_ДругойКлассТойЖеOpСамо()
    {
        var off = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx(disabledClasses: [WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend)]));
        var other = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend, WikiGate.OriginNote), Ctx(disabledClasses: [WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend)]));

        off.Decision.Should().Be(WikiPackage.DecisionInbox);
        off.Reason.Should().Contain("выключен").And.NotBeNullOrWhiteSpace();
        other.Decision.Should().Be(WikiPackage.DecisionAuto);
    }

    // ─── Пункты «само» (4) ────────────────────────────────────────────────────

    [Fact]
    public void Decide_CreateХроникиЧата_Само_БезДедупаИЛимита()
    {
        // Хроника — одна страница на чат: лимит «страниц знаний» и дедуп её не касают.
        var v = WikiApplyPolicy.Decide(
            Op(WikiPackage.OpCreate, page: ChroniclePage),
            Ctx(chronicle: true, counters: new(9, 1, 1), duplicate: new WikiDedup.Duplicate("Личное/Хроники/Другой.md", "заголовок", "заголовок")));

        v.Decision.Should().Be(WikiPackage.DecisionAuto);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_AppendУтверждения_Само()
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx());

        v.Decision.Should().Be(WikiPackage.DecisionAuto);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_Link_Само()
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpLink), Ctx());

        v.Decision.Should().Be(WikiPackage.DecisionAuto);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_CreateСтраницыЗнанийБезДубляИВЛимите_Само()
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpCreate), Ctx(counters: new(5, 1, 1)));

        v.Decision.Should().Be(WikiPackage.DecisionAuto);
        v.Reason.Should().NotBeNullOrWhiteSpace();
    }

    // ─── Порядок проверок и выключатель ───────────────────────────────────────

    [Fact]
    public void Decide_ВыключательВыключен_ВсеДвенадцатьКейсовВоВходящих_СПричинойВыключателя()
    {
        foreach (var (name, op, ctx) in TwelveCases())
        {
            var off = ctx with { AutoApplyEnabled = false };
            var v = WikiApplyPolicy.Decide(op, off);

            v.Decision.Should().Be(WikiPackage.DecisionInbox, "кейс «{0}»", name);
            v.Reason.Should().NotBeNullOrWhiteSpace("кейс «{0}»", name);
            // Причина называет ПЕРВУЮ сработавшую проверку: выключатель — первая.
            v.Reason.Should().Contain("Применять без ревью", "кейс «{0}»", name);
        }
    }

    [Fact]
    public void Decide_ПричинаНазываетПервуюСработавшую_ВыключательТоКлассТоРиск()
    {
        // Класс важнее риска (порядок: выключатель → класс → риск).
        var classFirst = WikiApplyPolicy.Decide(
            Op(WikiPackage.OpSupersede),
            Ctx(disabledClasses: [WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpSupersede)]));
        classFirst.Reason.Should().Contain("выключен");

        // Внутри рисков — порядок таблицы: дубль раньше лимита, url раньше «личного»,
        // «личное» раньше статуса страницы, статус раньше ремонта.
        var dup = WikiApplyPolicy.Decide(
            Op(WikiPackage.OpCreate),
            Ctx(duplicate: new WikiDedup.Duplicate("Общее/Сущность.md", "заголовок", "заголовок"), counters: new(9, 1, 1)));
        dup.Reason.Should().Contain("дубль");

        var url = WikiApplyPolicy.Decide(
            Op(WikiPackage.OpAppend, WikiGate.OriginUrl, CommonPage),
            Ctx(personalHome: true, pageStatus: WikiFrontmatter.StatusDrift, repaired: true));
        url.Reason.Should().Contain("url");

        var personal = WikiApplyPolicy.Decide(
            Op(WikiPackage.OpAppend, page: CommonPage),
            Ctx(personalHome: true, pageStatus: WikiFrontmatter.StatusDisputed, repaired: true));
        personal.Reason.Should().Contain("личное").And.Contain("Общем");

        var status = WikiApplyPolicy.Decide(
            Op(WikiPackage.OpAppend),
            Ctx(pageStatus: WikiFrontmatter.StatusDisputed, repaired: true));
        status.Reason.Should().Contain("disputed");
    }

    [Fact]
    public void Decide_НеизвестноеFailClosed_НикогдаНеСамо()
    {
        var unknownOp = WikiApplyPolicy.Decide(Op("retract"), Ctx());
        var emptyClass = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend) with { Class = "" }, Ctx());
        var foreignOrigin = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend, "vkazke"), Ctx());
        var wrongOpInClass = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend) with { Class = "session×link" }, Ctx());
        var noPage = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend) with { Page = "" }, Ctx());
        var nullOp = WikiApplyPolicy.Decide(null!, Ctx());
        var futureOp = WikiApplyPolicy.Decide(Op("promote"), Ctx());

        foreach (var v in new[] { unknownOp, emptyClass, foreignOrigin, wrongOpInClass, noPage, nullOp, futureOp })
        {
            v.Decision.Should().Be(WikiPackage.DecisionInbox);
            v.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Auto_НеизвестнойОперацииНеДаётСамо_Link_Даёт()
    {
        // Хвост ревью S4d: ветка `_` раньше молча отдавала «auto» любой операции,
        // до неё дошедшей (в т.ч. будущей из OpValues: promote/graduate). Теперь —
        // только link (строка §12г «link»), прочее во «Входящие» с причиной.
        var future = WikiApplyPolicy.Auto(Op("promote"), Ctx());
        future.Decision.Should().Be(WikiPackage.DecisionInbox);
        future.Reason.Should().Contain("вне известных").And.NotBeNullOrWhiteSpace();
        // Та же ветка, что и «операция вне перечня» в Decide: общий код unknown_op.
        future.ReasonCode.Should().Be(WikiApplyPolicy.CodeUnknownOp);

        var link = WikiApplyPolicy.Auto(Op(WikiPackage.OpLink), Ctx());
        link.Decision.Should().Be(WikiPackage.DecisionAuto);
        link.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_СтраницаStaleИАктивнаяНеСпорны_ПоСтатусу()
    {
        // Строка таблицы называет два статуса; stale к ним не относится (снимается
        // откатом, не ревью).
        var stale = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx(pageStatus: WikiFrontmatter.StatusStale));
        var active = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx(pageStatus: WikiFrontmatter.StatusActive));

        stale.Decision.Should().Be(WikiPackage.DecisionAuto);
        active.Decision.Should().Be(WikiPackage.DecisionAuto);
    }

    // ─── Лимиты меры 3: «ждёт», а не «Входящие» ───────────────────────────────

    [Fact]
    public void CheckLimits_ШестаяСтраницаИТридцатьПервоеУтверждение_ЖдутАНеВходящие()
    {
        var sixthPage = WikiApplyPolicy.CheckLimits(new(6, 1, 1), isBacklogPass: false);
        var thirtyFirst = WikiApplyPolicy.CheckLimits(new(1, 31, 1), isBacklogPass: false);
        var secondPackage = WikiApplyPolicy.CheckLimits(new(1, 1, 2), isBacklogPass: false);

        foreach (var v in new[] { sixthPage, thirtyFirst, secondPackage })
        {
            v.Within.Should().BeFalse();
            v.Waits.Should().BeTrue();
            v.WaitingReason.Should().NotBeNullOrWhiteSpace();
            // «ждёт» — не решение политики: ни auto, ни inbox здесь нет.
            v.WaitingReason.Should().Contain("ждёт");
        }
    }

    [Fact]
    public void CheckLimits_ГраницыЛимитовПроходят_АПустыеСчётчикиТоже()
    {
        // Ровно на границе — внутри лимита: 5-я страница, 30-е утверждение, 1-й пакет.
        WikiApplyPolicy.CheckLimits(new(5, 30, 1), isBacklogPass: false).Within.Should().BeTrue();
        // Бэклог — 15 (решение 12в): 15 проходит, 16 ждёт.
        WikiApplyPolicy.CheckLimits(new(15, 1, 1), isBacklogPass: true).Within.Should().BeTrue();
        WikiApplyPolicy.CheckLimits(new(16, 1, 1), isBacklogPass: true).Within.Should().BeFalse();
        // Пустой счётчик (ничего не сделано, текущий элемент первый) — внутри.
        var none = WikiApplyPolicy.CheckLimits(default, isBacklogPass: false);
        none.Within.Should().BeTrue();
        none.WaitingReason.Should().BeNull();
    }

    [Fact]
    public void CheckLimits_ПервыйПревысившийЛимитНазван_ОдинРаз()
    {
        var v = WikiApplyPolicy.CheckLimits(new(6, 31, 2), isBacklogPass: false);

        v.Within.Should().BeFalse();
        v.WaitingReason.Should().Contain("страниц").And.NotContain("утверждений");
    }

    // ── N7d: анти-петля — страницу, откатанную человеком, автомат 7 дней не правит ──

    private static readonly DateTimeOffset RevertedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static WikiApplyPolicy.ApplyContext CtxReverted(string page) => new()
    {
        RecentlyRevertedPages = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { [page] = RevertedAt },
    };

    // ── B4a: ключ класса с хвостом «@human» разбирается в одной точке ──

    [Theory]
    [InlineData(WikiPackage.OpSupersede)]
    [InlineData(WikiPackage.OpConflict)]
    public void КлассСХвостомHuman_ВыключенныйКласс_НазванВПричине_НеКривойКласс(string op)
    {
        var classKey = WikiPackage.ClassKey(WikiGate.OriginSession, op, humanTarget: true);
        var operation = Op(op) with { Class = classKey };

        var v = WikiApplyPolicy.Decide(operation, new() { DisabledClasses = new HashSet<string> { classKey } });

        v.Reason.Should().Contain("выключен", "класс опознан и выключен, а не отвергнут как «не пара источник × op»")
            .And.Contain(classKey);
    }

    [Fact]
    public void КлассСХвостомHuman_НеУAppend_КривойКласс()
    {
        var operation = Op(WikiPackage.OpAppend) with { Class = "session×append@human" };

        var v = WikiApplyPolicy.Decide(operation, new());

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().Contain("не опознан");
    }

    [Fact]
    public void ClassKey_СХвостом_ПарсерВозвращаетЧастиИПризнак()
    {
        WikiPackage.ParseClassKey("note×conflict@human").Should().Be((WikiGate.OriginNote, WikiPackage.OpConflict, true));
        WikiPackage.ParseClassKey("note×conflict").Should().Be((WikiGate.OriginNote, WikiPackage.OpConflict, false));
        WikiPackage.ParseClassKey(null).Should().BeNull();
    }

    [Fact]
    public void N7d_ОткатанныйЧеловекомСтраницы_AppendВоВходящие_ПричинаНазываетОткатИДату()
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), CtxReverted(ProjectPage));

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().Contain("откатили вручную").And.Contain("2026-10-30");
    }

    [Fact]
    public void N7d_ДругаяСтраница_НеЗатронута_Само()
    {
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend, page: "Проекты/p1/Другая.md"), CtxReverted(ProjectPage));

        v.IsAuto.Should().BeTrue();
    }

    [Fact]
    public void N7d_CreateПоТомуЖеПути_ПослеОткатаСУдалением_ВоВходящие()
    {
        // Revert пакета с create удаляет страницу, но путь остаётся в pages журнала:
        // тот же источник создал бы её заново — правило обязано сработать и на create.
        var v = WikiApplyPolicy.Decide(Op(WikiPackage.OpCreate), CtxReverted(ProjectPage));

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().Contain("откатили вручную");
    }

    [Fact]
    public void N7d_БезОткатов_ПоведениеПрежнее()
    {
        WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx()).IsAuto.Should().BeTrue();
    }

    // ─── J-J0: машинный код причины и допуск к судье (решение 12.11, И4′) ────────

    // По кейсу на каждую из 15 веток «Входящих» Decide — с ожидаемым кодом. Ветка `_`
    // Auto (тот же unknown_op) проверена в Auto_НеизвестнойОперацииНеДаётСамо_Link_Даёт.
    public static IEnumerable<object[]> InboxCodeCases() =>
    [
        new object[] { WikiApplyPolicy.CodeAutoApplyOff, Op(WikiPackage.OpAppend), Ctx(autoApply: false) },
        new object[] { WikiApplyPolicy.CodeUnknownOp, Op("retract"), Ctx() },
        new object[] { WikiApplyPolicy.CodeNoClass, Op(WikiPackage.OpAppend) with { Class = "" }, Ctx() },
        new object[] { WikiApplyPolicy.CodeBadClass, Op(WikiPackage.OpAppend, "vkazke"), Ctx() },
        new object[] { WikiApplyPolicy.CodeNoPage, Op(WikiPackage.OpAppend) with { Page = "" }, Ctx() },
        new object[] { WikiApplyPolicy.CodeClassDisabled, Op(WikiPackage.OpAppend), Ctx(disabledClasses: [WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend)]) },
        new object[] { WikiApplyPolicy.CodeRevertQuarantine, Op(WikiPackage.OpAppend), CtxReverted(ProjectPage) },
        new object[] { WikiApplyPolicy.CodeNoHistorySection, ReplOp(WikiPackage.OpSupersede, true), ReplCtx(author: WikiPackage.AuthorHuman) with { TargetHasNoHistory = true } },
        new object[] { WikiApplyPolicy.CodeOverridesHuman, ReplOp(WikiPackage.OpSupersede), ReplCtx() with { NewSourceTime = null } },
        new object[] { WikiApplyPolicy.CodePageDuplicate, Op(WikiPackage.OpCreate), Ctx(duplicate: new WikiDedup.Duplicate("Общее/Сущность.md", "алиас", "заголовок")) },
        new object[] { WikiApplyPolicy.CodeSectionNewPagesLimit, Op(WikiPackage.OpCreate), Ctx(counters: new(6, 1, 1)) },
        new object[] { WikiApplyPolicy.CodeOriginUrl, Op(WikiPackage.OpAppend, WikiGate.OriginUrl), Ctx() },
        new object[] { WikiApplyPolicy.CodePersonalToCommon, Op(WikiPackage.OpAppend, page: CommonPage), Ctx(personalHome: true) },
        new object[] { WikiApplyPolicy.CodeTargetDisputed, Op(WikiPackage.OpAppend), Ctx(pageStatus: WikiFrontmatter.StatusDisputed) },
        new object[] { WikiApplyPolicy.CodeAfterModelRepair, Op(WikiPackage.OpAppend), Ctx(repaired: true) },
    ];

    [Theory]
    [MemberData(nameof(InboxCodeCases))]
    public void Decide_КаждаяВеткаВходящих_СвойМашинныйКод(string code, WikiPackage.Operation op, WikiApplyPolicy.ApplyContext ctx)
    {
        var v = WikiApplyPolicy.Decide(op, ctx);

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.ReasonCode.Should().Be(code);
    }

    [Fact]
    public void ReasonCodes_КаждыйКодПолитикиПолученСвоейВеткой_ПереченьПолный()
    {
        var covered = InboxCodeCases().Select(c => (string)c[0]).ToList();

        // number_unconfirmed — код валидатора (числа), не политики: Decide его не рождает.
        covered.Distinct().Should().BeEquivalentTo(
            WikiApplyPolicy.ReasonCodes.Except([WikiApplyPolicy.CodeNumberUnconfirmed]));
    }

    // Ожидаемые множества — в тесте, не выведенные из кода: убрать origin_url из CodeRejected
    // или любой код из JudgeAdmitted — этот тест красный.
    private static readonly string[] JudgeAdmittedExpected =
        [WikiApplyPolicy.CodeClassDisabled, WikiApplyPolicy.CodeRevertQuarantine, WikiApplyPolicy.CodeNoHistorySection,
         WikiApplyPolicy.CodeOverridesHuman, WikiApplyPolicy.CodePageDuplicate, WikiApplyPolicy.CodeSectionNewPagesLimit,
         WikiApplyPolicy.CodePersonalToCommon, WikiApplyPolicy.CodeTargetDisputed, WikiApplyPolicy.CodeAfterModelRepair,
         WikiApplyPolicy.CodeNumberUnconfirmed];

    private static readonly string[] CodeRejectedExpected =
        [WikiApplyPolicy.CodeUnknownOp, WikiApplyPolicy.CodeNoClass, WikiApplyPolicy.CodeBadClass,
         WikiApplyPolicy.CodeNoPage, WikiApplyPolicy.CodeOriginUrl];

    [Fact]
    public void ДопускКСудье_РовноСвоиКоды_БезКодаИВыключателяНикогда()
    {
        foreach (var code in WikiApplyPolicy.ReasonCodes.Append((string?)null))
        {
            var inAdmitted = code is not null && JudgeAdmittedExpected.Contains(code);
            var inRejected = code is not null && CodeRejectedExpected.Contains(code);

            WikiApplyPolicy.JudgeAdmitted(code).Should().Be(inAdmitted, $"код «{code}»");
            WikiApplyPolicy.CodeRejected(code).Should().Be(inRejected, $"код «{code}»");
            (WikiApplyPolicy.JudgeAdmitted(code) && WikiApplyPolicy.CodeRejected(code)).Should().BeFalse(
                $"код «{code}» не может быть и допущен, и отвергнут");
        }

        // autoapply_off и отсутствие кода (старые пакеты) — ни туда, ни туда: ручной режим
        // судью не зовёт вовсе, операция без кода к судье не допущена (И4′).
        WikiApplyPolicy.JudgeAdmitted(null).Should().BeFalse();
        WikiApplyPolicy.CodeRejected(null).Should().BeFalse();
        WikiApplyPolicy.JudgeAdmitted(WikiApplyPolicy.CodeAutoApplyOff).Should().BeFalse();
        WikiApplyPolicy.CodeRejected(WikiApplyPolicy.CodeAutoApplyOff).Should().BeFalse();
    }

    [Fact]
    public void ВердикAuto_БезКода_InboxКаждойВетки_СКодом()
    {
        // У «auto» кода нет — он не несёт риска; код есть ровно у «inbox».
        var auto = WikiApplyPolicy.Decide(Op(WikiPackage.OpAppend), Ctx());
        auto.IsAuto.Should().BeTrue();
        auto.ReasonCode.Should().BeNull();

        foreach (var (_, op, ctx) in InboxCodeCases().Select(c => ((string)c[0], (WikiPackage.Operation)c[1], (WikiApplyPolicy.ApplyContext)c[2])))
            WikiApplyPolicy.Decide(op, ctx).ReasonCode.Should().NotBeNull();
    }

    [Fact]
    public void Операция_БезКодаВJSON_ЧитаетсяИПроверяется_СЧужимКодом_ОшибкаФормы()
    {
        // Старый пакет на диске: поля reasonCode нет вовсе — оно опционально (обратная
        // совместимость): читается и проходит форму без кода.
        var json = """
            {"v":1,"id":"11111111-1111-1111-1111-111111111111","author":"ai","section":"common",
             "sources":[{"origin":"session","ref":"session:s1#m1-9"}],
             "operations":[{"op":"append","page":"Общее/Сущность.md","text":"утверждение разбора",
               "class":"session×append","decision":"inbox","reason":"старый пакет без кода",
               "baseHashes":{}}],
             "status":"proposed","createdAt":"2026-11-01T00:00:00+00:00","updatedAt":"2026-11-01T00:00:00+00:00"}
            """;
        var pkg = JsonSerializer.Deserialize<WikiPackage>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        })!;

        pkg.Operations[0].ReasonCode.Should().BeNull();
        WikiPackage.ShapeErrors(pkg).Should().BeEmpty();

        var bad = pkg with { Operations = [pkg.Operations[0] with { ReasonCode = "xyz" }] };
        WikiPackage.ShapeErrors(bad).Should().Contain(e => e.Contains("xyz"));
    }

    // ─── J-J0: числа (валидатор) — код сохраняется за вердиктом политики ─────────

    // Основа — та же, что в WikiPlanValidatorSynthTests: карточка, страница и текст источника.
    private const string SectionKey = "projects/p1";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Проекты/p1/Карточка-чата.md";
    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Сборка занимает 12 минут.";
    private const string QMinutes = "Сборка занимает 12 минут";

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

    private static WikiPlanValidator.Result RunSynth(WikiApplyPolicy.ApplyContext ctx, params WikiPlanValidator.SynthOp[] ops) =>
        WikiPlanValidator.ValidateSynth(
            new([.. ops]),
            new(WikiGate.OriginSession, CardTitle, SourceText),
            SectionKey,
            [WikiSections.Common],
            [new(CardPath, CardFile), new(ProjectPage, PageFile)],
            ctx);

    [Fact]
    public void Числа_ПолитикаAuto_КодЧисел_ВердикПолитикиСохраняетСвойКод()
    {
        // auto + неподтверждённое число → код числа (автопричина снимается, и её код тоже).
        var auto = RunSynth(new(),
            new WikiPlanValidator.SynthOp(WikiPackage.OpAppend, null, "Сущность", "Сборка занимает 15 минут", [QMinutes], null, null));
        var autoOp = auto.Accepted.Should().ContainSingle().Subject;
        autoOp.Decision.Should().Be(WikiPackage.DecisionInbox);
        autoOp.ReasonCode.Should().Be(WikiApplyPolicy.CodeNumberUnconfirmed);

        // политики уже «Входящие» (дубль страницы) + число → код политики: главный риск —
        // риск политики, по нему судья решает допуск.
        var dup = RunSynth(new(),
            new WikiPlanValidator.SynthOp(WikiPackage.OpCreate, "decision", "Сущность", "Сборка занимает 15 минут", [QMinutes], null, null));
        var dupOp = dup.Accepted.Should().ContainSingle().Subject;
        dupOp.Decision.Should().Be(WikiPackage.DecisionInbox);
        dupOp.Reason.Should().Contain("дубль").And.Contain("число не подтверждено");
        dupOp.ReasonCode.Should().Be(WikiApplyPolicy.CodePageDuplicate);
    }
}
