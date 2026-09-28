using System.Reflection;
using System.Threading.RateLimiting;

namespace ClaudeHomeServer.Services.Http;

/// <summary>
/// <c>UseRateLimiter</c>, чьи лимитеры живут не дольше хоста.
///
/// Зачем. <c>RateLimitingMiddleware</c> в конструкторе создаёт секционированный лимитер
/// эндпоинтов, а у того — таймер-«сердцебиение», который крутится, пока лимитер не
/// утилизирован. Сам мидлвар его не утилизирует никогда (он не <see cref="IDisposable"/>),
/// и активный таймер держит лимитер → мидлвар → остаток конвейера → корневой провайдер
/// со всеми синглтонами. Вдобавок таймер захватывает <c>ExecutionContext</c> момента
/// старта, а в хосте из <c>WebApplicationFactory</c> там лежит <c>HostingListener</c>
/// с фабрикой и готовым хостом.
///
/// В проде хост один на процесс, и это безвредно. В тестах каждый хост после
/// <c>Dispose</c> оставался в памяти целиком: testhost <c>ClaudeHomeServer.Tests</c> рос
/// до 6+ GB и ловил memcg-OOM (разбор 2026-09-28).
///
/// Как. Мидлвар собирается в отдельной ветке конвейера, его лимитеры забирает
/// синглтон <see cref="RateLimiterOwner"/>, и контейнер утилизирует их вместе с собой.
/// Поля мидлвара — внутренности ASP.NET Core; если обновление их переименует, сторож
/// <c>RateLimiterLifetimeTests</c> покраснеет.
/// </summary>
public static class RateLimiterLifetime
{
    public static IApplicationBuilder UseRateLimiterOwnedByHost(this IApplicationBuilder app)
    {
        var owner = app.ApplicationServices.GetRequiredService<RateLimiterOwner>();
        return app.Use(next =>
        {
            var branch = app.New();
            branch.UseRateLimiter();
            branch.Run(next);
            var pipeline = branch.Build();
            owner.Track(pipeline.Target);
            return pipeline;
        });
    }
}

/// <summary>Утилизирует лимитеры <c>RateLimitingMiddleware</c> вместе с контейнером.</summary>
public sealed class RateLimiterOwner : IDisposable
{
    internal static readonly string[] LimiterFields = ["_endpointLimiter", "_globalLimiter"];

    private readonly List<IDisposable> _limiters = [];
    private readonly Lock _lock = new();

    /// <summary>Лимитеры под опекой — для сторожа.</summary>
    internal IReadOnlyList<IDisposable> Snapshot()
    {
        lock (_lock) return [.. _limiters];
    }

    internal void Track(object? middleware)
    {
        if (middleware is null) return;
        var type = middleware.GetType();
        foreach (var name in LimiterFields)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(middleware) is PartitionedRateLimiter<HttpContext> limiter)
                lock (_lock) _limiters.Add(limiter);
        }
    }

    public void Dispose()
    {
        IDisposable[] limiters;
        lock (_lock)
        {
            limiters = [.. _limiters];
            _limiters.Clear();
        }
        foreach (var limiter in limiters) limiter.Dispose();
    }
}
