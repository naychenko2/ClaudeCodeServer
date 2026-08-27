using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Живая проверка эндпоинта карты плана (часть B, шаг 8 плана visual-plan): реальный
// крупный markdown (docs/architecture/features.md) → POST /api/plans/map → карта.
// Модель подменена стабом раннера, но якоря ответа — НАСТОЯЩИЕ заголовки файла:
// валидация якорей и кэш прогоняются на реальном тексте, а не на мини-фикстуре.
public class PlansControllerTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    [Fact]
    public async Task BuildMap_НаРеальномFeaturesMd_ОтвечаетКартой()
    {
        var plan = await File.ReadAllTextAsync(FeaturesMdPath);
        plan.Length.Should().BeGreaterThan(30_000,
            "проверка идёт на крупном файле, а не на мини-фикстуре");
        var runner = new CountingRunner($$"""
            {"genre":"feature","oneLine":"Справочник фич продукта","numbers":[{"value":"2","label":"раздела"}],"blocks":[
              {"id":"b1","title":"Базовые возможности","type":"step","flags":[],"anchor":"Базовые возможности","dependsOn":[]},
              {"id":"b2","title":"Голосовой режим","type":"step","flags":["blocking"],"anchor":"Голосовой режим чата","dependsOn":["b1"]}]}
            """);
        _factory.ExtraServices = services => services.AddSingleton<ICheapTextRunner>(runner);
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync("/api/plans/map", new { plan });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var map = await resp.Content.ReadFromJsonAsync<JsonElement>();
        map.GetProperty("genre").GetString().Should().Be("feature");
        map.GetProperty("oneLine").GetString().Should().Be("Справочник фич продукта");
        map.GetProperty("blocks").GetArrayLength().Should().Be(2);
        map.GetProperty("blocks")[1].GetProperty("anchor").GetString().Should().Be("Голосовой режим чата");
        runner.Calls.Should().Be(1);
        runner.LastActionKey.Should().Be(LocalActionCatalog.PlanMap,
            "эндпоинт ходит через место каталога, а не мимо него");
    }

    [Fact]
    public async Task BuildMap_СбойРазбора_204()
    {
        var plan = await File.ReadAllTextAsync(FeaturesMdPath);
        var runner = new CountingRunner("модель проговорилась вместо JSON");
        _factory.ExtraServices = services => services.AddSingleton<ICheapTextRunner>(runner);
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync("/api/plans/map", new { plan });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "любая неудача молчит: фронт остаётся на тексте плана");
    }

    [Fact]
    public async Task BuildMap_ПустоеТело_400()
    {
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync("/api/plans/map", new { plan = "  " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BuildMap_БезАвторизации_401()
    {
        using var anonymous = _factory.CreateClient();

        var resp = await anonymous.PostAsJsonAsync("/api/plans/map", new { plan = "## План" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string FeaturesMdPath =>
        Path.Combine(RepoRoot(), "docs", "architecture", "features.md");

    // Корень репозитория от каталога сборки: bin/Debug/net10.0 → вверх до .git.
    // Путь строится Path.Combine без хардкода разделителей — тесты гоняются и на Linux (CI).
    // .git ищем и как ФАЙЛ: в git worktree это файл-указатель на служебный каталог.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }

    public void Dispose() => _factory.Dispose();

    // Стаб раннера: считает вызовы, помнит место последнего, отвечает фиксированно
    private sealed class CountingRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        public string? LastActionKey;

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            return Task.FromResult(answer);
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
