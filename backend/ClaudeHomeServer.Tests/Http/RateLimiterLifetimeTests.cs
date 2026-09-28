using System.Threading.RateLimiting;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Http;

/// <summary>
/// Лимитер эндпоинтов из <c>RateLimitingMiddleware</c> утилизируется вместе с хостом.
/// Без этого его таймер держал остановленный хост целиком, и testhost рос до 6+ GB.
/// Тест же сторожит внутренние поля ASP.NET Core, которые читает
/// <see cref="RateLimiterOwner"/>: после их переименования опека молча пустеет.
/// </summary>
public class RateLimiterLifetimeTests
{
    [Fact]
    public async Task ЛимитерМидлвара_ВзятПодОпеку_ИУтилизируетсяСХостом()
    {
        IReadOnlyList<IDisposable> limiters;
        using (var factory = new TestWebApplicationFactory())
        {
            using var client = factory.CreateClient();
            (await client.GetAsync("/api/health")).Should().NotBeNull();

            limiters = factory.Services.GetRequiredService<RateLimiterOwner>().Snapshot();
            limiters.Should().NotBeEmpty("иначе поля RateLimitingMiddleware переименованы");

            var endpoint = limiters.OfType<PartitionedRateLimiter<HttpContext>>().First();
            using var lease = endpoint.AttemptAcquire(new DefaultHttpContext());
        }

        foreach (var limiter in limiters.Cast<PartitionedRateLimiter<HttpContext>>())
        {
            var act = () => limiter.AttemptAcquire(new DefaultHttpContext());
            act.Should().Throw<ObjectDisposedException>("таймер утилизированного лимитера остановлен");
        }
    }
}
