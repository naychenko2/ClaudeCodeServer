using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Воспроизведение дефекта 1886eba9 (находка ревью S2c-4): гонка check-then-act на кэше
/// моделей NotesService при параллельной записи одного владельца. Поток, чей скан источников
/// прошёл место чужого файла до его создания, кладёт в кэш устаревшую модель: её запись
/// попадает в окно между чужим Invalidate и чужим GetModel (в Create это время журнала событий,
/// NotesService.cs:769-775); пострадавший читает чужую модель, не находит свою только что
/// записанную заметку и POST /api/notes отвечает 500 «Заметка создана, но не читается».
///
/// Все WriteAllText bursts создаются до начала любого Directory.EnumerateFiles (запись в Create
/// идёт раньше чтения), поэтому при одновременном старте устаревшая модель физически невозможна:
/// гонка требует позднего прихода — как в прода, где запросы приходят с разбросом. Тест строит
/// его структурно, без пауз-ожиданий: волна1 из N параллельных POST идёт на медленном скане
/// (фон notes/) и в длинном окне журнала (подменный IProjectEventLogService — тот же шов, что
/// в прода, только измеримой длины); волна2 из N параллельных POST стартует по первому ответу
/// волны1 — её записи попадают на хвост чужих сканов, а её окна журнала перекрывают чужие
/// записи в кэш. Пересечение фаз — конструкция, а не совпадение.
///
/// Защита, которую проверяет тест: поколение инвалидации в NotesService.GetModel — модель,
/// чей скан пересёкся с чужой записью, в кэш не кладётся. С ней обе волны всегда 200;
/// без неё волна2 отвечает 500 (не в каждом прогоне — позиция файла в перечислении
/// размыта), что и есть дефект.
/// </summary>
[Trait("Category", "Host")]
public class NotesConcurrentCreateTests : IClassFixture<TestWebApplicationFactory>
{
    // Фон делает скан источников ~200 мс: хвост чужого скана — измеримый отрезок времени,
    // в который успевает прийти запись второй волны.
    private const int Background = 2000;
    private const int Wave = 8;

    private readonly TestWebApplicationFactory _factory;

    public NotesConcurrentCreateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        // Медленный журнал — то же окно между Invalidate и GetDetail в Create, на котором
        // живёт гонка в прода (реальный сериализован локом + SQLite на единицы мс; здесь —
        // той же природы, но стабильной длины). Подменяется только в этом тесте.
        _factory.ExtraServices = services =>
            services.AddSingleton<IProjectEventLogService>(new SlowEventLog());
    }

    [Fact]
    public async Task ПараллельныеPOST_ЗаметокОдногоВладельца_КаждыйОтвечаетСвоейЗаметкой()
    {
        var owner = _factory.CreateAuthenticatedClient();

        var root = Path.Combine(_factory.TempDir, "race_notes");
        var notesDir = Path.Combine(root, "notes");
        Directory.CreateDirectory(notesDir);
        // Фон пишется до первого обращения к модели: первый скан обязан его увидеть.
        for (var i = 0; i < Background; i++)
            File.WriteAllText(Path.Combine(notesDir, $"Фон {i:D4}.md"), $"# Фон {i:D4}\n");

        var project = await owner.PostAsJsonAsync("/api/projects", new { name = "Проект гонки", rootPath = root });
        project.EnsureSuccessStatusCode();
        var projectId = JsonDocument.Parse(await project.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        // Волна1: N параллельных POST одного владельца — все сканируют фон одновременно.
        var wave1 = Enumerable.Range(0, Wave).Select(i => PostAsync(owner, projectId, i)).ToList();
        // Сигнал — первый завершённый Create: его скан уже дописывает чужие файлы, остальные
        // сканы ещё идут. Волна2: ещё N параллельных POST — их записи попадают на хвост чужих
        // сканов, их окна журнала (Append медленного шва) перекрывают чужие записи в кэш.
        await Task.WhenAny(wave1);
        var wave2 = Enumerable.Range(Wave, Wave).Select(i => PostAsync(owner, projectId, i)).ToList();

        var done = await Task.WhenAll(wave1.Concat(wave2)).WaitAsync(TimeSpan.FromSeconds(90));

        // Инвариант: заметка, созданная в ответе POST /api/notes, читается сразу.
        // 500 «Заметка создана, но не читается» — он же дефект.
        foreach (var (title, resp) in done)
        {
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"«{title}» ({(int)resp.StatusCode}): {body}");
            JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().Should().Be(title);
        }

        // Полный цикл: список проекта содержит все новые заметки — кэш не перечислен
        // устаревшей моделью.
        var list = await (await owner.GetAsync($"/api/notes?source={Uri.EscapeDataString(projectId)}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        list.EnumerateArray().Select(n => n.GetProperty("title").GetString())
            .Should().Contain(Enumerable.Range(0, 2 * Wave).Select(i => $"Гонка {i}"));
    }

    private static Task<(string Title, HttpResponseMessage Resp)> PostAsync(
        HttpClient client, string projectId, int i) => Task.Run(async () =>
        {
            var title = $"Гонка {i}";
            var resp = await client.PostAsJsonAsync("/api/notes",
                new { title, content = "тело", source = projectId });
            return (title, resp);
        });

    /// <summary>
    /// Подменный журнал событий: окно между Invalidate и GetDetail в Create (NotesService.cs:769-775)
    /// стабильной длины. Пауза — не ожидание гонки (запрещённого здесь «дождёмся таймером» нет):
    /// это сама измеряемая фаза, которую в прода держит сериализованная запись в SQLite.
    /// </summary>
    private sealed class SlowEventLog : IProjectEventLogService
    {
        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null, string? actorUserId = null)
        {
            Thread.Sleep(60);
            return null;
        }
    }
}
