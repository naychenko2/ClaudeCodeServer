using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backgrounds;
using ClaudeHomeServer.Services.Backup;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Фон проекта (ADR-008): контракт «от модели только числа и строки d», сборка тайла
// сервером, хранение и каскады.
public class ProjectBackgroundTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProjectManager _projects;
    private readonly string _userId = Guid.NewGuid().ToString();

    public ProjectBackgroundTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_bg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["DefaultProjectsPath"] = _tempDir,
            })
            .Build();
        var users = new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(config, users, new AppSettingsService(config));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Project NewProject(string? color = null)
    {
        var root = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return _projects.Create("Проект", root, _userId, "tester", color: color);
    }

    // Ответ модели с n годными фигурами (квадрат + круг у каждой)
    private static string Answer(int shapes, string colorKey = "green")
    {
        var sb = new StringBuilder();
        sb.Append("{\"colorKey\":\"").Append(colorKey).Append("\",\"shapes\":[");
        for (var i = 0; i < shapes; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture,
                $$"""{"x":{{10 + i * 3}},"y":{{20 + i}},"rotate":-5,"paths":["M0 0h20v14H0z"],"circles":[{"cx":5,"cy":5,"r":3}]}""");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ---------- Валидация путей ----------

    [Theory]
    [InlineData("M0 0h10<script>")]          // разметка в d
    [InlineData("M0 0h10\"/><path d=\"")]    // попытка закрыть атрибут
    [InlineData("M0 0L1e6 1e6")]             // экспонента
    [InlineData("M0 0L1000000 5")]           // координата в миллион
    [InlineData("M0 0L1.00000001 5")]        // длинный хвост мантиссы
    [InlineData("h10v10")]                   // не начинается с M
    [InlineData("M0 0 5")]                   // аргументы не кратны арности
    [InlineData("M0 0Z 5")]                  // аргумент у команды без арности
    [InlineData("M0 0l.5.5")]                // число без цифры до точки
    public void Негодная_строка_d_отбивается(string d)
    {
        Assert.False(ProjectDoodleTile.TryValidatePath(d, out _));
    }

    [Theory]
    [InlineData("M0 0h34v25H0z")]
    [InlineData("M7 9l4.5 4-4.5 4M17 17h10")]
    [InlineData("M0 0C1 2 3 4 5 6")]
    [InlineData("M0 0A5 5 0 0 1 10 10")]
    public void Годная_строка_d_принимается(string d)
    {
        Assert.True(ProjectDoodleTile.TryValidatePath(d, out _));
    }

    [Fact]
    public void Фигура_вылезающая_за_тайл_отбрасывается()
    {
        // x = 240 плюс габарит 40 → выезд за 250: в repeat-паттерне такая фигура рвётся
        var answer = """
            {"colorKey":"blue","shapes":[{"x":240,"y":10,"paths":["M0 0h40v40H0z"]}]}
            """;
        var result = ProjectDoodleTile.Build(answer);
        Assert.False(result.Ok);
        Assert.Equal("rejected", result.FailReason);
    }

    // ---------- Порог годности и цвет ----------

    [Fact]
    public void Меньше_восьми_годных_фигур_отказ()
    {
        var result = ProjectDoodleTile.Build(Answer(7));
        Assert.False(result.Ok);
        Assert.Equal("rejected", result.FailReason);
    }

    [Fact]
    public void Восемь_фигур_принимаются_и_собираются()
    {
        var result = ProjectDoodleTile.Build(Answer(8));
        Assert.True(result.Ok);
        Assert.Equal("green", result.ColorKey);
        Assert.Contains("<svg", result.Svg);
        Assert.Equal(8, result.Svg!.Split("<g ").Length - 1);
    }

    [Fact]
    public void Хвост_сверх_четырнадцати_фигур_отбрасывается()
    {
        var result = ProjectDoodleTile.Build(Answer(20));
        Assert.True(result.Ok);
        Assert.Equal(ProjectDoodleTile.MaxShapes, result.Svg!.Split("<g ").Length - 1);
    }

    [Fact]
    public void Невалидный_ключ_цвета_игнорируется_а_тайл_принимается()
    {
        var result = ProjectDoodleTile.Build(Answer(8, "#ff00ff"));
        Assert.True(result.Ok);
        Assert.Null(result.ColorKey);
    }

    [Fact]
    public void Сырой_svg_модели_не_разбирается()
    {
        var result = ProjectDoodleTile.Build("<svg><script>alert(1)</script></svg>");
        Assert.False(result.Ok);
        Assert.Equal("bad-json", result.FailReason);
    }

    [Fact]
    public void Ответ_в_markdown_заборе_разбирается()
    {
        var result = ProjectDoodleTile.Build("Вот фон:\n```json\n" + Answer(8) + "\n```\nГотово.");
        Assert.True(result.Ok);
    }

    // ---------- Сборка документа ----------

    [Fact]
    public void В_собранном_документе_нет_разметки_модели_даже_в_обход_валидатора()
    {
        // Прямой вызов Render мимо валидатора: XmlWriter — второй пояс
        var evil = "M0 0\"/><script>alert(1)</script><path d=\"M0 0";
        var svg = ProjectDoodleTile.Render(
            [new TileShape(1, 1, 0, [evil], [])]);
        Assert.DoesNotContain("<script", svg);
        Assert.DoesNotContain("<path d=\"M0 0\"/>", svg);
        Assert.Contains("&lt;script&gt;", svg);
    }

    [Fact]
    public void Числа_собираются_инвариантной_культурой()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            var svg = ProjectDoodleTile.Render(
                [new TileShape(10.5, 20.5, -7.5, ["M0 0h10"], [new TileCircle(1.5, 2.5, 3.5)])]);
            Assert.Contains("translate(10.5,20.5) rotate(-7.5)", svg);
            Assert.Contains("r=\"3.5\"", svg);
            Assert.DoesNotContain(",5", svg.Replace("10.5,20.5", ""));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Собранный_тайл_только_альфа_без_заливок_и_ссылок()
    {
        var svg = ProjectDoodleTile.Build(Answer(8)).Svg!;
        Assert.Contains("fill=\"none\"", svg);
        Assert.DoesNotContain("opacity", svg);
        Assert.DoesNotContain("style=", svg);
        Assert.DoesNotContain("href", svg);
        Assert.DoesNotContain("<text", svg);
    }

    // ---------- Хранение, каскады, идемпотентность ----------

    [Fact]
    public void Взятие_в_работу_не_даётся_дважды_подряд()
    {
        var project = NewProject();
        Assert.True(_projects.TryBeginBackground(project.Id));
        Assert.False(_projects.TryBeginBackground(project.Id));
        Assert.Equal(ProjectBackgroundKind.Pending, _projects.GetById(project.Id)!.Background!.Kind);
    }

    [Fact]
    public void Протухший_Pending_перезабирается()
    {
        var project = NewProject();
        Assert.True(_projects.TryBeginBackground(project.Id));
        Assert.True(_projects.TryBeginBackground(project.Id, staleAfter: TimeSpan.Zero));
    }

    [Fact]
    public void Возврат_стандартного_удаляет_файл_тайла()
    {
        var project = NewProject();
        var dir = Path.Combine(_projects.BackgroundsDir, project.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tile-a.svg"), "<svg/>");
        _projects.SetBackgroundGenerated(project.Id, "tile-a.svg");

        _projects.SetBackgroundStandard(project.Id);

        Assert.Equal(ProjectBackgroundKind.Standard, _projects.GetById(project.Id)!.Background!.Kind);
        Assert.False(File.Exists(Path.Combine(dir, "tile-a.svg")));
    }

    [Fact]
    public void Неудача_перегенерации_оставляет_прежний_тайл()
    {
        var project = NewProject();
        _projects.SetBackgroundGenerated(project.Id, "tile-a.svg");
        _projects.TryBeginBackground(project.Id);

        _projects.SetBackgroundFailed(project.Id, "bad-json");

        var background = _projects.GetById(project.Id)!.Background!;
        Assert.Equal(ProjectBackgroundKind.Generated, background.Kind);
        Assert.Equal("tile-a.svg", background.TileFile);
        Assert.Equal("bad-json", background.FailReason);
    }

    [Fact]
    public void Удаление_проекта_уносит_папку_тайлов()
    {
        var project = NewProject();
        var dir = Path.Combine(_projects.BackgroundsDir, project.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tile-a.svg"), "<svg/>");

        _projects.Delete(project.Id);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Тайлы_едут_в_основной_архив()
    {
        Assert.True(BackupPaths.ShouldInclude("project-backgrounds/abc/tile-x.svg"));
        // …а недописанный временный файл — нет
        Assert.False(BackupPaths.ShouldInclude("project-backgrounds/abc/tile-x.svg.tmp"));
    }

    // ---------- Сервис генерации ----------

    private ProjectBackgroundService Service(string answer) =>
        new(_projects, new FakeCheap(answer), NullLogger<ProjectBackgroundService>.Instance);

    [Fact]
    public async Task Удачная_генерация_пишет_файл_и_ссылку()
    {
        var project = NewProject();
        var result = await Service(Answer(10)).GenerateAsync(project);

        Assert.Equal(ProjectBackgroundKind.Generated, result.Kind);
        var saved = _projects.GetById(project.Id)!.Background!;
        Assert.Equal(result.TileVersion, saved.TileFile);
        var full = Path.Combine(_projects.BackgroundsDir, project.Id, saved.TileFile!);
        Assert.True(File.Exists(full));
        Assert.StartsWith("<svg", await File.ReadAllTextAsync(full));
        // Временных файлов не остаётся
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(full)!, "*.tmp"));
    }

    [Fact]
    public async Task Автоматический_цвет_проставляется_молча()
    {
        var project = NewProject();
        var result = await Service(Answer(10, "purple")).GenerateAsync(project);

        Assert.True(result.ColorApplied);
        Assert.Null(result.SuggestedColorKey);
        Assert.Equal("purple", _projects.GetById(project.Id)!.Icon.Color);
    }

    [Fact]
    public async Task Выбранный_руками_цвет_не_перезаписывается_а_предлагается()
    {
        var project = NewProject(color: "red");
        var result = await Service(Answer(10, "purple")).GenerateAsync(project);

        Assert.False(result.ColorApplied);
        Assert.Equal("purple", result.SuggestedColorKey);
        Assert.Equal("red", _projects.GetById(project.Id)!.Icon.Color);
    }

    [Fact]
    public async Task Битый_ответ_модели_оставляет_стандартный_фон()
    {
        var project = NewProject();
        var result = await Service("не сегодня").GenerateAsync(project);

        Assert.Equal(ProjectBackgroundKind.Failed, result.Kind);
        Assert.Equal("bad-json", result.FailReason);
        Assert.Null(_projects.GetById(project.Id)!.Background!.TileFile);
        Assert.False(Directory.Exists(Path.Combine(_projects.BackgroundsDir, project.Id)));
    }

    [Fact]
    public async Task Модель_не_ответила_фон_остаётся_стандартным()
    {
        var project = NewProject();
        var service = new ProjectBackgroundService(_projects,
            new FakeCheap(null), NullLogger<ProjectBackgroundService>.Instance);

        var result = await service.GenerateAsync(project);

        Assert.Equal(ProjectBackgroundKind.Failed, result.Kind);
        Assert.Equal("no-model", result.FailReason);
    }

    [Fact]
    public async Task Сброс_после_генерации_переводит_в_стандартный()
    {
        var project = NewProject();
        var service = Service(Answer(10));
        await service.GenerateAsync(project);

        var result = service.Reset(project.Id);

        Assert.Equal(ProjectBackgroundKind.Standard, result.Kind);
        Assert.Null(_projects.GetById(project.Id)!.Background!.TileFile);
    }

    // Ответ модели задан заранее; null — вызов падает (модели нет)
    private sealed class FakeCheap(string? answer) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Assert.Equal(LocalActionCatalog.ProjectBackground, actionKey);
            return answer is null
                ? throw new InvalidOperationException("модель недоступна")
                : Task.FromResult(answer);
        }

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
