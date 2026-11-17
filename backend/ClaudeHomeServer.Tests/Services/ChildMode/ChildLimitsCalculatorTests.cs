using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Одна точка расчёта детских лимитов (KM-B5c, ADR-041 И3): ChildLimitsCalculator → ChildStatus.
///
/// Что фиксируют тесты (критерии KM-B5c):
/// 1. все шесть значений state достижимы, DTO заполнен по контракту (minutesLeft,
///    windowEndsAt с явным смещением, allow-list, maxAttachmentBytes);
/// 2. DailyMinutes=40, набрано 36 → warn, minutesLeft=4; ровно на пороге warnMinutes — warn;
/// 3. порядок причин строго по И3: rules_pending старше и отсутствия окна, и бюджета;
///    бюджет старше отсутствия окна, отсутствие окна — исчерпанного дневного лимита;
/// 4. fail-closed: швы Spend нет или она бросила → budget_exhausted без исключения;
///    DenyAll → типизированный отказ policy_unavailable; не-ребёнок → null;
/// 5. таймзона: User.TimeZone старше ChildMode:DefaultTimeZone; без пользователя —
///    DefaultTimeZone, не UTC (14:30 UTC = 17:30 МСК внутри окна 16:00–18:00);
/// 6. полунтервал окна [from, to): ровно на правой границе — уже вне; пустые окна —
///    out_of_window без nextWindowAt;
/// 7. сериализация DTO — дословно контракт фронта (snake_case состояния, camelCase поля);
/// 8. журнала не прочитать (стор бросает) — time_up с minutesLeft null, без исключения наружу;
/// 9. пользовательской таймзоны нет или она неразборная — берётся зона из конфигурации
///    (Asia/Yekaterinburg — смещение +05:00), не фолбэк инстанса.
/// Мутации: «spend is null → не проверять бюджет» краснеет НетШвыSpend_...;
/// «фолбэк таймзоны на UTC» краснеет Таймзона_БезПользовательской_...;
/// «nextWindowAt при любом state» краснеет ПравилаПорядка_... и НетОкон_...;
/// «поменять местами budget и out_of_window» краснеет ПорядокПричин_БюджетСтаршеВнеОкна...;
/// «не читать конфиг, сразу Moscow» краснеет ТаймзонаИзКонфигурации_....
/// Платформонезависимо: Path.GetTempPath(), фиксированные смещения в моментах;
/// Europe/Moscow — IANA-зона без DST (всегда +03:00), есть и на Linux, и на Windows.
/// </summary>
public sealed class ChildLimitsCalculatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly ChildUsageStore _usage;
    private readonly ChildModePolicy _policy;

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    // Вторник 2026-11-17, фиксированные смещения: момент задан так, как его видит ребёнок.
    private static readonly DateTimeOffset InWindow = new(2026, 11, 17, 17, 0, 0, TimeSpan.FromHours(3));    // 17:00 МСК
    private static readonly DateTimeOffset WindowStart = new(2026, 11, 17, 16, 0, 0, TimeSpan.FromHours(3)); // ровно начало — входит
    private static readonly DateTimeOffset WindowEnd = new(2026, 11, 17, 19, 0, 0, TimeSpan.FromHours(3));  // ровно конец — [from,to) не входит
    private static readonly DateTimeOffset AfterWindow = new(2026, 11, 17, 20, 0, 0, TimeSpan.FromHours(3)); // 20:00 МСК
    private const string NextDayStart = "2026-11-18T16:00:00.0000000+03:00"; // ближайшее окно после AfterWindow

    public ChildLimitsCalculatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs-child-limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var userConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        _users = new UserStore(userConfig, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _usage = new ChildUsageStore(Path.Combine(_tempDir, ChildUsageStore.DirName));
        _policy = new ChildModePolicy(_users, NullLogger<ChildModePolicy>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration ChildModeConfig(string defaultTimeZone = "Europe/Moscow") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ChildLimitsCalculator.ConfigTimeZoneKey] = defaultTimeZone,
            [ChildLimitsCalculator.ConfigMaxAttachmentBytesKey] = "10485760",
        }).Build();

    // Работающая шва с нулевым расходом — фон для всех состояний, кроме бюджетных:
    // отсутствующая шва сама даёт budget_exhausted (fail-closed).
    private ChildLimitsCalculator Calc(IOwnerSpendReader? spend = null, string defaultTimeZone = "Europe/Moscow") =>
        new(_policy, _usage, spend ?? new FixedSpend(0m, 0m), _users, ChildModeConfig(defaultTimeZone),
            NullLogger<ChildLimitsCalculator>.Instance);

    private string AddChild(ChildProfile profile, string? timeZone = null)
    {
        var user = _users.Add("kid-" + Guid.NewGuid().ToString("N"), "pass", "user");
        user.TimeZone = timeZone;
        user.ChildMode = profile;
        return user.Id;
    }

    // Образец: окно 16:00–19:00 каждый день, дневный лимит 60 минут, правила согласованы.
    private static ChildProfile Profile(
        int? dailyMinutes = 60,
        string from = "16:00",
        string to = "19:00",
        decimal? dailyBudgetUsd = null,
        decimal? monthlyBudgetUsd = null,
        bool rulesAgreed = true,
        int warnMinutes = 5) => new()
        {
            Enabled = true,
            TutorPersonaId = "tutor-1",
            ProviderKey = "pool-key",
            Model = "sonnet",
            Spaces =
            {
                ["lessons"] = new SpaceLimits
                {
                    Windows = [new TimeWindow { From = from, To = to }],
                    DailyMinutes = dailyMinutes,
                },
            },
            DailyBudgetUsd = dailyBudgetUsd,
            MonthlyBudgetUsd = monthlyBudgetUsd,
            WarnMinutes = warnMinutes,
            RulesAgreedAt = rulesAgreed ? new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc) : null,
        };

    // 36 минут присутствия сегодня: 16:00..16:35 МСК, по одной целой минуте.
    private void RecordMinutes(string ownerId, int count, DateTimeOffset? start = null)
    {
        var from = start ?? WindowStart;
        for (var i = 0; i < count; i++)
            _usage.RecordMinute(ownerId, "lessons", from.AddMinutes(i), Moscow);
    }

    [Fact]
    public void ОкноИЛимиты_Ок_ИDtoЗаполненПоКонтракту()
    {
        var id = AddChild(Profile());
        var status = Calc().Evaluate(id, "lessons", InWindow)!;

        status.State.Should().Be(ChildState.Ok);
        status.Space.Should().Be("lessons");
        status.MinutesLeft.Should().Be(60);                  // лимит не тратился
        status.WindowEndsAt.Should().Be("2026-11-17T19:00:00.0000000+03:00"); // явное смещение таймзоны ребёнка
        status.NextWindowAt.Should().BeNull();               // не out_of_window — по контракту null
        status.WarnMinutes.Should().Be(5);
        status.MaxAttachmentBytes.Should().Be(10_485_760);   // ChildMode:MaxAttachmentBytes из конфигурации
        status.AttachmentTypes.Should().Equal(ChildLimitsCalculator.AttachmentTypesAllowList);
        status.RulesAgreedAt.Should().NotBeNull();
    }

    [Fact]
    public void Предупреждение_Набрано36Из40_MinutesLeftРавно4()
    {
        var id = AddChild(Profile(dailyMinutes: 40));
        RecordMinutes(id, 36);
        var status = Calc().Evaluate(id, "lessons", InWindow)!;

        status.State.Should().Be(ChildState.Warn);
        status.MinutesLeft.Should().Be(4);
    }

    [Fact]
    public void Предупреждение_РовноНаПороге_WarnАНаМинутуБольшеУжеОк()
    {
        // warn — «осталось ≤ warnMinutes»: ровно 5 из 40 ещё warn, 6 — уже ok (граница порога).
        var warnId = AddChild(Profile(dailyMinutes: 40));
        RecordMinutes(warnId, 35);
        Calc().Evaluate(warnId, "lessons", InWindow)!.State.Should().Be(ChildState.Warn);
        Calc().Evaluate(warnId, "lessons", InWindow)!.MinutesLeft.Should().Be(5);

        var okId = AddChild(Profile(dailyMinutes: 40));
        RecordMinutes(okId, 34);
        Calc().Evaluate(okId, "lessons", InWindow)!.State.Should().Be(ChildState.Ok);
    }

    [Fact]
    public void ОстатокМинут_ЦелыйДажеВСерединеМинуты()
    {
        // R-KM-F3 (ревью фронта R-KM-F3): minutesLeft — целые минуты с округлением вниз.
        // Момент в середине минуты не добавляет дробной части и не считается присутствием:
        // 36 записанных минут из 40 → ровно 4, при 17:00:40 МСК тоже ровно 4.
        var id = AddChild(Profile(dailyMinutes: 40));
        RecordMinutes(id, 36);
        var midMinute = new DateTimeOffset(2026, 11, 17, 17, 0, 40, TimeSpan.FromHours(3));
        var status = Calc().Evaluate(id, "lessons", midMinute)!;

        status.MinutesLeft.Should().Be(4);
        status.State.Should().Be(ChildState.Warn);
    }

    [Fact]
    public void ЛимитВыбран_TimeUpИMinutesLeftНоль()
    {
        var id = AddChild(Profile(dailyMinutes: 40));
        RecordMinutes(id, 40);
        var status = Calc().Evaluate(id, "lessons", InWindow)!;

        status.State.Should().Be(ChildState.TimeUp);
        status.MinutesLeft.Should().Be(0);
    }

    [Fact]
    public void ВнеОкна_АутОВиндоуСNextWindowАНаПравойГраницеУжеВне()
    {
        var id = AddChild(Profile());

        var after = Calc().Evaluate(id, "lessons", AfterWindow)!;
        after.State.Should().Be(ChildState.OutOfWindow);
        after.WindowEndsAt.Should().BeNull();               // вне окна — по контракту null
        after.NextWindowAt.Should().Be(NextDayStart);       // ближайшее начало — завтра 16:00 МСК

        // Полунтервал [from, to): ровно на 19:00 — уже вне; ровно на 16:00 — внутри.
        Calc().Evaluate(id, "lessons", WindowEnd)!.State.Should().Be(ChildState.OutOfWindow);
        Calc().Evaluate(id, "lessons", WindowStart)!.State.Should().Be(ChildState.Ok);
    }

    [Fact]
    public void НетОкон_ВходитьНекуда_ИNextWindowНеВыдумывается()
    {
        var profile = Profile();
        profile.Spaces["lessons"].Windows.Clear();
        var id = AddChild(profile);

        var status = Calc().Evaluate(id, "lessons", InWindow)!;
        status.State.Should().Be(ChildState.OutOfWindow);
        status.NextWindowAt.Should().BeNull(); // начала нет ни у какого окна
        status.MinutesLeft.Should().Be(60);    // отсутствие окна не отменяет дневный лимит
    }

    [Fact]
    public void ПравилаПорядка_RulesPendingСтаршеВнеОкнаИБюджета()
    {
        // «rules_pending и вне окна одновременно» → rules_pending; nextWindowAt при этом null
        // (по контракту он только при out_of_window).
        var id = AddChild(Profile(rulesAgreed: false));
        var status = Calc().Evaluate(id, "lessons", AfterWindow)!;
        status.State.Should().Be(ChildState.RulesPending);
        status.NextWindowAt.Should().BeNull();

        // и старше бюджета: расход переведён, правил нет → всё равно rules_pending
        var over = AddChild(Profile(rulesAgreed: false, dailyBudgetUsd: 1m));
        Calc(new FixedSpend(5m, 5m)).Evaluate(over, "lessons", InWindow)!.State.Should().Be(ChildState.RulesPending);
    }

    [Fact]
    public void ПорядокПричин_БюджетСтаршеВнеОкнаАВнеОкнаСтаршеИсчерпанногоЛимита()
    {
        // Ревью R-KM-B5c: границы между соседними причинами И3.
        // Бюджет превышен И время вне окна → budget_exhausted, не out_of_window.
        var budgetId = AddChild(Profile(dailyBudgetUsd: 10m));
        var overBudget = Calc(new FixedSpend(10m, 0m)).Evaluate(budgetId, "lessons", AfterWindow)!;
        overBudget.State.Should().Be(ChildState.BudgetExhausted);
        overBudget.NextWindowAt.Should().BeNull(); // nextWindowAt — только при out_of_window

        // Дневный лимит исчерпан И время вне окна → out_of_window, не time_up:
        // до проверки лимита дело не доходит — отсутствие окна старше.
        var limitId = AddChild(Profile(dailyMinutes: 40));
        RecordMinutes(limitId, 40);
        var overLimit = Calc().Evaluate(limitId, "lessons", AfterWindow)!;
        overLimit.State.Should().Be(ChildState.OutOfWindow);
        overLimit.NextWindowAt.Should().Be(NextDayStart);
    }

    [Fact]
    public void БюджетДостигнутИлиПревышен_БюджетЭкзостед()
    {
        // достигнут = >=: новый ход переведёт бюджет, поэтому ровно на границе уже отказ
        var dailyId = AddChild(Profile(dailyBudgetUsd: 10m));
        Calc(new FixedSpend(10m, 0m)).Evaluate(dailyId, "lessons", InWindow)!.State.Should().Be(ChildState.BudgetExhausted);

        var monthlyId = AddChild(Profile(monthlyBudgetUsd: 100m));
        Calc(new FixedSpend(0m, 100.01m)).Evaluate(monthlyId, "lessons", InWindow)!.State.Should().Be(ChildState.BudgetExhausted);

        // чуть ниже бюджета — ещё ok
        var underId = AddChild(Profile(dailyBudgetUsd: 10m));
        Calc(new FixedSpend(9.99m, 0m)).Evaluate(underId, "lessons", InWindow)!.State.Should().Be(ChildState.Ok);
    }

    [Fact]
    public void НетШвыSpend_БюджетНеПроверить_BudgetExhausted()
    {
        // Spend выключена: отказа хода нет только с проверенным бюджетом — и при профиле
        // БЕЗ бюджетов это всё равно budget_exhausted (ADR-041 решение 4, fail-closed).
        var id = AddChild(Profile(dailyBudgetUsd: null, monthlyBudgetUsd: null));
        var calc = new ChildLimitsCalculator(_policy, _usage, null, _users, ChildModeConfig(),
            NullLogger<ChildLimitsCalculator>.Instance);

        calc.Evaluate(id, "lessons", InWindow)!.State.Should().Be(ChildState.BudgetExhausted);
    }

    [Fact]
    public void ШваSpendБросает_BudgetExhaustedБезИсключенияНаружу()
    {
        var id = AddChild(Profile(dailyBudgetUsd: 10m));
        var status = Calc(new ThrowingSpend()).Evaluate(id, "lessons", InWindow)!;
        status.State.Should().Be(ChildState.BudgetExhausted);
    }

    [Fact]
    public void ЖурналБросает_ВремениНеПроверить_TimeUp()
    {
        // Ревью R-KM-B5c: журнала не прочитать — fail-closed time_up, а не исключение наружу.
        // Триггер — отказ стора читать журнал по недопустимому имени пространства
        // (ChildUsageStore.RequireSpace бросает ArgumentException): сам файл цел,
        // отказывает сам вызов чтения — для калькулятора это «времени не проверить».
        var profile = Profile();
        var limits = profile.Spaces["lessons"];
        profile.Spaces.Remove("lessons");
        profile.Spaces["bad space"] = limits;
        var id = AddChild(profile);

        var status = Calc().Evaluate(id, "bad space", InWindow)!;
        status.State.Should().Be(ChildState.TimeUp);
        status.MinutesLeft.Should().BeNull(); // «времени не проверить» — остатка не выдумываем
    }

    [Fact]
    public void ТаймзонаБезПользовательской_DefaultTimeZoneИнстансаАПяtermUtc()
    {
        // 14:30 UTC = 17:30 МСК внутри окна 16:00–18:00: по UTC это было бы out_of_window.
        var id = AddChild(Profile(dailyMinutes: null, from: "16:00", to: "18:00")); // TimeZone пользователя нет
        var status = Calc().Evaluate(id, "lessons", new DateTimeOffset(2026, 11, 17, 14, 30, 0, TimeSpan.Zero))!;

        status.State.Should().Be(ChildState.Ok);
        status.WindowEndsAt.Should().Be("2026-11-17T18:00:00.0000000+03:00"); // смещение +03:00, не Z
    }

    [Fact]
    public void ТаймзонаПользователяСтаршеКонфигурацииИнстанса()
    {
        // конфигурация инстанса — UTC, пользователь — Europe/Moscow: окна считаются по пользователю.
        var id = AddChild(Profile(dailyMinutes: null, from: "16:00", to: "18:00"), timeZone: "Europe/Moscow");
        var status = Calc(defaultTimeZone: "UTC").Evaluate(id, "lessons", new DateTimeOffset(2026, 11, 17, 14, 30, 0, TimeSpan.Zero))!;

        status.State.Should().Be(ChildState.Ok);
        status.WindowEndsAt.Should().Be("2026-11-17T18:00:00.0000000+03:00");
    }

    [Fact]
    public void ТаймзонаИзКонфигурации_ПользовательскойНет_СмещениеПятьЧасов()
    {
        // Ревью R-KM-B5c: зона из конфигурации — не декоративная строка, а живая зона:
        // пользовательской TimeZone нет → окна и время наружу считаются по Asia/Yekaterinburg
        // (её смещение +05:00), а не по фолбэку инстанса Europe/Moscow (+03:00).
        var id = AddChild(Profile(dailyMinutes: null, from: "16:00", to: "18:00")); // TimeZone пользователя нет
        var status = Calc(defaultTimeZone: "Asia/Yekaterinburg")
            .Evaluate(id, "lessons", new DateTimeOffset(2026, 11, 17, 17, 30, 0, TimeSpan.FromHours(5)))!;

        status.State.Should().Be(ChildState.Ok);
        status.WindowEndsAt.Should().Be("2026-11-17T18:00:00.0000000+05:00");
    }

    [Fact]
    public void ТаймзонаПользователяНеразборная_БерётсяЗонаКонфигурации()
    {
        // "Mars/Base" не разбирается — это не отказ и не прыжок к фолбэку инстанса:
        // следующий кандидат после пользователя существует, и это зона из конфигурации.
        var id = AddChild(Profile(dailyMinutes: null, from: "16:00", to: "18:00"), timeZone: "Mars/Base");
        var status = Calc(defaultTimeZone: "Asia/Yekaterinburg")
            .Evaluate(id, "lessons", new DateTimeOffset(2026, 11, 17, 17, 30, 0, TimeSpan.FromHours(5)))!;

        status.State.Should().Be(ChildState.Ok);
        status.WindowEndsAt.Should().Be("2026-11-17T18:00:00.0000000+05:00");
    }

    [Fact]
    public void DenyAll_ТипизированныйОтказPolicyUnavailable()
    {
        var profile = Profile();
        profile.ProviderKey = ""; // нечитаемый профиль: ребёнок остаётся ребёнком, но всё запрещено
        var id = AddChild(profile);

        var act = () => Calc().Evaluate(id, "lessons", InWindow);
        act.Should().Throw<ChildPolicyUnavailableException>();
    }

    [Fact]
    public void НеРебёнок_СтатусаНет_Null()
    {
        var id = AddChild(profile: null!);
        Calc().Evaluate(id, "lessons", InWindow).Should().BeNull();
        Calc().Evaluate("нет-такого-владельца", "lessons", InWindow).Should().BeNull();
    }

    [Fact]
    public void КонтрактСериализуетсяДословноКакНаФронте()
    {
        // Опции как у MVC в Program.cs (camelCase + JsonStringEnumConverter): без атрибута на
        // свойстве состояния он даёт camelCase имя члена ("outOfWindow") — красным поймано.
        // Конвертер типа (атрибут на enum) этим рантаймом НЕ перекрывает конвертер из опций —
        // работает только атрибут уровня свойства.
        var status = new ChildStatus(
            ChildState.OutOfWindow, "lessons", null, null, NextDayStart, 5,
            10_485_760, ChildLimitsCalculator.AttachmentTypesAllowList, null,
            // Добавочные поля KM-B5e2: здесь — «не заданы» (null / пусто), их содержательные
            // значения проверяет Status_ПрофильСУстановками (тест ручки).
            null, [], null);
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });

        // Разбор документом, а не подстрокой: кодировщик экранирует «+» в смещении (+),
        // имена полей проверяет сам GetProperty — их отсутствие в контракте падает тестом.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("state").GetString().Should().Be("out_of_window");
        root.GetProperty("space").GetString().Should().Be("lessons");
        root.GetProperty("minutesLeft").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("windowEndsAt").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("nextWindowAt").GetString().Should().Be(NextDayStart);
        root.GetProperty("warnMinutes").GetInt32().Should().Be(5);
        root.GetProperty("maxAttachmentBytes").GetInt64().Should().Be(10_485_760);
        root.GetProperty("attachmentTypes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("image/png", "image/jpeg", "image/webp", "application/pdf", "text/plain");
        root.GetProperty("rulesAgreedAt").ValueKind.Should().Be(JsonValueKind.Null);
        // Добавочные поля KM-B5e2 — те же camelCase-имена контракта (ADR-041 «DTO»).
        root.GetProperty("dailyMinutes").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("windows").GetArrayLength().Should().Be(0);
        root.GetProperty("minutesToday").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private sealed class FixedSpend(decimal todayUsd, decimal monthUsd) : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => todayUsd;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => monthUsd;
    }

    private sealed class ThrowingSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => throw new InvalidOperationException("стор расхода лёг");
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => throw new InvalidOperationException("стор расхода лёг");
    }
}
