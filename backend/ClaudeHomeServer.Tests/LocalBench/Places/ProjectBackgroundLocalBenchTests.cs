using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backgrounds;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>project-background</c> на живой модели (Батарея II, ось A).
///
/// Берётся НАСТОЯЩИЙ <see cref="ProjectBackgroundService"/> поверх настоящего
/// <see cref="ProjectManager"/> во временном каталоге: промпт строится из проекта (имя
/// плюс его системный промпт), а собранный тайл ложится на диск — то есть меряется место
/// целиком, вместе со сборкой документа, а не один ответ модели.
///
/// Самое дорогое место батареи: профиль Large, ответ — десяток фигур с путями. Кейс
/// стоит секунд, а не сотен миллисекунд, и это часть замеряемого.
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов валидности тест не утверждает.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class ProjectBackgroundLocalBenchTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "localbench_bg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Замер_фона_проекта()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.ProjectBackground);
        var projects = BuildProjectManager();
        var service = new ProjectBackgroundService(projects,
            new ProjectBackgroundWriterAdapter(projects), runner,
            NullLogger<ProjectBackgroundService>.Instance);

        var report = await LocalBenchLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var project = EnsureProject(projects, c);
                var result = await service.GenerateAsync(project, CancellationToken.None);
                // Что место выдало на выходе: состояние фона, число принятых фигур и
                // судьба цвета — рядом с цветом, который у проекта выбран на самом деле.
                var (shapes, colorKey) = ProjectBackgroundOracle.Summary(
                    runner.LastShot?.RawAnswer);
                var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "colorKey") ?? "-";
                var state = result.Kind == ProjectBackgroundKind.Generated
                    ? $"тайл из {shapes} фигур, цвет {colorKey ?? "нет"}"
                    : $"стандартный фон ({result.FailReason})";
                return $"→ {state}; у проекта {expected}";
            },
            judge: (_, turns) => ProjectBackgroundOracle.Violation(turns.Last.RawAnswer),
            output,
            reference: PlaceReferences.ProjectBackground);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }

    // Проект кейса: имя из input, системный промпт из context.prompt — из них место и
    // строит свой промпт. Прогревочный проход идёт по первому кейсу повторно, поэтому
    // уже созданный проект берём как есть, сбрасывая фон на «не пробовали».
    private static Project EnsureProject(ProjectManager projects, LocalBenchCase c)
    {
        var existing = projects.GetAll().FirstOrDefault(p => p.Name == c.Input);
        if (existing is not null)
        {
            // Без сброса второй проход по тому же проекту не дошёл бы до модели:
            // Generated фон повторно не генерируется (ADR-008 §10).
            projects.SetBackgroundStandard(existing.Id);
            return projects.GetById(existing.Id)!;
        }

        var root = Path.Combine(Path.GetTempPath(), "localbench_bg_root_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = projects.Create(c.Input, root, "bench-owner", "bench");
        var prompt = TaskClassifyLocalBenchTests.Text(c.Context, "prompt");
        return string.IsNullOrWhiteSpace(prompt)
            ? project
            : projects.Update(project.Id, null, null, prompt);
    }

    private ProjectManager BuildProjectManager()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "projects"),
            }).Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        return new ProjectManager(config, users, new AppSettingsService(config));
    }
}
