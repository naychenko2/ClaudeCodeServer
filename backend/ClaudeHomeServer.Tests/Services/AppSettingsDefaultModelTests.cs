using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Глобальная «модель по умолчанию для новых чатов» (AppSettings.DefaultChatModel):
// хранится в app-settings.json, патчится через PUT /api/settings (null = не трогать,
// "" = сознательный сброс к дефолту CLI). Применяется в SessionManager и как route "default".
public class AppSettingsDefaultModelTests : IDisposable
{
    private readonly string _tempDir;

    public AppSettingsDefaultModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "appsettings_dcm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private AppSettingsService BuildService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new AppSettingsService(config);
    }

    [Fact]
    public void DefaultChatModel_Пуста_ПоУмолчанию()
    {
        BuildService().Get().DefaultChatModel.Should().BeNull();
    }

    [Fact]
    public void DefaultChatModel_СохраняетсяИЧитается()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { DefaultChatModel = "glm-5.2" });

        svc.Get().DefaultChatModel.Should().Be("glm-5.2");
    }

    [Fact]
    public void DefaultChatModel_ПереживаетПерезапуск()
    {
        BuildService().Save(new AppSettings { DefaultChatModel = "glm-5.2" });

        BuildService().Get().DefaultChatModel.Should().Be("glm-5.2");
    }

    [Fact]
    public void Патч_СоседнегоПоля_НеТрогаетDefaultChatModel()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { DefaultChatModel = "glm-5.2" });

        svc.Save(new AppSettings { ClaudeBilling = "api" });

        svc.Get().DefaultChatModel.Should().Be("glm-5.2");
    }

    [Fact]
    public void ПустаяСтрока_СбрасываетDefaultChatModel()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { DefaultChatModel = "glm-5.2" });

        svc.Save(new AppSettings { DefaultChatModel = "" });

        svc.Get().DefaultChatModel.Should().Be("");
    }
}
