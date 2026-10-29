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
    public void Decide_ЗамещениеИПротиворечие_ВсегдаВоВходящих(string op)
    {
        var v = WikiApplyPolicy.Decide(Op(op), Ctx());

        v.Decision.Should().Be(WikiPackage.DecisionInbox);
        v.Reason.Should().NotBeNullOrWhiteSpace();
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
}
