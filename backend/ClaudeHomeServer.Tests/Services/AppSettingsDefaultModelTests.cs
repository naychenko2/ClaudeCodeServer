using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Слоты тиров моделей (AppSettings.ModelTierStrong/Medium/Weak): хранятся в app-settings.json,
// патчатся через PUT /api/settings (null = не трогать, "" = сознательная очистка слота).
// На слоты ссылаются назначения мест ("tier:strong|medium|weak") и дефолты каталога.
// Легаси-поле DefaultChatModel мигрирует в слот «средняя» при загрузке.
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
    public void Слоты_Пусты_ПоУмолчанию()
    {
        var s = BuildService().Get();
        s.ModelTierStrong.Should().BeNull();
        s.ModelTierMedium.Should().BeNull();
        s.ModelTierWeak.Should().BeNull();
    }

    [Fact]
    public void Слоты_СохраняютсяИПереживаютПерезапуск()
    {
        BuildService().Save(new AppSettings
        {
            ModelTierStrong = "opus",
            ModelTierMedium = "glm-5.2",
            ModelTierWeak = "haiku",
        });

        var s = BuildService().Get();
        s.ModelTierStrong.Should().Be("opus");
        s.ModelTierMedium.Should().Be("glm-5.2");
        s.ModelTierWeak.Should().Be("haiku");
    }

    [Fact]
    public void Патч_ОдногоСлота_НеТрогаетСоседние()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { ModelTierStrong = "opus", ModelTierWeak = "haiku" });

        svc.Save(new AppSettings { ModelTierMedium = "glm-5.2" });

        var s = svc.Get();
        s.ModelTierStrong.Should().Be("opus");
        s.ModelTierMedium.Should().Be("glm-5.2");
        s.ModelTierWeak.Should().Be("haiku");
    }

    [Fact]
    public void ПустаяСтрока_ОчищаетСлот()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { ModelTierMedium = "glm-5.2" });

        svc.Save(new AppSettings { ModelTierMedium = "" });

        svc.Get().ModelTierMedium.Should().Be("");
        svc.TierModel(ModelTier.Medium).Should().BeNull("очищенный слот — не модель с именем \"\"");
    }

    [Fact]
    public void TierModel_ОтдаётМодельСлота()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { ModelTierStrong = "opus", ModelTierWeak = "haiku" });

        svc.TierModel(ModelTier.Strong).Should().Be("opus");
        svc.TierModel(ModelTier.Weak).Should().Be("haiku");
        svc.TierModel(ModelTier.Medium).Should().BeNull("слот не задан — решает CLI");
    }

    // --- Миграция v1 → v2: одиночная DefaultChatModel переезжает в слот «средняя» ---

    [Fact]
    public void Миграция_DefaultChatModel_ПереезжаетВСреднийСлот()
    {
        File.WriteAllText(Path.Combine(_tempDir, "app-settings.json"),
            """{"DefaultChatModel":"glm-5.2"}""");

        var svc = BuildService();

        svc.TierModel(ModelTier.Medium).Should().Be("glm-5.2");
        // Миграция одноразовая: легаси-поле очищено и в файл больше не пишется
        BuildService().TierModel(ModelTier.Medium).Should().Be("glm-5.2");
    }

    [Fact]
    public void Миграция_НеПеретираетУжеЗаданныйСреднийСлот()
    {
        File.WriteAllText(Path.Combine(_tempDir, "app-settings.json"),
            """{"DefaultChatModel":"glm-5.2","ModelTierMedium":"sonnet"}""");

        BuildService().TierModel(ModelTier.Medium).Should().Be("sonnet");
    }
}
