using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож шва IUserStore (Core) → UserStoreAdapter (Main, Ф5).
// Проверяет:
//   - адаптер зарегистрирован в Program.cs (резолвится как UserStoreAdapter);
//   - мутационная проверка: Stub-адаптер возвращает «нет данных» по всем 3 методам.
public class UserStoreAdapterTests
{
    [Fact]
    public void Program_RegistersAdapter_ServiceResolvable()
    {
        // Сторож факта подключения в Program.cs: поднимает полный стенд через
        // `TestWebApplicationFactory<Program>` и резолвит IUserStore из РЕАЛЬНОГО DI-графа.
        // Если кто-то уберёт регистрацию адаптера или вернёт фабрику-синоним —
        // тест поймает, потому что требует именно тип UserStoreAdapter.
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        var adapter = sp.GetRequiredService<IUserStore>();
        adapter.Should().BeOfType<UserStoreAdapter>();
    }

    [Fact]
    public void StubAdapter_ReturnsEmptyAndFalseForAllReadMethods()
    {
        // Мутационная проверка: если заменить адаптер на no-op (Stub),
        // все методы шва возвращают «нет данных» — это и есть то, ради
        // чего адаптер существует. Тест НЕ ловит регрессию «адаптер пустышка»,
        // но пара (этот тест + Program_RegistersAdapter_ServiceResolvable)
        // даёт симметрию: один проверяет, что подмена возможна, второй — что
        // зарегистрирован не Stub.
        var stub = new StubUserStore();
        var services = new ServiceCollection();
        services.AddSingleton<IUserStore>(stub);
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<IUserStore>();
        resolved.GetById("anything").Should().BeNull();
        resolved.GetAll().Should().BeEmpty();
        resolved.IsTokenVersionCurrent("anyone", 42).Should().BeFalse();
    }

    // Подменный IUserStore: реализует шов. Используется в
    // StubAdapter_ReturnsEmptyAndFalseForAllReadMethods.
    internal sealed class StubUserStore : IUserStore
    {
        public User? GetById(string id) => null;
        public IReadOnlyList<User> GetAll() => Array.Empty<User>();
        public bool IsTokenVersionCurrent(string userId, int version) => false;
    }
}
