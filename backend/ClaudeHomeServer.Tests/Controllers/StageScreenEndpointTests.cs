using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// D1a: ручки экрана этапа GET /api/tasks/{id}/stage и /stage/commits/{sha}/diff на настоящем сервере:
// три блока на фикстуре (коммит с трейлером, лента чата, ревью-задача), изоляция по владельцу (чужой — 404
// на обеих ручках), белый список sha, толерантность к обрезанному history.json.
// Мутация изоляции: убрать проверку владения в StageScreenService.Visible — краснеют тесты «Чужой».
[Trait("Category", "Host")]
public class StageScreenEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StageScreenEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private HttpClient Stranger() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, "git " + string.Join(' ', args));
    }

    private static readonly JsonSerializerOptions HistoryOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record Arranged(TaskItem Step, string Sha, string HistoryPath);

    private async Task<Arranged> ArrangeAsync()
    {
        var repo = Path.Combine(_factory.TempDir, "repo_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "master");
        Git(repo, "config", "user.email", "t@example.com");
        Git(repo, "config", "user.name", "t");

        var epic = Tasks.Create(null, OwnerId, new CreateTaskRequest("фича", Role: "feature"));
        var step = Tasks.Create(null, OwnerId, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: "step", StepRef: "D1a"));
        File.WriteAllText(Path.Combine(repo, "A.cs"), "class A {}");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "feat: A\n\nCCS-Task: " + step.Id);
        var sha = GitOut(repo, "rev-parse", "HEAD");
        step.WorktreePath = repo;

        var review = Tasks.Create(null, OwnerId, new CreateTaskRequest("ревью", FeatureId: epic.Id, Role: "review", ReviewOf: [step.Id]));
        review.Verification = new TaskVerification { Verdict = "returned", Notes = "замечание", VerifiedAt = DateTime.UtcNow };

        var chat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "исполнитель");
        step.LinkedSessionId = chat.Id;
        var historyPath = Path.Combine(_factory.TempDir, "sessions", chat.ClaudeSessionId ?? chat.Id, "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        List<StoredMessage> history =
        [
            new StoredUserMessage("Сделай ручку этапа"),
            new StoredToolUseMessage { Id = "1", Name = "Read", Input = JsonSerializer.SerializeToElement(new { file_path = "/x/A.cs" }) },
            new StoredToolUseMessage { Id = "2", Name = "Bash", Input = JsonSerializer.SerializeToElement(new { command = "dotnet build" }) },
        ];
        File.WriteAllText(historyPath, JsonSerializer.Serialize(history, HistoryOpts));
        return new Arranged(step, sha, historyPath);
    }

    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return o;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    [Fact]
    public async Task Владелец_видит_три_блока()
    {
        var a = await ArrangeAsync();

        var resp = await _factory.CreateAuthenticatedClient().GetAsync($"/api/tasks/{a.Step.Id}/stage");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(resp);
        body.GetProperty("artifacts").GetProperty("commits")[0].GetProperty("sha").GetString().Should().Be(a.Sha);
        body.GetProperty("input").GetProperty("transcript").GetProperty("prompt").GetString().Should().Be("Сделай ручку этапа");
        body.GetProperty("input").GetProperty("transcript").GetProperty("toolCalls").GetArrayLength().Should().Be(2);
        body.GetProperty("review").GetProperty("reviews")[0].GetProperty("verdict").GetString().Should().Be("returned");
        body.GetProperty("metrics").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Чужой_пользователь_404_на_обеих_ручках()
    {
        var a = await ArrangeAsync();
        var stranger = Stranger();

        (await stranger.GetAsync($"/api/tasks/{a.Step.Id}/stage")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/tasks/{a.Step.Id}/stage/commits/{a.Sha}/diff")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Дифф_владельцу_и_отказы_по_sha()
    {
        var a = await ArrangeAsync();
        var owner = _factory.CreateAuthenticatedClient();

        var ok = await owner.GetAsync($"/api/tasks/{a.Step.Id}/stage/commits/{a.Sha}/diff");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(ok)).GetProperty("diff").GetString().Should().Contain("class A");

        (await owner.GetAsync($"/api/tasks/{a.Step.Id}/stage/commits/..%2Fx/diff")).StatusCode
            .Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        (await owner.GetAsync($"/api/tasks/{a.Step.Id}/stage/commits/ZZZZZZZ/diff")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.GetAsync($"/api/tasks/{a.Step.Id}/stage/commits/abcdef1/diff")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Обрезанный_history_json_даёт_200_без_хвостовой_записи_и_файл_цел()
    {
        var a = await ArrangeAsync();
        var full = File.ReadAllText(a.HistoryPath);
        // Обрыв посередине последней записи: хвостовой Bash-вызов дописан не до конца
        var cut = full[..(full.LastIndexOf("dotnet build", StringComparison.Ordinal) + 3)];
        File.WriteAllText(a.HistoryPath, cut);

        var resp = await _factory.CreateAuthenticatedClient().GetAsync($"/api/tasks/{a.Step.Id}/stage");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var calls = (await JsonOf(resp)).GetProperty("input").GetProperty("transcript").GetProperty("toolCalls");
        calls.GetArrayLength().Should().Be(1);
        calls[0].GetProperty("name").GetString().Should().Be("Read");
        File.ReadAllText(a.HistoryPath).Should().Be(cut, "ручка только читает: файл не переименован и не переписан");
        Directory.GetFiles(Path.GetDirectoryName(a.HistoryPath)!).Should().ContainSingle();
    }
}
