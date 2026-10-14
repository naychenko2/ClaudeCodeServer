using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Реестр прогонов ComfyUI (шаг 7): «чей этот prompt_id». Регистрация, перенос в кольцо
// завершённых, чистка кольца старше часа — на фейковых часах (ManualTime), не на Task.Delay,
// поэтому платформонезависимо
public class ComfyPromptLedgerTests
{
    private const string Prompt = "prompt-1";
    private const string Other = "prompt-2";

    private static LocalJobOrigin Origin() => new("owner-1", "session-1", "image", "generate_image", Heavy: false);

    // ─── Регистрация ─────────────────────────────────────────────────────────

    [Fact]
    public void Register_СоздаётАктивнуюЗапись_СПроисхождениемИEta()
    {
        var ledger = new ComfyPromptLedger(new ManualTime());
        ledger.Register(Prompt, Origin(), etaSeconds: 42);

        ledger.Contains(Prompt).Should().BeTrue("реестр знает этот prompt_id");
        ledger.TryGet(Prompt, out var record).Should().BeTrue();
        record!.Origin.Should().Be(Origin());
        record.EtaSeconds.Should().Be(42);
        record.Outcome.Should().BeNull("ещё не завершён");
        record.StartedAt.Should().BeNull("ещё не начался");
        ledger.Active().Should().ContainSingle().Which.PromptId.Should().Be(Prompt);
    }

    [Fact]
    public void Register_Повторно_НеЗатираетСуществующуюЗапись()
    {
        var ledger = new ComfyPromptLedger(new ManualTime());
        ledger.Register(Prompt, Origin(), etaSeconds: 42);
        ledger.Register(Prompt, null, etaSeconds: null);

        ledger.TryGet(Prompt, out var record).Should().BeTrue();
        record!.Origin.Should().Be(Origin(), "первая регистрация не затёрта повтором");
        record.EtaSeconds.Should().Be(42);
    }

    [Fact]
    public void Register_ПустойPromptId_НеКопится()
    {
        var ledger = new ComfyPromptLedger(new ManualTime());
        ledger.Register("", Origin(), 42);

        ledger.Active().Should().BeEmpty();
    }

    // ─── MarkStarted / Complete ──────────────────────────────────────────────

    [Fact]
    public void MarkStarted_СтавитВремяНачала_ОдинРаз()
    {
        var time = new ManualTime();
        var ledger = new ComfyPromptLedger(time);
        ledger.Register(Prompt, Origin(), 42);

        ledger.MarkStarted(Prompt);
        ledger.TryGet(Prompt, out var record).Should().BeTrue();
        record!.StartedAt.Should().Be(time.GetUtcNow());

        time.Advance(TimeSpan.FromSeconds(5));
        ledger.MarkStarted(Prompt);
        record.StartedAt.Should().Be(time.GetUtcNow() - TimeSpan.FromSeconds(5), "время начала не двигается");
    }

    [Fact]
    public void Complete_ПереноситИзАктивныхВКольцо_СИсходомИДлительностью()
    {
        var ledger = new ComfyPromptLedger(new ManualTime());
        ledger.Register(Prompt, Origin(), 42);
        ledger.MarkStarted(Prompt);

        ledger.Complete(Prompt, "completed", runSeconds: 30);

        ledger.Contains(Prompt).Should().BeFalse("завершённый прогон не активен");
        ledger.TryGet(Prompt, out var record).Should().BeTrue("запись живёт в кольце завершённых");
        record!.Outcome.Should().Be("completed");
        record.RunSeconds.Should().Be(30);
        record.CompletedAt.Should().NotBeNull();
        ledger.Recent().Should().ContainSingle().Which.PromptId.Should().Be(Prompt);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Complete_ВсеИсходы_ХранятсяВКольце(string outcome)
    {
        var ledger = new ComfyPromptLedger(new ManualTime());
        ledger.Register(Prompt, Origin(), null);

        ledger.Complete(Prompt, outcome, null);

        ledger.TryGet(Prompt, out var record).Should().BeTrue();
        record!.Outcome.Should().Be(outcome);
    }

    [Fact]
    public void Complete_НеизвестногоPromptId_НеРоняетРеестр()
    {
        var ledger = new ComfyPromptLedger(new ManualTime());
        ledger.Register(Prompt, Origin(), 42);

        ledger.Complete("prompt-unknown", "failed", 10);

        ledger.Contains(Prompt).Should().BeTrue("чужой prompt_id наш не трогает");
    }

    // Есть ли активные прогоны: слушатель прогресса держит сокет, пока есть кому слушать.
    // Только по _active: завершённые (в кольце) активных не дают
    [Fact]
    public void HasActive_ПоАктивным_КольцоНеСчитает()
    {
        var ledger = new ComfyPromptLedger(new ManualTime());
        ledger.HasActive().Should().BeFalse("пустой реестр — нет активных");

        ledger.Register(Prompt, Origin(), 42);
        ledger.HasActive().Should().BeTrue("поставленный прогон активен");

        ledger.Complete(Prompt, "completed", 30);
        ledger.HasActive().Should().BeFalse("завершённый переехал в кольцо, активных нет");
    }

    // ─── Кольцо завершённых: чистка старше часа ───────────────────────────────

    [Fact]
    public void Recent_ЗаписьСтаршеЧаса_Вычищается_Молодая_Остаётся()
    {
        var time = new ManualTime();
        var ledger = new ComfyPromptLedger(time);
        ledger.Register(Prompt, Origin(), 42);
        ledger.Complete(Prompt, "completed", 30);

        time.Advance(ComfyPromptLedger.RecentWindow - TimeSpan.FromSeconds(1));
        ledger.Recent().Should().ContainSingle("на границе окна ещё держим");

        time.Advance(TimeSpan.FromSeconds(2));
        ledger.Recent().Should().BeEmpty("старше часа — вычищено");
    }

    [Fact]
    public void Prune_СнимаетТолькоСтарое_МолодоеДержит()
    {
        var time = new ManualTime();
        var ledger = new ComfyPromptLedger(time);
        ledger.Register(Prompt, Origin(), 42);
        ledger.Complete(Prompt, "completed", 30);

        time.Advance(ComfyPromptLedger.RecentWindow + TimeSpan.FromSeconds(1));
        ledger.Register(Other, Origin(), 10);
        ledger.Complete(Other, "failed", 5);

        ledger.Prune();

        ledger.TryGet(Prompt, out _).Should().BeFalse("старое вычищено");
        ledger.TryGet(Other, out _).Should().BeTrue("молодое держим");
    }

    // ─── Фейковые часы: время двигает тест, а не сон ─────────────────────────

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
