using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Маршрут фичи через MCP tasks_* (эпик → план → шаг → ревью → возврат → rework → финал → закрытие)
// в ОБОИХ режимах ответа (задача 85fbaee8): итоговое состояние задач в сторе обязано совпасть
// (статусы, связи reviewOf, свидетельства), а компактные ответы — быть минимум вдвое короче полных.
// Мутация: в MutationJson вернуть Json(view) — тест веса краснеет; в compact-ветке TaskJson/MutationCard
// выбросить id — маршрут обрывается на первой же ссылке reviewOf.
[Trait("Category", "Integration")]
[Trait("Category", "Host")]
public class TasksToolsetFeatureRouteSizeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public TasksToolsetFeatureRouteSizeTests(TestWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _output = output;
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<string> CreateProjectAsync(string tag)
    {
        var dir = Path.Combine(_factory.TempDir, "mcp_route_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "McpRoute" + tag, rootPath = dir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private record Call(string Tool, int Chars);

    private async Task<(JsonElement Card, string Text)> CallAsync(List<Call> log, string sessionId, string tool, object args)
    {
        var resp = await _client.AsCaller(sessionId).PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var text = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        (result.TryGetProperty("isError", out var e) && e.GetBoolean()).Should().BeFalse($"{tool}: {text}");
        log.Add(new Call(tool, text.Length));
        // tasks_get эпика дописывает блок «Сверка трекера» после JSON — берём первую строку-JSON
        var json = text.Split('\n')[0];
        return (JsonSerializer.Deserialize<JsonElement>(json), text);
    }

    // Прогон маршрута: возвращает журнал размеров и снимок итогового состояния задач
    private async Task<(List<Call> Log, string Snapshot)> RunRouteAsync(string tag)
    {
        var log = new List<Call>();
        var projectId = await CreateProjectAsync(tag);
        var curator = await CreateSessionAsync(projectId);
        const string longText = "Подробная постановка шага: что сделать, какие файлы трогать, как проверить результат. ";
        var description = string.Concat(Enumerable.Repeat(longText, 8));

        string Id(JsonElement c) => c.GetProperty("id").GetString()!;

        var epic = Id((await CallAsync(log, curator, "tasks_create", new { title = "Фича " + tag, role = "feature", description })).Card);
        var plan = Id((await CallAsync(log, curator, "tasks_create", new
        {
            title = "План", role = "plan", featureId = epic, description,
            planSteps = new[] { new { stepRef = "B1", title = "Шаг B1", lane = "back" } },
        })).Card);
        var step = Id((await CallAsync(log, curator, "tasks_create", new
        {
            title = "Шаг B1", role = "step", featureId = epic, stepRef = "B1", lane = "back", description,
            acceptance = new[] { new { text = "Тесты зелёные" }, new { text = "Сборка без ошибок" } },
        })).Card);
        await CallAsync(log, curator, "tasks_get", new { id = step });
        await CallAsync(log, curator, "tasks_update", new { id = step, status = "inProgress" });
        await CallAsync(log, curator, "tasks_add_subtask", new { taskId = step, title = "Написать код" });
        var sub = Tasks.GetById(step)!.Subtasks.Single().Id;
        await CallAsync(log, curator, "tasks_toggle_subtask", new { taskId = step, subtaskId = sub, isDone = true });
        await CallAsync(log, curator, "tasks_complete", new
        {
            id = step, resultMarkdown = description, linkedFiles = new[] { "backend/A.cs" },
            evidence = new[]
            {
                new { criterionId = "c1", how = "run_tests по набору", result = "277 из 277 зелёные" },
                new { criterionId = "c2", how = "mcp__dev__build", result = "0 ошибок" },
            },
        });
        var review = Id((await CallAsync(log, curator, "tasks_create", new
        {
            title = "Ревью B1", role = "review", featureId = epic, reviewOf = new[] { step }, description,
        })).Card);
        await CallAsync(log, curator, "tasks_complete", new
        {
            id = review, resultMarkdown = "Найдены замечания: " + description,
            verification = new { verdict = "returned", returnReason = "quality", notes = "доработать" },
        });
        var rework = Id((await CallAsync(log, curator, "tasks_create", new
        {
            title = "Доработка B1", role = "rework", featureId = epic, stepRef = "B1", lane = "back",
            reviewOf = new[] { review }, description,
        })).Card);
        await CallAsync(log, curator, "tasks_complete", new { id = rework, resultMarkdown = description });
        var final = Id((await CallAsync(log, curator, "tasks_create", new
        {
            title = "Финал", role = "final", featureId = epic, reviewOf = new[] { step, rework }, description,
        })).Card);
        await CallAsync(log, curator, "tasks_complete", new
        {
            id = final, resultMarkdown = "Принято",
            verification = new { verdict = "accepted" },
        });
        await CallAsync(log, curator, "tasks_get", new { id = epic });
        await CallAsync(log, curator, "tasks_update", new { id = plan, status = "done" });

        string Line(string name, string id)
        {
            var t = Tasks.GetById(id)!;
            return $"{name}:{t.Status}:{t.Role}:rev=[{string.Join(",", (t.ReviewOf ?? []).Select(r => r == step ? "step" : r == review ? "review" : r == rework ? "rework" : "?"))}]:" +
                   $"evidence={t.Evidence.Count}:acc={t.Acceptance.Count}:verdict={t.Verification?.Verdict}:subs={t.Subtasks.Count(s => s.IsDone)}/{t.Subtasks.Count}";
        }
        var snapshot = string.Join("\n", new[]
        {
            Line("plan", plan), Line("step", step), Line("review", review),
            Line("rework", rework), Line("final", final),
        });
        return (log, snapshot);
    }

    [Fact]
    public async Task Маршрут_ОдинаковыйИтог_ВОбоихРежимах_КомпактныеОтветыВДваРазаКороче()
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var previous = config[TasksCompactView.ConfigKey];
        List<Call> compactLog, fullLog;
        string compactSnap, fullSnap;
        try
        {
            config[TasksCompactView.ConfigKey] = "true";
            (compactLog, compactSnap) = await RunRouteAsync("compact");
            config[TasksCompactView.ConfigKey] = "false";
            (fullLog, fullSnap) = await RunRouteAsync("full");
        }
        finally
        {
            config[TasksCompactView.ConfigKey] = previous;
        }

        compactSnap.Should().Be(fullSnap, "режим ответа не меняет домен: статусы, reviewOf, свидетельства, вердикты");
        compactSnap.Should().Contain("final:Done").And.Contain("rework:Done").And.Contain("evidence=2");

        var lines = new List<string> { "tool\tшт\tполный_ср\tкомпакт_ср\tотношение" };
        foreach (var g in fullLog.GroupBy(c => c.Tool))
        {
            var full = g.Average(c => c.Chars);
            var compact = compactLog.Where(c => c.Tool == g.Key).Average(c => c.Chars);
            lines.Add($"{g.Key}\t{g.Count()}\t{full:F0}\t{compact:F0}\t{full / compact:F2}");
        }
        var fullTotal = fullLog.Sum(c => c.Chars);
        var compactTotal = compactLog.Sum(c => c.Chars);
        lines.Add($"ВСЕГО\t{fullLog.Count}\t{fullTotal}\t{compactTotal}\t{(double)fullTotal / compactTotal:F2}");
        foreach (var l in lines) _output.WriteLine(l);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "tasks-route-sizes.tsv"), lines);

        compactLog.Count.Should().Be(fullLog.Count);
        compactTotal.Should().BeLessThan(fullTotal / 2, "изменяющие вызовы и tasks_get вдвое короче");
    }
}
