using System.Text;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S2-3 (ADR-039, решение 2): операция пакета несёт утверждение (claim) отдельно от
// доказательства (quotes). Поля аддитивные: файл старого формата (без них) читается и
// проходит форму как раньше; новые проверки включаются только когда цитаты есть.
// Границы цитат — не свои константы, а контракт цитаты страницы (WikiPage.MinQuoteLength /
// MaxQuoteLength); мера — общая с гейтом (QuoteMatcher.MeasureQuoteLength): нижняя граница
// по нормализованной строке, верхняя — по сырой (минор R-S2-3).
public class WikiPackageClaimTests : IDisposable
{
    // Форма хэша та же, что в манифесте: 64 hex нижнего регистра.
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Page = "Общее/X.md";
    private const string Claim = "утверждение пакета — отдельно от цитаты";

    private readonly string _root;

    public WikiPackageClaimTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wiki_claim_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ─── 1. Старый пакет: новых полей нет, форма прежняя ────────────────────

    [Fact]
    public void ОперацияБезЦитат_ФормаПрежняя_ИНовыеПроверкиМолчат()
    {
        // Доказательство в Text (WikiIngestService.VerifiedQuote) — старый пакет:
        // claim не требуется, пустой claim и отсутствие claim — не ошибка.
        var legacy = OpWith(WikiPackage.OpAppend);
        WikiPackage.ShapeErrors(Pkg(legacy)).Should().BeEmpty();
        WikiPackage.ShapeErrors(Pkg(legacy with { Claim = "" })).Should().BeEmpty();
    }

    [Fact]
    public void ФайлСтарогоФормата_БезНовыхКлючей_ЧитаетсяВалидным()
    {
        // Так выглядит файл, записанный до S2-3: ключей claim/quotes/kind/asOf в нём нет.
        var id = WikiPackageStore.NewId();
        // Пишем файл старого формата руками: стора его уже не запишет — она пишет новые ключи.
        Directory.CreateDirectory(Path.GetDirectoryName(PackageFile(id))!);
        File.WriteAllText(PackageFile(id),
            "{\"v\":1,\"id\":\"" + id + "\",\"section\":\"common\",\"status\":\"checking\"," +
            "\"sources\":[{\"origin\":\"session\",\"ref\":\"chat-1\"}],\"operations\":[{\"op\":\"append\"," +
            "\"page\":\"Общее/X.md\",\"text\":\"утверждение\",\"class\":\"session×append\",\"decision\":\"auto\"," +
            "\"baseHashes\":{\"" + Page + "\":\"" + Hash + "\"}}]}");

        var read = WikiPackageStore.TryRead(_root, id);
        read.State.Should().Be(WikiPackageFileState.Valid, "старый файл — пакет, а не мусор");
        read.Package!.Operations[0].Claim.Should().BeNull();
        read.Package.Operations[0].Quotes.Should().BeNull();
    }

    // ─── 2. Форма цитат: границы WikiPage.MinQuoteLength..MaxQuoteLength ───

    [Theory]
    // 8 и 300 включительно; 7 и 301 — ровно одна ошибка формы.
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void Цитата_ГраницаДлины8и300Включительно(int len, bool допустима)
    {
        var errors = WikiPackage.ShapeErrors(Pkg(OpWithQuotes([new string('с', len)], Claim)));
        if (допустима)
            errors.Should().BeEmpty($"цитата {len} символов — внутри контракта");
        else
            errors.Should().ContainSingle(e => e.Contains(len + " символов"));
    }

    [Fact]
    public void Цитата_ПробелыКороткуюНеСпасают()
    {
        // 7 значимых символов в обрамлении пробелов — всё ещё короткая: сообщение, как у
        // гейта, называет и сырую длину, и значимую (13 → 7). 8 значимых — проходит.
        WikiPackage.ShapeErrors(Pkg(OpWithQuotes(["   " + new string('с', 7) + "   "], Claim)))
            .Should().ContainSingle(e => e.Contains("13 символов (7 после нормализации)"));
        WikiPackage.ShapeErrors(Pkg(OpWithQuotes(["  " + new string('с', 8) + "  "], Claim)))
            .Should().BeEmpty();
    }

