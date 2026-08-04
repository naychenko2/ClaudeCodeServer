using System.Text.Json;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Страховка: appsettings.Testing.json должен сдерживать DefaultProjectsPath="" — иначе
// тестовый хост без override TestWebApplicationFactory подхватит "/projects" из appsettings.json
// и создаст проект в продовой папке (через volume-mount /projects на C:\ClaudeHome). Файл
// существует ровно для этого — держим его контракт в регрессии.
public class AppSettingsTestingConfigTests
{
    private static string TestingSettingsPath
    {
        get
        {
            // Тестовый bin лежит в ClaudeHomeServer.Tests/bin/<config>/<tfm>/.
            // slnx — на 3 уровня выше; appsettings.Testing.json — рядом с ним, в ClaudeHomeServer/.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClaudeHomeServer.slnx")))
                dir = dir.Parent;
            dir.Should().NotBeNull("корень решения должен быть достижим из тестового bin");
            return Path.Combine(dir!.FullName, "ClaudeHomeServer", "appsettings.Testing.json");
        }
    }

    [Fact]
    public void Файл_Существует_ИВалидныйJson()
    {
        File.Exists(TestingSettingsPath).Should().BeTrue(
            "appsettings.Testing.json — страховка от попадания тестов в продовую папку проектов");

        using var stream = File.OpenRead(TestingSettingsPath);
        var act = () => JsonDocument.Parse(stream);
        act.Should().NotThrow();
    }

    [Fact]
    public void DefaultProjectsPath_Пустой()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestingSettingsPath));
        var has = doc.RootElement.TryGetProperty("DefaultProjectsPath", out var value);
        has.Should().BeTrue();
        value.ValueKind.Should().Be(JsonValueKind.String);
        value.GetString().Should().BeNullOrEmpty(
            "пустой путь заставит UserHomeResolver вернуть null с явным сообщением — лучше молчаливого /projects из appsettings.json");
    }
}
