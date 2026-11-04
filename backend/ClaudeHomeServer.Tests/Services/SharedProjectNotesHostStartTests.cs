using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Регресс зависания хоста S1-3b (2026-10-29): DI-цикл NotesService → IProjectAccess →
/// ProjectAccessService → ISessionDirectory → SessionManager → PersonaBindingsService →
/// INoteAccessor → форвардер (NotesSubsystem.cs:71) → NotesService не бросал исключение —
/// он вешал StartAsync хоста навсегда (GetOrAdd того же синглтона на ре-энтри), и каждый
/// тест с WebApplicationFactory висел вместе с ним. Поэтому проверка — не исключение,
/// а таймаут: хост с вертикалью Notes и включённым рубильником общих проектов обязан
/// стартовать, дать авторизованного клиента и разрешить оба узла цикла — точку доступа
/// и сервис заметок.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectNotesHostStartTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectNotesHostStartTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        // Рубильник включаем до первого запроса: именно он поднимал путь через точку доступа
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    [Fact]
    public async Task ХостСNotesИОбщимиПроектамиСтартует_ИРезолвитТочкуДоступа()
    {
        // Хост стартует лениво — на первом запросе. Живой цикл — не падение, а вечное
        // ожидание, поэтому мерило — таймаут, а не Throw: за 60 с здоровый хост стартует
        // (замер бисекта: ~4 с), зависший — не стартует никогда. Сам тест не блокируется
        // (xUnit1031): ожидание — WhenAny с await, зависший старт остаётся вешать свой
        // поток пула, а тест по таймауту краснеет.
        var startTask = Task.Run(() => _factory.CreateAuthenticatedClient());
        var finished = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(60)));
        finished.Should().BeSameAs(startTask,
            "хост с вертикалью Notes обязан стартовать; зависание означает живой DI-цикл через IProjectAccess");

        (await startTask).DefaultRequestHeaders.Authorization.Should().NotBeNull();

        // Оба узла цикла резолвятся на стартовавшем хосте: греет не сам факт регистрации,
        // а отсутствие ре-энтри при их создании.
        _factory.Services.GetRequiredService<IProjectAccess>().Should().NotBeNull();
        _factory.Services.GetRequiredService<NotesService>().Should().NotBeNull();
    }
}
