using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// W4-ANB (разрез §4): выжимка и кэш ручки feature-summary. Настоящий вызов модели подменяет
// заглушка ICheapTextRunner со счётчиком вызовов. К2: повторный вызов с тем же отпечатком
// не пересчитывает; force=true — пересчитывает; параллельные запросы по одному ключу —
// single-flight (один вызов); потолок LRU — вытеснение старых; отказ модели не кэшируется.
public class FeatureSummaryServiceTests
{
    private sealed class StubRunner : ICheapTextRunner
    {
        public int Calls;
        public string Text = "резюме фичи";
        public Exception? Failure;

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "local=stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(Text);

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult<string?>(Text);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new OneShotResult(Text, null, 100));
        }
    }

    private static FeatureSummaryService MakeService(StubRunner runner) =>
        new FeatureSummaryService(runner, new ConfigurationBuilder().AddInMemoryCollection().Build());

    private static FeatureRun OpenRun(string featureId) =>
        new FeatureRun { FeatureId = featureId, Epic = new TaskItem { Id = featureId, Role = TaskRoles.Feature } };

    // К2: повторный вызов с тем же отпечатком — без второго вызова модели.
    [Fact]
    public async Task ПовторныйВызов_ТотЖеОтпечаток_БезВторогоВызова()
    {
        var stub = new StubRunner();
        var service = MakeService(stub);
        var run = OpenRun("f1");

        var first = await service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);
        var second = await service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);

        first.Cached.Should().BeFalse("первый вызов считает");
        second.Cached.Should().BeTrue("тот же отпечаток — из кэша");
        second.Text.Should().Be(first.Text);
        stub.Calls.Should().Be(1, "повторный вызов не зовёт модель");
    }

    // ?force=true — пересчёт принудительно, мимо кэша.
    [Fact]
    public async Task Force_True_Пересчитывает()
    {
        var stub = new StubRunner();
        var service = MakeService(stub);
        var run = OpenRun("f1");

        await service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);
        var forced = await service.SummarizeAsync("owner", "f1", run, true, CancellationToken.None);

        stub.Calls.Should().Be(2, "force=true игнорирует кэш");
        forced.Cached.Should().BeFalse();
    }

    // Отпечаток считается от входа выжимки: изменение состояния фичи меняет и отпечаток —
    // кэш не отдаёт устаревшее резюме.
    [Fact]
    public async Task ИзменениеФичи_ДругойОтпечаток_Пересчёт()
    {
        var stub = new StubRunner();
        var service = MakeService(stub);

        await service.SummarizeAsync("owner", "f1", OpenRun("f1"), false, CancellationToken.None);

        var runWithStep = new FeatureRun
        {
            FeatureId = "f1",
            Epic = new TaskItem { Id = "f1", Role = TaskRoles.Feature },
            Steps = [new TaskItem { Id = "s1", FeatureId = "f1", Role = TaskRoles.Step, Status = TaskItemStatus.Done }],
        };
        var after = await service.SummarizeAsync("owner", "f1", runWithStep, false, CancellationToken.None);

        stub.Calls.Should().Be(2, "состояние фичи изменилось — отпечаток другой");
        after.Cached.Should().BeFalse();
    }

    // Single-flight: два параллельных запроса по одному ключу — один вызов модели, оба получают тот же результат.
    [Fact]
    public async Task ПараллельныеЗапросы_SingleFlight()
    {
        var stub = new StubRunner();
        var service = MakeService(stub);
        var run = OpenRun("f1");

        var a = service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);
        var b = service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);
        var results = await Task.WhenAll(a, b);

        stub.Calls.Should().Be(1, "второй запрос ждёт первый, без второго вызова");
        results[0].Text.Should().Be(results[1].Text);
        results[1].Cached.Should().BeTrue("ожидающий не звал модель");
    }

    // Потолок LRU: после вытеснения старой записи она считается заново, свежая остаётся в кэше.
    [Fact]
    public async Task LRU_ВытесняетСтарыеЗаписи()
    {
        var stub = new StubRunner();
        var service = MakeService(stub);

        for (int i = 0; i < 65; i++)
            await service.SummarizeAsync("owner", $"f{i}", OpenRun($"f{i}"), false, CancellationToken.None);
        stub.Calls.Should().Be(65);

        // f0 — самая старая — вытеснена (потолок 64): повторный запрос считает заново.
        var evicted = await service.SummarizeAsync("owner", "f0", OpenRun("f0"), false, CancellationToken.None);
        evicted.Cached.Should().BeFalse("запись вытеснена LRU — пересчёт");
        stub.Calls.Should().Be(66);

        // Свежая запись — в кэше.
        var kept = await service.SummarizeAsync("owner", "f64", OpenRun("f64"), false, CancellationToken.None);
        kept.Cached.Should().BeTrue();
        stub.Calls.Should().Be(66);
    }

    // Отказ модели не кэшируется: следующий запрос пробует снова (ручка гасит исключение в 502).
    [Fact]
    public async Task ОтказМодели_НеМемоизируется()
    {
        var stub = new StubRunner { Failure = new LlmTimeoutException("не ответил за отведённое время") };
        var service = MakeService(stub);
        var run = OpenRun("f1");

        var act = () => service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);
        await act.Invoking(f => f()).Should().ThrowAsync<LlmTimeoutException>();

        stub.Failure = null;
        var retry = await service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);
        stub.Calls.Should().Be(2, "сбой не попал в кэш — повторный запрос зовёт модель снова");
        retry.Cached.Should().BeFalse();
    }

    // Пустой ответ модели — отказ (502 на ручке), а не пустое резюме в кэше.
    [Fact]
    public async Task ПустойОтвет_Отказ()
    {
        var stub = new StubRunner { Text = "   " };
        var service = MakeService(stub);
        var run = OpenRun("f1");

        var act = () => service.SummarizeAsync("owner", "f1", run, false, CancellationToken.None);
        await act.Invoking(f => f()).Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── Золотые проверки выжимки (разрез §4: «выжимку собирает одна чистая функция») ───────
    private static (string Digest, string Fingerprint) GoldenDigest()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var epic = new TaskItem { Id = "epic-g", Role = TaskRoles.Feature, Title = "фича-золото", CreatedAt = now - TimeSpan.FromDays(3) };
        var step = new TaskItem
        {
            Id = "g1", FeatureId = "epic-g", Role = TaskRoles.Step, StepRef = "Ш1", Title = "сделать",
            Status = TaskItemStatus.Done,
            ClaudeStartedAt = now - TimeSpan.FromDays(2), CompletedAt = now - TimeSpan.FromDays(2) + TimeSpan.FromMinutes(30),
        };
        var review = new TaskItem
        {
            Id = "r1", FeatureId = "epic-g", Role = TaskRoles.Review, Title = "проверить",
            Status = TaskItemStatus.Done, ReviewOf = ["g1"],
            CompletedAt = now - TimeSpan.FromDays(1),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned, Notes = "нет тестов на границу" },
        };
        var run = FeatureProjector.Build(epic, [epic, step, review], now);
        return FeatureSummaryDigest.Build(run);
    }

    [Fact]
    public void Digest_СодержитФактыХода()
    {
        var (digest, fingerprint) = GoldenDigest();

        digest.Should().Contain("Фича: фича-золото");
        digest.Should().Contain("Состояние:");
        digest.Should().Contain("возвращено 1");
        digest.Should().Contain("Ш1");
        digest.Should().Contain("нет тестов на границу"); // причина возврата из Verification.Notes
        fingerprint.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    // Детерминизм: один и тот же run — одна и та же выжимка и отпечаток (ключ кэша стабилен).
    [Fact]
    public void Digest_Детерминировна()
    {
        var a = GoldenDigest();
        var b = GoldenDigest();
        a.Digest.Should().Be(b.Digest);
        a.Fingerprint.Should().Be(b.Fingerprint);
    }

    // Длинная история шагов: run с 20 цепочками — «Тревога» и «Итог» идут до «Шагов» и
    // остаются в выжимке; в «Шаги» попадают только последние цепочки (вердикт W4-R-ANB, MAJOR-1).
    private static FeatureRun LongRun(string firstChainReason)
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var chains = new List<FeatureStepChain>();
        for (int i = 1; i <= 20; i++)
            chains.Add(new FeatureStepChain
            {
                StepRef = $"Ш{i}",
                Title = $"шаг {i}",
                Outcome = "accepted",
                Loops = 1,
                Rounds = [new FeatureRound { N = 1, Verdict = "returned", Reason = i == 1 ? firstChainReason : new string('б', 150) }],
            });
        return new FeatureRun
        {
            FeatureId = "epic-long",
            Epic = new TaskItem { Id = "epic-long", Role = TaskRoles.Feature, Title = "фича-длинная", CreatedAt = now - TimeSpan.FromDays(5) },
            Attention = new FeatureAttention { Kind = "stopped", Since = now - TimeSpan.FromHours(2), Reason = "исполнитель встал" },
            Summary = new FeatureSummary { DurationMinutes = 120, StepsOpen = 3, Loops = 5 },
            StepChains = chains,
        };
    }

    [Fact]
    public void Digest_LongStepHistory_TrivogaIItogDoSchagov()
    {
        var (digest, _) = FeatureSummaryDigest.Build(LongRun("первая"));

        digest.Length.Should().BeLessOrEqualTo(FeatureSummaryDigest.MaxDigestChars);
        digest.Should().Contain("Тревога: stopped", "тревога — в выжимке при 20 цепочках");
        digest.Should().Contain("Итог:", "итог — в выжимке при 20 цепочках");
        digest.IndexOf("Тревога:").Should().BeLessThan(digest.IndexOf("Шаги:"), "тревога идёт до «Шагов»");
        digest.IndexOf("Итог:").Should().BeLessThan(digest.IndexOf("Шаги:"), "итог идёт до «Шагов»");
        digest.Should().Contain("Ш20", "последние цепочки показаны");
        digest.Should().Contain("ранних цепочек опущено", "старые цепочки свернуты, а не обрезаны слепым [..4000]");
    }

    // Отпечаток — по полному тексту: правка в хвосте истории (цепочка, опущенная в выжимке)
    // меняет отпечаток, хотя сама выжимка идентична (вердикт W4-R-ANB, MAJOR-2).
    [Fact]
    public void Digest_PравkaVKhvoste_OtprintokDrugoy()
    {
        var (digestA, fingerprintA) = FeatureSummaryDigest.Build(LongRun("причина возврата первой цепочки — такая"));
        var (digestB, fingerprintB) = FeatureSummaryDigest.Build(LongRun("причина возврата первой цепочки — другая"));

        digestA.Should().Be(digestB, "первая цепочка опущена — выжимка не меняется");
        fingerprintA.Should().NotBe(fingerprintB, "отпечаток считается по полному тексту — хвост значим");
    }

    // Потолки размера: длинная причина витка обрезается, выжимка не большеMaxDigestChars.
    [Fact]
    public void Digest_ОбрезаетДлинныеПричиныИХвост()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var epic = new TaskItem { Id = "epic-l", Role = TaskRoles.Feature, CreatedAt = now - TimeSpan.FromDays(3) };
        var step = new TaskItem
        {
            Id = "l1", FeatureId = "epic-l", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Done,
            ClaudeStartedAt = now - TimeSpan.FromDays(2), CompletedAt = now - TimeSpan.FromDays(1),
        };
        var review = new TaskItem
        {
            Id = "r1", FeatureId = "epic-l", Role = TaskRoles.Review, ReviewOf = ["l1"],
            Status = TaskItemStatus.Done, CompletedAt = now - TimeSpan.FromHours(1),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned, Notes = new string('а', 1000) },
        };
        var run = FeatureProjector.Build(epic, [epic, step, review], now);
        var (digest, _) = FeatureSummaryDigest.Build(run);

        digest.Should().Contain(new string('а', 200) + "…", "причина витка — потолок 200 символов");
        digest.Length.Should().BeLessOrEqualTo(FeatureSummaryDigest.MaxDigestChars);
    }
}
