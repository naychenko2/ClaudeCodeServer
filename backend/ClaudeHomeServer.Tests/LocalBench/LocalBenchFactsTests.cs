using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Проверки машинной части оси B: сличитель фактов и банки разметки.
///
/// Категории LocalBench тут НЕТ намеренно (как и у <see cref="LocalBenchHarnessTests"/>):
/// живой модели эти проверки не требуют и обязаны идти в CI. Молча сломавшийся сличитель
/// выдал бы recall 100% на любом мусоре, а незаземлённая разметка превратила бы замер в
/// измерение фантазии разметчика — оба провала невидимы глазами.
/// </summary>
public class LocalBenchFactsTests
{
    /// <summary>Семь мест оси B — те, где модель пересказывает чужой текст.</summary>
    public static readonly string[] Places =
    [
        LocalActionCatalog.SessionSummary,
        LocalActionCatalog.ChatDigest,
        LocalActionCatalog.DocSummary,
        LocalActionCatalog.DocExtract,
        LocalActionCatalog.DiscussionDigest,
        LocalActionCatalog.DossierSummary,
        LocalActionCatalog.DailyBriefing,
    ];

    public static TheoryData<string> PlaceNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in Places) data.Add(p);
        return data;
    }

    // ─── Сличитель ───────────────────────────────────────────────────────────

    [Theory]
    // Якорь ищется подстрокой по нормализованному тексту: регистр, ё и пунктуация значения
    // не имеют, окончание слова тоже (якорь «бэкап» ловит «бэкапы»).
    [InlineData("Решили хранить бэкапы вне data.", "бэкап")]
    [InlineData("РЕШИЛИ ХРАНИТЬ БЭКАПЫ", "бэкап")]
    [InlineData("Правка в CLAUDE.md обязательна", "claude md")]
    [InlineData("Смотри\nфайл  ADR-014 —  там всё", "adr 014")]
    public void Якорь_находится_невзирая_на_регистр_и_пунктуацию(string summary, string anchor) =>
        LocalBenchFactMatcher.Contains(LocalBenchFactMatcher.Normalize(summary), [anchor])
            .Should().BeTrue();

    [Fact]
    public void Факт_без_якоря_в_выжимке_считается_пропущенным()
    {
        var fact = new BenchFact("f1", BenchFact.Decision, "Решили вынести Notes", ["notes"]);

        var outcome = LocalBenchFactMatcher.Judge(fact, "Обсудили задачи и разошлись.");

        outcome.Verdict.Should().Be(FactVerdict.Missed);
        outcome.Negation.Should().BeNull("отрицания у факта-решения нет вовсе");
    }

    [Fact]
    public void Отказ_с_отрицанием_рядом_считается_сохранённым()
    {
        var fact = new BenchFact("f1", BenchFact.Refusal, "Backup отдельной вертикалью не выносят",
            ["backup"]);

        var outcome = LocalBenchFactMatcher.Judge(fact,
            "Разобрали кандидатов. Backup выносить отдельной вертикалью не стали: это срез поперёк всех.");

        outcome.Verdict.Should().Be(FactVerdict.Recalled);
        outcome.Negation.Should().Be(NegationVerdict.Preserved);
    }

    [Fact]
    public void Отказ_превращённый_в_решение_виден_как_подозрение()
    {
        // Ровно та ловушка, ради которой ось B заведена: во входе «решили НЕ выносить»,
        // в выжимке — «решили вынести». Машина не берётся звать это переворотом (отрицание
        // бывает оборотом вне списка), но обязана поднять флаг.
        var fact = new BenchFact("f1", BenchFact.Refusal, "Backup отдельной вертикалью не выносят",
            ["backup"]);

        var outcome = LocalBenchFactMatcher.Judge(fact,
            "Решили вынести Backup отдельной вертикалью в следующем этапе.");

        outcome.Verdict.Should().Be(FactVerdict.Recalled);
        outcome.Negation.Should().Be(NegationVerdict.Suspect);
    }

    [Fact]
    public void Отказ_не_упомянутый_вовсе_не_путается_с_переворотом()
    {
        var fact = new BenchFact("f1", BenchFact.Refusal, "Backup отдельной вертикалью не выносят",
            ["backup"]);

        var outcome = LocalBenchFactMatcher.Judge(fact, "Вынесли Notes и Knowledge, дальше Git.");

        outcome.Verdict.Should().Be(FactVerdict.Missed);
        outcome.Negation.Should().Be(NegationVerdict.Missed);
    }

    [Fact]
    public void Отрицание_из_соседнего_предложения_за_окном_не_засчитывается()
    {
        // Окно в 140 символов держит отрицание при СВОЁМ якоре: без окна любое «не» где-то
        // в сводке объявляло бы уцелевшим отказ, которого в тексте нет.
        var fact = new BenchFact("f1", BenchFact.Refusal, "Backup не выносят", ["backup"]);
        var far = "Решили вынести Backup отдельной вертикалью. " + new string('а', 200)
                  + " Тесты на Linux запускать не стали.";

        LocalBenchFactMatcher.Judge(fact, far).Negation.Should().Be(NegationVerdict.Suspect);
    }

    [Fact]
    public void Маркер_не_не_срабатывает_внутри_слова()
    {
        // « не » с пробелами по краям ищется целым словом. Сработай он основой — «нет»,
        // «неделя» и «некоторые» объявляли бы уцелевшим любой отказ.
        var fact = new BenchFact("f1", BenchFact.Refusal, "Модули не подключают", ["модул"]);

        LocalBenchFactMatcher.Judge(fact, "Модули подключили, неделя ушла на это.")
            .Negation.Should().Be(NegationVerdict.Suspect);
    }

    [Fact]
    public void Маркер_основой_ловит_окончания()
    {
        var fact = new BenchFact("f1", BenchFact.Refusal, "Вариант с очередью отвергли", ["очеред"]);

        LocalBenchFactMatcher.Judge(fact, "Вариант с очередью отвергли: она рвёт порядок ходов.")
            .Negation.Should().Be(NegationVerdict.Preserved);
    }

    [Fact]
    public void Свой_маркер_кейса_добавляется_к_общему_списку()
    {
        var fact = new BenchFact("f1", BenchFact.Refusal, "От второго семафора обошлись",
            ["семафор"], NegationMarkers: ["обошлись без"]);

        LocalBenchFactMatcher.Judge(fact, "Обошлись без второго семафора — потолок остался один.")
            .Negation.Should().Be(NegationVerdict.Preserved);
    }

    // ─── Отчёт ───────────────────────────────────────────────────────────────

    [Fact]
    public void Отчёт_считает_recall_и_отказы_раздельно()
    {
        var report = new LocalBenchFactsReport(LocalActionCatalog.DocSummary);
        report.Add(new LocalBenchFactsRow("к1", FactsTotal: 5, Recalled: 4, MissedFactIds: ["f5"],
            RefusalsTotal: 1, RefusalsPreserved: 1, RefusalsSuspect: 0, RefusalsMissed: 0,
            Truncated: false, Answered: true, DurationMs: 400, CompletionTokens: 120, SummaryChars: 600));
        report.Add(new LocalBenchFactsRow("к2", FactsTotal: 4, Recalled: 1, MissedFactIds: ["f1", "f2", "f3"],
            RefusalsTotal: 1, RefusalsPreserved: 0, RefusalsSuspect: 1, RefusalsMissed: 0,
            Truncated: true, Answered: true, DurationMs: 1200, CompletionTokens: 256, SummaryChars: 900));

        report.FactsTotal.Should().Be(9);
        report.FactsRecalled.Should().Be(5);
        report.RefusalsTotal.Should().Be(2);
        report.RefusalsPreserved.Should().Be(1);
        report.RefusalsSuspect.Should().Be(1);
        report.TruncatedCount.Should().Be(1);
        report.MedianMs.Should().Be(800);
    }

    [Fact]
    public void Банк_без_отказов_виден_в_сводке_как_непроверенная_ловушка()
    {
        // Ловушка на отрицания обязательна в КАЖДОМ банке. Банк без неё не должен
        // выглядеть как успешный прогон — сводка обязана сказать это словами.
        var report = new LocalBenchFactsReport(LocalActionCatalog.DocSummary);
        report.Add(new LocalBenchFactsRow("к1", 3, 3, [], 0, 0, 0, 0, false, true, 300, 80, 400));

        report.RefusalSummary().Should().Contain("НЕ проверена");
    }

    // ─── Банки разметки ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(PlaceNames))]
    public void Банк_места_несёт_десять_кейсов_с_разметкой(string place)
    {
        var bank = LocalBenchCases.Load(place);

        bank.Place.Should().Be(place);
        bank.Cases.Should().HaveCount(10, "спецификация оси B требует 10 кейсов на место");
        foreach (var c in bank.Cases)
        {
            c.Input.Should().NotBeNullOrWhiteSpace();
            var sheet = LocalBenchFactSheet.Of(c);
            sheet.Facts.Should().NotBeEmpty();
            sheet.Facts.Select(f => f.Id).Should().OnlyHaveUniqueItems(
                $"в кейсе «{c.Id}» повторяется id факта");
            sheet.Facts.Should().OnlyContain(f => BenchFact.Kinds.Contains(f.Kind),
                $"в кейсе «{c.Id}» вид факта не из списка {string.Join("/", BenchFact.Kinds)}");
            sheet.Facts.Should().OnlyContain(f => f.Anchors.Count > 0,
                $"в кейсе «{c.Id}» есть факт без якорей — сверять его нечем");
        }
    }

    [Theory]
    [MemberData(nameof(PlaceNames))]
    public void Разметка_банка_заземлена_входами(string place)
    {
        // Сторож против главного способа испортить ось B: разметить факт, которого во
        // входе нет. Замер по такому банку мерил бы фантазию разметчика, а выглядел бы
        // как провал модели по recall.
        var bank = LocalBenchCases.Load(place);

        foreach (var c in bank.Cases)
        {
            var ungrounded = LocalBenchFactSheet.Of(c).Ungrounded(c.Input);
            ungrounded.Should().BeEmpty(
                $"в кейсе «{c.Id}» нет во входе якорей фактов: "
                + string.Join(", ", ungrounded.Select(f => $"{f.Id} ({f.What})")));
        }
    }

    [Theory]
    [MemberData(nameof(PlaceNames))]
    public void В_банке_есть_ловушка_на_отрицания(string place)
    {
        // «В каждый банк положить кейсы, где во входе есть решили НЕ делать X» —
        // требование спецификации, а не пожелание: без него главная ловушка оси не
        // проверяется вовсе, и это молча.
        var bank = LocalBenchCases.Load(place);

        var withRefusal = bank.Cases.Count(c => LocalBenchFactSheet.Of(c).Refusals.Count > 0);
        withRefusal.Should().BeGreaterThanOrEqualTo(3,
            "ловушка на одном кейсе из десяти ничего не измеряет");
    }
}
