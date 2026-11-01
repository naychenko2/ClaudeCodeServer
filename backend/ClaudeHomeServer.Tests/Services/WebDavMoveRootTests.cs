using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Дефект e69f9de1 (ревью W3-0): MOVE с Destination = URI корня проекта со завершающим слэшем
// резолвил цель в корень, снос существующей цели сносил весь проект, а files.Rename падал
// FileNotFoundException → 404. Отказ на пустой стороне (цель или источник — корень) стоит
// раньше SafeJoin и любого сноса, по образцу DELETE/LOCK: 403, проект цел.
//
// Раскладка здесь — боевая: корень проекта ОТДЕЛЁН от data-корня (соседний каталог), поэтому
// гейт вики о корне проекта молчит (он предок вики только в раскладке «проект = домашний
// каталог с data/», как в WebDavWikiGateTests). Единственный стопор — проверка пустого пути:
// в той, вики-гейтовой раскладке новый случай закрылся бы старым гейтом и мутация его не
// покраснила бы.
public class WebDavMoveRootTests : IDisposable
{
    private const string Project = "p1";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "webdav-move-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;    // корень проекта — не содержит data/
    private readonly string _dataDir; // data-корень — соседний, тот же резолв от DataPath, что у FileService

    public WebDavMoveRootTests()
    {
        _root = Path.Combine(_home, "project");
        _dataDir = Path.Combine(_home, "data");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_dataDir, "wiki")); // вики реальна и отделена: гейт жив, но о корне молчит
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* временная папка */ }
    }

    private DefaultHttpContext Ctx()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dataDir, "projects.json") })
            .Build();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider(),
        };
        ctx.Request.Method = "MOVE";
        return ctx;
    }

    // Destination — абсолютный URI с /projects/{name}/ (ParseDestination), как шлёт клиент
    private static void SetMoveDestination(DefaultHttpContext ctx, string relPath)
    {
        ctx.Request.Method = "MOVE";
        ctx.Request.Headers["Destination"] = $"http://localhost/projects/{Project}/{relPath}";
    }

    [Fact]
    public void Move_ЦельКореньПроекта_Overwrite_403_ПроектЦел_ИсточникНаМесте()
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        var keep = Path.Combine(_root, "keep.txt"); // другой файл проекта: снос был рекурсивным
        File.WriteAllText(keep, "файл проекта");

        var ctx = Ctx();
        SetMoveDestination(ctx, ""); // URI корня со завершающим слэшем → ParseDestination отдаёт ""
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(_root).Should().BeTrue();  // проект цел: отказ до любого следа на диске
        File.Exists(keep).Should().BeTrue();
        File.Exists(src).Should().BeTrue();         // источник не тронут
        File.ReadAllText(src).Should().Be("обычный файл");
        Directory.Exists(_dataDir).Should().BeTrue();
    }

    // Та же граница с другой стороны: источник — корень проекта (MOVE /projects/{name}/
    // куда-то). Корень не двигается: files.Rename с пустым relPath не зовётся.
    [Fact]
    public void Move_ИсточникКореньПроекта_403_КореньНеДвигается_ЦельНаМесте()
    {
        var dst = Path.Combine(_root, "docs", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(dst, "обычный файл");

        var ctx = Ctx();
        SetMoveDestination(ctx, "docs/b.txt");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "", Project);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(_root).Should().BeTrue();
        File.Exists(dst).Should().BeTrue(); // цель не снесена
        File.ReadAllText(dst).Should().Be("обычный файл");
    }
}
