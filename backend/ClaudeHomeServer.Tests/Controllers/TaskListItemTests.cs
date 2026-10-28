using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

// Облегчённый список задач: GET /api/tasks и GET /api/projects/{id}/tasks не отдают
// Description/ResultMarkdown, полная карточка — GET /api/tasks/{id}.
[Trait("Category", "Host")]
public class TaskListItemTests : IClassFixture<TestWebApplicationFactory>
{
    private static readonly string[] HeavyAndListOnly =
        ["description", "resultMarkdown", "hasDescription", "hasResult", "descriptionPreview", "listItem"];

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public TaskListItemTests(TestWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _output = output;
    }

    private async Task<string> CreateAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        return task.GetProperty("id").GetString()!;
    }

    private async Task<JsonArray> ListAsync(string query = "")
    {
        var response = await _client.GetAsync("/api/tasks" + query);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    private static JsonObject Find(JsonArray list, string id) =>
        list.Select(n => n!.AsObject()).Single(o => o["id"]!.GetValue<string>() == id);

    [Fact]
    public async Task Список_НеСодержитТяжёлыхТекстов_НоНесётПризнакиИПревью()
    {
        var longDescription = new string('д', 5000);
        var id = await CreateAsync(new { title = "тяжёлая", description = longDescription, resultMarkdown = "итог работы" });

        var item = Find(await ListAsync(), id);

        item["description"]!.GetValue<string>().Should().BeEmpty();
        item["resultMarkdown"].Should().BeNull();
        item["hasDescription"]!.GetValue<bool>().Should().BeTrue();
        item["hasResult"]!.GetValue<bool>().Should().BeTrue();
        item["listItem"]!.GetValue<bool>().Should().BeTrue();
        var preview = item["descriptionPreview"]!.GetValue<string>();
        preview.Should().StartWith(new string('д', 200)).And.EndWith("…");
        preview.Length.Should().Be(201);
        item["title"]!.GetValue<string>().Should().Be("тяжёлая");
    }

    [Fact]
    public async Task Список_ЗадачаБезТекстов_ПризнакиЛожные()
    {
        var id = await CreateAsync(new { title = "пустая" });

        var item = Find(await ListAsync(), id);

        item["hasDescription"]!.GetValue<bool>().Should().BeFalse();
        item["hasResult"]!.GetValue<bool>().Should().BeFalse();
        item["descriptionPreview"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public async Task ПолнаяКарточка_ОтдаётТекстыЦеликом()
    {
        var longDescription = new string('о', 5000);
        var id = await CreateAsync(new { title = "полная", description = longDescription, resultMarkdown = "итог" });

        var response = await _client.GetAsync($"/api/tasks/{id}");

        var full = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        full["description"]!.GetValue<string>().Should().Be(longDescription);
        full["resultMarkdown"]!.GetValue<string>().Should().Be("итог");
        full.ContainsKey("listItem").Should().BeFalse("полная карточка не помечается как облегчённая");
    }

    // Сторож парности: элемент списка = полная задача за вычетом тяжёлых и списочных полей.
    // Забытое при копировании поле TaskItem (новое свойство модели) краснит этот тест.
    [Fact]
    public async Task Список_ВсеОстальныеПоля_СовпадаютСПолнойКарточкой()
    {
        var id = await CreateAsync(new
        {
            title = "паритет", description = "описание", resultMarkdown = "итог", priority = "high",
            dueDate = "2026-12-31", labels = new[] { "а", "б" }, subtasks = new[] { new { title = "шаг" } },
            kind = "defect", repro = new { steps = "шаги", expected = "ждём", actual = "вышло" },
        });

        var fromList = Find(await ListAsync(), id);
        var fullResponse = await _client.GetAsync($"/api/tasks/{id}");
        var full = JsonNode.Parse(await fullResponse.Content.ReadAsStringAsync())!.AsObject();

        foreach (var key in HeavyAndListOnly)
        {
            fromList.Remove(key);
            full.Remove(key);
        }
        JsonNode.DeepEquals(fromList, full).Should().BeTrue(
            $"список: {fromList.ToJsonString()}\nполная: {full.ToJsonString()}");
    }

    [Fact]
    public async Task Поиск_Q_ИщетПоПолномуОписанию_ДажеЗаПределамиПревью()
    {
        var marker = $"уникальныймаркер{Guid.NewGuid():N}";
        var id = await CreateAsync(new { title = "поиск", description = new string('х', 3000) + marker });

        var found = await ListAsync($"?q={marker}");

        found.Select(n => n!["id"]!.GetValue<string>()).Should().Contain(id);
        Find(found, id)["description"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public async Task СписокПроекта_ТожеОблегчённый()
    {
        var projectResponse = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "ПроектСписка",
            rootPath = Path.Combine(_factory.TempDir, "light-list-proj"),
            createDirectory = true,
        });
        projectResponse.IsSuccessStatusCode.Should().BeTrue();
        var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var created = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "в проекте", description = "длинное описание проекта" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/tasks");
        var list = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

        var item = Find(list, id);
        item["description"]!.GetValue<string>().Should().BeEmpty();
        item["hasDescription"]!.GetValue<bool>().Should().BeTrue();
    }

    // Цель задачи: ответ списка легче минимум в 10 раз. Синтетика — задачи с длинными
    // текстами, как на проде (описание ~3,5 КБ, итог ~3,8 КБ в среднем; здесь по 20 КБ).
    [Fact]
    public async Task РазмерОтвета_ПадаетМинимумВДесятьРаз()
    {
        const int count = 200;
        const int textLength = 20_000;
        var prefix = $"перф{Guid.NewGuid():N}";
        for (var i = 0; i < count; i++)
            await CreateAsync(new
            {
                title = $"{prefix}-{i}",
                description = new string('а', textLength),
                resultMarkdown = new string('б', textLength),
            });

        var response = await _client.GetAsync("/api/tasks");
        var body = await response.Content.ReadAsStringAsync();
        var list = JsonNode.Parse(body)!.AsArray();
        var mine = list.Where(n => n!["title"]!.GetValue<string>().StartsWith(prefix)).ToList();
        mine.Should().HaveCount(count);

        // «До»: вес тех же задач в полном виде (как отдавал прежний список) — сумма ответов
        // полной карточки; «после»: те же задачи в облегчённом списке
        long fullBytes = 0;
        foreach (var n in mine)
        {
            var full = await _client.GetStringAsync($"/api/tasks/{n!["id"]!.GetValue<string>()}");
            fullBytes += Encoding.UTF8.GetByteCount(full);
        }
        var lightBytes = Encoding.UTF8.GetByteCount(
            new JsonArray(mine.Select(n => n!.DeepClone()).ToArray()).ToJsonString());
        _output.WriteLine($"{count} задач: тексты в списке = {fullBytes:N0} Б, облегчённый список = {lightBytes:N0} Б, " +
                          $"выигрыш ×{(double)fullBytes / lightBytes:F1}");

        lightBytes.Should().BeLessThan((int)(fullBytes / 10));
    }
}
