using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Sessions;

// Б3 (И2″, часть «rate_limit_event»; ADR-041 ред. 4, разрез Александра §1 строка 9):
// rate_limit_event у ребёнка не переводит аккаунт пула и не предлагает карточку «Продолжить на…».
// LimitRecorder.Record остаётся — пул помечается исчерпанным; реакция на свой ход
// (TryPoolFailover / OfferProviderFallbackAsync) для детского владельца пропускается. Проверка —
// в TurnCompletion.OnMessageQueuedAsync; CliRoute и ProviderMigration не тронуты. Признак
// ребёнка — та же точка, что остальной детский код хода: entry.Info.ChildSpace.
//
// Фейковый пул (CountingPool) считает Pick: у ребёнка на событие он не зовётся ни разу, у
// взрослого — зовётся (TryPoolFailover выбирает здоровый аккаунт). Конфигурация — два аккаунта:
// acc-a старшего тарифа (его берёт Pick при создании чата) и acc-b здоровый, чтобы взрослый
// кейс реально переехал и это было видно по Provider чата.
//
// Доказательство «Record вызван» — пометка пула: IsExhausted(«acc-a») ложна до события и
// истинна после (в этом пути пул метит только SubscriptionLimitRecorder.Record → MarkExhausted).
public class TurnCompletionChildRateLimitTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestSessionBroadcaster _broadcaster = new();

    public TurnCompletionChildRateLimitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "turn_child_rl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFs.DeleteDirectoryResilient(_tempDir);

    // Фейковый пул со счётчиком Pick (тот же образец, что ChildProviderRouteTests.CountingPool).
    private sealed class CountingPool(IConfiguration config) : ClaudeSubscriptionPool(config)
    {
        public int PickCalls;
        public override string Pick(string? model = null)
        {
            Interlocked.Increment(ref PickCalls);
            return base.Pick(model);
        }
    }

    // Ядро собирается SessionCoreTestHost (те же умолчания, что AddSessionCore на проде); в
    // configure подменяем только конфиг (с секцией пула), широковещатель и сам пул на фейковый —
    // остальное резолвится из контейнера. Последний AddSingleton выигрывает.
    private (SessionManager Sut, CountingPool Pool, UserStore Users) BuildSut(string suffix)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
            // acc-a — старший тариф (его берёт Pick при создании), acc-b здоровый.
            [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
            [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
        }).Build();

        var pool = new CountingPool(config);
        var host = SessionCoreTestHost.Build(_tempDir, s =>
        {
            s.AddSingleton<IConfiguration>(config);
            s.AddSingleton<ISessionBroadcaster>(_broadcaster);
            s.AddSingleton<ClaudeSubscriptionPool>(pool);
        });
        var users = host.Services.GetRequiredService<UserStore>();
        return (host.Manager, pool, users);
    }

    // Чат на аккаунте пула (Pick при создании берёт acc-a — старший тариф). childSpace != null —
    // детский (тот же признак, что ChildToolResultRedactionTests.MkChatAsync).
    private static async Task<Session> CreateChatOnPoolAsync(SessionManager sut, UserStore users,
        string suffix, string? childSpace)
    {
        var user = users.Add("kid-" + suffix, "password123", "kid" + suffix);
        var session = await sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");
        session.Provider.Should().Be("acc-a", "старший тариф — Pick выбирает его при создании");
        session.ChildSpace = childSpace;
        return session;
    }

    private static async Task InvokeRateLimitAsync(SessionManager sut, string sessionId)
    {
        var m = typeof(SessionManager).GetMethod("OnMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var msg = new RateLimitMessage("five_hour", null, Status: "rejected");
        await (Task)m.Invoke(sut, [sessionId, new TurnAccumulator(new List<StoredMessage>()), msg, 0L])!;
    }

    [Fact]
    public async Task ДетскийЧат_RateLimit_Rejected_НеПереводитАккаунтИБезКарточки()
    {
        var (sut, pool, users) = BuildSut("child");
        var session = await CreateChatOnPoolAsync(sut, users, "child", ChildSpaces.Lessons);

        var pickBefore = pool.PickCalls;
        pool.IsExhausted("acc-a").Should().BeFalse("до события аккаунт не исчерпан");

        await InvokeRateLimitAsync(sut, session.Id);

        // Record отработал: пул помечен исчерпанным (единственный, кто метит пул в этом пути).
        pool.IsExhausted("acc-a").Should().BeTrue(
            "LimitRecorder.Record остаётся: аккаунт пула помечен исчерпанным");
        // Ребёнка не переводили: провайдер прежний, Pick за время события не звался.
        session.Provider.Should().Be("acc-a", "детский чат остаётся на закреплённом аккаунте");
        pool.PickCalls.Should().Be(pickBefore, "TryPoolFailover ребёнку пропущен — Pick не звался");
        // Карточки «Продолжить на…» нет.
        _broadcaster.Session.Select(t => t.Message).OfType<ProviderLimitMessage>().Should().BeEmpty(
            "OfferProviderFallbackAsync ребёнку пропущен");
    }

    [Fact]
    public async Task ВзрослыйЧат_RateLimit_Rejected_ПереводитНаЗдоровыйАккаунт()
    {
        var (sut, pool, users) = BuildSut("adult");
        var session = await CreateChatOnPoolAsync(sut, users, "adult", childSpace: null);

        var pickBefore = pool.PickCalls;
        pool.IsExhausted("acc-a").Should().BeFalse();

        await InvokeRateLimitAsync(sut, session.Id);

        // Поведение взрослых не изменилось: аккаунт исчерпан и чат переведён на здоровый.
        pool.IsExhausted("acc-a").Should().BeTrue();
        session.Provider.Should().Be("acc-b", "TryPoolFailover перевёл взрослого на здоровый аккаунт");
        pool.PickCalls.Should().BeGreaterThan(pickBefore, "TryPoolFailover звал Pick");
    }
}
