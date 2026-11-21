using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Каскад удаления пользователя (ADR-040 §3): запись «Личного» уходит из стора, папка остаётся.
// Домашняя папка задаётся КОНФИГОМ СВОЕГО ХОСТА (ExtraConfig фабрики класса), а не переменной
// окружения процесса: env-ключ читается и любым другим хостом, который строится в это же время
// в параллельной коллекции (xunit.runner.json: parallelizeTestCollections: true) — так он и
// тек в соседние тесты (Х-W4-29: TestingDefaultProjectsPathGuardTests краснел с /tmp/pcascade_…).
// ExtraConfig — источник этой же фабрики, добавлен после её встроенных ключей
// (TestWebApplicationFactory.cs:163), поэтому перетирает DefaultProjectsPath TempDir'а.
// Что он за пределы класса не выходит держит одна вещь: хост ЭТОГО класса (IClassFixture,
// не общий пул и не ICollectionFixture) — предупреждение на словаре в TestWebApplicationFactory.cs:65.
[Trait("Category", "Host")]
public class UserDeletePersonalProjectTests : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private const string Username = "pcascade";
    private const string HomeKey = "DefaultProjectsPath";
    private const string HomeOverrideKey = "Projects:UserHomeOverrides:" + Username;

    private readonly TestWebApplicationFactory _factory;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pcascade_" + Guid.NewGuid().ToString("N"));

    public UserDeletePersonalProjectTests(TestWebApplicationFactory factory)
    {
        Directory.CreateDirectory(_home);
        _factory = factory;
        // Override действует только при заданной базе проектов — задаём её тем же путём.
        // Хост поднимается лениво (при первом обращении к фабрике), поэтому конфиг готов
        // до любой его сборки.
        _factory.ExtraConfig[HomeOverrideKey] = _home;
        _factory.ExtraConfig[HomeKey] = _home;
    }

    public void Dispose()
    {
        // Конфиг жил в хосте ЭТОГО класса и гасится вместе с ним — снимать в процессе нечего.
        // Верно ровно пока класс держит свой хост (IClassFixture выше): на общем хосте
        // (пул или ICollectionFixture) чистить ключи здесь было бы неправильно — такому
        // классу нужен свой подкласс фабрики, а не ExtraConfig (предупреждение на словаре).
        try { Directory.Delete(_home, recursive: true); } catch { /* не критично */ }
    }

    [Fact]
    public async Task УдалениеПользователя_УбираетЗаписьЛичного_ПапкаОстаётся()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var create = await admin.PostAsJsonAsync("/api/users", new
        {
            username = Username,
            password = "password12345",
            role = "user",
            executionEnvironment = "local",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var userId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var personal = _factory.Services.GetRequiredService<IPersonalProjects>();
        var project = await personal.EnsurePersonalAsync(userId);
        project.RootPath.Should().Be(Path.Combine(_home, "Personal"));

        var delete = await admin.DeleteAsync($"/api/users/{userId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        personal.GetPersonalOf(userId).Should().BeNull();
        personal.GetByIdIncludingPersonal(project.Id).Should().BeNull();
        Directory.Exists(project.RootPath).Should().BeTrue();
    }
}
