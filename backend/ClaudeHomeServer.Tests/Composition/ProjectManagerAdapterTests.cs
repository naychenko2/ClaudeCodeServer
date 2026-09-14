using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож шва IProjectManager (Core) → ProjectManagerAdapter (Main, Ф5).
// Проверяет:
//   - адаптер зарегистрирован в Program.cs (резолвится из полного графа как
//     ProjectManagerAdapter, а не как ProjectManager напрямую);
//   - мутационная проверка: Stub-адаптер возвращает «нет данных» по всем 4 методам.
public class ProjectManagerAdapterTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public void Program_RegistersAdapter_ServiceResolvable()
    {
        // Сторож факта подключения в Program.cs: поднимает полный стенд через
        // `TestWebApplicationFactory<Program>` и резолвит IProjectManager из
        // РЕАЛЬНОГО DI-графа. Если кто-то уберёт регистрацию адаптера или
        // вернёт фабрику-синоним — тест поймает, потому что требует именно
        // тип ProjectManagerAdapter.
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        var adapter = sp.GetRequiredService<IProjectManager>();
        adapter.Should().BeOfType<ProjectManagerAdapter>();
    }

    [Fact]
    public void StubAdapter_ReturnsEmptyForAllFourReadMethods()
    {
        // Мутационная проверка: если заменить адаптер на no-op (Stub),
        // все 4 метода шва возвращают «нет данных» — это и есть то, ради
        // чего адаптер существует. Тест НЕ ловит регрессию «адаптер пустышка»,
        // но пара (этот тест + Program_RegistersAdapter_ServiceResolvable)
        // даёт симметрию: один проверяет, что подмена возможна, второй — что
        // зарегистрирован не Stub.
        var stub = new StubProjectManager();
        var services = new ServiceCollection();
        services.AddSingleton<IProjectManager>(stub);
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<IProjectManager>();
        resolved.GetById("anything").Should().BeNull();
        resolved.GetAll().Should().BeEmpty();
        resolved.GetByOwner("anyone").Should().BeEmpty();
        resolved.GetByRootPath("anywhere").Should().BeEmpty();
    }

    // Подменный IProjectManager: реализует шов. Используется в
    // StubAdapter_ReturnsEmptyForAllFourReadMethods.
    internal sealed class StubProjectManager : IProjectManager
    {
        public Project? GetById(string id) => null;
        public IReadOnlyCollection<Project> GetByOwner(string userId) => Array.Empty<Project>();
        public IReadOnlyCollection<Project> GetAll() => Array.Empty<Project>();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => Array.Empty<Project>();
    }
}