    [Theory]
    // Ровно 1–3 цитат; 0 и 4 — ровно одна ошибка (порядок цитат несёт номера сносок, S2-7).
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ЦитатРовно1_3_СClaim_ФормаПроходит(int count)
    {
        WikiPackage.ShapeErrors(Pkg(OpWithQuotes(QuotesOf(count), Claim))).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ЦитатНе1_3_РовноОднаОшибкаФормы(int count)
    {
        var errors = WikiPackage.ShapeErrors(Pkg(OpWithQuotes(QuotesOf(count), Claim)));
        errors.Should().ContainSingle(e => e.Contains("цитат операции"));
    }

    [Fact]
    public void ЦитатаNullВСписке_ОшибкаФормы_АНеИсключение()
    {
        // Сообщение называет операцию, как соседние (минор R-S2-3): без op непонятно,
        // какой операции цитату прислал модель null'ом.
        var errors = WikiPackage.ShapeErrors(Pkg(OpWithQuotes([null!, "достаточно длинная цитата"], Claim)));
        errors.Should().ContainSingle(e => e.Contains("null в списке") && e.Contains(WikiPackage.OpAppend));
    }

    [Theory]
    // Одна мера с гейтом (минор R-S2-3): цитата в обрамлении разметки мерится по значимой
    // длине, не по сырой. 7 значимых (raw 13) — отбиваются обоими: старая мера Trim()
    // пропускала такую по форме, и пакет отбивался гейтом. 8 значимых — ровно контракт,
    // проходят обоими.
    [InlineData("***абвгдеж***", false)]
    [InlineData("***абвгдежз***", true)]
    public void ЦитатаВОбрамленииРазметки_ФормаИГейтОдинВердикт(string quote, bool допустима)
    {
        var formErrors = WikiPackage.ShapeErrors(Pkg(OpWithQuotes([quote], Claim)));
        // Та же валидная страница и та же карточка, что в WikiGateTests: общий каркас.
        var page = WikiGateFixtures.Page(
            swodka: "- Утверждение с цитатой.[^s1]",
            footnotes: "[^s1]: [[Карточка]] — «" + quote + "»");
        var gate = WikiGate.Check(
            [new WikiGate.Draft(WikiGateFixtures.P1Dir + "Тет.md", page), WikiGateFixtures.CardDraft],
            WikiGateFixtures.P1, []);

        if (допустима)
        {
            formErrors.Should().BeEmpty($"цитата из 8 значимых символов в обрамлении *** — внутри контракта");
            gate.Should().BeEmpty("тот же вердикт гейта");
        }
        else
        {
            formErrors.Should().ContainSingle(e => e.Contains("допустимо 8–300"));
            gate.Should().Contain(e => e.Contains("контракт 8–300"));
        }
    }

    // ─── 3. Форма claim: обязателен при цитатах, ≤ 600 ─────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClaimПустойПриЦитатах_РовноОднаОшибкаФормы(string? claim)
    {
        var errors = WikiPackage.ShapeErrors(Pkg(OpWithQuotes(["достаточно длинная цитата"], claim)));
        errors.Should().ContainSingle(e => e.Contains("без утверждения"));
    }

    [Theory]
    // 600 включительно, 601 — ровно одна ошибка.
    [InlineData(600, true)]
    [InlineData(601, false)]
    public void Claim_ГраницаДлины600Включительно(int len, bool допустим)
    {
        var errors = WikiPackage.ShapeErrors(Pkg(OpWithQuotes(["достаточно длинная цитата"], new string('к', len))));
        if (допустим)
            errors.Should().BeEmpty($"claim {len} символов — внутри предела");
        else
            errors.Should().ContainSingle(e => e.Contains("допустимо не более 600"));
    }

    // ─── 4. Круг через файл: поля и порядок цитат сохраняются ──────────────

    [Fact]
    public void КругЧерезФайл_ПоляСохраняются_ВJSONНовыеКлючи_ПорядокЦитатТотЖе()
    {
        var quotes = QuotesOf(3);
        var pkg = Pkg(OpWithQuotes(quotes, Claim) with { Kind = "state", AsOf = "2026-11-10" });
        var res = WikiPackageStore.Create(_root, pkg);
        res.Ok.Should().BeTrue(res.Reason);

        var json = File.ReadAllText(PackageFile(pkg.Id), new UTF8Encoding(false));
        json.Should().Contain("\"claim\"").And.Contain("\"quotes\"").And.Contain("\"kind\"").And.Contain("\"asOf\"");

        var read = WikiPackageStore.TryRead(_root, pkg.Id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        var op = read.Package!.Operations[0];
        op.Claim.Should().Be(Claim);
        // Порядок — по нему S2-7 берёт номера сносок: переставленный круг был бы другим доказательством.
        op.Quotes!.Should().Equal(quotes);
        op.Kind.Should().Be("state");
        op.AsOf.Should().Be("2026-11-10");
    }

    // ─── построители ───────────────────────────────────────────────────────

    private static IReadOnlyList<string> QuotesOf(int count) =>
        Enumerable.Range(1, count).Select(i => $"цитата источника номер {i}").ToList();

    private static WikiPackage Pkg(params WikiPackage.Operation[] ops) => new()
    {
        Id = WikiPackageStore.NewId(),
        Section = "common",
        Sources = [new WikiPackage.SourceRef("session", "chat-1")],
        Operations = ops,
        Status = WikiPackage.StatusChecking,
    };

    private static WikiPackage.Operation OpWith(string op) => new()
    {
        Op = op,
        Page = Page,
        Text = "утверждение из чата",
        Class = WikiPackage.ClassKey("session", op),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string> { [Page] = Hash },
    };

    // Text остаётся: проверка содержания в этом шаге не меняется (S2-8b перенесёт его в claim).
    private static WikiPackage.Operation OpWithQuotes(IReadOnlyList<string> quotes, string? claim) =>
        OpWith(WikiPackage.OpAppend) with { Quotes = quotes, Claim = claim };

    private string PackageFile(string id) => Path.Combine(_root, "keeper", "packages", id + ".json");
}
