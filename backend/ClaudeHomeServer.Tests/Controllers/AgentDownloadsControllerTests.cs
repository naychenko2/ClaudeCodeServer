using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Анонимная раздача агента (agent-distribution AD-3, Р11). Контроллер собирается руками: тут
/// проверяется поведение ручек и то, что строки запроса не доходят до диска, — по журналу
/// файловой системы каталога. Сквозной путь через роутинг — в <see cref="AgentDownloadsEndpointTests"/>.
/// </summary>
public class AgentDownloadsControllerTests : IDisposable
{
    private readonly AgentReleaseFixture _rel = new();
    private readonly RecordingReleaseFileSystem _fs = new();

    public void Dispose() => _rel.Dispose();

    private AgentReleaseCatalog Catalog(bool withRoot = true) => new(_rel.Config(withRoot), fileSystem: _fs);

    private static AgentDownloadsController Controller(AgentReleaseCatalog? catalog) =>
        new(NullLogger<AgentDownloadsController>.Instance, catalog)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static byte[] Body(IActionResult result)
    {
        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        using var stream = file.FileStream;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static int? Status(IActionResult result) => (result as IStatusCodeActionResult)?.StatusCode;

    [Fact]
    public void Архив_ОтдаётсяБайтВБайт()
    {
        var result = Controller(Catalog()).Archive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.WinFile);

        Body(result).Should().Equal(_rel.WinBytes);
        ((FileStreamResult)result).ContentType.Should().Be("application/zip");
    }

    [Fact]
    public void Архив_ДругогоRid_ЕгоСобственный()
    {
        var result = Controller(Catalog()).Archive(AgentReleaseFixture.Version, "linux-x64", AgentReleaseFixture.LinuxFile);

        Body(result).Should().Equal(_rel.LinuxBytes);
    }

    [Theory]
    [InlineData("install.ps1")]
    [InlineData("install.sh")]
    public void Скрипты_ВстроеныИОтдаются(string name)
    {
        var controller = Controller(Catalog());

        var result = name == "install.ps1" ? controller.InstallPs1() : controller.InstallSh();

        var text = Encoding.UTF8.GetString(Body(result));
        text.Should().NotBeNullOrWhiteSpace();
        File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "agent-install", name)).Should().Be(text,
            "раздаётся ровно файл из deploy/agent-install, встроенный в сборку");
    }

    [Fact]
    public void Указатель_ТекущаяВыкаткаСАрхивамиПоRid()
    {
        var result = Controller(Catalog()).Manifest();

        var json = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.GetProperty("version").GetString().Should().Be(AgentReleaseFixture.Version);
        var win = json.GetProperty("archives").GetProperty("win-x64");
        win.GetProperty("file").GetString().Should().Be(AgentReleaseFixture.WinFile);
        win.GetProperty("size").GetInt64().Should().Be(_rel.WinBytes.Length);
        win.GetProperty("sha256").GetString().Should().Be(AgentReleaseFixture.Sha(_rel.WinBytes));
    }

    public static TheoryData<string, string, string, string> Hostile
    {
        get
        {
            var v = AgentReleaseFixture.Version;
            var win = AgentReleaseFixture.WinFile;
            return new()
            {
                { "версия ..", "..", "win-x64", win },
                { "версия %2e%2e", "%2e%2e", "win-x64", win },
                { "версия с \\", "1.200.0\\..", "win-x64", win },
                { "RID ..", v, "..", win },
                { "имя ..", v, "win-x64", ".." },
                { "имя %2e%2e", v, "win-x64", "%2e%2e%2f..%2fagent-release.json" },
                { "имя с ../", v, "win-x64", "../../app/agent-release.json" },
                { "имя с \\", v, "win-x64", "..\\..\\app\\agent-release.json" },
                { "абсолютный путь", v, "win-x64", "/etc/passwd" },
                { "абсолютный путь Windows", v, "win-x64", "C:\\Windows\\win.ini" },
                { "чужой RID", v, "osx-arm64", win },
                { "RID другого регистра", v, "WIN-X64", win },
                { "архив чужого RID", v, "linux-x64", win },
                { "версия не из каталога", "1.9.0", "win-x64", win },
                { "имя не из манифеста, но файл есть на диске", v, "win-x64", "manifest.json" },
                { "имя не из манифеста", v, "win-x64", "ai-home-agent-1.200.0-win-x64.exe" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void ВраждебныйЗапрос_404_ИДоДискаНеДоходит(string why, string version, string rid, string file)
    {
        var catalog = Catalog();
        catalog.Current();
        _fs.Reset();

        var result = Controller(catalog).Archive(version, rid, file);

        result.Should().BeOfType<NotFoundObjectResult>(why);
        // Трогать можно только то, что задано конфигом: корень каталога и указатель (проверка
        // mtime). Ни один путь, собранный из строк запроса, до файловой системы не доезжает
        _fs.Paths.Distinct().Should().BeSubsetOf([_rel.Root, _rel.PointerPath], why);
        _fs.Count("open").Should().Be(0, why);
    }

    [Fact]
    public void ДесктопВыключен_503СПричинойНаВсехРучках()
    {
        var controller = Controller(null);

        foreach (var result in new[]
                 {
                     controller.InstallPs1(), controller.InstallSh(), controller.Manifest(),
                     controller.Archive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.WinFile),
                 })
        {
            Status(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
            JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("error").GetString()
                .Should().Be(AgentDownloadsController.DesktopDisabled);
        }
    }

    [Fact]
    public void КаталогНеНастроен_503НаУказательИАрхив()
    {
        var controller = Controller(Catalog(withRoot: false));

        Status(controller.Manifest()).Should().Be(StatusCodes.Status503ServiceUnavailable);
        Status(controller.Archive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.WinFile))
            .Should().Be(StatusCodes.Status503ServiceUnavailable);
        _fs.Count("open").Should().Be(0);
    }

    [Fact]
    public void АрхивОписанНоСтёрт_404АНе500()
    {
        File.Delete(Path.Combine(_rel.Root, AgentReleaseFixture.Version, AgentReleaseFixture.WinFile));

        var result = Controller(Catalog()).Archive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.WinFile);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Атрибуты_АнонимноИПодЛимитомПоIp()
    {
        var type = typeof(AgentDownloadsController);

        type.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull(
            "раздача анонимна намеренно: установщик качается до сопряжения");
        type.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
            .Should().Be(AgentDownloadsController.RateLimitPolicy).And.Be("agent-download");

        // Ни одна ручка не снимает лимит и не требует авторизации в обход класса
        foreach (var action in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            action.GetCustomAttribute<DisableRateLimitingAttribute>().Should().BeNull(action.Name);
            action.GetCustomAttribute<AuthorizeAttribute>().Should().BeNull(action.Name);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "deploy", "agent-install")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }
}
