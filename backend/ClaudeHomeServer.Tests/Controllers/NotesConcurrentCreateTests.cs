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
/// Воспроизведение дефекта 1886eba9 (находка ревью S2c-4): гонка check-then-act на кэше моделей
/// NotesService при параллельной записи одного владельца.
///
/// Хронология Create (NotesService.cs:785-794): запись файла → Invalidate → Append журнала →
/// GetDetail→GetModel. Окно между чужим Invalidate и чужим GetModel — время Append; выгрузка в кэш
/// (:101) другой волны, попавшая в это окно, и есть гонка: пострашший читает чужую модель и не
/// находит свою только что записанную заметку — POST /api/notes отвечает 500 «Заметка создана, но
/// не читается».
///
/// Пересечение фаз построено событиями барьера (доработка ревью d447582f; CLAUDE.md:493-496 —
/// ожидание события через TaskCompletionSource; в тесте нет ни Thread.Sleep, ни Task.Delay):
///   1. Первая волна — N параллельных POST на медленный скан (фон notes/). Её первый ответ —
///      сигнал: к нему скан самого быстрого потока первой уже закончен (ответ следует за выгрузкой,
///      выгрузка — за сканом, :92-104), значит его модель не содержит файлов, записанных позже.
///   2. Вторая волна стартует по этому сигналу: её файлы и Invalidate приходят после конца скана
///      быстрого потока. К этому моменту перечисление фонового каталога любым потоком первой волны
///      прошло уже почти всю пачку (позиция файла «Гонка N» в хеш-порядке — середина пачки, замер:
///      1730 из 2001), а файл второй волны создан только сейчас — в модели этого потока его нет.
///   3. Append каждой второй стоит на барьер до последнего ответа первой волны: GetModel каждой
///      второй — после всех выгрузок первой, а любая выгрузка опережает свой ответ — последняя
///      выгрузка первой лежит до GetModel пострашшего. Её скан к моменту записи второй ещё шёл
///      (разброс окончания параллельных сканов — десятки мс) — её выгрузка после чужого Invalidate:
///      между чужим Invalidate и чужим GetModel — это и есть окно гонки.
///
/// Защита, которую проверяет тест: сверка поколения в GetModel (:96,:101) — модель, чей скан
/// пересёкся с чужой записью, в кэш не кладётся. С ней обе волны всегда 200: устаревшие модели
/// первой волны не попадают в кэш, вторая перечитывает сама. Без неё — последняя устаревшая модель
/// первой волны уже в кэш, когда вторая читает модель после своего Invalidate: 500, что и есть
/// дефект. Остаточное окно check-then-act между сверкой поколения и записью в кэш (:101-102) —
/// отдельный дефект, этим тестом не проверяется.
/// </summary>
[Trait("Category", "Host")]
public class NotesConcurrentCreateTests : IClassFixture<TestWebApplicationFactory>
{
    // Фон делает полный скан ~1 с: разброс окончания сканов первой волны — десятки мс,
    // записи второй волны — единицы мс после первого ответа первой.
    private const int Background = 2000;
    private const int Wave = 8;

    private readonly TestWebApplicationFactory _factory;

    // Барьер — в подменном шве (RaceEventLog): фазы сцеплены его сигналами, временных окон в тесте нет.
    private readonly RaceEventLog _gate;

    public NotesConcurrentCreateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _gate = new RaceEventLog();
        // Подмена ставится до первого обращения к хосту: он строится на первом запросе.
        _factory.ExtraServices = services => services.AddSingleton<IProjectEventLogService>(_gate);
    }

    [Fact]
    public async Task ПараллельныеPOST_ЗаметокОдногоВладельца_КаждыйОтвечаетСвоейЗаметкой()
    {
        var owner = _factory.CreateAuthenticatedClient();

        var root = Path.Combine(_factory.TempDir, "race_notes");
        var notesDir = Path.Combine(root, "notes");
        Directory.CreateDirectory(notesDir);
        // Фон пишется до первого обращения к модели: скан первой волны обязан его увидеть.
        for (var i = 0; i < Background; i++)
            File.WriteAllText(Path.Combine(notesDir, $"Фон {i:D4}.md"), $"# Фон {i:D4}\n");

        var project = await owner.PostAsJsonAsync("/api/projects", new { name = "Проект гонки", rootPath = root });
        project.EnsureSuccessStatusCode();
        var projectId = JsonDocument.Parse(await project.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        // Первая волна: N параллельных POST в корень проекта на медленный скан.
        var wave1 = Enumerable.Range(0, Wave).Select(i => PostAsync(owner, projectId, i)).ToList();

        // Первый ответ первой — её самый быстрый поток уже выгрузил модель (ответ следует за
        // выгрузкой): с этого момента запись второй волны не попадёт в скан этой модели.
        var firstDone = await Task.WhenAny(wave1).WaitAsync(TimeSpan.FromSeconds(60));
        var wave2 = Enumerable.Range(Wave, Wave).Select(i => PostAsync(owner, projectId, i)).ToList();

        // Последний ответ первой — сигнал окончания всех её выгрузок. Append каждой второй держится
        // барьером до него: GetModel каждой второй — после всех выгрузок первой, последняя из которых
        // (модель устарела: её скан закончен до записи файлов второй) и попадает в окно гонки.
        var done1 = await Task.WhenAll(wave1).WaitAsync(TimeSpan.FromSeconds(60));
        _gate.W1AllDone.SetResult();

        foreach (var (title, resp) in done1)
        {
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"«{title}» ({(int)resp.StatusCode}): {body}");
            JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().Should().Be(title);
        }

        // Инвариант и для второй волны: созданная заметка читается сразу — независимо от того, кто выиграл гонку.
        var done2 = await Task.WhenAll(wave2).WaitAsync(TimeSpan.FromSeconds(60));
        foreach (var (title, resp) in done2)
        {
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"«{title}» ({(int)resp.StatusCode}): {body}");
            JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().Should().Be(title);
        }

        // Полный цикл: список проекта содержит заметки обеих волн — кэш не перечеркнут устаревшей моделью.
        var list = await (await owner.GetAsync($"/api/notes?source={Uri.EscapeDataString(projectId)}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var titles = list.EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        titles.Should().Contain(Enumerable.Range(0, 2 * Wave).Select(i => $"Гонка {i}"));
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
    /// Подменный журнал событий — барьер теста. Его единственный шов — Append в Create между
    /// Invalidate и GetModel (NotesService.cs:787-793), то самое окно, на котором живёт гонка.
    /// Append каждой второй стоит на барьер до сигнала теста — окончания всех ответов первой волны:
    /// окно второй накрывает все выгрузки первой. Никаких пауз: ни Thread.Sleep, ни Task.Delay —
    /// только ожидание события через TaskCompletionSource (CLAUDE.md:493-496).
    /// </summary>
    private sealed class RaceEventLog : IProjectEventLogService
    {
        public TaskCompletionSource W1AllDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null, string? actorUserId = null)
        {
            // Волны различаются индексом в заголовке: «Гонка 0..7» — первая, «Гонка 8..15» — вторая.
            if (!summary.Contains("«Гонка "))
                return null;
            var title = summary[summary.IndexOf("«Гонка ", StringComparison.Ordinal)..];
            var i = int.Parse(title[7..title.IndexOf('»')]);
            if (i < Wave)
                return null;
            // Любая вторая: окно держится барьером до окончания ответов первой.
            W1AllDone.Task.Wait();
            return null;
        }
    }
}
