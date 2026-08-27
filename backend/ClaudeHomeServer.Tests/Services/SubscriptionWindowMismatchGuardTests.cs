using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож «чужого» setup-токена: расхождение времени сброса 5h-окна между setup-токеном
// (probe/turn) и профильным логином (oauth) одного ключа — инцидент 20–23.08.2026.
// Алерт — ровно один на смену состояния; без пары каналов (аккаунт без профильного
// логина), на несвежих снимках и при мелком расхождении — тишина. Из ротации — никогда.
public class SubscriptionWindowMismatchGuardTests : IDisposable
{
    private readonly string _tempDir;

    public SubscriptionWindowMismatchGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sub_guard_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Молчаливый нотификатор для чужих фикстур (warmup/oauth собираются с живым сторожем)
    public sealed class SilentNotifier : ISubscriptionAlertNotifier
    {
        public Task NotifyAdminsAsync(string title, string body) => Task.CompletedTask;
    }

    // Считает вызовы и помнит текст — вместо NotificationService (стор/хаб/push):
    // проверяем дедуп и тексты, а не доставку
    private sealed class CountingNotifier : ISubscriptionAlertNotifier
    {
        public int Calls;
        public string? LastTitle;
        public string? LastBody;

        public Task NotifyAdminsAsync(string title, string body)
        {
            Interlocked.Increment(ref Calls);
            LastTitle = title;
            LastBody = body;
            return Task.CompletedTask;
        }
    }

    private (SubscriptionWindowMismatchGuard Guard, UsageService Usage, CountingNotifier Notifier)
        MkGuard(string? displayName = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [$"{ClaudeSubscriptionPool.Section}:claude-2:OAuthToken"] = "token-claude-2",
        };
        if (displayName is not null)
            dict[$"{ClaudeSubscriptionPool.Section}:claude-2:DisplayName"] = displayName;
        var config = TestConfig.Build(dict);

