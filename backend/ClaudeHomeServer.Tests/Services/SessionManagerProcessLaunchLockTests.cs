using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S3-5: лок запуска процесса держится по сессии (<see cref="SessionEntry.EnsureLock"/>), а не по
/// экземпляру класса — перенос тела в <see cref="TurnProcessHost"/> не должен был открыть
/// check-then-act «Process == null → создать адаптер» для двух конкурентных переподключений
/// одной сессии. Фейковая фабрика считает Create по id сессии.
/// </summary>
[Trait("Category", "Host")]
public class SessionManagerProcessLaunchLockTests : IClassFixture<SessionManagerProcessLaunchLockTests.Hosts>
{
    internal sealed class CountingFactory : ILlmSessionAdapterFactory
    {
        public ConcurrentDictionary<string, int> Created { get; } = new();

        // Сигнал «фабрика позвана» — им тест ловит, что вторая гонка вошла в check-then-act,
        // не дождавшись конца первой. Reset() даёт свежий несработанный сигнал.
        public TaskCompletionSource Entered { get; private set; } = new();

        public void Reset()
        {
            Created.Clear();
            Entered = new();
        }

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context)
        {
            Created.AddOrUpdate(session.Id, 1, (_, n) => n + 1);
            Entered.TrySetResult();
            var adapter = new Mock<ILlmSessionAdapter>();
            adapter.Setup(a => a.StartAsync()).Returns(Task.CompletedTask);
            adapter.SetupGet(a => a.Capabilities).Returns(LlmCapabilitiesCatalog.Claude);
            return adapter.Object;
        }
    }

    /// <summary>
    /// Хост на класс (храповик HostPerClassRatchetGuardTests): фабрика создаётся держателем
    /// хоста, а не тестом; счётчик и сигнал — у фикстуры, тест сбрасывает их в начале прогона.
    /// </summary>
    public sealed class Hosts : IDisposable
    {
        internal CountingFactory Adapters { get; } = new();

        public TestWebApplicationFactory Factory { get; }

        public Hosts()
        {
            // Инициализатор поля не видит другой поле этого типа (CS0236) — фабрика собирается
            // в конструкторе фикстуры, счётчик замыкается локальной переменной.
            var adapters = Adapters;
            Factory = new TestWebApplicationFactory
            {
                ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(adapters),
            };
        }

        public void Dispose() => Factory.Dispose();
    }

    private readonly Hosts _hosts;

    public SessionManagerProcessLaunchLockTests(Hosts hosts) => _hosts = hosts;

    private async Task<string> CreateChatAsync()
    {
        var client = _hosts.Factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"lock-{Guid.NewGuid():N}" });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ДваОдновременныхEnsureProcessAsync_ОднойСессии_АдаптерСоздаётсяОдинРаз()
    {
        var sessionId = await CreateChatAsync();
        var host = _hosts.Factory.Services.GetRequiredService<TurnProcessHost>();
        var entry = _hosts.Factory.Services.GetRequiredService<SessionStore>().Sessions[sessionId];

        // Первая гонка обязана СТОЯТЬ внутри критической секции раньше, чем ей присвоен
        // процесс: оставляю за ней устаревший адаптер с блокирующим DisposeAsync —
        // EnsureProcessCoreAsync дожидается его ДО проверки «Process is not null»
        // (блок «отложенная уборка устаревшего адаптера»). Без этой задержки окно
        // «проверка → присваивание» проходит синхронно, и вторая гонка честно увидела бы
        // готовый процесс: тест ничего бы не доказал.
        var disposeGate = new TaskCompletionSource();
        var stale = new Mock<ILlmSessionAdapter>();
        stale.Setup(a => a.DisposeAsync()).Returns(new ValueTask(disposeGate.Task));
        entry.Process = stale.Object;
        entry.AdapterStale = true;
        _hosts.Adapters.Reset();

        var first = host.EnsureProcessAsync(sessionId, entry);
        var second = host.EnsureProcessAsync(sessionId, entry);

        // С локом вторая стоит на нём и до Create не доходит. Без лока — входит в
        // критическую секцию рядом с первой: ловлю это сигналом с ограничением 500 мс,
        // а не догадкой.
        var entered = await Task.WhenAny(_hosts.Adapters.Entered.Task, Task.Delay(500)) == _hosts.Adapters.Entered.Task;

        disposeGate.SetResult();
        await Task.WhenAll(first, second);

        entered.Should().BeFalse(
            "вторая гонка дошла до Create, не дождавшись конца первой: лок запуска её не держал");
        _hosts.Adapters.Created.TryGetValue(sessionId, out var count).Should().BeTrue();
        count.Should().Be(1, "лок сериализует переподключение одной сессии по sessionId, а не по экземпляру класса");
    }
}
