using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Изоляция и живой состав http-тулсета dify (ADR-012, фаза 2 волна 4): базы знаний —
/// per-owner данные (классификация датасетов по username), эндпоинт торчит наружу вместе
/// с Kestrel, поэтому сессия из хвоста обязана принадлежать владельцу ТОКЕНА, а доступность
/// датасета — проверяться релевантностью (KnowledgeBaseCatalogService), как в REST.
/// Парные тесты: NotesHttpOwnerIsolationTests (волна 2), WorkspaceHttpOwnerIsolationTests (3).
///
/// Состав зависит от настроенного Dify (секция Dify appsettings): без него tools/list пуст
/// и оси живого состава скипаются; оси изоляции (чужая сессия) работают всегда — fail-closed
/// и на пустой конфигурации.
/// </summary>
public class DifyHttpOwnerIsolationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"dify-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    private async Task<JsonElement> CallToolAsync(HttpClient client, string sessionId, string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/dify/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private async Task<IReadOnlyList<string>> ListToolsAsync(HttpClient client, string sessionId)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/dify/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    // Dify настроен в тестовом хосте? Без секции оси живого состава не проверяемы
    private bool DifyConfigured =>
        _factory.Services.GetRequiredService<KnowledgeService>().IsConfigured;

    /// <summary>
    /// Свой чат проекта без базы: полный состав (12) — эквивалент stdio без
    /// DIFY_DEFAULT_DATASET_ID. Свежий проект датасета ещё не имеет.
    /// </summary>
    [SkippableFact]
    public async Task СвежийПроект_ПолныйСоставИз12Инструментов()
    {
        Skip.If(!DifyConfigured, "Dify не настроен — ось живого состава непроверяема");
        var (_, sessionId) = await CreateProjectWithSessionAsync(Client);

        (await ListToolsAsync(Client, sessionId)).Should().HaveCount(12,
            "проект без своей базы — полный состав (search + CRUD), как stdio без DIFY_SEARCH_ONLY");
    }

    /// <summary>
    /// Появился дефолтный датасет проекта — состав сужается до search-only (4), эквивалент
    /// env DIFY_SEARCH_ONLY=true stdio-ветки. Формула живая: сессия та же, изменился стор.
    /// </summary>
    [SkippableFact]
    public async Task ДатасетПроекта_СужаетСоставДоSearchOnly()
    {
        Skip.If(!DifyConfigured, "Dify не настроен — ось живого состава непроверяема");
        var (projectId, sessionId) = await CreateProjectWithSessionAsync(Client);
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var workspaceStore = _factory.Services.GetRequiredService<WorkspaceKnowledgeStore>();
        var root = projects.GetById(projectId)!.RootPath;
        var wk = workspaceStore.GetOrCreate(root);
        wk.DifyDatasetId = "ds-test";
        workspaceStore.Save(wk);

        var tools = await ListToolsAsync(Client, sessionId);
        tools.Should().HaveCount(4, "проект со своей базой — только поиск и чтение");
        tools.Should().BeEquivalentTo(
            ["search_knowledge", "list_datasets", "list_documents", "list_segments"]);
    }

    /// <summary>
    /// Токен B с хвостом сессии владельца A: ни состава, ни вызова — fail-closed, как у
    /// памяти/заметок/рабочего пространства.
    /// </summary>
    [Fact]
    public async Task ЧужойТокен_НиСоставаНиВызова()
    {
        var (_, sessionIdA) = await CreateProjectWithSessionAsync(Client);
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await ListToolsAsync(clientB, sessionIdA)).Should().BeEmpty(
            "чужая сессия — пустой состав (fail-closed)");

        var call = await CallToolAsync(clientB, sessionIdA, "list_datasets", new { });
        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!
            .Should().Contain("другому владельцу");
    }

    /// <summary>
    /// Незаказанный dataset_id — честный отказ с подсказкой (не пустой список и не исключение):
    /// модель должна понять, что нужно указать dataset_id или спросить list_datasets.
    /// </summary>
    [SkippableFact]
    public async Task БезДатасетаИDatasetId_ОтказСПодсказкой()
    {
        Skip.If(!DifyConfigured, "Dify не настроен — ось живого состава непроверяема");
        var (_, sessionId) = await CreateProjectWithSessionAsync(Client);

        var call = await CallToolAsync(Client, sessionId, "search_knowledge", new { query = "тест" });
        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        var text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        text.Should().Contain("dataset_id");
        // Ключ Dify — секрет: в текстах, которые читает модель, его не бывает
        text.Should().NotContain(DifyApiKey(), "ключ Dify не утекает в тексты ошибок");
    }

    /// <summary>
    /// ЖИВОЙ путь приёмки волны 4 (реальный Dify из секции/ENV): list_datasets отдаёт
    /// релевантные базы без ошибки, create_dataset → search_knowledge → delete_dataset
    /// проходят сквозь тулсет → KnowledgeService → внешний Dify. База за собой убираем.
    /// Скип без настроенного Dify (CI).
    /// </summary>
    [SkippableFact]
    public async Task ЖивойПуть_СписокПоискСозданиеУдаление_Работают()
    {
        Skip.If(!DifyConfigured, "Dify не настроен — живой путь непроверяем");
        var (_, sessionId) = await CreateProjectWithSessionAsync(Client);
        var apiKey = DifyApiKey();

        var listed = await CallToolAsync(Client, sessionId, "list_datasets", new { });
        var listText = listed.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        listed.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(
            $"list_datasets обязан работать через живой Dify, ответ: {listText}");
        listText.Should().NotContain(apiKey, "ключ Dify не утекает в тексты, которые читает модель");

        // Лимит Dify — 40 символов на имя датасета ЦЕЛИКОМ (с префиксом «{user}:kb:»),
        // поэтому тестовый суффикс короткий
        var title = "ccs-" + Guid.NewGuid().ToString("N")[..16];
        var created = await CallToolAsync(Client, sessionId, "create_dataset",
            new { name = title, permission = "only_me" });
        var createdText = created.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        created.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(
            $"create_dataset обязан работать через живой Dify, ответ: {createdText}");
        var dataset = JsonSerializer.Deserialize<JsonElement>(createdText);
        var datasetId = dataset.GetProperty("id").GetString()!;
        try
        {
            var searched = await CallToolAsync(Client, sessionId, "search_knowledge",
                new { query = "живая проверка http-тулсета", dataset_id = datasetId, top_k = 3 });
            var searchText = searched.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            searched.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(
                $"search_knowledge обязан работать через живой Dify (пустая база — ноль записей, не ошибка), ответ: {searchText}");
            searchText.Should().Contain("\"items\"", "ответ несёт форму поиска");
            searchText.Should().NotContain(apiKey, "ключ Dify не утекает в тексты, которые читает модель");
        }
        finally
        {
            var deleted = await CallToolAsync(Client, sessionId, "delete_dataset",
                new { dataset_id = datasetId });
            deleted.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(
                "тестовая база удаляется за собой (deletable: самостоятельная kb:)");
        }
    }

    private string DifyApiKey() =>
        _factory.Services.GetRequiredService<IOptions<DifyOptions>>().Value.ApiKey;
}
