using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>Билет проверяется у сервера и кэшируется не дольше своего срока; сбой канала — отказ.</summary>
public sealed class AgentTicketCacheTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Introspector(Func<string, AgentTicketIntrospection?> answer) : IAgentTicketIntrospector
    {
        public int Calls;
        public Task<AgentTicketIntrospection?> IntrospectAsync(string ticket, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(ticket));
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ПринятыйБилет_КэшДоСрока_ПотомСноваКСерверу()
    {
        var clock = new Clock(T0);
        var grant = new AgentTicketIntrospection("o", "p", "/x", T0.AddMinutes(2));
        var introspector = new Introspector(_ => grant);
        var cache = new AgentTicketCache(introspector, clock);
        var granted = 0;
        cache.Granted += _ => granted++;

        (await cache.ValidateAsync("t", default)).Should().Be(grant);
        clock.Now = T0.AddMinutes(1);
        (await cache.ValidateAsync("t", default)).Should().Be(grant);
        introspector.Calls.Should().Be(1);
        granted.Should().Be(1);

        clock.Now = T0.AddMinutes(3);
        await cache.ValidateAsync("t", default);
        introspector.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ПросроченныйОтветСервера_НеПринимается()
    {
        var cache = new AgentTicketCache(new Introspector(_ => new("o", "p", "/x", T0.AddSeconds(-1))), new Clock(T0));

        (await cache.ValidateAsync("t", default)).Should().BeNull();
    }

    [Fact]
    public async Task СбойКанала_Отказ_АПустойИДлинныйБилетНеСпрашиваются()
    {
        var introspector = new Introspector(_ => throw new InvalidOperationException("канал упал"));
        var cache = new AgentTicketCache(introspector, new Clock(T0));

        (await cache.ValidateAsync("t", default)).Should().BeNull();
        (await cache.ValidateAsync("", default)).Should().BeNull();
        (await cache.ValidateAsync(new string('x', 500), default)).Should().BeNull();
        introspector.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Отказ_КэшируетсяКоротко()
    {
        var clock = new Clock(T0);
        var introspector = new Introspector(_ => null);
        var cache = new AgentTicketCache(introspector, clock);

        await cache.ValidateAsync("bad", default);
        await cache.ValidateAsync("bad", default);
        introspector.Calls.Should().Be(1);

        clock.Now = T0.AddSeconds(11);
        await cache.ValidateAsync("bad", default);
        introspector.Calls.Should().Be(2);
    }

    [Fact]
    public async Task СбойКанала_КэшируетсяКакОтказ_ПовторНеБьётВСервер()
    {
        var clock = new Clock(T0);
        var introspector = new Introspector(_ => throw new InvalidOperationException("канал упал"));
        var cache = new AgentTicketCache(introspector, clock);

        (await cache.ValidateAsync("t", default)).Should().BeNull();
        (await cache.ValidateAsync("t", default)).Should().BeNull();
        introspector.Calls.Should().Be(1);

        clock.Now = T0.AddSeconds(11);
        await cache.ValidateAsync("t", default);
        introspector.Calls.Should().Be(2);
    }
}
