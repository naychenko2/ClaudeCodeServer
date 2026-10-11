using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Services;

// persona_ask обязан запускать one-shot от имени владельца: по ownerId раннер выбирает среду
// исполнения (local / песочница). Раньше effort персоны позиционно вставал на место ownerId —
// ход container-владельца уходил на хост мимо песочницы, а effort терялся.
public class PersonaAskServiceTests
{
    private sealed class CapturingOneShot : IOneShotRunner
    {
        public string? OwnerId;
        public string? Effort;
        public int Calls;

        public string? NormalizeModel(string? model) => model;

        public Task<string> RunAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default,
            string? ownerId = null, string? effort = null, string? label = null)
        {
            Calls++;
            OwnerId = ownerId;
            Effort = effort;
            return Task.FromResult("ответ персоны");
        }

        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default,
            string? ownerId = null, string? effort = null, string? label = null) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task ВладелецИУсилие_УходятВРаннерНаСвоиМеста()
    {
        var fake = new CapturingOneShot();
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.RemoveAll<IOneShotRunner>();
                s.AddSingleton<IOneShotRunner>(fake);
            }
        };
        var svc = factory.Services.GetRequiredService<PersonaAskService>();
        // Память выключена: recall и autolearn тест не касаются
        var persona = new Persona
        {
            OwnerId = "owner-42", Name = "Светлана", Handle = "svetlana",
            Effort = "high", MemoryEnabled = false,
        };

        var answer = await svc.AskAsync("owner-42", persona, "Как дела?", context: null);

        answer.Should().Be("ответ персоны");
        fake.Calls.Should().Be(1);
        fake.OwnerId.Should().Be("owner-42");
        fake.Effort.Should().Be("high");
    }
}
