using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// REST-парность для трёх находок ревью Глеба по правилам дефектов (Major в задаче
// c830d8cd). MCP-парные тесты — в TasksToolsetDefectTests. Сценарии:
// • Находка 1: tasks_update {kind: "task"} на дефекте → 400 (вид immutable после создания);
//   прежний effective.Kind=req.Kind ?? task.Kind снимал гейты, карточка закрывалась без
//   Verification.
// • Находка 2: дефект уже стоит в review-колонке, tasks_update {repro: {}} без columnId
//   → 400; прежний targetIsReview считался только по переданному columnId, гейт не
//   срабатывал, шаги стирались мимо инварианта.
// • Находка 3: tasks_update {verification: {}} (без notes) на дефекте → 400; прежний
//   HasMeaningfulNotes считал null Notes валидным, паспорт 432b6de2 это запрещает.
[Trait("Category", "Integration")]
public class DefectUpdateGatesTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public DefectUpdateGatesTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "defect_update_gates_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    private async Task<string> CreateProjectAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/projects",
            new { name = "DefectUpdateGates", rootPath = _projectDir, createDirectory = true });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task SetBoardAsync(string projectId, object columns)
    {
        var resp = await _client.PutAsJsonAsync($"/api/projects/{projectId}/board-columns", columns);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<string> CreateDefectAsync(string projectId, string? columnId = null,
        object? repro = null)
    {
        // Проектный эндпоинт — карточка принадлежит проекту, иначе BoardColumn доски
        // к ней не применима (у личных задач нет ProjectId, колонка в сторе не резолвится
        // — находка 2 на личных задачах структурно невозможна, тестируем на проектных).
        var body = new Dictionary<string, object?>
        {
            ["title"] = "Дефект",
            ["kind"] = "defect",
        };
        if (columnId is not null) body["columnId"] = columnId;
        if (repro is not null) body["repro"] = repro;
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", body);
        resp.EnsureSuccessStatusCode();
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return task.GetProperty("id").GetString()!;
    }

    // ─── Находка 1: смена kind снимает гейты ────────────────────────────────

    [Fact]
    public async Task Update_ДефектСоСменойKindНаTaskИDone_400()
    {
        // Сценарий из находки 1: kind=task + status=done на дефекте раньше снимал гейт
        // EnsureVerificationOnClose (effective.Kind=Task), карточка закрывалась без
        // вердикта и без ClosedWithoutCheck, а Kind затирался — следа не оставалось.
        // Вид immutable после создания: req.Kind != task.Kind молча игнорируется, вид
        // остаётся Defect (effective.Kind = task.Kind), гейт EnsureVerificationOnClose
        // срабатывает на закрытие без Verification. Та же 400, но текст от EnsureVerificationOnClose.
        var projectId = await CreateProjectAsync();
        var taskId = await CreateDefectAsync(projectId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new
        {
            kind = "task",
            status = "done",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Verification");
        // Карточка осталась дефектом и не закрылась
        var fresh = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
        fresh.GetProperty("kind").GetString().Should().Be("defect");
        fresh.GetProperty("status").GetString().Should().Be("todo");
    }

    [Fact]
    public async Task Update_ДефектСоСменойKindНаTaskБезStatus_Проходит()
    {
        // Вид immutable после создания: kind=task игнорируется, без status изменения
        // карточка остаётся дефектом в исходном статусе. Это легитимный сценарий формы
        // редактирования — сегмент «Задача / Дефект» шлёт kind безусловно при сохранении.
        var projectId = await CreateProjectAsync();
        var taskId = await CreateDefectAsync(projectId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new { kind = "task" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        task.GetProperty("kind").GetString().Should().Be("defect");
    }

    [Fact]
    public async Task Update_ДефектСоСменойKindНаDefectБезСмены_Проходит()
    {
        // Тот же Kind присылать не запрещено (req.Kind == task.Kind) — это пустая операция.
        var projectId = await CreateProjectAsync();
        var taskId = await CreateDefectAsync(projectId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new { kind = "defect" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        task.GetProperty("kind").GetString().Should().Be("defect");
    }

    [Fact]
    public async Task Update_ДефектБезKindВЗапросе_НеМеняется()
    {
        // Запрос без поля kind — карточка остаётся дефектом.
        var projectId = await CreateProjectAsync();
        var taskId = await CreateDefectAsync(projectId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new { priority = "high" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        task.GetProperty("kind").GetString().Should().Be("defect");
        task.GetProperty("priority").GetString().Should().Be("high");
    }

    // ─── Находка 2: repro:{} в review-колонке ───────────────────────────────

    [Fact]
    public async Task Update_ДефектВReviewСтираетReproБезColumnId_400()
    {
        // Сценарий из находки 2: дефект уже стоит в review-колонке, клиент шлёт
        // Put с repro:{} и без columnId — раньше targetIsReview>false пропускал гейт,
        // Repro.Steps стирался, инвариант «в ревью только с шагами» нарушался.
        // Теперь effectiveColumn = текущая колонка карточки, гейт срабатывает.
        var projectId = await CreateProjectAsync();
        await SetBoardAsync(projectId, new
        {
            columns = new[]
            {
                new { id = "review-col", name = "На согласовании", category = "inProgress", role = "review" },
            },
        });
        // Создаём дефект уже в review-колонке, с шагами — Create пропускает гейт
        var taskId = await CreateDefectAsync(projectId, columnId: "review-col",
            repro: new { steps = "1. Шаг воспроизведения" });

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new
        {
            repro = new { },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Repro.Steps");
    }

    [Fact]
    public async Task Update_ДефектВReviewСПустымиШагамиБезColumnId_400()
    {
        // Пустой объект repro и явный steps=null трактуются одинаково — обход через
        // любой «стиратель» должен блокироваться.
        var projectId = await CreateProjectAsync();
        await SetBoardAsync(projectId, new
        {
            columns = new[]
            {
                new { id = "review-col", name = "На согласовании", category = "inProgress", role = "review" },
            },
        });
        var taskId = await CreateDefectAsync(projectId, columnId: "review-col",
            repro: new { steps = "1. Шаг" });

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new
        {
            repro = new { steps = (string?)null },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Repro.Steps");
    }

    // ─── Находка 3: Verification без Notes ─────────────────────────────────

    [Fact]
    public async Task Update_ДефектСVerificationБезNotes_400()
    {
        // Сценарий из находки 3: tasks_update {verification: {}} — раньше null Notes
        // считался содержательным (HasMeaningfulNotes для null возвращала true),
        // карточка закрывалась без единого слова о проверке. Теперь null/whitespace
        // Notes — не вердикт (паспорт 432b6de2; гейт EnsureVerificationOnClose смотрит
        // на исходный task.Kind=Defect, а не на переданный req.Kind).
        var projectId = await CreateProjectAsync();
        var taskId = await CreateDefectAsync(projectId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new
        {
            status = "done",
            verification = new { },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Verification");
    }

    [Fact]
    public async Task Update_ДефектСVerificationСПробельнымNotes_400()
    {
        // Whitespace Notes — та же дыра, что null (паспорт 432b6de2: null/whitespace
        // — не вердикт).
        var projectId = await CreateProjectAsync();
        var taskId = await CreateDefectAsync(projectId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new
        {
            status = "done",
            verification = new { notes = "   " },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Verification");
    }
}