        var usage = new UsageService(config);
        var pool = new ClaudeSubscriptionPool(config);
        var notifier = new CountingNotifier();
        return (new SubscriptionWindowMismatchGuard(usage, pool, notifier), usage, notifier);
    }

    // Снимок 5h-окна ключа claude-2. Статусы каналов разные (probe — rejected, как в
    // инциденте), чтобы троттлинг UsageService (одинаковое Status+utilization < 3 мин)
    // не съедал вторую запись пары
    private static void Record(UsageService usage, string source, string resetsAt, double util)
        => usage.Record("five_hour", util, source == "oauth" ? "allowed" : "rejected",
            isUsingOverage: false, resetsAt, subscriptionKey: "claude-2", source: source);

    private static async Task<string> CaptureErrAsync(Func<Task> action)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { await action(); }
        finally { Console.SetError(prev); }
        return sw.ToString();
    }

    [Fact]
    public async Task РасхождениеСброса59Минут_АлертРовноОдин()
    {
        var (guard, usage, notifier) = MkGuard();
        var now = DateTime.UtcNow;
        // Картина инцидента: oauth-канал видит сброс на 59 минут позже setup-токена
        Record(usage, "oauth", now.AddHours(2).AddMinutes(59).ToString("o"), 0.1);
        Record(usage, "probe", now.AddHours(2).ToString("o"), 0.9);

        await guard.CheckAsync("claude-2", now);
        await guard.CheckAsync("claude-2", now); // повторный тик с тем же расхождением

        notifier.Calls.Should().Be(1, "алерт — только на смену состояния, не каждый тик");
    }

    [Fact]
    public async Task Расхождение2Минуты_Тишина()
    {
        // Окна выравнены по границе часа — легальная разница это секунды-минуты
        var (guard, usage, notifier) = MkGuard();
        var now = DateTime.UtcNow;

        Record(usage, "oauth", now.AddHours(2).ToString("o"), 0.1);
        Record(usage, "probe", now.AddHours(2).AddMinutes(2).ToString("o"), 0.2);

        await guard.CheckAsync("claude-2", now);

        notifier.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ТолькоПrobe_НетПрофильногоЛогина_Тишина()
    {
        // У аккаунта без `claude login` в профиле oauth-снимков нет вовсе (StatusUnauthorized
        // у поллера) — пары каналов нет, сравнивать нечего
        var (guard, usage, notifier) = MkGuard();
        var now = DateTime.UtcNow;

        Record(usage, "probe", now.AddHours(2).ToString("o"), 0.5);

        await guard.CheckAsync("claude-2", now);

        notifier.Calls.Should().Be(0);
    }

    [Fact]
    public async Task СнимкиСтаршеЧаса_НЕсравниваются()
    {
        // Устаревший сброс — это уже про другое окно: оба снимка обязаны быть свежими
        var (guard, usage, notifier) = MkGuard();
        Record(usage, "oauth", DateTime.UtcNow.AddHours(2).AddMinutes(59).ToString("o"), 0.1);
        Record(usage, "probe", DateTime.UtcNow.AddHours(2).ToString("o"), 0.9);

        // «Сейчас» — на два часа позже записи: оба снимка старше Freshness (1 час)
        await guard.CheckAsync("claude-2", DateTime.UtcNow.AddHours(2));

        notifier.Calls.Should().Be(0);
    }

    [Fact]
    public async Task СхождениеОкон_ГаситФлаг_НовоеРасхождениеБьётСнова()
    {
        var (guard, usage, notifier) = MkGuard();
        var now = DateTime.UtcNow;
        Record(usage, "oauth", now.AddHours(2).AddMinutes(59).ToString("o"), 0.1);
        Record(usage, "probe", now.AddHours(2).ToString("o"), 0.9);
        await guard.CheckAsync("claude-2", now);
        notifier.Calls.Should().Be(1);

        // Перегенерация токена — оба канала сошлись на одном сбросе
        Record(usage, "oauth", now.AddHours(3).ToString("o"), 0.2);
        Record(usage, "probe", now.AddHours(3).ToString("o"), 0.3);
        var log = await CaptureErrAsync(() => guard.CheckAsync("claude-2", now));
        notifier.Calls.Should().Be(1, "схождение — хорошая новость, без алерта");
        log.Should().Contain("[SubscriptionGuard]").And.Contain("согласованы");

        // Снова разъехались — состояние сменилось, алерт уходит заново
        Record(usage, "oauth", now.AddHours(4).AddMinutes(59).ToString("o"), 0.2);
        Record(usage, "probe", now.AddHours(4).ToString("o"), 0.9);
        await guard.CheckAsync("claude-2", now);
        notifier.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Алерт_НесётDisplayName_БезКлючаПодписки()
    {
        // Инвариант e862a991: сырой ключ подписки в тексты пользователю не течёт —
        // только DisplayName с фолбэком «Аккаунт Claude»
        var (guard, usage, notifier) = MkGuard(displayName: "Claude 2 (Max)");
        var now = DateTime.UtcNow;
        Record(usage, "oauth", now.AddHours(2).AddMinutes(59).ToString("o"), 0.1);
        Record(usage, "probe", now.AddHours(2).ToString("o"), 0.9);

        await guard.CheckAsync("claude-2", now);

        notifier.LastTitle.Should().Contain("Claude 2 (Max)");
        notifier.LastTitle.Should().NotContain("claude-2");
        notifier.LastBody.Should().NotContain("claude-2");
        notifier.LastBody.Should().Contain("setup-token");
    }

    [Fact]
    public async Task Алерт_БезDisplayName_ФолбэкАккаунтClaude()
    {
        var (guard, usage, notifier) = MkGuard();
        var now = DateTime.UtcNow;
        Record(usage, "oauth", now.AddHours(2).AddMinutes(59).ToString("o"), 0.1);
        Record(usage, "probe", now.AddHours(2).ToString("o"), 0.9);

        await guard.CheckAsync("claude-2", now);

        notifier.LastTitle.Should().Contain("Аккаунт Claude");
    }

    [Fact]
    public async Task Расхождение_СнимкомЖивогоХодаТожеЛовится()
    {
        // source=turn (живой ход чата) — тот же setup-токен, что и probe: свежий turn
        // против oauth-снимка тоже образует пару (у активно используемого аккаунта
        // probe может не ходить неделями)
        var (guard, usage, notifier) = MkGuard();
        var now = DateTime.UtcNow;
        Record(usage, "oauth", now.AddHours(2).AddMinutes(45).ToString("o"), 0.1);
        Record(usage, "turn", now.AddHours(2).ToString("o"), 0.8);

        await guard.CheckAsync("claude-2", now);

        notifier.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ПровалОдногоКанала_ФлагНеГасит_ПовторногоАлертаНет()
    {
        // oauth перестал отвечать (401 у поллера) — свежей пары нет, состояние ключа
        // не меняется: то же расхождение не бьёт повторным алертом при возврате канала
        var (guard, usage, notifier) = MkGuard();
        var now = DateTime.UtcNow;
        Record(usage, "oauth", now.AddHours(2).AddMinutes(59).ToString("o"), 0.1);
        Record(usage, "probe", now.AddHours(2).ToString("o"), 0.9);
        await guard.CheckAsync("claude-2", now);
        notifier.Calls.Should().Be(1);

        // тик, где oauth-снимок уже не свежий, а нового нет: пара развалилась
        await guard.CheckAsync("claude-2", now.AddMinutes(90));

        // oauth ожил (свежая пара, расхождение то же) — состояние и не гасло, алерта нет
        Record(usage, "oauth", now.AddHours(4).AddMinutes(59).ToString("o"), 0.1);
        Record(usage, "probe", now.AddHours(4).ToString("o"), 0.9);
        await guard.CheckAsync("claude-2", DateTime.UtcNow);

        notifier.Calls.Should().Be(1);
    }
}
