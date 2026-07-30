using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Реактивный триггер CodeGraph: изменение .cs в RootPath проекта → FileWatcherService
/// ловит → CodeGraphService.InvalidateIncremental → дебаунс → Rebuild → graph.json.
/// Главный BLOCKER-инвариант: без этой связки CodeGraph мёртв в проде (панель всегда empty).
/// Использует polling-режим FileWatcher (детерминированнее FileSystemWatcher в TestServer/CI)
/// и короткий CodeGraph:RebuildDebounceMs из TestWebApplicationFactory.
/// </summary>
public class FileWatcherServiceTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public FileWatcherServiceTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _factory.Services.GetRequiredService<CodeGraphService>()
            .RegisterProvider(".cs", new FakeCsGraphProvider());
    }

    [Fact]
    public async Task CsИзменение_ВRootPath_ТриггеритRebuildИГрафПоявляется()
    {
        // Подготовка: проект с двумя .cs, граф ещё не строился.
        var dir = Path.Combine(_factory.TempDir, "watcher_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "namespace Demo { public class Foo {} }");
        File.WriteAllText(Path.Combine(dir, "Bar.cs"), "namespace Demo { public class Bar {} }");

        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "watcherproj", rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var watcher = _factory.Services.GetRequiredService<FileWatcherService>();
        const string conn = "test-conn";
        try
        {
            // Поднимаем watch — polling начнёт сканировать RootPath (базовый снапшот).
            watcher.Watch(projectId, conn);

            // До изменения граф не построен — GET отдаёт 404 и запускает фоновый initial-build
            // (HOTFIX прода); граф материализуется асинхронно, поэтому статус — ещё 404.
            var before = await _client.GetAsync($"/api/projects/{projectId}/code-graph");
            before.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "в момент запроса граф ещё не построен");

            // Изменение: добавляем новый .cs с новым типом — именно то, что ловит FileWatcher.
            File.WriteAllText(Path.Combine(dir, "Baz.cs"), "namespace Demo { public class Baz {} }");

            // Ждём полного цикла: poll → flush → CodeGraph debounce → rebuild → save.
            // НЕ выходим по первому 200: build-on-first-GET мог построить граф (Foo+Bar) до того,
            // как FileWatcher донёс добавление Baz. Ждём именно новый класс — это и есть суть
            // реактивного обновления, и она устойчива к опережающему initial-build.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            JsonElement body = default;
            var built = false;
            while (DateTime.UtcNow < deadline)
            {
                var get = await _client.GetAsync($"/api/projects/{projectId}/code-graph");
                if (get.StatusCode == HttpStatusCode.OK)
                {
                    body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
                    var files = body.GetProperty("nodes").EnumerateArray()
                        .Select(n => n.GetProperty("sourceFile").GetString()!)
                        .ToArray();
                    if (files.Any(f => f.Contains("Baz.cs")))
                    {
                        built = true;
                        break;
                    }
                }
                await Task.Delay(200);
            }

            built.Should().BeTrue(
                "граф должен построиться реактивно: .cs-изменение → FileWatcher → CodeGraphService.Rebuild");
            body.GetProperty("nodes").GetArrayLength().Should().BeGreaterOrEqualTo(3,
                "Foo + Bar + новый Baz должны попасть в граф");

            // Новый класс из изменённого .cs действительно присутствует в графе.
            var sourceFiles = body.GetProperty("nodes").EnumerateArray()
                .Select(n => n.GetProperty("sourceFile").GetString()!)
                .ToArray();
            sourceFiles.Should().Contain(f => f.Contains("Baz.cs"),
                "инкремент/ребилд должен был обработать новый .cs-файл");
        }
        finally
        {
            // Снимаем watch — иначе polling-таймер переживёт тест и мусорил в фоне.
            watcher.Unwatch(projectId, conn);
        }
    }

    [Fact]
    public async Task WatchPath_ОтдельноеДеревоВнеПроекта_ТриггеритRebuildЕгоГрафа()
    {
        // Дерево чата лежит вне RootPath любого проекта (ADR-003) — проектный watcher его
        // не видит, поэтому граф обновляет отдельный watcher по пути.
        var worktree = Path.Combine(_factory.TempDir, "wt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "Foo.cs"), "namespace Demo { public class Foo {} }");

        var watcher = _factory.Services.GetRequiredService<FileWatcherService>();
        var graphs = _factory.Services.GetRequiredService<CodeGraphService>();
        const string key = "worktree:test-session";
        try
        {
            watcher.WatchPath(key, worktree);

            // Правка в дереве: новый тип, которого нет в основной ветке.
            File.WriteAllText(Path.Combine(worktree, "OnlyHere.cs"),
                "namespace Demo { public class OnlyHere {} }");

            // Ждём полный контур: poll → flush → NotifyCodeGraph → дебаунс → rebuild → save.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var found = false;
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = await graphs.GetSnapshotAsync(worktree, CancellationToken.None);
                if (snapshot is not null
                    && snapshot.Nodes.Any(n => n.SourceFile.Contains("OnlyHere.cs")))
                {
                    found = true;
                    break;
                }
                await Task.Delay(200);
            }

            found.Should().BeTrue(
                "правка .cs в отдельном дереве должна дойти до графа ЭТОГО дерева: WatchPath → CodeGraph");
        }
        finally
        {
            watcher.UnwatchPath(key);
        }
    }
}
